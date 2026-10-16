using System.Diagnostics;
using System.Globalization;
using Architecture.Testing;

namespace Checkout.Tests;

/// <summary>Checks OTLP wire decoding with valid exports, unknown fields and malformed input.</summary>
[TestClass]
public sealed class OtlpTracesTests
{
    /// <summary>OTLP span kinds map to their ActivityKind counterparts.</summary>
    [TestMethod]
    [DataRow(0, ActivityKind.Internal)]
    [DataRow(1, ActivityKind.Internal)]
    [DataRow(2, ActivityKind.Server)]
    [DataRow(3, ActivityKind.Client)]
    [DataRow(4, ActivityKind.Producer)]
    [DataRow(5, ActivityKind.Consumer)]
    [DataRow(6, ActivityKind.Internal)]
    public void SpanIdentityAndKindsAreDecoded(int kind, ActivityKind expected)
    {
        var message = OtlpFixture.Export("payments", "instrumentation",
            OtlpFixture.Concat(OtlpFixture.Bytes(1, Convert.FromHexString(TestSpan.TraceId)),
                OtlpFixture.Bytes(2, Convert.FromHexString("0102030405060708")),
                OtlpFixture.Bytes(4, Convert.FromHexString("1112131415161718")),
                OtlpFixture.Text(5, "charge €"), OtlpFixture.Number(6, (ulong)kind)));
        var spans = OtlpTraces.Decode(message);
        Assert.HasCount(1, spans);
        var span = spans[0];
        Assert.AreEqual("payments", span.Service);
        Assert.AreEqual("instrumentation", span.Source);
        Assert.AreEqual("charge €", span.Name);
        Assert.AreEqual(TestSpan.TraceId, span.TraceId);
        Assert.AreEqual("0102030405060708", span.SpanId);
        Assert.AreEqual("1112131415161718", span.ParentSpanId);
        Assert.AreEqual(expected, span.Kind);
    }

    /// <summary>Missing optional identities and empty parent IDs have explicit defaults.</summary>
    [TestMethod]
    public void MissingResourceAndScopeNamesUseDefaults()
    {
        var span = OtlpTraces.Decode(OtlpFixture.Export(null, null, OtlpFixture.Bytes(4)))[0];
        Assert.AreEqual("unknown", span.Service);
        Assert.AreEqual("", span.Source);
        Assert.AreEqual("", span.Name);
        Assert.AreEqual("", span.TraceId);
        Assert.AreEqual("", span.SpanId);
        Assert.IsNull(span.ParentSpanId);
        Assert.IsEmpty(OtlpTraces.Decode(ReadOnlyMemory<byte>.Empty));
        // A fixed wire fixture independent of the helper: ResourceSpans { ScopeSpans { Span {} } }.
        Assert.HasCount(1, OtlpTraces.Decode(Convert.FromHexString("0a0412021200")));
    }

    /// <summary>Primitive attribute values use invariant text, including signed integers.</summary>
    [TestMethod]
    public void PrimitiveAttributesAreDecodedWithoutCultureLoss()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-AT");
            var payload = OtlpFixture.Concat(
                OtlpFixture.SpanAttribute("text", OtlpFixture.Text(1, "hello")),
                OtlpFixture.SpanAttribute("true", OtlpFixture.Number(2, 1)),
                OtlpFixture.SpanAttribute("false", OtlpFixture.Number(2, 0)),
                OtlpFixture.SpanAttribute("negative", OtlpFixture.Number(3, unchecked((ulong)-123L))),
                OtlpFixture.SpanAttribute("double", OtlpFixture.Fixed64(4, 1.25)),
                OtlpFixture.SpanAttribute("bytes", OtlpFixture.Bytes(7, [0x00, 0xAB, 0xFF])),
                OtlpFixture.SpanAttribute("empty", []),
                OtlpFixture.SpanAttribute("unknown", OtlpFixture.Number(20, 1)));
            var attributes = OtlpTraces.Decode(OtlpFixture.Export("service", "scope", payload))[0].Attributes;
            Assert.AreEqual("hello", attributes["text"]);
            Assert.AreEqual("true", attributes["true"]);
            Assert.AreEqual("false", attributes["false"]);
            Assert.AreEqual("-123", attributes["negative"]);
            Assert.AreEqual("1.25", attributes["double"]);
            Assert.AreEqual("00abff", attributes["bytes"]);
            Assert.AreEqual("", attributes["empty"]);
            Assert.AreEqual("", attributes["unknown"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>Unknown fields of each supported wire type do not corrupt known fields.</summary>
    [TestMethod]
    public void UnknownFieldsAreSkippedAndLaterDuplicateAttributesWin()
    {
        var unknown = OtlpFixture.Concat(OtlpFixture.Number(40, 128), OtlpFixture.Fixed64(41, 3.5),
            OtlpFixture.Bytes(42, [1, 2, 3]), OtlpFixture.Fixed32(43, 123));
        var attribute = OtlpFixture.Bytes(9, unknown, OtlpFixture.Text(1, "key"), OtlpFixture.Bytes(2, OtlpFixture.Text(1, "first")));
        var request = OtlpFixture.Concat(unknown, OtlpFixture.Export("service", "scope",
            OtlpFixture.Concat(unknown, attribute, OtlpFixture.SpanAttribute("key", OtlpFixture.Text(1, "second")), OtlpFixture.Bytes(9))));
        var span = OtlpTraces.Decode(request)[0];
        Assert.AreEqual("second", span.Attributes["key"]);
        Assert.AreEqual("", span.Attributes[""]);
        Assert.AreEqual("service", span.Service);
    }

    /// <summary>Invalid wire tags, truncated values and overflowing lengths are rejected as input errors.</summary>
    [TestMethod]
    [DataRow("0b")]
    [DataRow("0e")]
    [DataRow("00")]
    [DataRow("808080801000")]
    [DataRow("80")]
    [DataRow("0880")]
    [DataRow("0900")]
    [DataRow("0d00")]
    [DataRow("0a02ff")]
    [DataRow("0affffffff0f")]
    [DataRow("088080808080808080808002")]
    [DataRow("0880808080808080808002")]
    public void MalformedWireDataIsRejected(string hex) =>
        _ = Assert.ThrowsExactly<InvalidDataException>(() => OtlpTraces.Decode(Convert.FromHexString(hex)));
}
