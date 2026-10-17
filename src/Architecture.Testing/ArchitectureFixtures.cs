using System.Runtime.ExceptionServices;
using Architecture.Testing;

[assembly: AssemblyFixtureProvider(typeof(ArchitectureFixtures))]

namespace Architecture.Testing;

/// <summary>Owns a lazy architecture session for each consuming test assembly.</summary>
[TestClass]
public static class ArchitectureFixtures
{
    private const string SessionKey = "Architecture.Testing.Run";

    /// <summary>Registers ownership without starting a collector for unrelated tests.</summary>
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.Properties.Add(SessionKey, new ArchitectureRun());
    }

    internal static ArchitectureRun GetRun(TestContext context) =>
        context.Properties.TryGetValue(SessionKey, out var session) && session is ArchitectureRun run
            ? run
            : throw new InvalidOperationException("The architecture assembly fixture was not initialized. Register ArchitectureFixtures when overriding assembly initialization.");

    /// <summary>Stops owned processes and attaches retained evidence even when shutdown fails.</summary>
    [AssemblyCleanup]
    public static async Task StopAsync(TestContext context)
    {
        if (!context.Properties.TryGetValue(SessionKey, out var session) || session is not ArchitectureRun run)
        {
            return;
        }

        var errors = new List<Exception>();
        try { await run.CloseAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }

        foreach (var path in run.Recorder.EvidenceFiles)
        {
            try { context.AddResultFile(path); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        if (errors.Count > 1)
        {
            throw new AggregateException("Architecture cleanup failed; retained evidence attachment was still attempted.", errors);
        }
    }
}
