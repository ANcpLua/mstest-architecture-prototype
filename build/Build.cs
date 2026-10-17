using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Solutions;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

sealed class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Default);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)", Name = "Configuration")]
    readonly Configuration _configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Native MSTest filter; omit to run every test", Name = "TestFilter")]
    readonly string? _testFilter;

    [Parameter("Minimum number of tests required for the selected scope", Name = "MinimumExpectedTests")]
    readonly int _minimumExpectedTests = 73;

    [Solution(Name = "Solution")]
    readonly Solution _solution;

    AbsolutePath TestResultsDirectory => RootDirectory / "TestResults";
    AbsolutePath TestProject => _solution.GetAllProjects("Checkout.Tests").Single().Path;

    Target Restore => target => target
        .Executes(() =>
        {
            DotNet("tool restore");
            DotNetRestore(s => s
                .SetProjectFile(_solution));
        });

    Target Compile => target => target
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(TestProject)
                .SetConfiguration(_configuration)
                .EnableNoLogo()
                .EnableNoRestore());
        });

    Target RunTests => target => target
        .DependsOn(Compile)
        .Produces(TestResultsDirectory / "**/*.trx")
        .Executes(() =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_minimumExpectedTests);
            if (_testFilter?.Any(character => character is '"' or '\r' or '\n') == true)
                throw new ArgumentException("The test filter cannot contain quotes or line breaks.", nameof(_testFilter));
            var filter = string.IsNullOrWhiteSpace(_testFilter) ? "" : $" --filter \"{_testFilter}\"";
            // Native test outcomes gate business invariants. Keep the coverage report without
            // the blanket library percentage gate removed by the owner on 2026-10-16.
            var reportPath = RootDirectory / "artifacts" / "coverage.json";
            reportPath.Parent.CreateDirectory();
            var process = ProcessTasks.StartProcess(
                    ToolPathResolver.GetPathExecutable("dotcov"),
                    $"test \"{TestProject}\" --min-line 0 --min-branch 0 --format json -- -c {_configuration} --no-build --no-restore --minimum-expected-tests {_minimumExpectedTests}{filter} --report-trx --coverage-settings \"{RootDirectory / "coverage.config"}\" --coverage-output coverage.cobertura.xml",
                    RootDirectory, logOutput: false);
            process.WaitForExit();
            File.WriteAllText(reportPath, string.Join(Environment.NewLine,
                process.Output.Where(line => line.Type == OutputType.Std).Select(line => line.Text)));
            var diagnostics = process.Output.Where(line => line.Type == OutputType.Err).Select(line => line.Text).ToArray();
            File.WriteAllLines(RootDirectory / "artifacts" / "coverage.stderr", diagnostics);
            foreach (var diagnostic in diagnostics) Console.WriteLine(diagnostic);
            // DotCov returns 4 for a deliberately disabled percentage gate. Failed tests and
            // unreadable reports have different exit codes and must still stop the build.
            if (process.ExitCode != 4) process.AssertZeroExitCode();
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;
            var summary = report.GetProperty("summary");
            if (summary.GetProperty("totalLines").GetInt32() == 0
                || summary.GetProperty("totalBranches").GetInt32() == 0
                || report.GetProperty("files").GetArrayLength() == 0
                || report.GetProperty("warnings").GetArrayLength() != 0)
                throw new InvalidOperationException($"Coverage is missing or ambiguous. See {reportPath}.");
            Console.WriteLine($"Coverage measured: {summary.GetProperty("lineRate")}% lines, {summary.GetProperty("branchRate")}% branches. Full report: {reportPath}.");
        });

    Target InspectCode => target => target
        .DependsOn(RunTests)
        .Executes(() =>
        {
            var artifacts = RootDirectory / "artifacts";
            artifacts.CreateDirectory();
            var reportPath = artifacts / "jb-inspections.xml";
            DotNet($"tool run jb -- inspectcode \"{RootDirectory / "Prototype.slnx"}\" --no-build --no-updates --jobs=4 --severity=SUGGESTION --format=Xml --output=\"{reportPath}\" --caches-home=\"{artifacts / "jb-cache"}\" --properties=Configuration={_configuration}");

            var report = XDocument.Load(reportPath);
            var issues = report.Root?.Element("Issues")
                ?? throw new InvalidOperationException($"InspectCode produced no Issues section in {reportPath}.");
            var findings = issues.Descendants("Issue").ToArray();
            if (findings.Length > 0)
                throw new InvalidOperationException($"InspectCode reported {findings.Length} warnings, errors or suggestions. See {reportPath}.");

            Console.WriteLine("InspectCode: no warnings, errors or suggestions.");
        });

    Target Default => target => target
        .DependsOn(InspectCode);
}
