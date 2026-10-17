using System.Net;
using System.Text.Json;
using Architecture.Testing;

namespace Checkout.Tests;

/// <summary>Checks ownership and isolation of the actual packaged qyl collector process.</summary>
/// <param name="testContext">The runner owns cancellation.</param>
[TestClass]
public sealed class QylCollectorTests(TestContext testContext)
{
    private static readonly HttpClient Client = new();
    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "qyl", "collector", "qyl.collector.dll");

    /// <summary>Ambient qyl and OTLP settings cannot redirect the isolated collector.</summary>
    [TestMethod]
    public async Task CollectorHasItsOwnEndpointAndCanBeDisposedTwice()
    {
        const string portSetting = "QYL_PORT";
        const string endpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var previousPort = Environment.GetEnvironmentVariable(portSetting);
        var previousEndpoint = Environment.GetEnvironmentVariable(endpointSetting);
        try
        {
            Environment.SetEnvironmentVariable(portSetting, "invalid");
            Environment.SetEnvironmentVariable(endpointSetting, "invalid");
            var collector = await QylCollector.StartAsync(AssemblyPath, testContext.CancellationToken);
            await using (collector)
            {
                using var health = await Client.GetAsync(new Uri(collector.Address, "health"), testContext.CancellationToken);
                Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
                Assert.IsEmpty(await collector.Telemetry.LogsAsync("00000000000000000000000000000001", testContext.CancellationToken));
            }

            await collector.DisposeAsync();
            if (!OperatingSystem.IsWindows())
            {
                using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(collector.DataDirectory, "shutdown.json"), testContext.CancellationToken));
                Assert.AreEqual(0, shutdown.RootElement.GetProperty("ExitCode").GetInt32());
                Assert.IsFalse(shutdown.RootElement.GetProperty("Forced").GetBoolean());
            }
            _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
                collector.Telemetry.LogsAsync("trace", testContext.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(portSetting, previousPort);
            Environment.SetEnvironmentVariable(endpointSetting, previousEndpoint);
        }
    }

    /// <summary>An early process exit reports its original output and is reaped.</summary>
    [TestMethod]
    public async Task MissingCollectorAssemblyReportsItsExit()
    {
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            QylCollector.StartAsync(Path.Combine(AppContext.BaseDirectory, "missing-qyl-collector.dll"), testContext.CancellationToken));
        Assert.Contains("qyl exited with code", failure.Message);
        Assert.Contains("missing-qyl-collector.dll", failure.Message);
    }

    /// <summary>Cancelling startup terminates and joins the process instead of leaking it.</summary>
    [TestMethod]
    public async Task CollectorStartupHonoursCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => QylCollector.StartAsync(AssemblyPath, cancellation.Token));
    }
}
