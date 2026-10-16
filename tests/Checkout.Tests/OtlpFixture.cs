using System.Buffers.Binary;
using System.Text;

namespace Checkout.Tests;

internal static class OtlpFixture
{
    public static byte[] Export(string? service, string? scope, params byte[][] spans) => Bytes(1,
        service is null ? [] : Bytes(1, Attribute("service.name", Text(1, service))),
        Bytes(2, scope is null ? [] : Bytes(1, Text(1, scope)), Concat([.. spans.Select(span => Bytes(2, span))])));

    private static byte[] Attribute(string key, byte[] value) => Bytes(1, Text(1, key), Bytes(2, value));

    public static byte[] SpanAttribute(string key, byte[] value) => Bytes(9, Text(1, key), Bytes(2, value));

    public static byte[] Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public static byte[] Bytes(int field, params byte[][] parts)
    {
        var bytes = Concat(parts);
        return Concat(Varint((ulong)((field << 3) | 2)), Varint((ulong)bytes.Length), bytes);
    }

    public static byte[] Number(int field, ulong value) => Concat(Varint((ulong)(field << 3)), Varint(value));

    public static byte[] Fixed64(int field, double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return Concat(Varint((ulong)((field << 3) | 1)), bytes);
    }

    public static byte[] Fixed32(int field, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Concat(Varint((ulong)((field << 3) | 5)), bytes);
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value > 127)
        {
            bytes.Add((byte)((value & 127) | 128));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }
}
