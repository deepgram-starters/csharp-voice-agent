/**
 * C# Voice Agent Starter - Backend Server
 *
 * A WebSocket bridge between browser clients and Deepgram's Voice Agent API,
 * built on the Deepgram .NET SDK's Agent WebSocket client
 * (ClientFactory.CreateAgentWebSocketClient) rather than a raw proxy.
 *
 * The browser-facing side is unchanged: the client still drives the agent
 * protocol. Because the SDK couples opening the socket with sending Settings,
 * the server synthesizes the "Welcome" locally, translates the client's first
 * "Settings" message into the SDK's typed Connect call, and then forwards
 * subsequent control messages and agent events. See HandleAgentStream for the
 * details of that handshake adaptation.
 *
 * Key Features:
 * - WebSocket endpoint: /api/voice-agent (backed by the SDK Agent client)
 * - JWT session auth with rate limiting (production only)
 * - Metadata endpoint: GET /api/metadata
 * - CORS enabled for frontend communication
 * - Graceful shutdown with connection tracking
 */

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deepgram;
using Deepgram.Models.Agent.v2.WebSocket;
using Deepgram.Models.Authenticate.v1;
using Microsoft.IdentityModel.Tokens;
using Tomlyn;
using Tomlyn.Model;
using HttpResults = Microsoft.AspNetCore.Http.Results;

// ============================================================================
// ENVIRONMENT LOADING
// ============================================================================

DotNetEnv.Env.Load();

// ============================================================================
// CONFIGURATION
// ============================================================================

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8081;
var host = Environment.GetEnvironmentVariable("HOST") ?? "0.0.0.0";
var frontendPort = int.TryParse(Environment.GetEnvironmentVariable("FRONTEND_PORT"), out var fp) ? fp : 8080;
const string DeepgramAgentUrl = "wss://agent.deepgram.com/v1/agent/converse";

// ============================================================================
// SESSION AUTH - JWT tokens with rate limiting for production security
// ============================================================================

var sessionSecretEnv = Environment.GetEnvironmentVariable("SESSION_SECRET");
var sessionSecret = sessionSecretEnv ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
var sessionSecretKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(sessionSecret));

const int JwtExpirySeconds = 3600; // 1 hour

string CreateSessionToken()
{
    var handler = new JwtSecurityTokenHandler();
    var descriptor = new SecurityTokenDescriptor
    {
        Expires = DateTime.UtcNow.AddSeconds(JwtExpirySeconds),
        SigningCredentials = new SigningCredentials(sessionSecretKey, SecurityAlgorithms.HmacSha256Signature),
    };
    var token = handler.CreateToken(descriptor);
    return handler.WriteToken(token);
}

bool ValidateSessionToken(string token)
{
    try
    {
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = sessionSecretKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        }, out _);
        return true;
    }
    catch
    {
        return false;
    }
}

/// Validates JWT from WebSocket subprotocol: access_token.<jwt>
string? ValidateWsToken(string? protocolHeader)
{
    if (string.IsNullOrEmpty(protocolHeader)) return null;
    var protocols = protocolHeader.Split(',', StringSplitOptions.TrimEntries);
    var tokenProto = protocols.FirstOrDefault(p => p.StartsWith("access_token."));
    if (tokenProto == null) return null;
    var token = tokenProto["access_token.".Length..];
    return ValidateSessionToken(token) ? tokenProto : null;
}

// ============================================================================
// API KEY LOADING
// ============================================================================

static string LoadApiKey()
{
    var apiKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");

    if (string.IsNullOrEmpty(apiKey))
    {
        Console.Error.WriteLine("\n❌ ERROR: Deepgram API key not found!\n");
        Console.Error.WriteLine("Please set your API key using one of these methods:\n");
        Console.Error.WriteLine("1. Create a .env file (recommended):");
        Console.Error.WriteLine("   DEEPGRAM_API_KEY=your_api_key_here\n");
        Console.Error.WriteLine("2. Environment variable:");
        Console.Error.WriteLine("   export DEEPGRAM_API_KEY=your_api_key_here\n");
        Console.Error.WriteLine("Get your API key at: https://console.deepgram.com\n");
        Environment.Exit(1);
    }

    return apiKey;
}

var apiKey = LoadApiKey();

// SDK Information logs include session settings, prompts, and custom endpoint headers.
Library.Initialize(AgentBridgeProtocol.SdkLogLevel, filename: null);

