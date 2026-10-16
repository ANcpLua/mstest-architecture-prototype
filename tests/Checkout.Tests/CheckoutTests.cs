using System.Net;
using System.Net.Http.Json;
using Architecture.Testing;
using Qyl.Telemetry.SemanticConventions.Names;
using OrdersApi = Orders.Api.EntryPoint;
using PaymentsApi = Payments.Api.EntryPoint;

[assembly: DoNotParallelize]

namespace Checkout.Tests;

/// <summary>Checks a checkout whose services own independent stores.</summary>
/// <param name="testContext">The runner context that supplies cancellation for this test.</param>
[Microservices]
[Service<OrdersApi>("orders-api")]
[Service<PaymentsApi>("payments-api")]
public sealed class CheckoutAsMicroservices(TestContext testContext)
{
    /// <summary>Payment succeeds while each service retains its own store.</summary>
    [TestMethod]
    public async Task OrderIsPaidAndEveryServiceKeepsItsOwnStore()
    {
        var order = await Checkout.PlaceOrderAsync(testContext.CancellationToken);

        Assert.AreEqual("orders-api", order.OrdersStore);
        Assert.AreEqual("payments-api", order.PaymentsStore);
    }

    /// <summary>The exported trace contains the service call and both store writes.</summary>
    [TestMethod]
    public async Task TraceShowsOrdersCallingPaymentsAndEachWritingItsOwnStore()
    {
        _ = await Checkout.PlaceOrderAsync(testContext.CancellationToken);

        var trace = await SystemUnderTest.Current.TraceAsync(testContext.CancellationToken);

        Assert.Contains(new ServiceCall("orders-api", "payments-api"), trace.Calls);
        var writes = trace.Spans.Where(span => span.Store is not null).ToArray();
        Assert.HasCount(2, writes);
        Assert.IsTrue(writes.All(span => span.Source == QylTelemetryNames.Scopes.QylTelemetryApplication));
        Assert.IsTrue(writes.All(span => span.Attribute("qyl.instrumentation.domain") == "db.client"));
        Assert.AreEqual("orders-api", trace.StoresOf("orders-api").Single());
        Assert.AreEqual("payments-api", trace.StoresOf("payments-api").Single());
    }
}

/// <summary>Checks a checkout whose services share a store.</summary>
/// <param name="testContext">The runner context that supplies cancellation for this test.</param>
[DistributedMonolith]
[Service<OrdersApi>("orders-api")]
[Service<PaymentsApi>("payments-api")]
public sealed class CheckoutAsDistributedMonolith(TestContext testContext)
{
    /// <summary>Both the response and trace identify the shared store.</summary>
    [TestMethod]
    public async Task OrderIsPaidAndBothServicesShareOneStore()
    {
        var order = await Checkout.PlaceOrderAsync(testContext.CancellationToken);
        var trace = await SystemUnderTest.Current.TraceAsync(testContext.CancellationToken);

        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, order.OrdersStore);
        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, order.PaymentsStore);
        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, trace.StoresOf("orders-api").Single());
        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, trace.StoresOf("payments-api").Single());
    }
}

/// <summary>Checks the payments service as one deployable.</summary>
/// <param name="testContext">The runner context that supplies cancellation for this test.</param>
[Monolith]
[Service<PaymentsApi>("payments-api")]
public sealed class PaymentsAsMonolith(TestContext testContext)
{
    /// <summary>Each charge is recorded in the single service's store and its own test trace.</summary>
    /// <param name="orderId">The order charged by this data row.</param>
    /// <param name="amount">The amount charged for the order.</param>
    [TestMethod]
    [DataRow("first-order", 12.5)]
    [DataRow("second-order", 25.0)]
    public async Task ChargeIsWrittenToTheStoreOfTheOneDeployable(string orderId, double amount)
    {
        using var payments = SystemUnderTest.Current.Client<PaymentsApi>();

        using var response = await payments.PostAsJsonAsync("/charge", new { OrderId = orderId, Amount = (decimal)amount }, testContext.CancellationToken);
        var receipt = await response.Content.ReadFromJsonAsync<Receipt>(testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(orderId, receipt?.OrderId);
        Assert.AreEqual("payments-api", receipt?.Store);

        var trace = await SystemUnderTest.Current.TraceAsync(testContext.CancellationToken);
        Assert.HasCount(1, trace.Spans.Where(span => span.Store is not null));
    }
}

internal static class Checkout
{
    public static async Task<Order> PlaceOrderAsync(CancellationToken cancellationToken)
    {
        using var orders = SystemUnderTest.Current.Client<OrdersApi>();

        using var response = await orders.PostAsJsonAsync("/orders", new { Amount = 12.5m }, cancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<Order>(cancellationToken)
            ?? throw new InvalidOperationException("The orders service returned no order.");
    }
}

internal sealed record Order(string Id, decimal Amount, string OrdersStore, string PaymentsStore);

internal sealed record Receipt(string OrderId, decimal Amount, string Store);
