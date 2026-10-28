using System.Text.Json;
using System.Text.Json.Serialization;
using Gozcu.Contracts.Events;

namespace Gozcu.Infrastructure.Messaging;

public static class EventSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Serialize(IntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return JsonSerializer.SerializeToUtf8Bytes(integrationEvent, integrationEvent.GetType(), Options);
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> body)
        where T : IntegrationEvent => JsonSerializer.Deserialize<T>(body, Options);
}
