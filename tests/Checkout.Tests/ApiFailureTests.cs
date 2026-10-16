using System.Net;
using System.Net.Http.Json;
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
        await upstream.StopAsync(testContext.CancellationToken);
    }

    private static QylApplicationBuilder CreateBuilder() => new() { MaxRestarts = 0, StartupTimeoutSeconds = 120 };

    private static QylProcessBuilder AddService(QylApplicationBuilder builder, Type entryPoint, string name) =>
        builder.AddDotnetService(name, entryPoint.Assembly.Location)
            .WithoutEnvironmentPrefix("OTEL_")
            .WithoutEnvironmentPrefix("QYL_")
            .WithoutEnvironmentPrefix("services__")
            .WithoutEnvironmentPrefix("STORE")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("OTEL_TRACES_EXPORTER", "none")
            .WithEnvironment("OTEL_METRICS_EXPORTER", "none")
            .WithEnvironment("OTEL_LOGS_EXPORTER", "none")
            .WithHealthCheck(requireProcessId: true);
}
