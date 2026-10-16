using JetBrains.Annotations;
using Payments.Api;
using Qyl;

var builder = WebApplication.CreateBuilder(args);

// qyl records HTTP and application spans and exports to the configured OTLP endpoint.
builder.AddQyl();

var app = builder.Build();

// The store this service writes to. The architecture attribute decides whether it is shared.
var store = new Store(app.Configuration["STORE"] ?? "none");
var logCharged = LoggerMessage.Define<string>(LogLevel.Information, new EventId(1002, "PaymentCharged"),
    "Payment for order {OrderId} charged");

app.MapGet("/health", () => Environment.ProcessId);
app.MapGet("/store", () => store.Name);
app.MapPost("/charge", (Charge charge) =>
{
    store.Write("charge");
    logCharged(app.Logger, charge.OrderId, null);
    return new Receipt(charge.OrderId, charge.Amount, store.Name);
});

app.Run();

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record Charge(string OrderId, decimal Amount);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record Receipt(string OrderId, decimal Amount, string Store);

namespace Payments.Api
{
    /// <summary>Identifies this executable to the architecture test fixture.</summary>
    public abstract class EntryPoint;
}
