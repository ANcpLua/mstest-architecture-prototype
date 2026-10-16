using System.Diagnostics;
using System.Diagnostics.Metrics;
using JetBrains.Annotations;
using Orders.Api;
using Qyl;

var builder = WebApplication.CreateBuilder(args);

// qyl records HTTP and application spans and exports to the configured OTLP endpoint.
builder.AddQyl(options => options.AdditionalMeters.Add("Checkout.Orders"));
using var meter = new Meter("Checkout.Orders", typeof(EntryPoint).Assembly.GetName().Version!.ToString(3));
var ordersCreated = meter.CreateCounter<long>("checkout.orders.created", "{order}", "Successfully created orders");

// The address of the payments service is handed in by the host that starts this service.
var paymentsAddress = new Uri(builder.Configuration["services:payments-api:http:0"]
    ?? throw new InvalidOperationException("No address for the payments service was configured."));
builder.Services.AddHttpClient("payments", client =>
    client.BaseAddress = paymentsAddress);

var app = builder.Build();

// The store this service writes to. The architecture attribute decides whether it is shared.
var store = new Store(app.Configuration["STORE"] ?? "none");
var logCreated = LoggerMessage.Define<string>(LogLevel.Information, new EventId(1001, "OrderCreated"),
    "Order {OrderId} created");

app.MapGet("/health", () => Environment.ProcessId);
app.MapGet("/store", () => store.Name);
app.MapPost("/orders", async (PlaceOrder order, IHttpClientFactory clients) =>
{
    var id = Guid.NewGuid().ToString("N");
    store.Write("order");

    var response = await clients.CreateClient("payments").PostAsJsonAsync("/charge", new Charge(id, order.Amount));
    _ = response.EnsureSuccessStatusCode();
    var receipt = await response.Content.ReadFromJsonAsync<Receipt>()
        ?? throw new InvalidOperationException("The payments service returned no receipt.");

    logCreated(app.Logger, id, null);
    ordersCreated.Add(1);
    _ = Activity.Current?.AddEvent(new ActivityEvent("checkout.order.created", tags: new ActivityTagsCollection
    {
        ["checkout.order.id"] = id
    }));

    return Results.Created($"/orders/{id}", new Order(id, order.Amount, store.Name, receipt.Store));
});

app.Run();

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record PlaceOrder(decimal Amount);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record Charge(string OrderId, decimal Amount);

internal sealed record Receipt(string OrderId, decimal Amount, string Store);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record Order(string Id, decimal Amount, string OrdersStore, string PaymentsStore);

namespace Orders.Api
{
    /// <summary>Identifies this executable to the architecture test fixture.</summary>
    public abstract class EntryPoint;
}
