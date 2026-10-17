using Qyl.Orchestration;

namespace Architecture.Testing;

/// <summary>The system a test class runs against: separate service processes coupled by the architecture attribute.</summary>
public sealed class RunningSystem : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);
    private readonly ArchitectureRun _run;
    private readonly ArchitectureAttribute _architecture;
    private readonly IReadOnlyList<BrickAttribute> _services;
    private readonly QylApplication _application;
    private bool _disposed;

    private RunningSystem(ArchitectureRun run, ArchitectureAttribute architecture, IReadOnlyList<BrickAttribute> services, QylApplication application)
    {
        _run = run;
        _architecture = architecture;
        Kind = architecture.Kind;
        Bricks = [.. services.Select(service => service.Name)];
        _services = services;
        _application = application;
    }

    /// <summary>The architecture the test class claims.</summary>
    public string Kind { get; }

    /// <summary>The names of the running bricks.</summary>
    public IReadOnlyList<string> Bricks { get; }
    internal IReadOnlyDictionary<string, Uri> Endpoints => _services.ToDictionary(
        service => service.Name, service => _application.GetEndpoint(service.Name), StringComparer.Ordinal);

    /// <summary>The qyl platform query client for the current test run.</summary>
    public QylTelemetry Telemetry
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _run.Recorder.Collector.Telemetry;
        }
    }

    /// <summary>Waits for every declared brick's store evidence and observed HTTP call pairs.</summary>
    public Task<TestTrace> TraceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CurrentTest.SettleAsync(cancellationToken);
    }

    /// <summary>Waits for explicit invocation evidence, including any required logs, events or metrics.</summary>
    /// <param name="until">The evidence that must be present; metric predicates must select their producer and measurement window.</param>
    /// <param name="cancellationToken">Cancels the evidence wait.</param>
    public Task<TestTrace> TraceAsync(Func<TestTrace, bool> until, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(until);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CurrentTest.SettleAsync(until, cancellationToken);
    }

    internal ArchitectureVerdict Verify(TestTrace trace) => _architecture.Verify(trace, Bricks);

    private TestScope CurrentTest
    {
        get
        {
            var scope = TestScope.Current;
            return scope.Running == this ? scope : throw new InvalidOperationException("This system does not own the current test scope.");
        }
    }

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

    internal TestScope Enter(string testName) => new(this, _run.Recorder, testName);

    internal static async Task<RunningSystem> StartAsync(ArchitectureRun run, ArchitectureAttribute architecture, IReadOnlyList<BrickAttribute> bricks,
        CancellationToken cancellationToken)
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

        await run.Recorder.StartAsync(cancellationToken).ConfigureAwait(false);
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
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", run.Recorder.ReceiverAddress.ToString())
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
            .WithEnvironment("OTEL_TRACES_SAMPLER", "always_on")
            .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "200")
            .WithEnvironment("OTEL_BLRP_SCHEDULE_DELAY", "200")
            .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "200")
            .WithEnvironment("Logging__LogLevel__Default", "Information")
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
            try { await application.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("Service startup and cleanup both failed.", exception, cleanup); }
            if (exception is InvalidOperationException)
            {
                throw new ArchitectureException(exception.Message, exception);
            }

            // Forward the original failed task after cleanup, preserving its exception and stack.
            return await startup.ConfigureAwait(false);
        }

        async Task<RunningSystem> StartApplicationAsync()
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return new RunningSystem(run, architecture, bricks, application);
        }
    }
}

/// <summary>The way a test reaches the system its class runs against.</summary>
public static class SystemUnderTest
{
    /// <summary>The system of the test that is running.</summary>
    public static RunningSystem Current => TestScope.Current.Running;
}
