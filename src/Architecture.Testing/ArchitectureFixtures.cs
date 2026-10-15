using Architecture.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Every test assembly that loads this library gets these two methods without declaring anything.
[assembly: AssemblyFixtureProvider(typeof(ArchitectureFixtures))]

namespace Architecture.Testing;

/// <summary>Starts the span recorder before the first test and stops every running system after the last one.</summary>
[TestClass]
public static class ArchitectureFixtures
{
    /// <summary>The file the span sources of the run are written to, next to the test assembly.</summary>
    public const string SourcesFile = "span-sources.txt";

    /// <summary>Starts listening for spans and the receiver the services export to.</summary>
    [AssemblyInitialize]
    public static Task StartAsync(TestContext context) => SpanRecorder.StartAsync();

    /// <summary>Stops every system the run has started, then the receiver, and writes down which sources produced spans.</summary>
    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        try
        {
            await RunningSystem.StopAllAsync().ConfigureAwait(false);
        }
        finally
        {
            await SpanRecorder.StopAsync().ConfigureAwait(false);
        }

        var lines = SpanRecorder.Sources
            .OrderBy(source => source.Key, StringComparer.Ordinal)
            .Select(source => $"{source.Value,5}  {source.Key}");
        await File.WriteAllLinesAsync(Path.Combine(AppContext.BaseDirectory, SourcesFile), lines).ConfigureAwait(false);
    }
}
