using System.Net;
using System.Net.Http.Json;
using Architecture.Testing;
using Qyl.Telemetry.SemanticConventions.Names;
using OrdersApi = Orders.Api.EntryPoint;
using PaymentsApi = Payments.Api.EntryPoint;

namespace Checkout.Tests;

[Microservices]
[Service<OrdersApi>("orders-api")]
[Service<PaymentsApi>("payments-api")]
public sealed class CheckoutAsMicroservices
{
    [TestMethod]
    public async Task Order_is_paid_and_every_service_keeps_its_own_store()
    {
        var order = await Checkout.PlaceOrderAsync();

        Assert.AreEqual("orders-api", order.OrdersStore);
        Assert.AreEqual("payments-api", order.PaymentsStore);
    }

    [TestMethod]
    public async Task Trace_shows_orders_calling_payments_and_each_writing_its_own_store()
    {
        await Checkout.PlaceOrderAsync();

        var trace = await SystemUnderTest.Current.TraceAsync();

        Assert.Contains(new Call("orders-api", "payments-api"), trace.Calls);
        var writes = trace.Spans.Where(span => span.Store is not null).ToArray();
        Assert.HasCount(2, writes);
        Assert.IsTrue(writes.All(span => span.Source == QylTelemetryNames.Scopes.QylTelemetryApplication));
        Assert.IsTrue(writes.All(span => span.Attribute("qyl.instrumentation.domain") == "db.client"));
        Assert.AreEqual("orders-api", trace.StoresOf("orders-api").Single());
        Assert.AreEqual("payments-api", trace.StoresOf("payments-api").Single());
    }
}

[DistributedMonolith]
[Service<OrdersApi>("orders-api")]
[Service<PaymentsApi>("payments-api")]
public sealed class CheckoutAsDistributedMonolith
{
    [TestMethod]
    public async Task Order_is_paid_and_both_services_share_one_store()
    {
        var order = await Checkout.PlaceOrderAsync();
        var trace = await SystemUnderTest.Current.TraceAsync();

        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, order.OrdersStore);
        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, order.PaymentsStore);
        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, trace.StoresOf("orders-api").Single());
        Assert.AreEqual(DistributedMonolithAttribute.SharedStore, trace.StoresOf("payments-api").Single());
    }
}

[Monolith]
[Service<PaymentsApi>("payments-api")]
public sealed class PaymentsAsMonolith
{
    [TestMethod]
    public async Task Charge_is_written_to_the_store_of_the_one_deployable()
    {
        using var payments = SystemUnderTest.Current.Client<PaymentsApi>();

        var response = await payments.PostAsJsonAsync("/charge", new { OrderId = "1", Amount = 12.5m });
        var receipt = await response.Content.ReadFromJsonAsync<Receipt>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("payments-api", receipt?.Store);
    }
}

internal static class Checkout
{
    public static async Task<Order> PlaceOrderAsync()
    {
        using var orders = SystemUnderTest.Current.Client<OrdersApi>();

        var response = await orders.PostAsJsonAsync("/orders", new { Amount = 12.5m });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<Order>()
            ?? throw new InvalidOperationException("The orders service returned no order.");
    }
}

internal sealed record Order(string Id, decimal Amount, string OrdersStore, string PaymentsStore);

internal sealed record Receipt(string OrderId, decimal Amount, string Store);
