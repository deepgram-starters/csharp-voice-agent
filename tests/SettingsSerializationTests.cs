using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal(1011, (int)AgentBridgeProtocol.InvalidSettingsCloseStatus);
    }

    public static IEnumerable<object[]> MissingRequiredSettingsFields()
    {
        string[][] paths =
        [
            ["type"],
            ["audio"],
            ["audio", "input"],
            ["audio", "input", "encoding"],
            ["audio", "input", "sample_rate"],
            ["audio", "output"],
            ["audio", "output", "encoding"],
            ["audio", "output", "sample_rate"],
            ["agent"],
            ["agent", "listen"],
            ["agent", "listen", "provider"],
            ["agent", "listen", "provider", "type"],
            ["agent", "listen", "provider", "model"],
            ["agent", "speak"],
            ["agent", "speak", "provider"],
            ["agent", "speak", "provider", "type"],
            ["agent", "speak", "provider", "model"],
            ["agent", "think"],
            ["agent", "think", "provider"],
            ["agent", "think", "provider", "type"],
            ["agent", "think", "provider", "model"],
        ];

        foreach (var path in paths)
        {
            var settings = JsonNode.Parse(ValidSettings)!.AsObject();
            var parent = settings;
            foreach (var segment in path[..^1])
                parent = parent[segment]!.AsObject();

            parent.Remove(path[^1]);
            yield return [string.Join('.', path), settings.ToJsonString()];
        }
    }

    [Theory]
    [MemberData(nameof(MissingRequiredSettingsFields))]
    public void SettingsMissingARequiredContractFieldReturnInvalidSettingsAnd1011(string field, string message)
    {
        Assert.False(AgentBridgeProtocol.TryParseInitialSettings(message, out _), field);

        using var error = JsonDocument.Parse(AgentBridgeProtocol.CreateInvalidSettingsError());
        Assert.Equal("INVALID_SETTINGS", error.RootElement.GetProperty("code").GetString());
        Assert.Equal(1011, (int)AgentBridgeProtocol.InvalidSettingsCloseStatus);
    }

    [Theory]
    [InlineData(ValidSettings)]
    public void SettingsWithSingleSpeakProviderUsesTheSdkBridge(string message)
    {
        Assert.True(AgentBridgeProtocol.TryParseInitialSettings(message, out var settings));
        Assert.NotNull(settings);
    }

    [Fact]
    public void SettingsWithFallbackSpeakArrayUsesTheRawBridge()
    {
        Assert.False(AgentBridgeProtocol.TryParseInitialSettings(ValidSettingsWithSpeakArray, out _));
        Assert.True(AgentBridgeProtocol.IsFallbackSpeakSettings(ValidSettingsWithSpeakArray));
    }

    [Fact]
    public void FallbackSpeakArraysAllowProviderSpecificFields()
    {
        const string crossProviderFallback = """
            {
              "type":"Settings",
              "audio":{"input":{"encoding":"linear16","sample_rate":16000},"output":{"encoding":"linear16","sample_rate":16000}},
              "agent":{
                "listen":{"provider":{"type":"deepgram","model":"nova-3"}},
                "speak":[
                  {"provider":{"type":"deepgram","model":"aura-2-thalia-en"}},
                  {"provider":{"type":"cartesia","model_id":"sonic-2","voice":"abc123"}},
                  {"provider":{"type":"elevenlabs","voice_id":"21m00Tcm4TlvDq8ikWAM"}}
                ],
                "think":{"provider":{"type":"open_ai","model":"gpt-4o-mini"}}
              }
            }
            """;

        Assert.True(AgentBridgeProtocol.IsFallbackSpeakSettings(crossProviderFallback));
    }

    [Fact]
    public void NestedSpeakProviderArraysAreRejected()
    {
        Assert.False(AgentBridgeProtocol.TryParseInitialSettings(ValidSettingsWithProviderAndSpeakArray, out _));
        Assert.False(AgentBridgeProtocol.IsFallbackSpeakSettings(ValidSettingsWithProviderAndSpeakArray));
    }

    public static IEnumerable<object[]> MalformedSpeakProviderArrays()
    {
        string[][] arrays =
        [
            ["empty", "[]"],
            ["not an array", "{}"],
            ["non-object entry", "[\"deepgram\"]"],
            ["entry without provider", "[{}]"],
            ["entry without provider type", "[{\"provider\":{\"model\":\"aura-2-thalia-en\"}}]"],
            ["mixed valid and invalid entries", "[{\"provider\":{\"type\":\"deepgram\",\"model\":\"aura-2-thalia-en\"}},{}]"],
        ];

        foreach (var array in arrays)
        {
            var settings = JsonNode.Parse(ValidSettings)!.AsObject();
            settings["agent"]!.AsObject()["speak"] = JsonNode.Parse(array[1]);
            yield return [array[0], settings.ToJsonString()];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedSpeakProviderArrays))]
    public void SettingsWithMalformedSpeakProviderArrayReturnInvalidSettings(string form, string message)
    {
        Assert.False(AgentBridgeProtocol.TryParseInitialSettings(message, out _), form);
        Assert.False(AgentBridgeProtocol.IsFallbackSpeakSettings(message), form);
    }

    public static IEnumerable<object[]> MalformedDualFormSpeakProviderArrays()
    {
        string[][] arrays =
        [
            ["empty", "[]"],
            ["not an array", "{}"],
            ["non-object entry", "[\"deepgram\"]"],
            ["entry without provider", "[{}]"],
            ["entry without provider type", "[{\"provider\":{\"model\":\"aura-2-thalia-en\"}}]"],
            ["entry without provider model", "[{\"provider\":{\"type\":\"deepgram\"}}]"],
            ["entry with blank provider model", "[{\"provider\":{\"type\":\"deepgram\",\"model\":\" \"}}]"],
            ["mixed valid and invalid entries", "[{\"provider\":{\"type\":\"deepgram\",\"model\":\"aura-2-thalia-en\"}},{}]"],
        ];

        foreach (var array in arrays)
        {
            var settings = JsonNode.Parse(ValidSettings)!.AsObject();
            settings["agent"]!.AsObject()["speak"]!.AsObject()["speak"] = JsonNode.Parse(array[1]);
            yield return [array[0], settings.ToJsonString()];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedDualFormSpeakProviderArrays))]
    public void SettingsWithProviderAndMalformedSpeakProviderArrayReturnInvalidSettings(string form, string message)
    {
        Assert.False(AgentBridgeProtocol.TryParseInitialSettings(message, out _), form);
        Assert.False(AgentBridgeProtocol.IsFallbackSpeakSettings(message), form);
    }

    [Fact]
    public void SdkLoggingIsDisabled()
    {
        Assert.Equal(Deepgram.Logger.LogLevel.Disable, AgentBridgeProtocol.SdkLogLevel);
    }

    [Fact]
    public void FallbackBridgeSuppressesOnlyTheUpstreamWelcome()
    {
        Assert.True(AgentBridgeProtocol.IsWelcomeMessage("{\"type\":\"Welcome\"}"u8));
        Assert.False(AgentBridgeProtocol.IsWelcomeMessage("{\"type\":\"SettingsApplied\"}"u8));
        Assert.False(AgentBridgeProtocol.IsWelcomeMessage("not-json"u8));
    }

    [Fact]
    public void ConnectionFailureUsesTheBrowserErrorContract()
    {
        using var error = JsonDocument.Parse(AgentBridgeProtocol.CreateConnectionFailedError());
        Assert.Equal("Error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal("CONNECTION_FAILED", error.RootElement.GetProperty("code").GetString());
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
                "speak": {
                  "provider": { "type": "deepgram", "model": "aura-2-thalia-en" }
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

    private const string ValidSettings = """
        {
          "type": "Settings",
          "audio": {
            "input": { "encoding": "linear16", "sample_rate": 16000 },
            "output": { "encoding": "linear16", "sample_rate": 16000 }
          },
          "agent": {
            "listen": { "provider": { "type": "deepgram", "model": "nova-3" } },
            "speak": { "provider": { "type": "deepgram", "model": "aura-2-thalia-en" } },
            "think": { "provider": { "type": "open_ai", "model": "gpt-4o-mini" } }
          }
        }
        """;

    private const string ValidSettingsWithSpeakArray = """
        {
          "type": "Settings",
          "audio": {
            "input": { "encoding": "linear16", "sample_rate": 16000 },
            "output": { "encoding": "linear16", "sample_rate": 16000 }
          },
          "agent": {
            "listen": { "provider": { "type": "deepgram", "model": "nova-3" } },
            "speak": [
              { "provider": { "type": "deepgram", "model": "aura-2-thalia-en" } }
            ],
            "think": { "provider": { "type": "open_ai", "model": "gpt-4o-mini" } }
          }
        }
        """;

    private const string ValidSettingsWithProviderAndSpeakArray = """
        {
          "type": "Settings",
          "audio": {
            "input": { "encoding": "linear16", "sample_rate": 16000 },
            "output": { "encoding": "linear16", "sample_rate": 16000 }
          },
          "agent": {
            "listen": { "provider": { "type": "deepgram", "model": "nova-3" } },
            "speak": {
              "provider": { "type": "deepgram", "model": "aura-2-thalia-en" },
              "speak": [
                { "provider": { "type": "deepgram", "model": "aura-2-thalia-en" } }
              ]
            },
            "think": { "provider": { "type": "open_ai", "model": "gpt-4o-mini" } }
          }
        }
        """;
}
