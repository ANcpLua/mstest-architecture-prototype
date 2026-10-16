using System.Diagnostics;
using System.Net.Http.Json;
using Architecture.Testing;
using Qyl;

namespace Checkout.Tests;

/// <summary>Checks invalid composition, cached startups and the lifetime of a test scope.</summary>
/// <param name="testContext">The runner context that supplies cancellation.</param>
[TestClass]
public sealed class HostingValidationTests(TestContext testContext)
{
    /// <summary>Names that cannot identify a service fail before launching a process.</summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("orders:api")]
    public async Task InvalidServiceNamesAreRejected(string? name)
    {
        var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(() => RunningSystem.StartAsync(
            new MonolithAttribute(), [new ServiceAttribute<Payments.Api.EntryPoint>(name!)], testContext.CancellationToken));
        Assert.Contains("nonempty service name without a colon", failure.Message);
    }

    /// <summary>The architecture count is checked before validating or launching services.</summary>
    [TestMethod]
    public async Task InvalidArchitectureIsRejected()
    {
        var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(() => RunningSystem.StartAsync(
            new MonolithAttribute(), [], testContext.CancellationToken));
        Assert.Contains("0 bricks", failure.Message);
    }

    /// <summary>Names are case-insensitively unique, and a service type cannot be launched twice.</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DuplicateServiceIdentitiesAreRejected(bool sameName)
    {
        BrickAttribute[] bricks = sameName
            ? [new ServiceAttribute<Orders.Api.EntryPoint>("service"), new ServiceAttribute<Payments.Api.EntryPoint>("SERVICE")]
            : [new ServiceAttribute<Payments.Api.EntryPoint>("first"), new ServiceAttribute<Payments.Api.EntryPoint>("second")];
        var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(() => RunningSystem.StartAsync(
            new MicroservicesAttribute(), bricks, testContext.CancellationToken));
        Assert.Contains("exactly one brick", failure.Message);
    }

    /// <summary>Access outside a test scope is rejected, and disposal is idempotent.</summary>
    [TestMethod]
    public async Task ScopeAndDisposalGuardAccess()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => SystemUnderTest.Current);
        var system = await RunningSystem.StartAsync(new MonolithAttribute(),
            [new ServiceAttribute<Payments.Api.EntryPoint>("payments-api")], testContext.CancellationToken);
        await using (system)
        {
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => system.TraceAsync(testContext.CancellationToken));
            using (system.Enter(nameof(ScopeAndDisposalGuardAccess)))
            {
                Assert.AreSame(system, SystemUnderTest.Current);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
                await cancellation.CancelAsync();
                var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => system.TraceAsync(cancellation.Token));
                Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            }

            _ = Assert.ThrowsExactly<InvalidOperationException>(() => SystemUnderTest.Current);
        }

        await system.DisposeAsync();
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => system.TraceAsync(testContext.CancellationToken));
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => system.Telemetry);
    }

    /// <summary>A cached failed startup does not prevent cleanup of a healthy cached process.</summary>
    [TestMethod]
    public async Task CachedStartupAndFailureAreBothCleanedUp()
    {
        var bricks = new BrickAttribute[] { new ServiceAttribute<Payments.Api.EntryPoint>("payments-api") };
        var first = RunningSystem.GetOrStartAsync(typeof(HealthyStartup), new MonolithAttribute(), bricks);
        var second = RunningSystem.GetOrStartAsync(typeof(HealthyStartup), new MonolithAttribute(), bricks);
        Assert.AreSame(first, second);
        var system = await first;
        using var client = system.Client<Payments.Api.EntryPoint>();
        var processId = await client.GetFromJsonAsync<int>("/health", testContext.CancellationToken);
        using var child = Process.GetProcessById(processId);
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<ArchitectureException>(() => RunningSystem.GetOrStartAsync(
                typeof(FailedStartup), new MonolithAttribute(), [new ServiceAttribute<Orders.Api.EntryPoint>("orders-api")]));
            Assert.Contains("No address for the payments service was configured.", failure.Message);
        }
        finally
        {
            await RunningSystem.StopAllAsync();
        }

        Assert.IsTrue(child.HasExited);
        await RunningSystem.StopAllAsync();
    }

    /// <summary>Cancellation after startup begins retains cancellation semantics and cleans up.</summary>
    [TestMethod]
    public async Task InFlightStartupCanBeCancelled()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        var start = RunningSystem.StartAsync(new MonolithAttribute(),
            [new ServiceAttribute<Payments.Api.EntryPoint>("payments-api")], cancellation.Token);
        await cancellation.CancelAsync();
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => start);
        Assert.IsNotNull(failure.StackTrace);
        Assert.Contains("Qyl.Orchestration", failure.StackTrace);
    }

    /// <summary>Completed service evidence remains usable while unrelated child work continues.</summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BackgroundWorkDoesNotDelayACompleteTrace()
    {
        await using var system = await RunningSystem.StartAsync(new MonolithAttribute(),
            [new ServiceAttribute<Payments.Api.EntryPoint>("payments-api")], testContext.CancellationToken);
        using var scope = system.Enter(nameof(BackgroundWorkDoesNotDelayACompleteTrace));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        var firstSpan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var productionToken = cancellation.Token;
        var producing = Task.Run(async () =>
        {
            while (!productionToken.IsCancellationRequested)
            {
                using (Spans.Work("continuous-test-work"))
                {
                    await Task.Delay(50, productionToken);
                }
                _ = firstSpan.TrySetResult();
            }
        }, cancellation.Token);

        try
        {
            await firstSpan.Task.WaitAsync(testContext.CancellationToken);
            using var client = system.Client<Payments.Api.EntryPoint>();
            Assert.AreEqual("payments-api", await client.GetStringAsync("/store", testContext.CancellationToken));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var trace = await scope.SettleAsync(deadline.Token);
            Assert.Contains(span => span.Name == "continuous-test-work", trace.Spans);
            Assert.Contains(span => span is { Service: "payments-api", ParentSpanId: not null }, trace.Spans);
            Assert.IsFalse(producing.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await producing;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The producer is joined before its trace scope is disposed.
            }
        }
    }

    private sealed class HealthyStartup;
    private sealed class FailedStartup;
}
