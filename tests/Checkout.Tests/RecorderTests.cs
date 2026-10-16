using System.Collections.Concurrent;
using System.Net;
using Architecture.Testing;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Qyl;
using Qyl.Telemetry.SemanticConventions.Names;

namespace Checkout.Tests;

/// <summary>Checks actual activity capture and the receiver's HTTP contract.</summary>
/// <param name="testContext">The runner context that supplies cancellation.</param>
[TestClass]
public sealed class RecorderTests(TestContext testContext)
{
    private static readonly HttpClient Client = new();

    /// <summary>Stopping twice is safe and starting again restores the receiver and listener.</summary>
    [TestMethod]
    public async Task RecorderCanStopAndRestart()
    {
        await SpanRecorder.StopAsync();
        try
        {
            await SpanRecorder.StopAsync();
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => SpanRecorder.ReceiverAddress);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => SpanRecorder.Collector);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => SpanRecorder.Begin("not-listening"));
        }
        finally
        {
            await SpanRecorder.StartAsync();
        }

        var address = SpanRecorder.ReceiverAddress;
        await SpanRecorder.StartAsync();
        Assert.AreEqual(address, SpanRecorder.ReceiverAddress);
    }

    /// <summary>Real activity tags, parent IDs and trace cleanup are preserved.</summary>
    [TestMethod]
    public void ActivitiesBelongToTheirOwnTraceAndAreRemovedAtTheEnd()
    {
        using var root = SpanRecorder.Begin(nameof(ActivitiesBelongToTheirOwnTraceAndAreRemovedAtTheEnd));
        var traceId = root.TraceId.ToHexString();
        try
        {
            using (var child = Spans.Work("recording-test"))
            {
                Assert.IsNotNull(child);
                _ = child.AddTag("http.response.status_code", 42);
                _ = child.AddTag("error.type", (object?)null);
            }

            var spans = SpanRecorder.SpansOf(traceId);
            Assert.HasCount(1, spans);
            Assert.AreEqual(root.SpanId.ToHexString(), spans[0].ParentSpanId);
            Assert.AreEqual("42", spans[0].Attribute("http.response.status_code"));
            Assert.IsNull(spans[0].Attribute("error.type"));
            Assert.IsGreaterThanOrEqualTo(1, SpanRecorder.Sources[QylTelemetryNames.Scopes.QylTelemetryApplication]);
            Assert.IsEmpty(SpanRecorder.SpansOf("unrecorded-trace"));
        }
        finally
        {
            SpanRecorder.End(root);
        }

        Assert.IsEmpty(SpanRecorder.SpansOf(traceId));
    }

    /// <summary>Arbitrary activity tag values keep their text, including a null ToString result.</summary>
    [TestMethod]
    [DynamicData(nameof(ActivityTagValues))]
    public void ActivityTagValuesAreConvertedToText(string key, object? value, string? expected)
    {
        using var root = SpanRecorder.Begin(nameof(ActivityTagValuesAreConvertedToText));
        var traceId = root.TraceId.ToHexString();
        try
        {
            using (var activity = Spans.Work("tag-conversion-test"))
            {
                Assert.IsNotNull(activity);
                _ = activity.AddTag(key, value);
            }

            var recorded = SpanRecorder.SpansOf(traceId);
            Assert.HasCount(1, recorded);
            Assert.AreEqual(expected, recorded[0].Attribute(key));
            Assert.AreEqual(value is not null, recorded[0].Attributes.ContainsKey(key));
        }
        finally
        {
            SpanRecorder.End(root);
        }
    }

    /// <summary>Each row supplies a fresh, inert tag value for the activity recorder.</summary>
    public static IEnumerable<object?[]> ActivityTagValues()
    {
        yield return ["error.type", "failed", "failed"];
        yield return ["http.response.status_code", 503, "503"];
        yield return ["error.type", null, null];
        yield return ["error.type", new NullText(), ""];
    }

    /// <summary>Both a missing feature and an empty address collection fail with the original diagnostic.</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReceiverRequiresABoundAddress(bool hasFeature)
    {
        var features = new FeatureCollection();
        if (hasFeature)
        {
            features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
        }

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => TraceReceiver.BoundAddress(features));
        Assert.AreEqual("The trace receiver has no address.", failure.Message);
    }

    /// <summary>The selected endpoint is the address that the server actually bound.</summary>
    [TestMethod]
    public void ReceiverUsesTheServersBoundAddress()
    {
        var features = new FeatureCollection();
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://127.0.0.1:43210");
        features.Set<IServerAddressesFeature>(addresses);
        Assert.AreEqual(new Uri("http://127.0.0.1:43210"), TraceReceiver.BoundAddress(features));
    }

    /// <summary>Trace exports are decoded and every signal is forwarded to qyl.</summary>
    [TestMethod]
    public async Task ReceiverAcceptsAllExportRoutes()
    {
        var received = new ConcurrentQueue<Span>();
        var forwarded = new ConcurrentQueue<byte[]>();
        await using var receiver = await TraceReceiver.StartAsync(received.Enqueue, (_, payload, _) =>
        {
            forwarded.Enqueue(payload.ToArray());
            return Task.CompletedTask;
        });
        foreach (var route in new[] { "/v1/traces", "/v1/metrics", "/v1/logs" })
        {
            using var empty = new ByteArrayContent([]);
            using var response = await Client.PostAsync(new Uri(receiver.Address, route), empty, testContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
            Assert.IsEmpty(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
        }

        using var content = new ByteArrayContent(OtlpFixture.Export("service", "scope", OtlpFixture.Text(5, "first"), OtlpFixture.Text(5, "second")));
        using var export = await Client.PostAsync(new Uri(receiver.Address, "/v1/traces"), content, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
        Assert.AreSequenceEqual(["first", "second"], received.Select(span => span.Name));
        Assert.HasCount(4, forwarded);
        Assert.AreSequenceEqual(await content.ReadAsByteArrayAsync(testContext.CancellationToken), forwarded.Last());
        using var malformed = new ByteArrayContent([0x0b]);
        using var rejected = await Client.PostAsync(new Uri(receiver.Address, "/v1/traces"), malformed, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.InternalServerError, rejected.StatusCode);
        Assert.HasCount(2, received);
    }

    /// <summary>A malformed Kestrel endpoint remains a startup error instead of a ready receiver.</summary>
    [TestMethod]
    public async Task ReceiverRejectsInvalidListenerConfiguration()
    {
        const string setting = "Kestrel__Endpoints__Broken__Url";
        var previous = Environment.GetEnvironmentVariable(setting);
        try
        {
            Environment.SetEnvironmentVariable(setting, "invalid-listener-address");
            _ = await Assert.ThrowsAsync<FormatException>(() => TraceReceiver.StartAsync(_ => { }, (_, _, _) => Task.CompletedTask));
        }
        finally
        {
            Environment.SetEnvironmentVariable(setting, previous);
        }
    }

    private sealed class NullText
    {
        public override string? ToString() => null;
    }
}
