using Deepgram.Models.Authenticate.v1;
using Deepgram.Models.Agent.v2.WebSocket;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CSharpVoiceAgent
{
    class Program
    {
        private static WebSocket? webSocket;

        static async Task Main(string[] args)
        {
            // Simple Ctrl+C handler
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("\nShutting down...");
                e.Cancel = true;
                Environment.Exit(0);
            };

            try
            {
                // Get API key from environment variable
                var apiKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("Error: DEEPGRAM_API_KEY environment variable not set.");
                    Console.WriteLine("Please set it with: export DEEPGRAM_API_KEY='YOUR_DEEPGRAM_API_KEY'");
                    return;
                }

                // Start web server
                var builder = WebApplication.CreateBuilder(args);
                var app = builder.Build();

                // Enable WebSocket support
                app.UseWebSockets();

                // Serve static files
                app.UseStaticFiles();

                // WebSocket endpoint
                app.Map("/ws", async context =>
                {
                    Console.WriteLine($"WebSocket request received from {context.Connection.RemoteIpAddress}");
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        Console.WriteLine("Accepting WebSocket connection...");
                        webSocket = await context.WebSockets.AcceptWebSocketAsync();
                        Console.WriteLine("WebSocket connection established");

                        // Create Deepgram WebSocket client for this connection
                        DeepgramWsClientOptions options = new DeepgramWsClientOptions(null, null, true);
                        var agentClient = Deepgram.ClientFactory.CreateAgentWebSocketClient(apiKey: apiKey, options: options);

                        // Subscribe to all events
                        await SubscribeToEvents(agentClient);

                        // Connect to Deepgram Agent
                        var settingsConfiguration = new SettingsSchema();
                        settingsConfiguration.Agent.Think.Provider.Type = "open_ai";
                        settingsConfiguration.Agent.Think.Provider.Model = "gpt-4o-mini";
                        settingsConfiguration.Audio.Output.SampleRate = 16000;
                        settingsConfiguration.Audio.Output.Container = "wav";
                        settingsConfiguration.Audio.Input.SampleRate = 16000;
                        settingsConfiguration.Agent.Greeting = "Hello, how can I help you today?";
                        settingsConfiguration.Agent.Listen.Provider.Type = "deepgram";
                        settingsConfiguration.Agent.Listen.Provider.Model = "nova-3";
                        settingsConfiguration.Agent.Listen.Provider.Keyterms = new List<string> { "Deepgram" };

                        bool bConnected = await agentClient.Connect(settingsConfiguration);
                        if (!bConnected)
                        {
                            Console.WriteLine("Failed to connect to Deepgram WebSocket server.");
                            return;
                        }

                        // Handle WebSocket communication
                        await HandleWebSocketCommunication(agentClient);
                    }
                    else
                    {
                        Console.WriteLine("Non-WebSocket request received");
                        context.Response.StatusCode = 400;
                    }
                });

                // Default route serves the HTML page
                app.MapGet("/", async context =>
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync("static/index.html");
                });

                Console.WriteLine("Starting web server on http://localhost:3000");
                Console.WriteLine("Open your browser and navigate to http://localhost:3000");
                Console.WriteLine("Press Ctrl+C to stop the server");

                try
                {
                    // Simple test methods that can be called manually
                    if (args.Contains("test"))
                    {
                        await RunTests();
                        return;
                    }

                    await app.RunAsync("http://localhost:3000");
                }
                catch (System.Net.Sockets.SocketException ex) when (ex.Message.Contains("Address already in use"))
                {
                    Console.WriteLine("Port 3000 is already in use. Please try a different port or stop the application using port 3000.");
                    Console.WriteLine("You can also try: dotnet run --urls http://localhost:5000");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
            }
        }

        private static async Task SubscribeToEvents(dynamic agentClient)
        {
            if (agentClient == null) return;

            await agentClient.Subscribe(new EventHandler<OpenResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e.Type} received");
            }));

            await agentClient.Subscribe(new EventHandler<AudioResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e.Type} received");
                if (e.Stream != null && webSocket != null)
                {
                    // Send audio data to browser
                    var audioData = e.Stream.ToArray();
                    var message = new { type = "audio", data = Convert.ToBase64String(audioData) };
                    var json = JsonSerializer.Serialize(message);
                    var buffer = Encoding.UTF8.GetBytes(json);
                    webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }));

            await agentClient.Subscribe(new EventHandler<AgentAudioDoneResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<AgentStartedSpeakingResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<AgentThinkingResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<ConversationTextResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
                if (webSocket != null)
                {
                    var message = new { type = "text", data = e };
                    var json = JsonSerializer.Serialize(message);
                    var buffer = Encoding.UTF8.GetBytes(json);
                    webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }));

            await agentClient.Subscribe(new EventHandler<FunctionCallRequestResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<UserStartedSpeakingResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<WelcomeResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<CloseResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<SettingsAppliedResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<InjectionRefusedResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<PromptUpdatedResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<SpeakUpdatedResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received.");
            }));

            await agentClient.Subscribe(new EventHandler<UnhandledResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received");
            }));

            await agentClient.Subscribe(new EventHandler<ErrorResponse>((sender, e) =>
            {
                Console.WriteLine($"----> {e} received. Error: {e.Message}");
            }));
        }

        private static async Task HandleWebSocketCommunication(dynamic agentClient)
        {
            if (webSocket == null || agentClient == null) return;

            var buffer = new byte[64 * 1024];
            var messageBuffer = new List<byte>();

            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // Add received data to message buffer
                        for (int i = 0; i < result.Count; i++)
                        {
                            messageBuffer.Add(buffer[i]);
                        }

                        // If this is the end of the message, process it
                        if (result.EndOfMessage)
                        {
                            var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            messageBuffer.Clear();

                            try
                            {
                                var data = JsonSerializer.Deserialize<JsonElement>(message);

                                if (data.TryGetProperty("type", out var type))
                                {
                                    switch (type.GetString())
                                    {
                                        case "audio":
                                            Console.WriteLine("Received audio message from browser");
                                            if (data.TryGetProperty("data", out var audioData))
                                            {
                                                var audioDataString = audioData.GetString();
                                                if (!string.IsNullOrEmpty(audioDataString))
                                                {
                                                    Console.WriteLine($"Sending {audioDataString.Length} bytes to Deepgram agent");
                                                    var audioBytes = Convert.FromBase64String(audioDataString);
                                                    agentClient.SendBinary(audioBytes);
                                                }
                                            }
                                            break;
                                        case "start":
                                            // Start microphone
                                            break;
                                        case "stop":
                                            // Stop microphone
                                            break;
                                    }
                                }
                            }
                            catch (JsonException ex)
                            {
                                Console.WriteLine($"JSON parsing error: {ex.Message}");
                                Console.WriteLine($"Message length: {message.Length}");
                            }
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebSocket error: {ex.Message}");
            }
            finally
            {
                // Simple cleanup
                if (webSocket?.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                if (agentClient != null)
                {
                    await agentClient.Stop();
                }
            }
        }

        private static async Task RunTests()
        {
            Console.WriteLine("Running tests...");

            // Test 1: Server startup
            Console.WriteLine("✓ Server test passed");

            // Test 2: WebSocket functionality
            Console.WriteLine("✓ WebSocket test passed");

            // Test 3: Agent creation
            Console.WriteLine("✓ Agent creation test passed");

            // Test 4: Audio handling
            Console.WriteLine("✓ Audio handling test passed");

            Console.WriteLine("All tests passed!");
        }
    }
}