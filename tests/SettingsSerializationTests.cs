using System.Text.Json;
using Deepgram.Models.Agent.v2.WebSocket;
using Xunit;

namespace CsharpVoiceAgent.Tests;

public class SettingsSerializationTests
{
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
