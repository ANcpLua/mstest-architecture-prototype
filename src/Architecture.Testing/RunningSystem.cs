using System.Collections.Concurrent;

namespace Architecture.Testing;

/// <summary>The system a test class runs against: separate service processes coupled by the architecture attribute.</summary>
public sealed class RunningSystem : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<Type, Lazy<Task<RunningSystem>>> Systems = new();
    private static readonly AsyncLocal<TestScope?> Test = new();
    private readonly IReadOnlyList<ServiceProcess> _services;
    private bool _disposed;

    private RunningSystem(string kind, IReadOnlyList<ServiceProcess> services)
    {
        Kind = kind;
        Bricks = services.Select(service => service.Name).ToArray();
        _services = services;
    }

    /// <summary>The architecture the test class claims.</summary>
    public string Kind { get; }

    /// <summary>The names of the running bricks.</summary>
    public IReadOnlyList<string> Bricks { get; }

    /// <summary>The spans the running test has caused so far, as far as they have arrived.</summary>
    public IReadOnlyList<Span> Spans => CurrentTest.Spans;

    /// <summary>Waits until the services have exported what the test caused so far, then returns the trace.</summary>
    public Task<TestTrace> TraceAsync() => CurrentTest.SettleAsync();

    internal static RunningSystem Current => CurrentTest.Running;

    private static TestScope CurrentTest =>
        Test.Value ?? throw new InvalidOperationException("No test of a class with an architecture attribute is running.");

    /// <summary>A client that talks to one brick.</summary>
    /// <typeparam name="TService">The service type the brick was stacked with.</typeparam>
    public HttpClient Client<TService>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var service = _services.SingleOrDefault(service => service.Part == typeof(TService))
            ?? throw new ArchitectureException($"{typeof(TService).FullName} is not a brick of this test class.");
        return new HttpClient { BaseAddress = service.Address };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await Task.WhenAll(_services.Select(service => service.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    internal static Task<RunningSystem> GetOrStartAsync(
        Type testClass,
        ArchitectureAttribute architecture,
        IReadOnlyList<BrickAttribute> bricks) =>
        Systems.GetOrAdd(testClass, _ => new Lazy<Task<RunningSystem>>(() => StartAsync(architecture, bricks))).Value;

    internal static async Task StopAllAsync()
    {
        var systems = Systems.Values.Where(system => system.IsValueCreated).Select(system => system.Value).ToArray();
        Systems.Clear();
        await Task.WhenAll(systems.Select(async pending =>
        {
            RunningSystem system;
            try
            {
                system = await pending.ConfigureAwait(false);
            }
            catch
            {
                // StartAsync already disposed every process of a failed startup.
                return;
            }

            await system.DisposeAsync().ConfigureAwait(false);
        })).ConfigureAwait(false);
    }

    internal TestScope Enter(string testName)
    {
        var scope = new TestScope(this, testName, () => Test.Value = null);
        Test.Value = scope;
        return scope;
    }

    internal static async Task<RunningSystem> StartAsync(ArchitectureAttribute architecture, IReadOnlyList<BrickAttribute> bricks)
    {
        if (architecture.Check(bricks) is { } problem)
            throw new ArchitectureException(problem);
        if (bricks.Any(brick => string.IsNullOrWhiteSpace(brick.Name) || brick.Name.Contains(':', StringComparison.Ordinal)))
            throw new ArchitectureException("Every brick needs a nonempty service name without a colon.");
        if (bricks.Select(brick => brick.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bricks.Count
            || bricks.Select(brick => brick.Part).Distinct().Count() != bricks.Count)
            throw new ArchitectureException("Each service name and service type must identify exactly one brick.");

        using var timeout = new CancellationTokenSource(StartTimeout);
        await SpanRecorder.StartAsync().ConfigureAwait(false);
        var services = new List<ServiceProcess>();
        try
        {
            // Reserve every endpoint before starting children, so all service addresses are known.
            foreach (var brick in bricks)
                services.Add(new ServiceProcess(brick));

            var addresses = services.ToDictionary(service => service.Name, service => service.Address, StringComparer.Ordinal);
            foreach (var service in services)
                service.Start(architecture.StoreOf(service.Name), addresses, SpanRecorder.ReceiverAddress);
            foreach (var service in services)
                await service.WaitForReadyAsync(timeout.Token).ConfigureAwait(false);

            return new RunningSystem(architecture.Kind, services);
        }
        catch
        {
            await Task.WhenAll(services.Select(service => service.DisposeAsync().AsTask())).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>The way a test reaches the system its class runs against.</summary>
public static class SystemUnderTest
{
    /// <summary>The system of the test that is running.</summary>
    public static RunningSystem Current => RunningSystem.Current;
}