// ============================================================================
// SETUP
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{host}:{port}");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                $"http://localhost:{frontendPort}",
                $"http://127.0.0.1:{frontendPort}")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// Track active connections for graceful shutdown
var activeConnections = new ConcurrentDictionary<string, WebSocket>();

// ============================================================================
// SESSION ROUTES - Auth endpoints (unprotected)
// ============================================================================

/// GET /api/session — Issues a JWT for API authentication
app.MapGet("/api/session", () =>
{
    var token = CreateSessionToken();
    return HttpResults.Json(new Dictionary<string, string> { ["token"] = token });
});

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

static async Task ForwardRawMessages(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
{
    var buffer = new byte[8192];
    while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
    {
        var result = await source.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await destination.CloseAsync(
                result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                result.CloseStatusDescription ?? "Connection closed",
                cancellationToken);
            return;
        }

        await destination.SendAsync(
            buffer.AsMemory(0, result.Count),
            result.MessageType,
            result.EndOfMessage,
            cancellationToken);
    }
}

static async Task HandleFallbackSpeakSettings(
    WebSocket clientWs,
    string apiKey,
    string settingsJson,
    CancellationToken appCt)
{
    using var deepgramWs = new ClientWebSocket();
    deepgramWs.Options.SetRequestHeader("Authorization", $"Token {apiKey}");
    await deepgramWs.ConnectAsync(new Uri(DeepgramAgentUrl), appCt);
    await deepgramWs.SendAsync(
        Encoding.UTF8.GetBytes(settingsJson),
        WebSocketMessageType.Text,
        true,
        appCt);

    using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
    var clientToDeepgram = ForwardRawMessages(clientWs, deepgramWs, bridgeCts.Token);
    var deepgramToClient = ForwardRawMessages(deepgramWs, clientWs, bridgeCts.Token);
    await Task.WhenAny(clientToDeepgram, deepgramToClient);
    bridgeCts.Cancel();
    try { await Task.WhenAll(clientToDeepgram, deepgramToClient); }
    catch (OperationCanceledException) { }
}

