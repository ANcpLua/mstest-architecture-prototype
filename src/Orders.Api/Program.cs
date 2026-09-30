using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Every span goes to wherever the host points this service, the way a service with Aspire's defaults does.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(Store.Source.Name))
    .UseOtlpExporter();

// The address of the payments service is handed in by the host that starts this service.
builder.Services.AddHttpClient("payments", client =>
    client.BaseAddress = new Uri(builder.Configuration["services:payments-api:http:0"]
        ?? throw new InvalidOperationException("No address for the payments service was configured.")));

var app = builder.Build();

// The store this service writes to. The architecture attribute decides whether it is shared.
var store = new Store(app.Configuration["STORE"] ?? "none");

app.MapGet("/store", () => store.Name);
app.MapPost("/orders", async (PlaceOrder order, IHttpClientFactory clients) =>
{
    var id = Guid.NewGuid().ToString("N");
    store.Write("order");

    var response = await clients.CreateClient("payments").PostAsJsonAsync("/charge", new Charge(id, order.Amount));
    response.EnsureSuccessStatusCode();
    var receipt = await response.Content.ReadFromJsonAsync<Receipt>()
        ?? throw new InvalidOperationException("The payments service returned no receipt.");

    return Results.Created($"/orders/{id}", new Order(id, order.Amount, store.Name, receipt.Store));
});

app.Run();

internal sealed record PlaceOrder(decimal Amount);

internal sealed record Charge(string OrderId, decimal Amount);

internal sealed record Receipt(string OrderId, decimal Amount, string Store);

internal sealed record Order(string Id, decimal Amount, string OrdersStore, string PaymentsStore);
