using System.Net;
using System.Text.Json;
using Architecture.Testing;
using Qyl.Api.Contracts.Common;
using Qyl.Api.Contracts.OTel.Metrics;
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
            records => records.Count >= 2, testContext.CancellationToken);

        Assert.IsTrue(logs.All(log => log.TraceId == trace.Id));
        Assert.Contains(log => log.Body is AttributeValue.StringValue body && body.Value.Contains(order.Id, StringComparison.Ordinal), logs);
        Assert.Contains(log => JsonSerializer.Serialize(log).Contains("OrderCreated", StringComparison.Ordinal), logs);
        Assert.Contains(log => JsonSerializer.Serialize(log).Contains("PaymentCharged", StringComparison.Ordinal), logs);
        var version = typeof(OrdersApi).Assembly.GetName().Version!.ToString(3);
        Assert.IsTrue(logs.All(log => log.Resource.Attributes!.Any(attribute => attribute is
        { Key: "service.version", Value: AttributeValue.StringValue value } && value.Value.Split('+')[0] == version)));
        testContext.WriteLine(JsonSerializer.Serialize(logs));
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