/// Handles a single session between a browser client and the Deepgram Voice Agent.
///
/// The browser-facing WebSocket is unchanged: the client still drives the agent
/// protocol (waits for "Welcome", sends its "Settings", then "UpdateSpeak",
/// "UpdatePrompt", "InjectUserMessage" and binary audio) and receives the agent's
/// native JSON events plus binary audio. Only the Deepgram-facing side now uses the
/// Deepgram .NET SDK (ClientFactory.CreateAgentWebSocketClient) instead of a raw
/// ClientWebSocket.
///
/// One adaptation is required: the SDK couples establishing the socket with sending
/// the Settings message (agentClient.Connect(SettingsSchema)), whereas the raw proxy
/// was fully transparent. To preserve the frontend's exact handshake (it waits for a
/// "Welcome" before sending "Settings"), the server synthesizes the "Welcome" locally,
/// then translates the client's first "Settings" message into the SDK's typed Connect
/// call. The real "Welcome" surfaced by the SDK is intentionally not re-forwarded.
/// Subsequent control messages are forwarded verbatim over the SDK connection.
async Task HandleAgentStream(WebSocket clientWs, string apiKey, CancellationToken appCt)
{
    var connectionId = Guid.NewGuid().ToString("N")[..8];
    activeConnections[connectionId] = clientWs;
    Console.WriteLine($"[{connectionId}] Client connected to /api/voice-agent");
    // An upstream Close stops browser receives, but must not cancel the sender before
    // any already-queued terminal event reaches the browser.
    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
    var closeStatus = WebSocketCloseStatus.NormalClosure;
    var closeDescription = "Connection ended";

    // Outbound queue → browser (agent JSON events + binary audio). SDK event handlers
    // fire from the receive loop and may overlap, so all sends are funneled through one
    // writer.
    var outbound = System.Threading.Channels.Channel.CreateUnbounded<AgentBridgeProtocol.OutboundMessage>();
    void SendText(string s) => outbound.Writer.TryWrite(new(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text));

    // Serialize an agent event to the wire JSON the frontend expects. This mirrors the
    // SDK response records' own serialization (Web defaults + explicit [JsonPropertyName]
    // fields and PascalCase enum "type" discriminators) but deliberately does NOT run the
    // SDK ToString()'s trailing Regex.Unescape. That unescape turns any reply containing a
    // quote or newline into invalid JSON, so the browser's JSON.parse throws and the chat
    // message is lost. Serializing directly keeps quotes/newlines properly escaped.
    void SendJson<T>(T e) => SendText(AgentBridgeProtocol.SerializeEvent(e));

    // Deepgram Voice Agent client (replaces the raw ClientWebSocket). KeepAlive keeps the
    // upstream connection open during silence.
    var options = new DeepgramWsClientOptions(keepAlive: true);
    var agentClient = ClientFactory.CreateAgentWebSocketClient(apiKey, options);

    // Forward every agent event to the browser as the raw JSON / audio the frontend
    // expects. The SDK response records are serialized to the agent wire format via
    // SendJson (see above). WelcomeResponse is deliberately NOT forwarded (see note above).
    await agentClient.Subscribe(new EventHandler<AudioResponse>((_, e) =>
    {
        if (e.Stream != null)
            outbound.Writer.TryWrite(new(e.Stream.ToArray(), WebSocketMessageType.Binary));
    }));
    await agentClient.Subscribe(new EventHandler<ConversationTextResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<UserStartedSpeakingResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<AgentThinkingResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<AgentStartedSpeakingResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<AgentAudioDoneResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<FunctionCallRequestResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<SettingsAppliedResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<PromptUpdatedResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<SpeakUpdatedResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<InjectionRefusedResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<ErrorResponse>((_, e) => SendJson(e)));
    await agentClient.Subscribe(new EventHandler<UnhandledResponse>((_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Raw))
            SendText(e.Raw);
    }));
    await agentClient.Subscribe(new EventHandler<CloseResponse>((_, _) =>
    {
        try { receiveCts.Cancel(); } catch (ObjectDisposedException) { }
    }));

    // Pump queued messages to the browser one at a time.
    var pump = Task.Run(async () =>
    {
        try
        {
            await AgentBridgeProtocol.DrainOutboundAsync(outbound.Reader, appCt, async (message, cancellationToken) =>
            {
                if (clientWs.State != WebSocketState.Open) return;
                await clientWs.SendAsync(message.Payload, message.Type, true, cancellationToken);
            });
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });

    var connected = false;
    try
    {
        // Unblock the frontend's handshake: it waits for a Welcome before sending Settings.
        SendText(AgentBridgeProtocol.CreateWelcome());

        var messageBuffer = new MemoryStream();
        var buffer = new byte[8192];
        while (clientWs.State == WebSocketState.Open)
        {
            var result = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            messageBuffer.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var payload = messageBuffer.ToArray();
            messageBuffer.SetLength(0);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Microphone audio → forward to the agent once configured.
                if (connected && payload.Length > 0)
                    await agentClient.SendBinaryImmediately(payload);
                continue;
            }

            // Text control message from the browser.
            var text = Encoding.UTF8.GetString(payload);
            if (!connected)
            {
                if (AgentBridgeProtocol.IsFallbackSpeakSettings(text))
                {
                    Console.WriteLine($"[{connectionId}] Connecting to Deepgram Agent API with fallback voices...");
                    await HandleFallbackSpeakSettings(clientWs, apiKey, text, appCt);
                    return;
                }

                // The first Settings message establishes the upstream connection.
                if (!AgentBridgeProtocol.TryParseInitialSettings(text, out var settings))
                {
                    Console.Error.WriteLine($"[{connectionId}] Could not parse initial Settings message");
                    SendText(AgentBridgeProtocol.CreateInvalidSettingsError());
                    closeStatus = AgentBridgeProtocol.InvalidSettingsCloseStatus;
                    closeDescription = AgentBridgeProtocol.InvalidSettingsDescription;
                    break;
                }

                try
                {
                    Console.WriteLine($"[{connectionId}] Connecting to Deepgram Agent API...");
                    connected = await agentClient.Connect(settings!);
                    if (!connected)
                    {
                        Console.Error.WriteLine($"[{connectionId}] Failed to connect to Deepgram Agent");
                        SendText("{\"type\":\"Error\",\"description\":\"Failed to connect to Deepgram Agent\",\"code\":\"CONNECTION_FAILED\"}");
                        closeStatus = WebSocketCloseStatus.InternalServerError;
                        closeDescription = "Failed to connect to Deepgram Agent";
                        break;
                    }
                    Console.WriteLine($"[{connectionId}] ✓ Connected to Deepgram Agent API");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{connectionId}] Failed to connect to Deepgram Agent: {ex.Message}");
                    SendText("{\"type\":\"Error\",\"description\":\"Failed to connect to Deepgram Agent\",\"code\":\"CONNECTION_FAILED\"}");
                    closeStatus = WebSocketCloseStatus.InternalServerError;
                    closeDescription = "Failed to connect to Deepgram Agent";
                    break;
                }
                continue;
            }

            // Forward post-connect control messages (UpdateSpeak, UpdatePrompt,
            // InjectUserMessage, KeepAlive, ...) verbatim over the SDK connection.
            await agentClient.SendMessageImmediately(Encoding.UTF8.GetBytes(text));
        }
    }
    catch (OperationCanceledException)
    {
        // App shutdown, client disconnect, or an upstream Agent close.
    }
    catch (WebSocketException ex)
    {
        Console.Error.WriteLine($"[{connectionId}] WebSocket error: {ex.Message}");
    }
    finally
    {
        try { await agentClient.Stop(); } catch { }
        outbound.Writer.TryComplete();
        try { await pump; } catch { }

        if (clientWs.State == WebSocketState.Open)
        {
            try
            {
                await clientWs.CloseAsync(
                    closeStatus,
                    closeDescription,
                    CancellationToken.None);
            }
            catch { }
        }

        activeConnections.TryRemove(connectionId, out _);
        Console.WriteLine($"[{connectionId}] Connection closed ({activeConnections.Count} active)");
    }
}

