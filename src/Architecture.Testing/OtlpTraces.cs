using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Architecture.Testing;

/// <summary>
/// Reads the spans out of an OTLP/HTTP trace export (an ExportTraceServiceRequest in protobuf),
/// with a small reader for the wire format instead of a protobuf library.
/// </summary>
internal static class OtlpTraces
{
    private enum Wire
    {
        Varint = 0,
        Fixed64 = 1,
        Bytes = 2,
        Fixed32 = 5,
    }

    private readonly record struct Field(int Number, ulong Value, ReadOnlyMemory<byte> Bytes);

    public static List<Span> Decode(ReadOnlyMemory<byte> request)
    {
        var spans = new List<Span>();
        foreach (var resourceSpans in Fields(request).Where(field => field.Number == 1))
        {
            DecodeResourceSpans(resourceSpans.Bytes, spans);
        }

        return spans;
    }

    private static void DecodeResourceSpans(ReadOnlyMemory<byte> message, List<Span> spans)
    {
        var fields = Fields(message);
        var service = fields
            .Where(field => field.Number == 1)
            .SelectMany(resource => Fields(resource.Bytes).Where(field => field.Number == 1))
            .Select(attribute => DecodeKeyValue(attribute.Bytes))
            .FirstOrDefault(attribute => attribute.Key == "service.name").Value ?? "unknown";

        foreach (var scopeSpans in fields.Where(field => field.Number == 2))
        {
            var scopeFields = Fields(scopeSpans.Bytes);
            var scope = scopeFields
                .Where(field => field.Number == 1)
                .SelectMany(instrumentationScope => Fields(instrumentationScope.Bytes).Where(field => field.Number == 1))
                .Select(name => Text(name.Bytes))
                .FirstOrDefault() ?? "";

            spans.AddRange(scopeFields.Where(field => field.Number == 2).Select(span => DecodeSpan(span.Bytes, service, scope)));
        }
    }

    private static Span DecodeSpan(ReadOnlyMemory<byte> message, string service, string scope)
    {
        var traceId = "";
        var spanId = "";
        string? parentSpanId = null;
        var name = "";
        var kind = ActivityKind.Internal;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in Fields(message))
        {
            switch (field.Number)
            {
                case 1:
                    traceId = Convert.ToHexStringLower(field.Bytes.Span);
                    break;
                case 2:
                    spanId = Convert.ToHexStringLower(field.Bytes.Span);
                    break;
                case 4:
                    parentSpanId = field.Bytes.Length == 0 ? null : Convert.ToHexStringLower(field.Bytes.Span);
                    break;
                case 5:
                    name = Text(field.Bytes);
                    break;
                case 6:
                    kind = field.Value switch
                    {
                        2 => ActivityKind.Server,
                        3 => ActivityKind.Client,
                        4 => ActivityKind.Producer,
                        5 => ActivityKind.Consumer,
                        _ => ActivityKind.Internal,
                    };
                    break;
                case 9:
                    var (key, value) = DecodeKeyValue(field.Bytes);
                    attributes[key] = value;
                    break;
            }
        }

        return new Span(service, scope, name, kind, traceId, spanId, parentSpanId, attributes);
    }

    private static (string Key, string Value) DecodeKeyValue(ReadOnlyMemory<byte> message)
    {
        var key = "";
        var value = "";
        foreach (var field in Fields(message))
        {
            switch (field.Number)
            {
                case 1:
                    key = Text(field.Bytes);
                    break;
                case 2:
                    value = DecodeAnyValue(field.Bytes);
                    break;
            }
        }

        return (key, value);
    }

    private static string DecodeAnyValue(ReadOnlyMemory<byte> message)
    {
        foreach (var field in Fields(message))
        {
            switch (field.Number)
            {
                case 1:
                    return Text(field.Bytes);
                case 2:
                    return field.Value != 0 ? "true" : "false";
                case 3:
                    return ((long)field.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case 4:
                    return BitConverter.Int64BitsToDouble((long)field.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case 7:
                    return Convert.ToHexStringLower(field.Bytes.Span);
            }
        }

        return "";
    }

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static List<Field> Fields(ReadOnlyMemory<byte> message)
    {
        var fields = new List<Field>();
        var position = 0;
        while (position < message.Length)
        {
            var tag = ReadVarint(message.Span, ref position);
            var number = (int)(tag >> 3);
            switch ((Wire)(tag & 7))
            {
                case Wire.Varint:
                    fields.Add(new Field(number, ReadVarint(message.Span, ref position), default));
                    break;
                case Wire.Fixed64:
                    fields.Add(new Field(number, BinaryPrimitives.ReadUInt64LittleEndian(message.Span[position..]), default));
                    position += 8;
                    break;
                case Wire.Bytes:
                    var length = (int)ReadVarint(message.Span, ref position);
                    fields.Add(new Field(number, 0, message.Slice(position, length)));
                    position += length;
                    break;
                case Wire.Fixed32:
                    fields.Add(new Field(number, BinaryPrimitives.ReadUInt32LittleEndian(message.Span[position..]), default));
                    position += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {tag & 7} at byte {position}.");
            }
        }

        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int position)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}
