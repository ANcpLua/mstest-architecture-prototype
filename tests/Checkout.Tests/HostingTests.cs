using System.Diagnostics;
using System.Net.Http.Json;
using Architecture.Testing;

namespace Checkout.Tests;

/// <summary>Checks service process isolation, startup failures and cleanup.</summary>
/// <param name="testContext">The runner context that supplies cancellation for this test.</param>
[TestClass]
public sealed class HostingTests(TestContext testContext)
{
    /// <summary>Disposing the selected service stops its process and invalidates further clients.</summary>
    [TestMethod]
    public async Task SelectedServiceRunsInItsOwnProcessAndDisposalStopsIt()
    {
        var system = await RunningSystem.StartAsync(new MonolithAttribute(),
            [new ServiceAttribute<Payments.Api.EntryPoint>("payments-api")], testContext.CancellationToken);
        Process? child = null;
        try
        {
            await using (system)
            {
                using var client = system.Client<Payments.Api.EntryPoint>();
                var processId = await client.GetFromJsonAsync<int>("/health", testContext.CancellationToken);
                child = Process.GetProcessById(processId);

                Assert.AreNotEqual(Environment.ProcessId, processId);
                Assert.HasCount(1, system.Bricks);
                Assert.AreEqual("payments-api", system.Bricks[0]);
                _ = Assert.ThrowsExactly<ArchitectureException>(system.Client<Orders.Api.EntryPoint>);
            }

            Assert.IsTrue(child.HasExited);
            _ = Assert.ThrowsExactly<ObjectDisposedException>(system.Client<Payments.Api.EntryPoint>);
        }
        finally
        {
            child?.Dispose();
        }
    }

    /// <summary>A missing service reference retains the child process's startup diagnostic.</summary>
    [TestMethod]
    public async Task MissingDependencyReportsTheChildStartupError()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(async () =>
        {
            await using var system = await RunningSystem.StartAsync(new MonolithAttribute(),
                [new ServiceAttribute<Orders.Api.EntryPoint>("orders-api")], testContext.CancellationToken);
        });

        Assert.Contains("exited with code", failure.Message);
        Assert.Contains("No address for the payments service was configured.", failure.Message);
    }

    /// <summary>A canceled launch request stops before starting a service.</summary>
    [TestMethod]
    public async Task CancelledStartupIsNotReportedAsAServiceFailure() =>
        _ = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RunningSystem.StartAsync(
            new MonolithAttribute(), [new ServiceAttribute<Payments.Api.EntryPoint>("payments-api")],
            new CancellationToken(canceled: true)));
}
