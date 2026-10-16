using Architecture.Testing;

[assembly: DoNotParallelize]

namespace Architecture.Probes;

/// <summary>A passing body with no service spans must fail architecture verification.</summary>
[Monolith]
[Service<Payments.Api.EntryPoint>("payments-api")]
public sealed class MissingSpans
{
    /// <summary>The runner must report the missing architecture measurement.</summary>
    [TestMethod]
    public void PassingBodyWithNoMeasurement() => Assert.IsNotNull(SystemUnderTest.Current);
}

/// <summary>The wrapper must retain the body's failure when the architecture also fails.</summary>
[Monolith]
[Service<Payments.Api.EntryPoint>("payments-api")]
public sealed class OriginalFailure
{
    /// <summary>This failure must survive architecture verification unchanged.</summary>
    [TestMethod]
    public void BodyFailureSurvives() => Assert.Fail("original body failure must survive");
}

/// <summary>Invalid composition is rejected before the test body runs.</summary>
[Microservices]
[Service<Payments.Api.EntryPoint>("payments-api")]
public sealed class InvalidComposition
{
    /// <summary>This message must never become the reported error.</summary>
    [TestMethod]
    public void BodyMustNotRun() => Assert.Fail("invalid composition body ran");
}
