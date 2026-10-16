using System.Collections.Concurrent;
using System.Diagnostics;

namespace Architecture.Testing;

/// <summary>
/// Collects the spans of every test: the test process's own through an activity listener,
/// the services' through the trace receiver. Needs no entry point of its own.
/// </summary>
internal static class SpanRecorder
{
    private const string NoParent = "0000000000000000";
    private static readonly ActivitySource Source = new("Architecture.Testing");
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<Span>> Traces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> SpansPerSource = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ActivityListener? _listener;
    private static TraceReceiver? _receiver;
    private static QylCollector? _collector;

    /// <summary>How many spans each source produced in this process, inside a test or not.</summary>
    public static IReadOnlyDictionary<string, int> Sources => SpansPerSource;

    /// <summary>The address the services export their spans to.</summary>
    public static Uri ReceiverAddress =>
        _receiver?.Address ?? throw new InvalidOperationException("The trace receiver is not running.");

    public static QylCollector Collector =>
        _collector ?? throw new InvalidOperationException("The qyl collector is not running.");

    public static async Task StartAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listener is null)
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = _ => true,
                    Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = Record,
                };
                ActivitySource.AddActivityListener(_listener);
            }

            _collector ??= await QylCollector.StartAsync(Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll")).ConfigureAwait(false);
            _receiver ??= await TraceReceiver.StartAsync(Record, _collector.Telemetry.ExportAsync).ConfigureAwait(false);
        }
        finally
        {
            _ = Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_receiver is not null)
            {
                await _receiver.DisposeAsync().ConfigureAwait(false);
                _receiver = null;
            }

            _listener?.Dispose();
            _listener = null;
            if (_collector is not null)
            {
                await _collector.DisposeAsync().ConfigureAwait(false);
                _collector = null;
            }
        }
        finally
        {
            _ = Gate.Release();
        }
    }

    public static Activity Begin(string testName)
    {
        var root = Source.StartActivity($"test {testName}", ActivityKind.Internal, parentContext: default)
            ?? throw new InvalidOperationException("The span recorder is not listening.");
        Traces[root.TraceId.ToHexString()] = new ConcurrentQueue<Span>();
        return root;
    }

    public static IReadOnlyList<Span> SpansOf(string traceId) =>
        Traces.TryGetValue(traceId, out var spans) ? [.. spans] : [];

    public static void End(Activity root)
    {
        root.Stop();
        _ = Traces.TryRemove(root.TraceId.ToHexString(), out _);
    }

    private static void Record(Span span)
    {
        _ = SpansPerSource.AddOrUpdate(span.Source, 1, (_, count) => count + 1);
        if (Traces.TryGetValue(span.TraceId, out var spans))
        {
            spans.Enqueue(span);
        }
    }

    private static void Record(Activity activity)
    {
        var parent = activity.ParentSpanId.ToHexString();
        var attributes = activity.TagObjects
            .Where(tag => tag.Value is not null)
            .ToDictionary(tag => tag.Key, tag => tag.Value!.ToString() ?? "", StringComparer.Ordinal);

        Record(new Span(
            Span.TestProcess,
            activity.Source.Name,
            activity.DisplayName,
            activity.Kind,
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            parent == NoParent ? null : parent,
            attributes));
    }
}

/// <summary>The time one test spends inside the running system, and the trace it leaves behind.</summary>
internal sealed class TestScope : IDisposable
{
    private static readonly TimeSpan QuietFor = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan WaitAtMost = TimeSpan.FromSeconds(10);
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

    private IReadOnlyList<Span> Spans => SpanRecorder.SpansOf(TraceId);

    /// <summary>Waits until the services have exported what the test caused, then returns the whole trace.</summary>
    public async Task<TestTrace> SettleAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var quietSince = Stopwatch.StartNew();
        var seen = Spans.Count;
        while (started.Elapsed < WaitAtMost && quietSince.Elapsed < QuietFor)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            var count = Spans.Count;
            if (count != seen)
            {
                seen = count;
                quietSince.Restart();
            }
        }

        return new TestTrace(TraceId, Spans);
    }

    public void Dispose()
    {
        SpanRecorder.End(_root);
        _leave();
    }
}
