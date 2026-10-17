using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Qyl;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Names;
using Qyl.Telemetry.Testing;
using SpanKind = Qyl.Telemetry.SemanticConventions.SpanKind;

namespace Architecture.Testing;

/// <summary>Owns the canonical OTLP capture, the published Collector and the test process's SDK.</summary>
internal static class SpanRecorder
{
    public const string TestProcess = "test";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentQueue<string> Evidence = new();
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static TelemetrySnapshot _lastCapture = new([], [], [], []);
    private static OtlpReceiver? _capture;
    private static TraceReceiver? _receiver;
    private static QylCollector? _collector;
    private static TracerProvider? _provider;

    public static TelemetrySnapshot Capture => _capture?.Snapshot() ?? _lastCapture;
    public static IReadOnlyList<string> EvidenceFiles => [.. Evidence];
    public static IReadOnlyDictionary<string, int> Sources => Capture.Spans
        .GroupBy(span => span.Scope?.Name ?? "<unnamed>", StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    public static Uri ReceiverAddress => _receiver?.Address
        ?? throw new InvalidOperationException("The trace receiver is not running.");
    public static QylCollector Collector => _collector
        ?? throw new InvalidOperationException("The qyl collector is not running.");

    public static async Task StartAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_provider is not null)
            {
                return;
            }

            try
            {
                _collector = await QylCollector.StartAsync(Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll")).ConfigureAwait(false);
                _capture = await OtlpReceiver.StartAsync().ConfigureAwait(false);
                _receiver = await TraceReceiver.StartAsync(_capture, _collector.Telemetry).ConfigureAwait(false);
                _provider = Sdk.CreateTracerProviderBuilder()
                    .SetSampler(new AlwaysOnSampler())
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(TestProcess,
                        serviceVersion: typeof(SpanRecorder).Assembly.GetName().Version!.ToString(3)))
                    .AddSource(QylTelemetryNames.Scopes.QylTelemetryApplication, "System.Net.Http")
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(_receiver.Address, "/v1/traces");
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                        options.Compression = OtlpExportCompression.GZip;
                        options.Headers = "";
                        options.TimeoutMilliseconds = 10_000;
                        options.ExportProcessorType = ExportProcessorType.Simple;
                    })
                    .Build();
            }
            catch (Exception startup)
            {
                try { await StopCoreAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException("Telemetry startup and cleanup both failed.", startup, cleanup); }
                throw;
            }
        }
        finally
        {
            _ = Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _ = Gate.Release(); }
    }

    private static async Task StopCoreAsync()
    {
        var provider = _provider; _provider = null;
        var receiver = _receiver; _receiver = null;
        var capture = _capture; _capture = null;
        var collector = _collector; _collector = null;
        try { provider?.Dispose(); }
        finally
        {
            try
            {
                if (receiver is not null)
                {
                    await receiver.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (capture is not null)
                    {
                        _lastCapture = capture.Snapshot();
                        if (collector is not null)
                        {
                            var path = Path.Combine(collector.DataDirectory, "capture.json");
                            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                            {
                                Capture = _lastCapture,
                                receiver?.Deliveries
                            }, EvidenceJson)).ConfigureAwait(false);
                            Evidence.Enqueue(path);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (capture is not null)
                        {
                            await capture.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        if (collector is not null)
                        {
                            await collector.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
        }
    }

    public static Activity Begin(string testName)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("The span recorder is not listening.");
        }

        var root = Spans.Source.StartActivity("architecture.test", ActivityKind.Internal,
            new ActivityContext(ActivityTraceId.CreateRandom(), default, ActivityTraceFlags.Recorded))
            ?? throw new InvalidOperationException("The span recorder is not listening.");
        _ = root.SetTag("test.name", testName);
        return root;
    }

    public static IReadOnlyList<SpanRecord> SpansOf(string traceId) =>
        [.. Capture.Spans.Where(span => span.TraceId == traceId)];

    public static Task<TelemetrySnapshot> AwaitAsync(Func<TelemetrySnapshot, bool> until,
        CancellationToken cancellationToken) =>
        (_capture ?? throw new InvalidOperationException("The OTLP capture is not running."))
        .AwaitAsync(until, TimeSpan.FromSeconds(10), cancellationToken);

    public static void Flush()
    {
        if (_provider?.ForceFlush(10_000) != true)
        {
            throw new InvalidOperationException("The test process could not flush its spans to the receiver.");
        }
    }

    public static void End(Activity root)
    {
        root.Stop();
        Flush();
    }
}

/// <summary>One invocation's root and its evidence from the running system.</summary>
internal sealed class TestScope : IDisposable
{
    private readonly Activity _root;
    private readonly Action _leave;

    public TestScope(RunningSystem running, string testName, Action leave)
    {
        Running = running;
        _leave = leave;
        _root = SpanRecorder.Begin(testName);
        TraceId = _root.TraceId.ToHexString();
    }

    public RunningSystem Running { get; }
    private string TraceId { get; }

    public async Task<TestTrace> SettleAsync(CancellationToken cancellationToken = default)
    {
        SpanRecorder.Flush();
        var capture = await SpanRecorder.AwaitAsync(Complete, cancellationToken).ConfigureAwait(false);
        return new TestTrace(TraceId, capture);
    }

    private bool Complete(TelemetrySnapshot snapshot)
    {
        var spans = snapshot.Spans.Where(span => span.TraceId == TraceId).ToArray();
        foreach (var client in spans.Where(span => span.Kind == SpanKind.Client))
        {
            if (!Uri.TryCreate(client.GetString("url.full"), UriKind.Absolute, out var url))
            {
                continue;
            }

            var target = Running.Endpoints.FirstOrDefault(endpoint => Uri.Compare(endpoint.Value, url,
                UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0);
            if (target.Key is not null && !spans.Any(server => server.Kind == SpanKind.Server
                    && server.ParentSpanId == client.SpanId && server.Service == target.Key))
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        try { SpanRecorder.End(_root); }
        finally { _leave(); }
    }
}
