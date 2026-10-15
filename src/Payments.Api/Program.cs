using Qyl;

var builder = WebApplication.CreateBuilder(args);

// qyl records HTTP and application spans and exports to the configured OTLP endpoint.
builder.AddQyl();

var app = builder.Build();

// The store this service writes to. The architecture attribute decides whether it is shared.
var store = new Store(app.Configuration["STORE"] ?? "none");

app.MapGet("/health", () => Environment.ProcessId);
app.MapGet("/store", () => store.Name);
app.MapPost("/charge", (Charge charge) =>
{
    store.Write("charge");
    return new Receipt(charge.OrderId, charge.Amount, store.Name);
});

app.Run();

internal sealed record Charge(string OrderId, decimal Amount);

internal sealed record Receipt(string OrderId, decimal Amount, string Store);

namespace Payments.Api
{
    /// <summary>Identifies this executable to the architecture test fixture.</summary>
    public sealed class EntryPoint;
}
