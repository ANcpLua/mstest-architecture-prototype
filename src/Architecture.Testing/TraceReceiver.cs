using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using Qyl.Telemetry.Testing;

namespace Architecture.Testing;

/// <summary>Captures every OTLP export and relays the Collector's actual response to the producer.</summary>
internal sealed class TraceReceiver : IAsyncDisposable
{
    private static readonly HttpClient Transport = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ExportDelivery> _deliveries;

    private TraceReceiver(WebApplication app, Uri address, ConcurrentQueue<ExportDelivery> deliveries)
    {
        _app = app;
        Address = address;
        _deliveries = deliveries;
    }

    public Uri Address { get; }
    public IReadOnlyList<ExportDelivery> Deliveries => [.. _deliveries];

    public static async Task<TraceReceiver> StartAsync(OtlpReceiver capture, QylTelemetry collector,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
        _ = builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
        var app = builder.Build();
        var deliveries = new ConcurrentQueue<ExportDelivery>();
        foreach (var signal in new[] { "traces", "metrics", "logs" })
        {
            _ = app.MapPost($"/v1/{signal}", async (HttpRequest request, HttpResponse response, CancellationToken token) =>
            {
                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer, token).ConfigureAwait(false);
                var payload = buffer.ToArray();
                var encoding = request.Headers.ContentEncoding.ToString();
                // This endpoint is an exporter destination. Its forwarding requests must not create
                // telemetry that exports back into the same destination.
                using var suppression = SuppressInstrumentationScope.Begin();
                using var body = new ByteArrayContent(payload);
                if (request.ContentType is not null)
                {
                    body.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
                }

                if (encoding.Length > 0)
                {
                    body.Headers.ContentEncoding.Add(encoding);
                }

                using var captured = await Transport.PostAsync(new Uri(capture.Endpoint, $"v1/{signal}"), body, token).ConfigureAwait(false);
                var captureReply = await captured.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                using var forwarded = await collector.ExportAsync($"v1/{signal}", payload, request.ContentType, encoding, token).ConfigureAwait(false);
                var collectorReply = await forwarded.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                deliveries.Enqueue(new ExportDelivery(signal, (int)captured.StatusCode, captureReply,
                    (int)forwarded.StatusCode, collectorReply));
                response.StatusCode = (int)forwarded.StatusCode;
                response.ContentType = forwarded.Content.Headers.ContentType?.ToString();
                response.Headers.ContentEncoding = forwarded.Content.Headers.ContentEncoding.ToArray();
                await response.Body.WriteAsync(collectorReply, token).ConfigureAwait(false);
            });
        }

        var transferred = false;
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var receiver = new TraceReceiver(app, BoundAddress(app.Services.GetRequiredService<IServer>().Features), deliveries);
            transferred = true;
            return receiver;
        }
        finally
        {
            if (!transferred)
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static Uri BoundAddress(IFeatureCollection features)
    {
        var address = features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The trace receiver has no address.");
        return new Uri(address);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed record ExportDelivery(string Signal, int CaptureStatus, byte[] CaptureResponse,
    int CollectorStatus, byte[] CollectorResponse);
