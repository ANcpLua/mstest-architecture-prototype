using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Architecture.Testing;

/// <summary>The system a test class runs against: its bricks, started by their host and coupled by the architecture attribute.</summary>
public sealed class RunningSystem : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<Type, Lazy<Task<RunningSystem>>> Systems = new();
    private static readonly AsyncLocal<TestScope?> Test = new();

    private readonly DistributedApplication _application;

    private RunningSystem(string kind, IReadOnlyList<string> bricks, DistributedApplication application)
    {
        Kind = kind;
        Bricks = bricks;
        _application = application;
    }

    /// <summary>The architecture the test class claims.</summary>
    public string Kind { get; }

    /// <summary>The names the host gave the running bricks.</summary>
    public IReadOnlyList<string> Bricks { get; }

    /// <summary>The spans the running test has caused so far, as far as they have arrived.</summary>
    public IReadOnlyList<Span> Spans => CurrentTest.Spans;

    /// <summary>Waits until the services have exported what the test caused so far, then returns the trace.</summary>
    public Task<TestTrace> TraceAsync() => CurrentTest.SettleAsync();

    internal static RunningSystem Current => CurrentTest.Running;

    private static TestScope CurrentTest =>
        Test.Value ?? throw new InvalidOperationException("No test of a class with an architecture attribute is running.");

    /// <summary>A client that talks to one brick.</summary>
    /// <typeparam name="TProject">The project the brick was stacked with.</typeparam>
    public HttpClient Client<TProject>()
        where TProject : IProjectMetadata, new()
    {
        var model = _application.Services.GetRequiredService<DistributedApplicationModel>();
        var brick = Brick.Find<TProject>(model.Resources)
            ?? throw new ArchitectureException($"{typeof(TProject).Name} is not a brick of this test class.");

        return _application.CreateHttpClient(brick.Name);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _application.DisposeAsync().ConfigureAwait(false);

    internal static Task<RunningSystem> GetOrStartAsync(
        Type testClass,
        ArchitectureAttribute architecture,
        IReadOnlyList<BrickAttribute> bricks) =>
        Systems.GetOrAdd(testClass, _ => new Lazy<Task<RunningSystem>>(() => StartAsync(architecture, bricks))).Value;

    internal static async Task StopAllAsync()
    {
        foreach (var system in Systems.Values.Where(system => system.IsValueCreated))
        {
            await (await system.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }

        Systems.Clear();
    }

    internal TestScope Enter(string testName)
    {
        var scope = new TestScope(this, testName, () => Test.Value = null);
        Test.Value = scope;
        return scope;
    }

    private static async Task<RunningSystem> StartAsync(ArchitectureAttribute architecture, IReadOnlyList<BrickAttribute> bricks)
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        await SpanRecorder.StartAsync().ConfigureAwait(false);

        // A brick is a type from the host's assembly, so the bricks themselves say which host puts the system together.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync(bricks[0].Part, timeout.Token).ConfigureAwait(false);

        var chosen = bricks.Select(brick => brick.FindIn(builder)).ToList();
        foreach (var brick in chosen)
        {
            builder.CreateResourceBuilder(brick)
                .WithEnvironment("STORE", architecture.StoreOf(brick.Name))
                // Every brick exports its spans to the receiver in the test process, promptly.
                .WithEnvironment("OTEL_SERVICE_NAME", brick.Name)
                .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", SpanRecorder.ReceiverAddress.ToString())
                .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
                .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200");
        }

        // Only the stacked bricks run. What the host would start beyond them is left out.
        foreach (var other in builder.Resources.OfType<ProjectResource>().Where(project => !chosen.Contains(project)).ToList())
        {
            builder.Resources.Remove(other);
        }

        var application = await builder.BuildAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await application.StartAsync(timeout.Token).ConfigureAwait(false);
            foreach (var brick in chosen)
            {
                await application.ResourceNotifications
                    .WaitForResourceAsync(brick.Name, KnownResourceStates.Running, timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new RunningSystem(architecture.Kind, [.. chosen.Select(brick => brick.Name)], application);
    }
}

/// <summary>The way a test reaches the system its class runs against.</summary>
public static class SystemUnderTest
{
    /// <summary>The system of the test that is running.</summary>
    public static RunningSystem Current => RunningSystem.Current;
}
