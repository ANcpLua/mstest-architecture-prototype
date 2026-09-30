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

var app = builder.Build();

// The store this service writes to. The architecture attribute decides whether it is shared.
var store = new Store(app.Configuration["STORE"] ?? "none");

app.MapGet("/store", () => store.Name);
app.MapPost("/charge", (Charge charge) =>
{
    store.Write("charge");
    return new Receipt(charge.OrderId, charge.Amount, store.Name);
});

app.Run();

internal sealed record Charge(string OrderId, decimal Amount);

internal sealed record Receipt(string OrderId, decimal Amount, string Store);
