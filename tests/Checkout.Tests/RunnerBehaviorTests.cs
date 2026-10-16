using System.Diagnostics;
using System.Xml.Linq;

namespace Checkout.Tests;

/// <summary>Checks failing architecture tests through the real MSTest process and its TRX report.</summary>
/// <param name="testContext">The runner context that supplies cancellation.</param>
[TestClass]
public sealed class RunnerBehaviorTests(TestContext testContext)
{
    /// <summary>Expected failing probes stay visible as failed tests with the original diagnostic.</summary>
    [TestMethod]
    [DataRow("MissingSpans", "No span from any brick")]
    [DataRow("OriginalFailure", "original body failure must survive")]
    [DataRow("InvalidComposition", "Microservices are several deployables")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task RunnerPreservesFailureOutcomes(string probe, string diagnostic)
    {
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        var assembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../Architecture.Probes/bin", target.Parent!.Name, target.Name, "Architecture.Probes.dll"));
        var results = Path.Combine(testContext.TestRunResultsDirectory!, "runner-probes", Guid.NewGuid().ToString("N"), probe);
        _ = Directory.CreateDirectory(results);
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { assembly, "--filter", $"FullyQualifiedName~Architecture.Probes.{probe}.",
                "--minimum-expected-tests", "1", "--report-trx", "--results-directory", results },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start the MSTest failure probe.");

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(testContext.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(testContext.CancellationToken);
            await process.WaitForExitAsync(testContext.CancellationToken);
            var log = await output + await error;
            testContext.WriteLine(log);
            Assert.AreEqual(2, process.ExitCode, log);
            var resultFile = Directory.GetFiles(results, "*.trx").Single();
            testContext.AddResultFile(resultFile);
            var report = XDocument.Load(resultFile);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var cases = report.Descendants(ns + "UnitTestResult").ToArray();
            Assert.HasCount(1, cases);
            Assert.AreEqual("Failed", (string?)cases[0].Attribute("outcome"));
            var message = cases[0].Descendants(ns + "Message").Single().Value;
            Assert.Contains(diagnostic, message);
            Assert.DoesNotContain("invalid composition body ran", message);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
