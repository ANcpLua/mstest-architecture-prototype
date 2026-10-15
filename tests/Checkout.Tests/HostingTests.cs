using System.Diagnostics;
using System.Net.Http.Json;
using Architecture.Testing;
using OrdersApi = Orders.Api.EntryPoint;
using PaymentsApi = Payments.Api.EntryPoint;

namespace Checkout.Tests;

[TestClass]
public sealed class HostingTests
{
    [TestMethod]
    public async Task Selected_service_runs_in_its_own_process_and_disposal_stops_it()
    {
        await using var system = await RunningSystem.StartAsync(new MonolithAttribute(),
            [new ServiceAttribute<PaymentsApi>("payments-api")]);
        using var client = system.Client<PaymentsApi>();
        var processId = await client.GetFromJsonAsync<int>("/health");
        using var child = Process.GetProcessById(processId);

        Assert.AreNotEqual(Environment.ProcessId, processId);
        CollectionAssert.AreEqual(new[] { "payments-api" }, system.Bricks.ToArray());
        Assert.ThrowsExactly<ArchitectureException>(() => system.Client<OrdersApi>());

        await system.DisposeAsync();

        Assert.IsTrue(child.HasExited);
        Assert.ThrowsExactly<ObjectDisposedException>(() => system.Client<PaymentsApi>());
    }

    [TestMethod]
    public async Task Missing_dependency_reports_the_child_startup_error()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(async () =>
        {
            await using var system = await RunningSystem.StartAsync(new MonolithAttribute(),
                [new ServiceAttribute<OrdersApi>("orders-api")]);
        });

        StringAssert.Contains(failure.Message, "exited with code");
        StringAssert.Contains(failure.Message, "No address for the payments service was configured.");
    }
}
