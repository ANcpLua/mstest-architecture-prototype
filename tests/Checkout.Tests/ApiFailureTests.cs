using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Architecture.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Qyl.Orchestration;

namespace Checkout.Tests;

/// <summary>Exercises the real API processes with absent settings and failing upstream responses.</summary>
/// <param name="testContext">The runner context that supplies cancellation.</param>
[TestClass]
public sealed class ApiFailureTests(TestContext testContext)
{
    private SpanRecorder Recorder => ArchitectureFixtures.GetRun(testContext).Recorder;

    /// <summary>Starts telemetry only for tests that explicitly exercise the recorder.</summary>
    [TestInitialize]
    public Task StartRecorder() => Recorder.StartAsync(testContext.CancellationToken);

    private static readonly HttpClient Client = new();

    /// <summary>Both APIs expose the documented fallback when no store was supplied by their host.</summary>
    [TestMethod]
    public async Task MissingStoreConfigurationUsesTheFallback()
    {
        var builder = CreateBuilder();
        var payments = AddService(builder, typeof(Payments.Api.EntryPoint), "payments-api");
        _ = AddService(builder, typeof(Orders.Api.EntryPoint), "orders-api").WithReference(payments);
        await using var app = builder.Build();
        await app.StartAsync(testContext.CancellationToken);
        var ordersAddress = app.GetEndpoint("orders-api");
        var paymentsAddress = app.GetEndpoint("payments-api");

        Assert.AreEqual("none", await Client.GetStringAsync(new Uri(ordersAddress, "/store"), testContext.CancellationToken));
        Assert.AreEqual("none", await Client.GetStringAsync(new Uri(paymentsAddress, "/store"), testContext.CancellationToken));
        using var response = await Client.PostAsJsonAsync(new Uri(ordersAddress, "/orders"), new { Amount = 12.5m }, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<Order>(testContext.CancellationToken);
        Assert.IsNotNull(order);
        Assert.AreEqual("none", order.OrdersStore);
        Assert.AreEqual("none", order.PaymentsStore);
    }

    /// <summary>An upstream error or a JSON null receipt never becomes a successful order.</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidPaymentResponsesFailTheOrder(bool upstreamError)
    {
        using var root = Recorder.Begin(nameof(InvalidPaymentResponsesFailTheOrder));
        try
        {
            var upstreamBuilder = WebApplication.CreateSlimBuilder();
            _ = upstreamBuilder.Logging.ClearProviders();
            _ = upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var upstream = upstreamBuilder.Build();
            _ = upstream.MapPost("/charge", () => upstreamError
                ? Results.StatusCode(StatusCodes.Status502BadGateway)
                : Results.Text("null", "application/json"));
            await upstream.StartAsync(testContext.CancellationToken);

            var builder = CreateBuilder();
            _ = AddService(builder, typeof(Orders.Api.EntryPoint), "orders-api")
                .WithEnvironment("services__payments-api__http__0", upstream.Urls.Single());
            await using var app = builder.Build();
            await app.StartAsync(testContext.CancellationToken);
            using var response = await Client.PostAsJsonAsync(new Uri(app.GetEndpoint("orders-api"), "/orders"), new { Amount = 12.5m }, testContext.CancellationToken);

            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            var diagnostic = await response.Content.ReadAsStringAsync(testContext.CancellationToken);
            Assert.Contains(upstreamError ? "502 (Bad Gateway)" : "The payments service returned no receipt.", diagnostic);
            await AssertStoredFailureAsync(root.TraceId.ToHexString());
            await upstream.StopAsync(testContext.CancellationToken);
        }
        finally
        {
            Recorder.End(root);
        }
    }

    private async Task AssertStoredFailureAsync(string traceId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var logs = await Recorder.Collector.Telemetry.LogsAsync(traceId, deadline.Token);
            if (logs.FirstOrDefault(log => (int)log.SeverityNumber >= 17) is { } error)
            {
                Assert.AreEqual("UnhandledException", error.EventName);
                try
                {
                    var trace = await Client.GetFromJsonAsync<JsonElement>(new Uri(Recorder.Collector.Address, $"api/v1/traces/{traceId}"), deadline.Token);
                    if (trace.GetProperty("spans").EnumerateArray().Any(span => span.GetProperty("status").GetProperty("code").GetInt32() == 2))
                    {
                        testContext.WriteLine(trace.GetRawText());
                        testContext.WriteLine(JsonSerializer.Serialize(logs));
                        return;
                    }
                }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }

            await Task.Delay(100, deadline.Token);
        }
    }

    private static QylApplicationBuilder CreateBuilder() => new() { MaxRestarts = 0, StartupTimeoutSeconds = 120 };

    private QylProcessBuilder AddService(QylApplicationBuilder builder, Type entryPoint, string name) =>
        builder.AddDotnetService(name, entryPoint.Assembly.Location)
            .WithoutEnvironmentPrefix("OTEL_")
            .WithoutEnvironmentPrefix("QYL_")
            .WithoutEnvironmentPrefix("services__")
            .WithoutEnvironmentPrefix("STORE")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", Recorder.ReceiverAddress.ToString())
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200")
            .WithEnvironment("OTEL_BLRP_SCHEDULE_DELAY", "200")
            .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "200")
            .WithHealthCheck(requireProcessId: true);
}
