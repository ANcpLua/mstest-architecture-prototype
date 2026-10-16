using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Qyl;

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
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> export, Uri? collectorAddress = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (collectorAddress is not null)
        {
            _ = builder.AddQyl(options =>
            {
                options.ServiceName = Span.TestProcess;
                options.CollectorEndpoint = collectorAddress;
            });
        }

        var app = builder.Build();
        string[] routes = ["/v1/traces", "/v1/metrics", "/v1/logs"];
        foreach (var route in routes)
        {
            _ = app.MapPost(route, async (HttpRequest request, HttpResponse response, CancellationToken cancellationToken) =>
            {
                using var body = new MemoryStream();
                await request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
                var payload = body.ToArray();
                var spans = new List<Span>();
                if (route == "/v1/traces")
                {
                    try
                    {
                        spans = OtlpTraces.Decode(payload);
                    }
                    catch (InvalidDataException error)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        return Results.Bytes(ErrorStatus(error.Message), "application/x-protobuf");
                    }
                }

                await export(route, payload, cancellationToken).ConfigureAwait(false);
                foreach (var span in spans)
                {
                    record(span);
                }

                return Results.Bytes([], "application/x-protobuf");
            });
        }

        await app.StartAsync().ConfigureAwait(false);
        var address = BoundAddress(app.Services.GetRequiredService<IServer>().Features);
        return new TraceReceiver(app, address);
    }

    private static byte[] ErrorStatus(string message)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)0x12); // google.rpc.Status.message: field 2, length-delimited.
        writer.Write(message);
        writer.Flush();
        return output.ToArray();
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
