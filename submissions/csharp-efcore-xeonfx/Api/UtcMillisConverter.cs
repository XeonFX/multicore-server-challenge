using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Api;

/// <summary>RFC 3339 in UTC with exactly three fractional digits, truncated: 2025-04-01T12:00:00.000Z.</summary>
public class UtcMillisConverter : JsonConverter<DateTime>
{
    const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
