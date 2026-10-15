using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Architecture.Testing;

/// <summary>Wraps the test method attribute of every test in a class that carries an architecture attribute.</summary>
internal sealed class ArchitectureTestMethodAttribute(
    ArchitectureAttribute architecture,
    TestMethodAttribute inner,
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = -1)
    : TestMethodAttribute(callerFilePath, callerLineNumber)
{
    public override async Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        var testClass = testMethod.MethodInfo.ReflectedType
            ?? throw new InvalidOperationException($"{testMethod.TestMethodName} has no test class.");
        var bricks = testClass.GetCustomAttributes<BrickAttribute>(inherit: false)
            .OrderBy(brick => brick.Name, StringComparer.Ordinal)
            .ToList();

        if (architecture.Check(bricks) is { } problem)
        {
            return [new TestResult { Outcome = UnitTestOutcome.Failed, TestFailureException = new ArchitectureException(problem) }];
        }

        var system = await RunningSystem.GetOrStartAsync(testClass, architecture, bricks).ConfigureAwait(false);

        TestResult[] results;
        TestTrace trace;
        using (var test = system.Enter(testMethod.TestMethodName))
        {
            results = await inner.ExecuteAsync(testMethod).ConfigureAwait(false);
            trace = await test.SettleAsync().ConfigureAwait(false);
        }

        var verdict = architecture.Verify(trace, system.Bricks);
        var last = results[^1];
        last.LogOutput += $"{Environment.NewLine}{system.Kind} of {string.Join(", ", system.Bricks)}{Environment.NewLine}"
            + trace.Summary()
            + (verdict ?? $"Fits {system.Kind}.") + Environment.NewLine;

        if (verdict is not null && last.Outcome == UnitTestOutcome.Passed)
        {
            last.Outcome = UnitTestOutcome.Failed;
            last.TestFailureException = new ArchitectureException(verdict);
        }

        return results;
    }
}

/// <summary>The stacked bricks do not fit the architecture the test class claims, or a service cannot start.</summary>
public sealed class ArchitectureException(string message) : Exception(message);
