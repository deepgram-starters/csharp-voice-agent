using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Deepgram.Models.Agent.v2.WebSocket;

internal static class AgentBridgeProtocol
{
    internal const string InvalidSettingsCode = "INVALID_SETTINGS";
    internal const string InvalidSettingsDescription = "Invalid Settings message";
    internal const WebSocketCloseStatus InvalidSettingsCloseStatus = WebSocketCloseStatus.InternalServerError;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
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
                !HasObjectProperty(root.GetProperty("agent"), "think"))
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
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object;
    }
}
