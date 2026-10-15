using System.Diagnostics;
using System.Net.Http.Json;
using Architecture.Testing;

namespace Checkout.Tests;

[TestClass]
public sealed class HostingTests
{
    [TestMethod]
    public async Task Selected_service_runs_in_its_own_process_and_disposal_stops_it()
    {
        var system = await RunningSystem.StartAsync(new MonolithAttribute(),
            [new ServiceAttribute<Payments.Api.EntryPoint>("payments-api")]);
        Process? child = null;
        try
        {
            await using (system)
            {
                using var client = system.Client<Payments.Api.EntryPoint>();
                var processId = await client.GetFromJsonAsync<int>("/health");
                child = Process.GetProcessById(processId);

                Assert.AreNotEqual(Environment.ProcessId, processId);
                CollectionAssert.AreEqual(new[] { "payments-api" }, system.Bricks.ToArray());
                Assert.ThrowsExactly<ArchitectureException>(system.Client<Orders.Api.EntryPoint>);
            }

            Assert.IsTrue(child.HasExited);
            Assert.ThrowsExactly<ObjectDisposedException>(system.Client<Payments.Api.EntryPoint>);
        }
        finally
        {
            child?.Dispose();
        }
    }

    [TestMethod]
    public async Task Missing_dependency_reports_the_child_startup_error()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(async () =>
        {
            await using var system = await RunningSystem.StartAsync(new MonolithAttribute(),
                [new ServiceAttribute<Orders.Api.EntryPoint>("orders-api")]);
        });

        StringAssert.Contains(failure.Message, "exited with code");
        StringAssert.Contains(failure.Message, "No address for the payments service was configured.");
    }
}
