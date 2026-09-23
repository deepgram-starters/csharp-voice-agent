using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Deepgram.Models.Agent.v2.WebSocket;

internal static class AgentBridgeProtocol
{
    internal const string InvalidSettingsCode = "INVALID_SETTINGS";
    internal const string InvalidSettingsDescription = "Invalid Settings message";
    internal const WebSocketCloseStatus InvalidSettingsCloseStatus = WebSocketCloseStatus.InternalServerError;
    internal const Deepgram.Logger.LogLevel SdkLogLevel = Deepgram.Logger.LogLevel.Disable;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    internal readonly record struct OutboundMessage(byte[] Payload, WebSocketMessageType Type);

    internal static string CreateWelcome()
    {
        return JsonSerializer.Serialize(new WelcomeResponse
        {
            RequestId = Guid.NewGuid().ToString(),
        }, JsonOptions);
    }

    internal static string CreateInvalidSettingsError()
    {
        return JsonSerializer.Serialize(new ErrorResponse
        {
            Code = InvalidSettingsCode,
            Description = InvalidSettingsDescription,
        }, JsonOptions);
    }

    internal static string CreateConnectionFailedError()
    {
        return JsonSerializer.Serialize(new ErrorResponse
        {
            Code = "CONNECTION_FAILED",
            Description = "Failed to connect to Deepgram Agent",
        }, JsonOptions);
    }

    internal static string SerializeEvent<T>(T message) => JsonSerializer.Serialize(message, JsonOptions);

    internal static bool TryParseInitialSettings(string json, out SettingsSchema? settings)
    {
        settings = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasStringProperty(root, "type", "Settings") ||
                !HasObjectProperty(root, "audio") ||
                !HasObjectProperty(root, "agent") ||
                !HasObjectProperty(root.GetProperty("audio"), "input") ||
                !HasObjectProperty(root.GetProperty("audio"), "output") ||
                !HasObjectProperty(root.GetProperty("agent"), "listen") ||
                !HasObjectProperty(root.GetProperty("agent"), "speak") ||
                !HasObjectProperty(root.GetProperty("agent"), "think") ||
                !HasRequiredAudioFields(root.GetProperty("audio").GetProperty("input")) ||
                !HasRequiredAudioFields(root.GetProperty("audio").GetProperty("output")) ||
                !HasRequiredProviderFields(root.GetProperty("agent").GetProperty("listen")) ||
                !HasSimpleSpeakProvider(root.GetProperty("agent").GetProperty("speak")) ||
                !HasRequiredProviderFields(root.GetProperty("agent").GetProperty("think")))
            {
                return false;
            }

            settings = JsonSerializer.Deserialize<SettingsSchema>(json);
            return settings != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsFallbackSpeakSettings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasStringProperty(root, "type", "Settings") ||
                !HasObjectProperty(root, "audio") ||
                !HasObjectProperty(root, "agent") ||
                !HasObjectProperty(root.GetProperty("audio"), "input") ||
                !HasObjectProperty(root.GetProperty("audio"), "output") ||
                !HasRequiredAudioFields(root.GetProperty("audio").GetProperty("input")) ||
                !HasRequiredAudioFields(root.GetProperty("audio").GetProperty("output")))
            {
                return false;
            }

            var agent = root.GetProperty("agent");
            return agent.TryGetProperty("speak", out var speak) &&
                speak.ValueKind == JsonValueKind.Array &&
                speak.GetArrayLength() > 0 &&
                agent.TryGetProperty("listen", out var listen) &&
                agent.TryGetProperty("think", out var think) &&
                HasRequiredProviderFields(listen) &&
                HasRequiredProviderFields(think) &&
                speak.EnumerateArray().All(HasFallbackSpeakProvider);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsWelcomeMessage(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            return HasStringProperty(document.RootElement, "type", "Welcome");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static async Task DrainOutboundAsync(
        ChannelReader<OutboundMessage> reader,
        CancellationToken cancellationToken,
        Func<OutboundMessage, CancellationToken, Task> send)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            await send(message, cancellationToken);
        }
    }

    private static bool HasStringProperty(JsonElement element, string name, string expectedValue)
    {
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() == expectedValue;
    }

    private static bool HasObjectProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Object;
    }

    private static bool HasRequiredAudioFields(JsonElement audio)
    {
        return HasNonEmptyStringProperty(audio, "encoding") && HasPositiveIntegerProperty(audio, "sample_rate");
    }

    private static bool HasRequiredProviderFields(JsonElement agent)
    {
        return HasObjectProperty(agent, "provider") &&
            HasNonEmptyStringProperty(agent.GetProperty("provider"), "type") &&
            HasNonEmptyStringProperty(agent.GetProperty("provider"), "model");
    }

    private static bool HasSimpleSpeakProvider(JsonElement speak)
    {
        return !speak.TryGetProperty("speak", out _) && HasRequiredProviderFields(speak);
    }

    private static bool HasFallbackSpeakProvider(JsonElement speak)
    {
        // A fallback chain can cross TTS providers, so only require the shared
        // discriminator and leave provider-specific settings untouched.
        return HasObjectProperty(speak, "provider") &&
            HasNonEmptyStringProperty(speak.GetProperty("provider"), "type");
    }

    private static bool HasNonEmptyStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString());
    }

    private static bool HasPositiveIntegerProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var sampleRate) &&
            sampleRate > 0;
    }
}
