#nullable enable
using System.Text;
using System.Text.Json;

namespace Apeworks.GodotCSharpProfiler.Protocol;

/// <summary>Bounded scalar JSON envelope for debugger transports that cannot reliably carry nested Variants.</summary>
public static class WireJsonEnvelope
{
    public static string Encode(WireValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value, 0);
        if (stream.Length > ProtocolLimits.MaxPayloadBytes)
            throw new ArgumentException("Profiler protocol payload exceeds the transport limit.", nameof(value));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryDecode(string? json, out WireMap? map)
    {
        map = null;
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > ProtocolLimits.MaxPayloadBytes)
            return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = ProtocolLimits.MaxDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (!TryRead(document.RootElement, 0, out var value) || value is not WireMap decoded)
                return false;
            map = decoded;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void Write(Utf8JsonWriter writer, WireValue value, int depth)
    {
        if (depth > ProtocolLimits.MaxDepth)
            throw new ArgumentException("Profiler protocol payload is too deep.", nameof(value));
        switch (value)
        {
            case WireString text: writer.WriteStringValue(text.Value); break;
            case WireInteger integer: writer.WriteNumberValue(integer.Value); break;
            case WireBoolean boolean: writer.WriteBooleanValue(boolean.Value); break;
            case WireArray array:
                writer.WriteStartArray();
                foreach (var item in array.Items) Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case WireMap objectMap:
                writer.WriteStartObject();
                foreach (var field in objectMap.Fields)
                {
                    writer.WritePropertyName(field.Key);
                    Write(writer, field.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            default: throw new ArgumentException("Unsupported profiler wire value.", nameof(value));
        }
    }

    private static bool TryRead(JsonElement element, int depth, out WireValue? value)
    {
        value = null;
        if (depth > ProtocolLimits.MaxDepth) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = new WireString(element.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number when element.TryGetInt64(out var integer):
                value = new WireInteger(integer);
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = new WireBoolean(element.GetBoolean());
                return true;
            case JsonValueKind.Array:
            {
                var items = new List<WireValue>();
                foreach (var child in element.EnumerateArray())
                {
                    if (items.Count >= ProtocolLimits.MaxMethodsPerBatch * 3 + 64 ||
                        !TryRead(child, depth + 1, out var item) || item is null) return false;
                    items.Add(item);
                }
                value = new WireArray(items);
                return true;
            }
            case JsonValueKind.Object:
            {
                var fields = new List<KeyValuePair<string, WireValue>>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (fields.Count >= 64 || string.IsNullOrEmpty(property.Name) ||
                        !names.Add(property.Name) || !TryRead(property.Value, depth + 1, out var child) || child is null)
                        return false;
                    fields.Add(new KeyValuePair<string, WireValue>(property.Name, child));
                }
                value = new WireMap(fields);
                return true;
            }
            default: return false;
        }
    }
}
