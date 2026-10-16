using System.Net;
using System.Text.Json;
using Architecture.Testing;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.OTel.Metrics;
using Qyl.Telemetry.Testing;
using OrdersApi = Orders.Api.EntryPoint;
using PaymentsApi = Payments.Api.EntryPoint;

namespace Checkout.Tests;

/// <summary>Checks real qyl persistence of application logs and automatically recorded HTTP metrics.</summary>
/// <param name="testContext">The context owns cancellation and evidence attachments.</param>
[Microservices]
[Service<OrdersApi>("orders-api")]
[Service<PaymentsApi>("payments-api")]
public sealed class TelemetryTests(TestContext testContext)
{
    /// <summary>Both services export structured logs with the request's trace identity.</summary>
    [TestMethod]
    public async Task QylStoresCorrelatedApplicationLogs()
    {
        var order = await Checkout.PlaceOrderAsync(testContext.CancellationToken);
        var trace = await SystemUnderTest.Current.TraceAsync(testContext.CancellationToken);
        var logs = await Eventually(async token => await SystemUnderTest.Current.Telemetry.LogsAsync(trace.Id, token),
            records => records.Any(log => log.EventName == "OrderCreated") && records.Any(log => log.EventName == "PaymentCharged"), testContext.CancellationToken);

        Assert.IsTrue(logs.All(log => log.TraceId == trace.Id));
        Assert.Contains(log => log.Body is AttributeValue.StringValue body && body.Value.Contains(order.Id, StringComparison.Ordinal), logs);
        Assert.Contains(log => JsonSerializer.Serialize(log).Contains("OrderCreated", StringComparison.Ordinal), logs);
        Assert.Contains(log => JsonSerializer.Serialize(log).Contains("PaymentCharged", StringComparison.Ordinal), logs);
        var version = typeof(OrdersApi).Assembly.GetName().Version!.ToString(3);
        Assert.IsTrue(logs.All(log => log.Resource.Attributes!.Any(attribute => attribute is
        { Key: "service.version", Value: AttributeValue.StringValue value } && value.Value.Split('+')[0] == version)));
        testContext.WriteLine(JsonSerializer.Serialize(logs));
    }

    /// <summary>A successful order produces correlated business events and a monotonic order counter.</summary>
    [TestMethod]
    public async Task BusinessSignalsRetainTheirTraceAndResourceIdentity()
    {
        var order = await Checkout.PlaceOrderAsync(testContext.CancellationToken);
        var trace = await SystemUnderTest.Current.TraceAsync(testContext.CancellationToken);
        var logs = await Eventually(async token => await SystemUnderTest.Current.Telemetry.LogsAsync(trace.Id, token),
            records => records.Any(log => log.EventName == "OrderCreated") && records.Any(log => log.EventName == "PaymentCharged"), testContext.CancellationToken);

        Assert.Contains("OrderCreated", logs.Select(log => log.EventName));
        Assert.Contains("PaymentCharged", logs.Select(log => log.EventName));
        var server = Assert.ContainsSingle(span => span is { Service: "orders-api", Name: "POST /orders" }, trace.Spans);
        var created = Assert.ContainsSingle(item => item.Name == "checkout.order.created", server.Events);
        Assert.AreEqual(order.Id, created.Attributes["checkout.order.id"]);
        Assert.IsGreaterThanOrEqualTo(server.StartTimeUnixNano, created.TimeUnixNano);
        Assert.IsLessThanOrEqualTo(server.EndTimeUnixNano, created.TimeUnixNano);
        var instance = server.Resource.Attributes["service.instance.id"];
        var capture = await SpanRecorder.AwaitAsync(snapshot => snapshot.Metrics.Any(metric =>
            metric.Name == "checkout.orders.created" && Equals(metric.Resource.Attributes["service.instance.id"], instance)), testContext.CancellationToken);
        var counter = capture.Metrics.Last(item => item.Name == "checkout.orders.created"
            && Equals(item.Resource.Attributes["service.instance.id"], instance));
        Assert.AreEqual("{order}", counter.Unit);
        Assert.AreEqual("Checkout.Orders", counter.Scope?.Name);
        var sum = Assert.IsInstanceOfType<SumData>(counter.Data);
        Assert.IsTrue(sum.IsMonotonic);
        Assert.IsGreaterThanOrEqualTo(1L, Assert.IsInstanceOfType<long>(Assert.ContainsSingle(sum.Points).Value));
        var original = Assert.ContainsSingle(item => item.EventName == "OrderCreated", capture.Logs.Where(item => item.TraceId == trace.Id));
        Assert.AreEqual(server.SpanId, original.SpanId);
        Assert.AreEqual(1U, original.Flags & 1U);
        Assert.Contains(order.Id, Assert.IsInstanceOfType<string>(original.Body));
        Assert.AreEqual(instance, original.Resource.Attributes["service.instance.id"]);
        testContext.WriteLine(JsonSerializer.Serialize(new { Event = created, Log = original, Metric = counter }));
    }

