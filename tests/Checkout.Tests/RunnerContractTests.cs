using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Architecture.Testing;

namespace Checkout.Tests;

/// <summary>Checks wrapper contracts that cannot be produced by ordinary test discovery.</summary>
[TestClass]
public sealed class RunnerContractTests
{
    /// <summary>A method without a declaring test class is rejected before invocation.</summary>
    [TestMethod]
    public async Task MethodWithoutATestClassIsRejected()
    {
        var method = new StubMethod(new DynamicMethod("UnboundTest", typeof(void), []));
        var wrapper = new MonolithAttribute().GetTestMethodAttribute(new TestMethodAttribute());
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => wrapper.ExecuteAsync(method));
        Assert.AreEqual("UnboundTest has no test class.", failure.Message);
    }

    /// <summary>Invalid class composition becomes a failed result without invoking its body.</summary>
    [TestMethod]
    public async Task InvalidCompositionReturnsAFailedResult()
    {
        var wrapper = new MicroservicesAttribute().GetTestMethodAttribute(new TestMethodAttribute());
        var method = new StubMethod(typeof(WrapperFixture).GetMethod(nameof(WrapperFixture.Body))!);
        var results = await wrapper.ExecuteAsync(method);
        Assert.HasCount(1, results);
        Assert.AreEqual(UnitTestOutcome.Failed, results[0].Outcome);
        var failure = Assert.IsInstanceOfType<ArchitectureException>(results[0].TestFailureException);
        Assert.Contains("Microservices are several deployables", failure.Message);
    }

    /// <summary>Architecture failure retains the original result array, metadata and earlier failures.</summary>
    [TestMethod]
    public async Task WrappedResultsRetainTheirIdentityAndOriginalFailures()
    {
        var original = new AssertFailedException("earlier row failed");
        var first = new TestResult { Outcome = UnitTestOutcome.Failed, TestFailureException = original, DisplayName = "first row", Duration = TimeSpan.FromSeconds(1) };
        var last = new TestResult { Outcome = UnitTestOutcome.Passed, DisplayName = "last row", LogOutput = "existing output" };
        TestResult[] results = [first, last];
        var wrapper = new MonolithAttribute().GetTestMethodAttribute(new ResultsAttribute(results));
        var method = new StubMethod(typeof(WrapperFixture).GetMethod(nameof(WrapperFixture.Body))!);
        var actual = await wrapper.ExecuteAsync(method);

        Assert.AreSame(results, actual);
        Assert.AreSame(first, actual[0]);
        Assert.AreSame(original, actual[0].TestFailureException);
        Assert.AreEqual("first row", actual[0].DisplayName);
        Assert.AreEqual(TimeSpan.FromSeconds(1), actual[0].Duration);
        Assert.AreSame(last, actual[1]);
        Assert.AreEqual(UnitTestOutcome.Failed, actual[1].Outcome);
        _ = Assert.IsInstanceOfType<ArchitectureException>(actual[1].TestFailureException);
        var log = actual[1].LogOutput;
        Assert.IsNotNull(log);
        Assert.StartsWith("existing output", log);
        Assert.Contains("not measured", log);
    }

    [Service<Payments.Api.EntryPoint>("payments-api")]
    private sealed class WrapperFixture
    {
        public static void Body() { }
    }

    private sealed class ResultsAttribute(TestResult[] results,
        [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        : TestMethodAttribute(callerFilePath, callerLineNumber)
    {
        public override Task<TestResult[]> ExecuteAsync(ITestMethod testMethod) => Task.FromResult(results);
    }

    private sealed class StubMethod(MethodInfo method) : ITestMethod
    {
        public string TestMethodName => method.Name;
        public string TestClassName => method.ReflectedType?.Name ?? "";
        public Type ReturnType => method.ReturnType;
        public object?[] Arguments => [];
        public ParameterInfo[] ParameterTypes => method.GetParameters();
        public MethodInfo MethodInfo => method;
        public Task<TestResult> InvokeAsync(object?[]? arguments) => throw new InvalidOperationException("The wrapper should not invoke this method.");
        public Attribute[] GetAllAttributes() => [];
        public TAttributeType[] GetAttributes<TAttributeType>() where TAttributeType : Attribute => [];
    }
}
