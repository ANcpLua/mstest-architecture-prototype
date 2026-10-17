using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Architecture.Testing;

/// <summary>Composes architecture verification with the method attribute selected by MSTest.</summary>
internal sealed class ArchitectureTestMethodAttribute(
    ArchitectureAttribute architecture,
    TestMethodAttribute inner,
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = -1)
    : TestMethodAttribute(callerFilePath, callerLineNumber)
{
    [Experimental("MSTESTEXP")]
    public override async Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        var testClass = testMethod.MethodInfo.ReflectedType
            ?? throw new InvalidOperationException($"{testMethod.TestMethodName} has no test class.");
        var bricks = testClass.GetCustomAttributes<BrickAttribute>(inherit: false)
            .OrderBy(brick => brick.Name, StringComparer.Ordinal).ToArray();
        if (architecture.Check(bricks) is { } problem)
        {
            return [new TestResult { Outcome = UnitTestOutcome.Failed, TestFailureException = new ArchitectureException(problem) }];
        }

        var context = TestContext.Current
            ?? throw new InvalidOperationException("Architecture execution requires a native MSTest invocation context.");
        var run = ArchitectureFixtures.GetRun(context);
        var system = await run.GetOrStartAsync(testClass, architecture, bricks, context.CancellationToken).ConfigureAwait(false);
        var scope = system.Enter(testMethod.TestMethodName);
        TestResult[]? results = null;
        TestTrace? trace = null;
        Exception? executionFailure = null;
        var infrastructureFailures = new List<Exception>();
        try
        {
            results = await inner.ExecuteAsync(testMethod).ConfigureAwait(false);
            if (results.Any(result => result.Outcome == UnitTestOutcome.Passed))
            {
                trace = await scope.SettleAsync(context.CancellationToken).ConfigureAwait(false);
            }
            else if (results.Length > 0)
            {
                trace = scope.Snapshot();
            }
        }
        catch (Exception error)
        {
            if (results is null) { executionFailure = error; }
            else { infrastructureFailures.Add(error); }
        }
        finally
        {
            try { scope.Dispose(); }
            catch (Exception error) { infrastructureFailures.Add(error); }
        }

        if (executionFailure is not null)
        {
            for (var index = 0; index < infrastructureFailures.Count; index++)
            {
                executionFailure.Data[$"Architecture infrastructure failure {index + 1}"] = infrastructureFailures[index].ToString();
            }
            ExceptionDispatchInfo.Capture(executionFailure).Throw();
        }
        if (results is null)
        {
            throw new InvalidOperationException("The method executor did not return its results.");
        }
        if (results.Length == 0)
        {
            return infrastructureFailures.Count == 0 ? results
                : throw new AggregateException("Architecture cleanup failed after an empty result array.", infrastructureFailures);
        }

        trace ??= scope.Snapshot();
        var verdict = system.Verify(trace);
        Exception? failure = infrastructureFailures.Count > 0
            ? new ArchitectureException($"Architecture evidence collection or cleanup failed. {verdict.Message}",
                infrastructureFailures.Count == 1 ? infrastructureFailures[0] : new AggregateException(infrastructureFailures))
            : verdict.Outcome != ArchitectureOutcome.Conforms ? new ArchitectureException(verdict.Message) : null;
        var diagnostics = $"{Environment.NewLine}{system.Kind} of {string.Join(", ", system.Bricks)}{Environment.NewLine}"
            + trace.Summary() + verdict.Message + Environment.NewLine
            + (failure is null ? "" : failure + Environment.NewLine);
        foreach (var result in results)
        {
            result.LogOutput += diagnostics;
            if (failure is not null && result.Outcome == UnitTestOutcome.Passed)
            {
                result.Outcome = UnitTestOutcome.Failed;
                result.TestFailureException = failure;
            }
        }
        return results;
    }
}

/// <summary>The declared architecture lacks evidence, was violated, or could not be started.</summary>
public sealed class ArchitectureException : Exception
{
    /// <summary>Creates an architecture diagnostic.</summary>
    public ArchitectureException(string message) : base(message) { }

    /// <summary>Creates an architecture diagnostic while retaining its underlying failure.</summary>
    public ArchitectureException(string message, Exception innerException) : base(message, innerException) { }
}
