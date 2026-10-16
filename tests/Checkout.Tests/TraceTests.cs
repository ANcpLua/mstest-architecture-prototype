using System.Diagnostics;
using Architecture.Testing;

namespace Checkout.Tests;

/// <summary>Checks how recorded spans describe calls, stores and diagnostics.</summary>
[TestClass]
public sealed class TraceTests
{
    /// <summary>Only a server span parented by another service's client creates a call.</summary>
    [TestMethod]
    public void CallsRequireACrossServiceClientParent()
    {
        var client = TestSpan.Create("orders", "client", ActivityKind.Client);
        var server = TestSpan.Create("payments", "server", ActivityKind.Server, "client");
        var trace = TestSpan.Trace(client, server, server,
            TestSpan.Create("payments", "root", ActivityKind.Server),
            TestSpan.Create("payments", "orphan", ActivityKind.Server, "missing"),
            TestSpan.Create("orders", "same-service", ActivityKind.Server, "client"),
            TestSpan.Create("payments", "internal", ActivityKind.Internal, "client"),
            TestSpan.Create("payments", "wrong-parent", ActivityKind.Server, "internal"));

        Assert.AreSequenceEqual([new ServiceCall("orders", "payments")], trace.Calls);
        Assert.AreEqual("orders -> payments", trace.Calls[0].ToString());
    }

    /// <summary>Stores are distinct and sorted, and test-process spans are not services.</summary>
    [TestMethod]
    public void ServicesAndStoresAreDistinctAndSorted()
    {
        var trace = TestSpan.Trace(TestSpan.Create("z", "1", store: "second"),
            TestSpan.Create("a", "2", store: "first"), TestSpan.Create("a", "3", store: "first"),
            TestSpan.Create("a", "4", store: "another"), TestSpan.Create(Span.TestProcess, "5"));

        Assert.AreSequenceEqual(["a", "z"], trace.Services);
        Assert.AreSequenceEqual(["another", "first"], trace.StoresOf("a"));
        Assert.IsEmpty(trace.StoresOf("missing"));
        Assert.Contains("a: another, first; z: second", trace.Summary());
    }

    /// <summary>Both current and legacy semantic-convention keys retain useful diagnostics.</summary>
    [TestMethod]
    public void SpanDetailsPreferCurrentStoreAndUrlKeys()
    {
        var span = TestSpan.Create("orders", "1") with
        {
            Attributes = new Dictionary<string, string>
            {
                ["db.namespace"] = "current-store",
                ["db.name"] = "legacy-store",
                ["url.full"] = "https://current/",
                ["http.url"] = "https://legacy/"
            }
        };

        Assert.AreEqual("current-store", span.Store);
        Assert.EndsWith(" -> store current-store", span.ToString());
        var legacyStore = span with { Attributes = new Dictionary<string, string> { ["db.name"] = "legacy-store" } };
        Assert.AreEqual("legacy-store", legacyStore.Store);
        Assert.EndsWith(" -> store legacy-store", legacyStore.ToString());
        var urls = span with { Attributes = new Dictionary<string, string> { ["url.full"] = "https://current/", ["http.url"] = "https://legacy/" } };
        Assert.EndsWith(" -> https://current/", urls.ToString());
        var legacyUrl = span with { Attributes = new Dictionary<string, string> { ["http.url"] = "https://legacy/" } };
        Assert.EndsWith(" -> https://legacy/", legacyUrl.ToString());
        Assert.IsNull(TestSpan.Create("orders", "2").Attribute("missing"));
    }

    /// <summary>Empty traces and services without stores remain distinguishable in the output.</summary>
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
    public const string TraceId = "00112233445566778899aabbccddeeff";

    public static Span Create(string service, string id, ActivityKind kind = ActivityKind.Internal,
        string? parent = null, string? store = null) =>
        new(service, "test-source", "operation", kind, TraceId, id, parent,
            store is null ? [] : new Dictionary<string, string> { ["db.namespace"] = store });

    public static TestTrace Trace(params Span[] spans) => new(TraceId, spans);
}
