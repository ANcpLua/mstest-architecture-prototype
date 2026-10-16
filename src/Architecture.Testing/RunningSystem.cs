using System.Collections.Concurrent;
using Qyl.Orchestration;

namespace Architecture.Testing;

/// <summary>The system a test class runs against: separate service processes coupled by the architecture attribute.</summary>
public sealed class RunningSystem : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<Type, Lazy<Task<RunningSystem>>> Systems = new();
    private static readonly AsyncLocal<TestScope?> Test = new();
    private readonly IReadOnlyList<BrickAttribute> _services;
    private readonly QylApplication _application;
    private bool _disposed;

    private RunningSystem(string kind, IReadOnlyList<BrickAttribute> services, QylApplication application)
    {
        Kind = kind;
        Bricks = [.. services.Select(service => service.Name)];
        _services = services;
        _application = application;
    }

    /// <summary>The architecture the test class claims.</summary>
    public string Kind { get; }

    /// <summary>The names of the running bricks.</summary>
    public IReadOnlyList<string> Bricks { get; }

    /// <summary>Waits until the services have exported what the test caused so far, then returns the trace.</summary>
    public Task<TestTrace> TraceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CurrentTest.SettleAsync(cancellationToken);
    }

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
        return new HttpClient { BaseAddress = _application.GetEndpoint(service.Name) };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _application.DisposeAsync().ConfigureAwait(false);
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

    internal static async Task<RunningSystem> StartAsync(ArchitectureAttribute architecture, IReadOnlyList<BrickAttribute> bricks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (architecture.Check(bricks) is { } problem)
        {
            throw new ArchitectureException(problem);
        }

        if (bricks.Any(brick => string.IsNullOrWhiteSpace(brick.Name) || brick.Name.Contains(':', StringComparison.Ordinal)))
        {
            throw new ArchitectureException("Every brick needs a nonempty service name without a colon.");
        }

        if (bricks.Select(brick => brick.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bricks.Count
            || bricks.Select(brick => brick.Part).Distinct().Count() != bricks.Count)
        {
            throw new ArchitectureException("Each service name and service type must identify exactly one brick.");
        }

        await SpanRecorder.StartAsync().ConfigureAwait(false);
        var builder = new QylApplicationBuilder
        {
            StartupTimeoutSeconds = (int)StartTimeout.TotalSeconds,
            MaxRestarts = 0
        };
        var services = bricks.Select(brick => builder.AddDotnetService(brick.Name, brick.Part.Assembly.Location)
            .WithoutEnvironmentPrefix("OTEL_")
            .WithoutEnvironmentPrefix("QYL_")
            .WithoutEnvironmentPrefix("services__")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("STORE", architecture.StoreOf(brick.Name))
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", SpanRecorder.ReceiverAddress.ToString())
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithEnvironment("OTEL_TRACES_SAMPLER", "always_on")
            .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200")
            .WithEnvironment("Logging__LogLevel__Default", "Warning")
            .WithHealthCheck(requireProcessId: true)).ToArray();
        foreach (var service in services)
        {
            foreach (var reference in services.Where(reference => reference != service))
            {
                _ = service.WithReference(reference);
            }
        }

        var application = builder.Build();
        var startup = StartApplicationAsync();
        try
        {
            return await startup.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await application.DisposeAsync().ConfigureAwait(false);
            if (exception is InvalidOperationException)
            {
                throw new ArchitectureException(exception.Message);
            }

            // Forward the original failed task after cleanup, preserving its exception and stack.
            return await startup.ConfigureAwait(false);
        }

        async Task<RunningSystem> StartApplicationAsync()
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return new RunningSystem(architecture.Kind, bricks, application);
        }
    }
}

/// <summary>The way a test reaches the system its class runs against.</summary>
public static class SystemUnderTest
{
    /// <summary>The system of the test that is running.</summary>
    public static RunningSystem Current => RunningSystem.Current;
}
