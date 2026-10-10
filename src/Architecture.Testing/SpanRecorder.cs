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
    private static ActivityListener? listener;
    private static TraceReceiver? receiver;

    /// <summary>How many spans each source produced in this process, inside a test or not.</summary>
    public static IReadOnlyDictionary<string, int> Sources => SpansPerSource;

    /// <summary>The address the services export their spans to.</summary>
    public static Uri ReceiverAddress =>
        receiver?.Address ?? throw new InvalidOperationException("The trace receiver is not running.");

    public static async Task StartAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (listener is null)
            {
                listener = new ActivityListener
                {
                    ShouldListenTo = _ => true,
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = Record,
                };
                ActivitySource.AddActivityListener(listener);
            }

            receiver ??= await TraceReceiver.StartAsync(Record).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (receiver is not null)
            {
                await receiver.DisposeAsync().ConfigureAwait(false);
                receiver = null;
            }

            listener?.Dispose();
            listener = null;
        }
        finally
        {
            Gate.Release();
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
        Traces.TryRemove(root.TraceId.ToHexString(), out _);
    }

    public static void Record(Span span)
    {
        SpansPerSource.AddOrUpdate(span.Source, 1, (_, count) => count + 1);
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

    public string TraceId { get; }

    public IReadOnlyList<Span> Spans => SpanRecorder.SpansOf(TraceId);

    /// <summary>Waits until the services have exported what the test caused, then returns the whole trace.</summary>
    public async Task<TestTrace> SettleAsync()
    {
        var started = Stopwatch.StartNew();
        var quietSince = Stopwatch.StartNew();
        var seen = Spans.Count;
        while (started.Elapsed < WaitAtMost && quietSince.Elapsed < QuietFor)
        {
            await Task.Delay(100).ConfigureAwait(false);
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
