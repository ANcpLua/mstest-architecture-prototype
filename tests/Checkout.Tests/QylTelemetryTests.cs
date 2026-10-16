using System.Net;
using System.Text;
using System.Text.Json;
using Architecture.Testing;
using Qyl.Api.Contracts.OTel.Metrics;

namespace Checkout.Tests;

/// <summary>Checks qyl query pagination, wire failures and explicit metric windows.</summary>
/// <param name="testContext">The runner context supplies cancellation.</param>
[TestClass]
public sealed class QylTelemetryTests(TestContext testContext)
{
    /// <summary>Every log page is read and cursors and trace IDs are safely encoded.</summary>
    [TestMethod]
    public async Task LogsFollowContinuationCursors()
    {
        var requests = new List<string>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!.Query);
            return Json(requests.Count == 1
                ? """{"items":[],"has_more":true,"next_cursor":"next +&"}"""
                : """{"items":[],"has_more":false}""");
        });
        var logs = await new QylTelemetry(client).LogsAsync("trace&one", testContext.CancellationToken);
        Assert.IsEmpty(logs);
        Assert.HasCount(2, requests);
        Assert.Contains("trace_id=trace%26one", requests[0]);
        Assert.Contains("cursor=next%20%2B%26", requests[1]);
    }

    /// <summary>A missing, empty or repeated continuation cannot silently truncate the result.</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("repeated")]
    public async Task InvalidLogContinuationFails(string? cursor)
    {
        using var client = Client(_ => Json(JsonSerializer.Serialize(new { items = Array.Empty<object>(), has_more = true, next_cursor = cursor })));
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new QylTelemetry(client).LogsAsync("trace", testContext.CancellationToken));
        Assert.Contains("continuation cursor", failure.Message);
    }

    /// <summary>HTTP errors and empty JSON stay visible to the caller.</summary>
    [TestMethod]
    public async Task QueryErrorsAreNotConvertedToEmptyResults()
    {
        using var failed = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new QylTelemetry(failed).LogsAsync("trace", testContext.CancellationToken));
        using var empty = Client(_ => Json("null"));
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QylTelemetry(empty).LogsAsync("trace", testContext.CancellationToken));
    }

    /// <summary>Metric queries preserve their explicit UTC window, aggregation and response metadata.</summary>
    [TestMethod]
    public async Task MetricQueriesPreserveWindowAndAggregation()
    {
        var start = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(2));
        var expected = new MetricQueryResult
        {
            Name = "http.server.request.duration",
            Kind = MetricKind.Histogram,
            Unit = "s",
            Aggregation = MetricAggregation.P95,
            StartTime = start,
            EndTime = start.AddMinutes(1),
            StepMs = 1000,
            Series = [],
            Truncated = true
        };
        using var client = Client(request =>
        {
            Assert.Contains("aggregation=p95", request.RequestUri!.Query);
            Assert.Contains("start_time=2026-10-16T10%3A00%3A00", request.RequestUri.Query);
            return Json(JsonSerializer.Serialize(expected));
        });
        var telemetry = new QylTelemetry(client);
        var actual = await telemetry.QueryMetricAsync(expected.Name, start, start.AddMinutes(1),
            MetricAggregation.P95, testContext.CancellationToken);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Aggregation, actual.Aggregation);
        Assert.IsTrue(actual.Truncated);
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() => telemetry.QueryMetricAsync(" ", start, start, cancellationToken: testContext.CancellationToken));
    }

    /// <summary>Forwarding requires a complete OTLP acknowledgement and propagates HTTP failures.</summary>
    [TestMethod]
    public async Task TraceForwardingRejectsPartialAcceptance()
    {
        using var accepted = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        await new QylTelemetry(accepted).ExportAsync("v1/traces", ReadOnlyMemory<byte>.Empty, testContext.CancellationToken);
        using var partial = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("\n\0"u8.ToArray()) });
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new QylTelemetry(partial).ExportAsync("v1/traces", ReadOnlyMemory<byte>.Empty, testContext.CancellationToken));
        using var failed = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new QylTelemetry(failed).ExportAsync("v1/traces", ReadOnlyMemory<byte>.Empty, testContext.CancellationToken));
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Handler(respond)) { BaseAddress = new Uri("https://qyl.test/") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
