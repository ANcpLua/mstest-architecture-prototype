using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Architecture.Testing;

/// <summary>Owns one real service process, its loopback endpoint, readiness and bounded startup diagnostics.</summary>
internal sealed class ServiceProcess : IAsyncDisposable
{
    private readonly TcpListener _reservation;
    private readonly Process _process = new();
    private readonly ConcurrentQueue<string> _output = new();
    private Task _stdout = Task.CompletedTask;
    private Task _stderr = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    public ServiceProcess(BrickAttribute brick)
    {
        Part = brick.Part;
        Name = brick.Name;
        var assembly = Part.Assembly.Location;
        if (Part.Assembly.EntryPoint is null || !File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
            throw new ArchitectureException($"{Part.FullName} must belong to a referenced executable with its runtimeconfig.json beside it.");

        _reservation = new TcpListener(IPAddress.Loopback, 0);
        _reservation.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_reservation.LocalEndpoint).Port}");
    }

    public Type Part { get; }
    public string Name { get; }
    public Uri Address { get; }

    public void Start(string store, IReadOnlyDictionary<string, Uri> addresses, Uri receiver)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = Path.GetDirectoryName(Part.Assembly.Location)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Part.Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(Address.ToString());

        // Each test system owns its telemetry destination and discovery; ambient settings must
        // neither send a test to another collector nor supply an unstacked service's address.
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("QYL_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("services__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);

        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["STORE"] = store;
        start.Environment["OTEL_SERVICE_NAME"] = Name;
        start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = receiver.ToString();
        start.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        start.Environment["OTEL_TRACES_SAMPLER"] = "always_on";
        start.Environment["OTEL_BSP_SCHEDULE_DELAY"] = "200";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (name, address) in addresses)
            start.Environment[$"services__{name}__http__0"] = address.ToString();

        _process.StartInfo = start;
        _reservation.Stop();
        _started = _process.Start();
        if (!_started)
            throw Failure("could not start");
        _stdout = CaptureAsync(_process.StandardOutput);
        _stderr = CaptureAsync(_process.StandardError);
    }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
                    throw Failure($"exited with code {_process.ExitCode} before becoming ready");
                }

                try
                {
                    using var response = await client.GetAsync("/health", cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    // A lost port reservation must not make a different process count as our service.
                    if (response.IsSuccessStatusCode && body == _process.Id.ToString(CultureInfo.InvariantCulture))
                        return;
                }
                catch (HttpRequestException)
                {
                    // The process has not bound its endpoint yet.
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // An individual health request timed out; the startup deadline still applies.
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw Failure("did not become ready before the startup deadline");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _reservation.Stop();
        try
        {
            if (_started)
            {
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                    // The process exited between the check and Kill.
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private ArchitectureException Failure(string message) =>
        new($"{Name} {message} at {Address}.{Environment.NewLine}{string.Join(Environment.NewLine, _output)}");

    private async Task CaptureAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            _output.Enqueue(line.Length > 2048 ? line[..2048] : line);
            while (_output.Count > 100)
                _output.TryDequeue(out _);
        }
    }
}
