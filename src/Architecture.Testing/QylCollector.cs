using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Architecture.Testing;

/// <summary>Owns an isolated instance of the published qyl collector for a test run.</summary>
internal sealed class QylCollector : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly Task<string> _output;
    private readonly Task<string> _error;
    private readonly string _directory;
    private bool _disposed;

    private QylCollector(Process process, Uri address, string directory)
    {
        _process = process;
        Address = address;
        _directory = directory;
        _client = new HttpClient { BaseAddress = address };
        _output = process.StandardOutput.ReadToEndAsync();
        _error = process.StandardError.ReadToEndAsync();
        Telemetry = new QylTelemetry(_client);
    }

    public Uri Address { get; }
    public QylTelemetry Telemetry { get; }

    public static async Task<QylCollector> StartAsync(string assembly, CancellationToken cancellationToken = default)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        var directory = Path.Combine(AppContext.BaseDirectory, "qyl-data", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { assembly },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("QYL_", StringComparison.Ordinal)
                     || key.StartsWith("OTEL_", StringComparison.Ordinal)).ToArray())
        {
            _ = start.Environment.Remove(key);
        }

        start.Environment["QYL_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        start.Environment["QYL_OTLP_PORT"] = "0";
        start.Environment["QYL_GRPC_PORT"] = "0";
        start.Environment["QYL_BIND_ADDRESS"] = "127.0.0.1";
        start.Environment["QYL_OTLP_AUTH_MODE"] = "Unsecured";
        start.Environment["QYL_DATA_PATH"] = Path.Combine(directory, "telemetry.duckdb");
        start.Environment["QYL_DB_THREADS"] = "2";
        start.Environment["OTEL_TRACES_EXPORTER"] = "none";
        start.Environment["OTEL_METRICS_EXPORTER"] = "none";
        start.Environment["OTEL_LOGS_EXPORTER"] = "none";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        reservation.Stop();
        var process = Process.Start(start)!;
        var collector = new QylCollector(process, new Uri($"http://127.0.0.1:{port}/"), directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var startup = WaitUntilReadyAsync(deadline.Token);
        try
        {
            return await startup.ConfigureAwait(false);
        }
        catch (Exception)
        {
            await collector.DisposeAsync().ConfigureAwait(false);
            return await startup.ConfigureAwait(false);
        }

        async Task<QylCollector> WaitUntilReadyAsync(CancellationToken token)
        {
            while (true)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"qyl exited with code {process.ExitCode}: {await collector._output.ConfigureAwait(false)}{await collector._error.ConfigureAwait(false)}");
                }

                try
                {
                    using var response = await collector._client.GetAsync("health", token).ConfigureAwait(false);
                    _ = response.EnsureSuccessStatusCode();
                    return collector;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_directory, "collector.log"),
            await _output.ConfigureAwait(false) + await _error.ConfigureAwait(false)).ConfigureAwait(false);
        _client.Dispose();
        _process.Dispose();
    }
}
