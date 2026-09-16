using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Deepgram.Models.Agent.v2.WebSocket;
using Xunit;

namespace CsharpVoiceAgent.Tests;

public class SettingsSerializationTests
{
    [Fact]
    public void WelcomeHasANonEmptyUuidRequestId()
    {
        using var welcome = JsonDocument.Parse(AgentBridgeProtocol.CreateWelcome());

        Assert.Equal("Welcome", welcome.RootElement.GetProperty("type").GetString());
        Assert.True(Guid.TryParse(welcome.RootElement.GetProperty("request_id").GetString(), out var requestId));
        Assert.NotEqual(Guid.Empty, requestId);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"type\":\"Settings\"}")]
    [InlineData("{\"type\":\"UpdateSpeak\"}")]
    public void InvalidInitialSettingsReturnProtocolErrorAndInternalServerErrorClose(string message)
    {
        Assert.False(AgentBridgeProtocol.TryParseInitialSettings(message, out _));

        using var error = JsonDocument.Parse(AgentBridgeProtocol.CreateInvalidSettingsError());
        Assert.Equal("Error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("INVALID_SETTINGS", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(WebSocketCloseStatus.InternalServerError, AgentBridgeProtocol.InvalidSettingsCloseStatus);
    }

    [Fact]
    public async Task TerminalEventsDrainAfterTheReceiveLoopIsCancelled()
    {
        var outbound = Channel.CreateUnbounded<AgentBridgeProtocol.OutboundMessage>();
        var sent = new List<string>();
        using var receiveCts = new CancellationTokenSource();

        receiveCts.Cancel();
        outbound.Writer.TryWrite(new(System.Text.Encoding.UTF8.GetBytes("terminal"), WebSocketMessageType.Text));
        outbound.Writer.TryComplete();

        await AgentBridgeProtocol.DrainOutboundAsync(outbound.Reader, CancellationToken.None, (message, _) =>
        {
            sent.Add(System.Text.Encoding.UTF8.GetString(message.Payload));
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "terminal" }, sent);
    }

    [Fact]
    public void FunctionCallAndErrorFieldsSurviveBridgeSerialization()
    {
        const string functionRequestJson = """
            {"type":"FunctionCallRequest","functions":[{"id":"fc_123","name":"get_weather","arguments":"{\"location\":\"Seattle\"}","client_side":true}]}
            """;
        const string errorJson = """
            {"type":"Error","description":"Client did not respond in time","code":"CLIENT_MESSAGE_TIMEOUT"}
            """;

        var functionRequest = JsonSerializer.Deserialize<FunctionCallRequestResponse>(functionRequestJson);
        var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorJson);

        Assert.NotNull(functionRequest);
        Assert.NotNull(errorResponse);

        using var serializedFunction = JsonDocument.Parse(AgentBridgeProtocol.SerializeEvent(functionRequest));
        using var serializedError = JsonDocument.Parse(AgentBridgeProtocol.SerializeEvent(errorResponse));
        var function = serializedFunction.RootElement.GetProperty("functions")[0];

        Assert.Equal("fc_123", function.GetProperty("id").GetString());
        Assert.Equal("get_weather", function.GetProperty("name").GetString());
        Assert.Equal("{\"location\":\"Seattle\"}", function.GetProperty("arguments").GetString());
        Assert.True(function.GetProperty("client_side").GetBoolean());
        Assert.Equal("Error", serializedError.RootElement.GetProperty("type").GetString());
        Assert.Equal("CLIENT_MESSAGE_TIMEOUT", serializedError.RootElement.GetProperty("code").GetString());
        Assert.Equal("Client did not respond in time", serializedError.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public void SettingsWithQuotedMultilinePromptRemainValidJson()
    {
        const string browserSettings = """
            {
              "type": "Settings",
              "audio": {
                "input": { "encoding": "linear16", "sample_rate": 16000 },
                "output": { "encoding": "linear16", "sample_rate": 16000 }
              },
              "agent": {
                "listen": {
                  "provider": {
                    "type": "deepgram",
                    "version": "v2",
                    "model": "flux-general-en",
                    "language_hint": "en-US"
                  }
                },
                "think": {
                  "provider": { "type": "open_ai", "model": "gpt-4o-mini" },
                  "prompt": "Say \"hello\".\nThen ask a follow-up question."
                }
              }
            }
            """;

        var settings = JsonSerializer.Deserialize<SettingsSchema>(browserSettings);

        Assert.NotNull(settings);
        Assert.True(AgentBridgeProtocol.TryParseInitialSettings(browserSettings, out _));
        using var serialized = JsonDocument.Parse(settings.ToString());
        var provider = serialized.RootElement
            .GetProperty("agent")
            .GetProperty("listen")
            .GetProperty("provider");

        Assert.Equal("v2", provider.GetProperty("version").GetString());
        Assert.Equal("en-US", provider.GetProperty("language_hint").GetString());
        Assert.Equal("Say \"hello\".\nThen ask a follow-up question.", serialized.RootElement
            .GetProperty("agent")
            .GetProperty("think")
            .GetProperty("prompt")
            .GetString());
    }
}
