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
internal sealed class SpanRecorder : IAsyncDisposable
{
    public const string TestProcess = "test";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _evidence = new();
    private readonly JsonSerializerOptions _evidenceJson = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private TelemetrySnapshot _lastCapture = new([], [], [], []);
    private OtlpReceiver? _capture;
    private TraceReceiver? _receiver;
    private QylCollector? _collector;
    private TracerProvider? _provider;
    private int _disposed;

    internal TelemetrySnapshot Capture => _capture?.Snapshot() ?? _lastCapture;
    public IReadOnlyList<string> EvidenceFiles => [.. _evidence];
    public IReadOnlyDictionary<string, int> Sources => Capture.Spans
        .GroupBy(span => span.Scope?.Name ?? "<unnamed>", StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    public Uri ReceiverAddress => _receiver?.Address
        ?? throw new InvalidOperationException("The trace receiver is not running.");
    public QylCollector Collector => _collector
        ?? throw new InvalidOperationException("The qyl collector is not running.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_provider is not null)
            {
                return;
            }

            try
            {
                _collector = await QylCollector.StartAsync(Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll"), cancellationToken).ConfigureAwait(false);
                _capture = await OtlpReceiver.StartAsync(cancellationToken).ConfigureAwait(false);
                _receiver = await TraceReceiver.StartAsync(_capture, _collector.Telemetry, cancellationToken).ConfigureAwait(false);
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
            _ = _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _ = _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try { await StopAsync().ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }

    private async Task StopCoreAsync()
    {
        var provider = _provider; _provider = null;
        var receiver = _receiver; _receiver = null;
        var capture = _capture; _capture = null;
        var collector = _collector; _collector = null;
        var errors = new List<Exception>();
        try { provider?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (receiver is not null)
        {
            try { await receiver.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (capture is not null)
        {
            try
            {
                _lastCapture = capture.Snapshot();
                if (collector is not null)
                {
                    var path = Path.Combine(collector.DataDirectory, "capture.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                    {
                        Capture = _lastCapture,
                        receiver?.Deliveries
                    }, _evidenceJson)).ConfigureAwait(false);
                    _evidence.Enqueue(path);
                    var deliveries = receiver?.Deliveries ?? [];
                    if (deliveries.Count != _lastCapture.Requests.Count
                        || deliveries.Any(delivery => delivery.Failure is not null
                            || delivery.CollectorStatus is not (>= 200 and < 300)))
                    {
                        errors.Add(new InvalidDataException($"Not every captured export completed successfully: {_lastCapture.Requests.Count} captured, {deliveries.Count} delivery records. See {path}."));
                    }
                    var sourcesPath = Path.Combine(collector.DataDirectory, "span-sources.txt");
                    var sources = Sources.OrderBy(source => source.Key, StringComparer.Ordinal)
                        .Select(source => $"{source.Value,5}  {source.Key}");
                    await File.WriteAllLinesAsync(sourcesPath, sources).ConfigureAwait(false);
                    _evidence.Enqueue(sourcesPath);
                }
            }
            catch (Exception error) { errors.Add(error); }
            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        }
        if (collector is not null)
        {
            try { await collector.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            foreach (var name in new[] { "shutdown.json", "collector.log" })
            {
                var path = Path.Combine(collector.DataDirectory, name);
                if (File.Exists(path))
                {
                    _evidence.Enqueue(path);
                }
            }
        }
        if (errors.Count > 0)
        {
            throw new AggregateException("Telemetry shutdown failed; all owned resources were given a cleanup attempt.", errors);
        }
    }

    public Activity Begin(string testName)
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

    public IReadOnlyList<SpanRecord> SpansOf(string traceId) =>
        [.. Capture.Spans.Where(span => span.TraceId == traceId)];

    public Task<TelemetrySnapshot> AwaitAsync(Func<TelemetrySnapshot, bool> until,
        CancellationToken cancellationToken) =>
        (_capture ?? throw new InvalidOperationException("The OTLP capture is not running."))
        .AwaitAsync(until, TimeSpan.FromSeconds(10), cancellationToken);

    public void Flush()
    {
        if (_provider?.ForceFlush(10_000) != true)
        {
            throw new InvalidOperationException("The test process could not flush its spans to the receiver.");
        }
    }

    public void End(Activity root)
    {
        root.Stop();
        Flush();
    }
}

/// <summary>One invocation's root and the evidence required by its architecture or caller.</summary>
internal sealed class TestScope : IDisposable
{
    private readonly Activity _root;
    private readonly SpanRecorder _recorder;
    private static readonly AsyncLocal<TestScope?> Ambient = new();
    private readonly TestScope? _previous;
    private bool _disposed;

    public TestScope(RunningSystem running, SpanRecorder recorder, string testName)
    {
        Running = running;
        _recorder = recorder;
        _root = recorder.Begin(testName);
        TraceId = _root.TraceId.ToHexString();
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    public static TestScope Current
    {
        get
        {
            var scope = Ambient.Value
                ?? throw new InvalidOperationException("No test of a class with an architecture attribute is running.");
            ObjectDisposedException.ThrowIf(scope._disposed, scope);
            return scope;
        }
    }

    public RunningSystem Running { get; }
    private string TraceId { get; }

    public TestTrace Snapshot() => new(TraceId, _recorder.Capture);

    public Task<TestTrace> SettleAsync(CancellationToken cancellationToken = default) =>
        SettleAsync(trace => Running.Verify(trace).Outcome != ArchitectureOutcome.InsufficientEvidence, cancellationToken);

    public async Task<TestTrace> SettleAsync(Func<TestTrace, bool> until, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.Flush();
        try
        {
            var capture = await _recorder.AwaitAsync(snapshot =>
            {
                var trace = new TestTrace(TraceId, snapshot);
                return CallsAreComplete(trace) && until(trace);
            }, cancellationToken).ConfigureAwait(false);
            return new TestTrace(TraceId, capture);
        }
        catch (TimeoutException error)
        {
            var trace = new TestTrace(TraceId, _recorder.Capture);
            throw new ArchitectureException($"Timed out waiting for invocation evidence. {Running.Verify(trace).Message}\n{trace.Summary()}", error);
        }
    }

    private bool CallsAreComplete(TestTrace trace)
    {
        foreach (var client in trace.Spans.Where(span => span.Kind == SpanKind.Client))
        {
            if (!Uri.TryCreate(client.GetString("url.full"), UriKind.Absolute, out var url))
            {
                continue;
            }

            var target = Running.Endpoints.FirstOrDefault(endpoint => Uri.Compare(endpoint.Value, url,
                UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0);
            if (target.Key is not null && !trace.Spans.Any(server => server.Kind == SpanKind.Server
                    && server.ParentSpanId == client.SpanId && server.Service == target.Key))
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _recorder.End(_root); }
        finally { Ambient.Value = _previous; }
    }
}
