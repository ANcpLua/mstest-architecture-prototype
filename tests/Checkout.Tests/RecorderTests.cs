using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Architecture.Testing;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Qyl;
using Qyl.Api.Contracts.Common;
using Qyl.Telemetry.Testing;
using RpcStatus = Google.Rpc.Status;

namespace Checkout.Tests;

/// <summary>Checks real SDK capture, persisted records and the forwarding HTTP boundary.</summary>
/// <param name="testContext">The runner owns cancellation and evidence attachments.</param>
[TestClass]
public sealed class RecorderTests(TestContext testContext)
{
    private SpanRecorder Recorder => ArchitectureFixtures.GetRun(testContext).Recorder;

    private static readonly HttpClient Client = new();
    private static readonly string[] Checkpoints = ["first", "second"];

    /// <summary>The completed root and its actual HTTP child reach the published Collector.</summary>
    [TestMethod]
    public async Task QylStoresCompletedTestRootAndClient()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var root = Recorder.Begin(nameof(QylStoresCompletedTestRootAndClient));
        var traceId = root.TraceId.ToHexString();
        var rootId = root.SpanId.ToHexString();
        try
        {
            using var response = await Client.GetAsync(new Uri(Recorder.Collector.Address, "health"), testContext.CancellationToken);
            _ = response.EnsureSuccessStatusCode();
        }
        finally
        {
            Recorder.End(root);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        JsonElement trace;
        while (true)
        {
            try
            {
                trace = await Client.GetFromJsonAsync<JsonElement>(new Uri(Recorder.Collector.Address, $"api/v1/traces/{traceId}"), deadline.Token);
                if (trace.GetProperty("spans").EnumerateArray().Any(span => span.GetProperty("span_id").GetString() == rootId))
                {
                    break;
                }
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
            await Task.Delay(100, deadline.Token);
        }
        var spans = trace.GetProperty("spans").EnumerateArray().ToArray();
        var ids = spans.Select(span => span.GetProperty("span_id").GetString()).ToHashSet();
        Assert.Contains(span => span.GetProperty("span_id").GetString() == rootId
            && span.GetProperty("name").GetString() == "architecture.test", spans);
        Assert.Contains(span => span.GetProperty("kind").GetInt32() == 3
            && span.GetProperty("parent_span_id").GetString() == rootId, spans);
        Assert.IsTrue(spans.All(span => !span.TryGetProperty("parent_span_id", out var parent)
            || parent.GetString() is null or "" || ids.Contains(parent.GetString())));
        testContext.WriteLine(trace.GetRawText());
    }

    /// <summary>Capture keeps exception details; the Collector applies its declared persistence policy.</summary>
    [TestMethod]
    public async Task QylStoresExceptionSpanEvents()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var root = Recorder.Begin(nameof(QylStoresExceptionSpanEvents));
        var traceId = root.TraceId.ToHexString();
        try
        {
            try
            {
                throw new InvalidOperationException("payment declined");
            }
            catch (InvalidOperationException exception)
            {
                _ = root.RecordFailure(exception);
            }
        }
        finally
        {
            Recorder.End(root);
        }
        var original = Assert.ContainsSingle(Assert.ContainsSingle(Recorder.SpansOf(traceId)).Events);
        Assert.AreEqual("exception", original.Name);
        Assert.AreEqual("payment declined", original.Attributes["exception.message"]);
        Assert.IsNotNull(original.Attributes["exception.stacktrace"]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                var events = await Recorder.Collector.Telemetry.SpanEventsAsync(traceId, deadline.Token);
                if (events.FirstOrDefault(spanEvent => spanEvent.Name == "exception") is { } exception)
                {
                    var attributes = exception.Attributes!.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
                    Assert.AreEqual("System.InvalidOperationException", Assert.IsInstanceOfType<AttributeValue.StringValue>(attributes["exception.type"]).Value);
                    Assert.AreEqual("payment declined", Assert.IsInstanceOfType<AttributeValue.StringValue>(attributes["exception.message"]).Value);
                    Assert.AreEqual(original.Attributes["exception.stacktrace"], Assert.IsInstanceOfType<AttributeValue.StringValue>(attributes["exception.stacktrace"]).Value);
                    Assert.IsGreaterThan(0UL, exception.TimeUnixNano);
                    return;
                }
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
            await Task.Delay(100, deadline.Token);
        }
    }

