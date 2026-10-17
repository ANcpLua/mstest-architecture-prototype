using Architecture.Testing;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.Testing;

namespace Checkout.Tests;

/// <summary>Checks architecture relationships against canonical records without changing captured evidence.</summary>
[TestClass]
public sealed class TraceTests
{
    /// <summary>A selected trace retains access to the complete original capture.</summary>
    [TestMethod]
    public void SelectingATracePreservesOtherCapturedTraces()
    {
        var selected = TestSpan.Create("orders", "selected");
        var unrelated = new SpanRecord("ffeeddccbbaa99887766554433221100", "unrelated", "other operation");
        var capture = new TelemetrySnapshot([selected, unrelated], [], [], []);
        var trace = new TestTrace(selected.TraceId, capture);

        Assert.AreSame(capture, trace.Telemetry.Capture);
        Assert.HasCount(2, trace.Telemetry.Capture.Spans);
        Assert.AreSame(selected, Assert.ContainsSingle(trace.Spans));
    }

    /// <summary>A repeated export is one call, and only a cross-service client/server parent chain is a call.</summary>
    [TestMethod]
    public void CallsRequireACrossServiceClientParent()
    {
        var client = TestSpan.Create("orders", "client", SpanKind.Client);
        var server = TestSpan.Create("payments", "server", SpanKind.Server, "client");
        var trace = TestSpan.Trace(client, server, server,
            TestSpan.Create("payments", "root", SpanKind.Server),
            TestSpan.Create("payments", "orphan", SpanKind.Server, "missing"),
            TestSpan.Create("orders", "same-service", SpanKind.Server, "client"),
            TestSpan.Create("payments", "internal", SpanKind.Internal, "client"),
            TestSpan.Create("payments", "wrong-parent", SpanKind.Server, "internal"));
        var call = Assert.ContainsSingle(trace.Calls);
        Assert.AreEqual("orders", call.From);
        Assert.AreEqual("payments", call.To);
        Assert.HasCount(8, trace.Spans);
        Assert.Contains("orders -> payments", trace.Summary());
    }

    /// <summary>Service names are ordered while complete database identities remain distinct.</summary>
    [TestMethod]
    public void ServicesAndStoresKeepTheirIdentities()
    {
        var trace = TestSpan.Trace(TestSpan.Create("z", "1", store: "second"),
            TestSpan.Create("a", "2", store: "first"), TestSpan.Create("a", "3", store: "first"),
            TestSpan.Create("a", "4", store: "another"), TestSpan.Create(SpanRecorder.TestProcess, "5"));
        Assert.AreSequenceEqual(["a", "z"], trace.Services);
        Assert.AreSequenceEqual(["another", "first"], trace.StoresOf("a").Select(store => store.Namespace).Order(StringComparer.Ordinal));
        Assert.IsEmpty(trace.StoresOf("missing"));
        Assert.Contains("store another", trace.Summary());
    }

    /// <summary>Databases sharing a namespace on different servers must not become the same store.</summary>
    [TestMethod]
    public void StoreIdentityIncludesTheServer()
    {
        var trace = TestSpan.Trace(TestSpan.Create("orders", "1", store: "orders", server: "db-a"),
            TestSpan.Create("orders", "2", store: "orders", server: "db-b"));
        Assert.HasCount(2, trace.StoresOf("orders"));
        Assert.AreSequenceEqual(["db-a", "db-b"], trace.StoresOf("orders").Select(store => store.ServerAddress).Order(StringComparer.Ordinal));
    }

    /// <summary>Empty evidence and measured services with no observed store stay explicit.</summary>
    [TestMethod]
    public void SummaryReportsMissingCallsAndStores()
    {
        var empty = TestSpan.Trace().Summary();
        Assert.Contains("0 span(s)", empty);
        Assert.Contains("Calls: none", empty);
        Assert.Contains("Stores: none", empty);
        Assert.Contains("Stores: orders: none", TestSpan.Trace(TestSpan.Create("orders", "1")).Summary());
    }
}

internal static class TestSpan
{
    private const string TraceId = "00112233445566778899aabbccddeeff";

    public static SpanRecord Create(string service, string id, SpanKind kind = SpanKind.Internal,
        string? parent = null, string? store = null, string? server = null)
    {
        var attributes = new Dictionary<string, object?>();
        if (store is not null)
        {
            attributes["db.system.name"] = "memory";
            attributes["db.namespace"] = store;
        }
        if (server is not null)
        {
            attributes["server.address"] = server;
        }
        return new SpanRecord(TraceId, id, "operation")
        {
            Kind = kind,
            ParentSpanId = parent,
            Attributes = attributes,
            Resource = new ResourceRecord(new Dictionary<string, object?> { ["service.name"] = service })
        };
    }

    public static TestTrace Trace(params SpanRecord[] spans) => new(TraceId, new TelemetrySnapshot(spans, [], [], []));
}
