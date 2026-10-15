using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Architecture.Testing;

/// <summary>Receives the spans the services export over OTLP/HTTP, inside the test process.</summary>
internal sealed class TraceReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TraceReceiver(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>The address the services export to.</summary>
    public Uri Address { get; }

    public static async Task<TraceReceiver> StartAsync(Action<Span> record)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        _ = app.MapPost("/v1/traces", async (HttpRequest request) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body).ConfigureAwait(false);
            foreach (var span in OtlpTraces.Decode(body.ToArray()))
            {
                record(span);
            }

            return Results.Bytes([], "application/x-protobuf");
        });

        // Metrics and logs are accepted and dropped, so the services do not log failed exports.
        _ = app.MapPost("/v1/metrics", AcceptExport);
        _ = app.MapPost("/v1/logs", AcceptExport);

        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The trace receiver has no address.");

        return new TraceReceiver(app, new Uri(address));

        static IResult AcceptExport() => Results.Bytes([], "application/x-protobuf");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