// ============================================================================
// WEBSOCKET ENDPOINT
// ============================================================================

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/api/voice-agent" && context.WebSockets.IsWebSocketRequest)
    {
        // Validate JWT from WebSocket subprotocol
        var protocolHeader = context.Request.Headers["Sec-WebSocket-Protocol"].FirstOrDefault();
        var validProto = ValidateWsToken(protocolHeader);
        if (validProto == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var clientWs = await context.WebSockets.AcceptWebSocketAsync(validProto);
        await HandleAgentStream(clientWs, apiKey, context.RequestAborted);
    }
    else
    {
        await next(context);
    }
});

// ============================================================================
// API ROUTES
// ============================================================================

// Health check endpoint
app.MapGet("/health", () => HttpResults.Json(new { status = "ok", service = "voice-agent" }));

/// GET /api/metadata
///
/// Returns metadata about this starter application from deepgram.toml
app.MapGet("/api/metadata", () =>
{
    try
    {
        var tomlPath = Path.Combine(Directory.GetCurrentDirectory(), "deepgram.toml");
        var tomlContent = File.ReadAllText(tomlPath);
        var tomlModel = Toml.ToModel(tomlContent);

        if (!tomlModel.ContainsKey("meta") || tomlModel["meta"] is not TomlTable metaTable)
        {
            return HttpResults.Json(new Dictionary<string, string>
            {
                ["error"] = "INTERNAL_SERVER_ERROR",
                ["message"] = "Missing [meta] section in deepgram.toml",
            }, statusCode: 500);
        }

        var meta = new Dictionary<string, object?>();
        foreach (var kvp in metaTable)
        {
            meta[kvp.Key] = kvp.Value;
        }

        return HttpResults.Json(meta);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading metadata: {ex}");
        return HttpResults.Json(new Dictionary<string, string>
        {
            ["error"] = "INTERNAL_SERVER_ERROR",
            ["message"] = "Failed to read metadata from deepgram.toml",
        }, statusCode: 500);
    }
});

// ============================================================================
// GRACEFUL SHUTDOWN
// ============================================================================

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine($"\nShutting down... Closing {activeConnections.Count} active connection(s)...");
    foreach (var kvp in activeConnections)
    {
        try
        {
            if (kvp.Value.State == WebSocketState.Open)
            {
                kvp.Value.CloseAsync(
                    WebSocketCloseStatus.EndpointUnavailable,
                    "Server shutting down",
                    CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error closing connection {kvp.Key}: {ex.Message}");
        }
    }
    Console.WriteLine("All connections closed.");
});

// ============================================================================
// SERVER START
// ============================================================================

Console.WriteLine();
Console.WriteLine(new string('=', 70));
Console.WriteLine($"🚀 Backend API Server running at http://localhost:{port}");
Console.WriteLine($"📡 CORS enabled for http://localhost:{frontendPort}");
Console.WriteLine($"📡 GET  /api/session");
Console.WriteLine($"📡 WebSocket endpoint: ws://localhost:{port}/api/voice-agent (auth required)");
Console.WriteLine($"📡 GET  /health");
Console.WriteLine($"📡 GET  /api/metadata");
Console.WriteLine($"\n💡 Frontend should be running on http://localhost:{frontendPort}");
Console.WriteLine(new string('=', 70));
Console.WriteLine();

app.Run();
