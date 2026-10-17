using Qyl.Telemetry.SemanticConventions;
using Architecture.Testing;

namespace Checkout.Tests;

/// <summary>Checks architecture claims against traces with and without violations.</summary>
[TestClass]
public sealed class ArchitectureRulesTests
{
    /// <summary>Each architecture requires its advertised number of deployables.</summary>
    [TestMethod]
    [DataRow(0, false, false)]
    [DataRow(1, true, false)]
    [DataRow(2, false, true)]
    public void DeployableCountsAreValidated(int count, bool monolith, bool distributed)
    {
        var bricks = Enumerable.Range(0, count).Select(index => (BrickAttribute)new ServiceAttribute<Payments.Api.EntryPoint>($"service-{index}")).ToArray();
        Assert.AreEqual(monolith, new MonolithAttribute().Check(bricks) is null);
        Assert.AreEqual(distributed, new MicroservicesAttribute().Check(bricks) is null);
        Assert.AreEqual(distributed, new DistributedMonolithAttribute().Check(bricks) is null);
    }

    /// <summary>No evidence and evidence from an undeclared service cannot establish conformance.</summary>
    [TestMethod]
    public void MissingAndForeignSpansFailVerification()
    {
        var architecture = new MonolithAttribute();
        Assert.Contains("not measured", architecture.Verify(TestSpan.Trace(), ["orders"]).Message);
        Assert.Contains("not measured", architecture.Verify(TestSpan.Trace(TestSpan.Create(SpanRecorder.TestProcess, "1")), ["orders"]).Message);
        Assert.Contains("intruder produced spans", architecture.Verify(TestSpan.Trace(TestSpan.Create("intruder", "1")), ["orders"]).Message);
    }

    /// <summary>Arguments required by public execution and verification APIs are checked.</summary>
    [TestMethod]
    public void RequiredArgumentsAreChecked()
    {
        var architecture = new MonolithAttribute();
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => architecture.GetTestMethodAttribute(null!));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => architecture.Verify(null!, ["orders"]));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => architecture.Verify(TestSpan.Trace(), null!));
    }

    /// <summary>A store owned by a different service is reported by name.</summary>
    [TestMethod]
    public void ForeignStoreNamesAreReported()
    {
        var trace = TestSpan.Trace(TestSpan.Create("orders", "1", store: "payments"));
        var failure = new MicroservicesAttribute().Verify(trace, ["orders", "payments"]);
        Assert.Contains("orders wrote to payments", failure.Message);
        Assert.Contains("owns the store orders", failure.Message);
    }

    /// <summary>A monolith accepts calls from the test process but rejects service-to-service calls.</summary>
    [TestMethod]
    public void MonolithRejectsCallsBetweenServices()
    {
        var architecture = new MonolithAttribute();
        var external = TestSpan.Create(SpanRecorder.TestProcess, "client", SpanKind.Client);
        var server = TestSpan.Create("orders", "server", SpanKind.Server, "client");
        Assert.AreEqual(ArchitectureOutcome.Conforms, architecture.Verify(TestSpan.Trace(external, server,
            TestSpan.Create("orders", "write", store: "orders")), ["orders"]).Outcome);
        var internalClient = TestSpan.Create("orders", "client", SpanKind.Client);
        var internalServer = TestSpan.Create("payments", "server", SpanKind.Server, "client");
        Assert.Contains("orders called payments", architecture.Verify(TestSpan.Trace(internalClient, internalServer), ["orders", "payments"]).Message);
    }

    /// <summary>Independent services can write their own stores but cannot share one.</summary>
    [TestMethod]
    public void MicroservicesRejectSharedStores()
    {
        var architecture = new MicroservicesAttribute();
        var orders = TestSpan.Create("orders", "1", store: "orders");
        var payments = TestSpan.Create("payments", "2", store: "payments");
        Assert.AreEqual(ArchitectureOutcome.Conforms, architecture.Verify(TestSpan.Trace(orders, payments), ["orders", "payments"]).Outcome);
        var failure = architecture.Verify(TestSpan.Trace(orders, TestSpan.Create("payments", "2", store: "orders")), ["orders", "payments"]);
        Assert.Contains("Microservices own their stores", failure.Message);
        Assert.Contains("orders and payments", failure.Message);
    }

    /// <summary>A distributed monolith reports distinct stores before its per-service ownership check.</summary>
    [TestMethod]
    public void DistributedMonolithRejectsSeparateStores()
    {
        var architecture = new DistributedMonolithAttribute();
        var orders = TestSpan.Create("orders", "1", store: "shared");
        var payments = TestSpan.Create("payments", "2", store: "shared");
        Assert.AreEqual(ArchitectureOutcome.Conforms, architecture.Verify(TestSpan.Trace(orders, payments), ["orders", "payments"]).Outcome);
        Assert.AreEqual(ArchitectureOutcome.InsufficientEvidence, architecture.Verify(TestSpan.Trace(orders), ["orders", "payments"]).Outcome);
        var failure = architecture.Verify(TestSpan.Trace(orders, TestSpan.Create("payments", "2", store: "other")), ["orders", "payments"]);
        Assert.Contains("shares one store", failure.Message);
        Assert.Contains("shared, other", failure.Message);
    }
}
