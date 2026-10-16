using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Architecture.Testing;

/// <summary>Records exported spans for architecture checks and forwards every OTLP signal to qyl.</summary>
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

    public static async Task<TraceReceiver> StartAsync(Action<Span> record,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> export)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        string[] routes = ["/v1/traces", "/v1/metrics", "/v1/logs"];
        foreach (var route in routes)
        {
            _ = app.MapPost(route, async (HttpRequest request, CancellationToken cancellationToken) =>
            {
                using var body = new MemoryStream();
                await request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
                var payload = body.ToArray();
                if (route == "/v1/traces")
                {
                    foreach (var span in OtlpTraces.Decode(payload))
                    {
                        record(span);
                    }
                }

                await export(route, payload, cancellationToken).ConfigureAwait(false);
                return Results.Bytes([], "application/x-protobuf");
            });
        }

        await app.StartAsync().ConfigureAwait(false);
        var address = BoundAddress(app.Services.GetRequiredService<IServer>().Features);
        return new TraceReceiver(app, address);
    }

    internal static Uri BoundAddress(IFeatureCollection features)
    {
        var address = features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The trace receiver has no address.");
        return new Uri(address);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