    /// <summary>Native HTTP and runtime metrics retain their units, shapes, scopes and producer resource.</summary>
    [TestMethod]
    [DataRow("http.server.request.duration", "s", "Microsoft.AspNetCore.Hosting", typeof(HistogramData))]
    [DataRow("http.client.request.duration", "s", "System.Net.Http", typeof(HistogramData))]
    [DataRow("dotnet.process.cpu.time", "s", "System.Runtime", typeof(SumData))]
    [DataRow("dotnet.process.memory.working_set", "By", "System.Runtime", typeof(SumData))]
    public async Task NativeMetricsRetainTheirProducerMetadata(string name, string unit, string scope, Type dataType)
    {
        _ = await Checkout.PlaceOrderAsync(testContext.CancellationToken);
        var trace = await SystemUnderTest.Current.TraceAsync(testContext.CancellationToken);
        var instances = trace.Spans.Where(span => span.Service is "orders-api" or "payments-api")
            .Select(span => span.Resource.Attributes["service.instance.id"]).ToHashSet();
        var capture = await SpanRecorder.AwaitAsync(snapshot => snapshot.Metrics.Any(metric => metric.Name == name
            && instances.Contains(metric.Resource.Attributes["service.instance.id"])), testContext.CancellationToken);
        // These are process measurements, selected by resource identity, not attributed to this request.
        var observed = capture.Metrics.Where(metric => metric.Name == name
            && instances.Contains(metric.Resource.Attributes["service.instance.id"])).ToArray();
        Assert.IsNotEmpty(observed);
        foreach (var metric in observed)
        {
            Assert.AreEqual(unit, metric.Unit);
            Assert.AreEqual(scope, metric.Scope?.Name);
            Assert.IsInstanceOfType(metric.Data, dataType);
            Assert.AreEqual("Development", metric.Resource.Attributes["deployment.environment.name"]);
            Assert.AreEqual("opentelemetry", metric.Resource.Attributes["telemetry.sdk.name"]);
            Assert.AreEqual("dotnet", metric.Resource.Attributes["telemetry.sdk.language"]);
            Assert.AreEqual("1.20.0", metric.Resource.Attributes["telemetry.sdk.version"]);
            Assert.AreEqual("https://opentelemetry.io/schemas/1.44.0", metric.Resource.SchemaUrl);
        }
        testContext.WriteLine(JsonSerializer.Serialize(observed));
    }

    /// <summary>HTTP metric buckets come from qyl's real collector, with histogram units preserved.</summary>
    [TestMethod]
    public async Task QylStoresAndQueriesHttpMetrics()
    {
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        _ = await Checkout.PlaceOrderAsync(testContext.CancellationToken);
        var result = await Eventually(async token =>
        {
            try
            {
                return await SystemUnderTest.Current.Telemetry.QueryMetricAsync("http.server.request.duration",
                    start, DateTimeOffset.UtcNow.AddSeconds(1), MetricAggregation.Count, token);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }, metric => metric is not null && metric.Series.SelectMany(series => series.Buckets).Any(HasObservations), testContext.CancellationToken);

        Assert.IsNotNull(result);
        testContext.WriteLine(JsonSerializer.Serialize(result));
        Assert.AreEqual(MetricKind.Histogram, result.Kind);
        Assert.AreEqual("s", result.Unit);
        Assert.AreEqual(MetricAggregation.Count, result.Aggregation);
        Assert.IsFalse(result.Truncated);
        Assert.Contains(HasObservations, result.Series.SelectMany(series => series.Buckets));
    }

    private static bool HasObservations(MetricBucket bucket) =>
        bucket.Value is not null && JsonSerializer.SerializeToElement(bucket.Value) is { ValueKind: JsonValueKind.Number } value
        && value.GetDouble() >= 1;

    private static async Task<T> Eventually<T>(Func<CancellationToken, Task<T>> read, Func<T, bool> ready, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var result = await read(deadline.Token);
            if (ready(result))
            {
                return result;
            }

            await Task.Delay(100, deadline.Token);
        }
    }
}