    /// <summary>Stopping twice is safe and restarting creates another owned receiver and SDK.</summary>
    [TestMethod]
    public async Task RecorderCanStopAndRestart()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        await ArchitectureFixtures.GetRun(testContext).StopAsync();
        try
        {
            await ArchitectureFixtures.GetRun(testContext).StopAsync();
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => Recorder.ReceiverAddress);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => Recorder.Collector);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => Recorder.Begin("not-listening"));
        }
        finally
        {
            await Recorder.StartAsync(testContext.CancellationToken);
        }
        var address = Recorder.ReceiverAddress;
        await Recorder.StartAsync(testContext.CancellationToken);
        Assert.AreEqual(address, Recorder.ReceiverAddress);
        Assert.IsNotEmpty(Recorder.EvidenceFiles);
    }

    /// <summary>The SDK and canonical decoder preserve typed values, event order, metadata and ended roots.</summary>
    [TestMethod]
    public async Task SdkCapturePreservesTypedValuesAndOrderedEvents()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var root = Recorder.Begin(nameof(SdkCapturePreservesTypedValuesAndOrderedEvents));
        var traceId = root.TraceId.ToHexString();
        try
        {
            using (var child = Spans.Work("recording-test"))
            {
                Assert.IsNotNull(child);
                _ = child.SetTag("checkout.sequence", long.MaxValue);
                _ = child.SetTag("checkout.accepted", true);
                _ = child.SetTag("checkout.checkpoints", Checkpoints);
                _ = child.AddEvent(new ActivityEvent("order.created", tags: new ActivityTagsCollection
                {
                    ["checkout.sequence"] = long.MaxValue,
                    ["checkout.accepted"] = true
                }));
                _ = child.AddEvent(new ActivityEvent("order.charged"));
            }
            var captured = Assert.ContainsSingle(Recorder.SpansOf(traceId));
            Assert.AreEqual(root.SpanId.ToHexString(), captured.ParentSpanId);
            Assert.AreEqual(long.MaxValue, captured.GetInt64("checkout.sequence"));
            Assert.IsNull(captured.GetString("checkout.sequence"));
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(captured.Attributes["checkout.accepted"]));
            Assert.AreSequenceEqual(Checkpoints, captured.GetStrings("checkout.checkpoints"));
            Assert.AreSequenceEqual(["order.created", "order.charged"], captured.Events.Select(item => item.Name));
            Assert.AreEqual(long.MaxValue, captured.Events[0].Attributes["checkout.sequence"]);
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(captured.Events[0].Attributes["checkout.accepted"]));
            Assert.IsTrue(captured.Events.All(item => item.TimeUnixNano >= captured.StartTimeUnixNano && item.TimeUnixNano <= captured.EndTimeUnixNano));
            Assert.AreEqual(SpanRecorder.TestProcess, captured.Service);
            Assert.IsNotNull(captured.Scope);
            Assert.IsNotNull(captured.Scope.SchemaUrl);
            Assert.IsNotNull(captured.Resource.Attributes["telemetry.sdk.name"]);
            Assert.AreEqual(1U, captured.Flags & 1U);
            Assert.IsGreaterThan(0UL, captured.StartTimeUnixNano);
            Assert.IsGreaterThanOrEqualTo(captured.StartTimeUnixNano, captured.EndTimeUnixNano);
        }
        finally
        {
            Recorder.End(root);
        }
        Assert.HasCount(2, Recorder.SpansOf(traceId));
        Assert.IsEmpty(Recorder.SpansOf("unrecorded-trace"));
    }

    /// <summary>Each invocation owns a trace while preserving its caller's ambient activity.</summary>
    [TestMethod]
    public async Task InvocationRootsHaveSeparateTracesAndRestoreTheirAmbientParent()
    {
        await Recorder.StartAsync(testContext.CancellationToken);
        using var outer = Recorder.Begin("outer invocation");
        try
        {
            using var inner = Recorder.Begin("inner invocation");
            try
            {
                Assert.AreNotEqual(outer.TraceId, inner.TraceId);
                Assert.AreSame(inner, Activity.Current);
            }
            finally
            {
                Recorder.End(inner);
            }
            Assert.AreSame(outer, Activity.Current);
        }
        finally
        {
            Recorder.End(outer);
        }
    }

    /// <summary>Missing listener features remain startup diagnostics.</summary>
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

    /// <summary>The receiver reports the endpoint Kestrel actually bound.</summary>
    [TestMethod]
    public void ReceiverUsesTheServersBoundAddress()
    {
        var features = new FeatureCollection();
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("http://127.0.0.1:43210");
        features.Set<IServerAddressesFeature>(addresses);
        Assert.AreEqual(new Uri("http://127.0.0.1:43210"), TraceReceiver.BoundAddress(features));
    }

    /// <summary>A nonempty zero-rejection acknowledgement is valid for every signal and must be relayed intact.</summary>
    [TestMethod]
    [DataRow("traces")]
    [DataRow("metrics")]
    [DataRow("logs")]
    public async Task ReceiverRelaysNonemptyAcknowledgements(string signal)
    {
        var acknowledgement = Convert.FromHexString("0A00");
        using var collector = CollectorClient(HttpStatusCode.OK, acknowledgement);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await TraceReceiver.StartAsync(capture, new QylTelemetry(collector), testContext.CancellationToken);
        using var body = new ByteArrayContent([]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await Client.PostAsync(new Uri(receiver.Address, $"/v1/{signal}"), body, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreSequenceEqual(acknowledgement, await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken));
        Assert.AreEqual(signal, Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
        Assert.AreSequenceEqual(acknowledgement, Assert.ContainsSingle(receiver.Deliveries).CollectorResponse);
    }

    /// <summary>Malformed originals and the actual upstream protobuf error stay available.</summary>
    [TestMethod]
    public async Task ReceiverPreservesRejectedOriginalsAndErrors()
    {
        var diagnostic = new RpcStatus { Code = 3, Message = "Malformed OTLP request." }.ToByteArray();
        using var collector = CollectorClient(HttpStatusCode.BadRequest, diagnostic);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await TraceReceiver.StartAsync(capture, new QylTelemetry(collector), testContext.CancellationToken);
        using var body = new ByteArrayContent([0x0b]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await Client.PostAsync(new Uri(receiver.Address, "/v1/traces"), body, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("Malformed OTLP request.", RpcStatus.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken)).Message);
        var snapshot = capture.Snapshot();
        Assert.IsEmpty(snapshot.Spans);
        Assert.AreSequenceEqual((byte[])[0x0b], Assert.ContainsSingle(snapshot.Requests).Payload.ToArray());
        var delivery = Assert.ContainsSingle(receiver.Deliveries);
        Assert.AreEqual("traces", delivery.Signal);
        Assert.AreEqual(400, delivery.CaptureStatus);
        var captureError = RpcStatus.Parser.ParseFrom(delivery.CaptureResponse);
        Assert.AreEqual(3, captureError.Code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(captureError.Message));
        Assert.AreEqual(400, delivery.CollectorStatus);
        Assert.AreSequenceEqual(diagnostic, delivery.CollectorResponse);
    }

    /// <summary>Invalid listener configuration is cleaned up and reported by Kestrel.</summary>
    [TestMethod]
    public async Task ReceiverRejectsInvalidListenerConfiguration()
    {
        using var collector = CollectorClient(HttpStatusCode.OK, []);
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        const string setting = "Kestrel__Endpoints__Broken__Url";
        var previous = Environment.GetEnvironmentVariable(setting);
        try
        {
            Environment.SetEnvironmentVariable(setting, "invalid-listener-address");
            _ = await Assert.ThrowsAsync<FormatException>(() => TraceReceiver.StartAsync(capture, new QylTelemetry(collector), testContext.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(setting, previous);
        }
    }

    private static HttpClient CollectorClient(HttpStatusCode status, byte[] bytes) => new(new CollectorHandler(status, bytes))
    {
        BaseAddress = new Uri("https://collector.test/")
    };

    private sealed class CollectorHandler(HttpStatusCode status, byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-protobuf") } }
            });
    }

    /// <summary>A complete export is still forwarded after its producer disconnects.</summary>
    [TestMethod]
    [DataRow("traces")]
    [DataRow("logs")]
    [DataRow("metrics")]
    public async Task ReceiverDrainsAcceptedExportAfterProducerDisconnects(string signal)
    {
        using var handler = new PendingCollectorHandler();
        using var collector = new HttpClient(handler);
        collector.BaseAddress = new Uri("https://collector.test/");
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        var receiver = await TraceReceiver.StartAsync(capture, new QylTelemetry(collector), testContext.CancellationToken);
        try
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
            using var body = new ByteArrayContent([]);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            var send = Client.PostAsync(new Uri(receiver.Address, $"v1/{signal}"), body, cancelled.Token);
            await handler.Started.Task.WaitAsync(testContext.CancellationToken);
            await cancelled.CancelAsync();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => send);
            var stop = receiver.DisposeAsync().AsTask();
            Assert.IsFalse(stop.IsCompleted, "Shutdown must join the active export.");
            _ = handler.Complete.TrySetResult();
            await stop;
            var delivery = Assert.ContainsSingle(receiver.Deliveries);
            Assert.AreEqual(200, delivery.CaptureStatus);
            Assert.AreEqual(200, delivery.CollectorStatus);
            Assert.IsNull(delivery.Failure);
            Assert.AreEqual(signal, Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
        }
        finally
        {
            _ = handler.Complete.TrySetResult();
            await receiver.DisposeAsync();
        }
    }

    /// <summary>Transport failures remain visible rather than disappearing from the delivery list.</summary>
    [TestMethod]
    public async Task ReceiverRetainsTransportFailure()
    {
        using var handler = new PendingCollectorHandler();
        using var collector = new HttpClient(handler);
        collector.BaseAddress = new Uri("https://collector.test/");
        await using var capture = await OtlpReceiver.StartAsync(testContext.CancellationToken);
        await using var receiver = await TraceReceiver.StartAsync(capture, new QylTelemetry(collector), testContext.CancellationToken);
        using var body = new ByteArrayContent([]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var send = Client.PostAsync(new Uri(receiver.Address, "v1/logs"), body, testContext.CancellationToken);
        await handler.Started.Task.WaitAsync(testContext.CancellationToken);
        _ = handler.Complete.TrySetException(new HttpRequestException("upstream unavailable"));
        using var response = await send;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(14, RpcStatus.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync(testContext.CancellationToken)).Code);
        var delivery = Assert.ContainsSingle(receiver.Deliveries);
        Assert.AreEqual(200, delivery.CaptureStatus);
        Assert.IsNull(delivery.CollectorStatus);
        Assert.AreEqual(nameof(HttpRequestException), delivery.Failure);
        Assert.AreEqual("logs", Assert.ContainsSingle(capture.Snapshot().Requests).Signal);
    }

    private sealed class PendingCollectorHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-protobuf") } }
            };
        }
    }
}
