using System;
using System.Linq;
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

    [Solution(Name = "Solution")]
    readonly Solution _solution;

    AbsolutePath TestResultsDirectory => RootDirectory / "TestResults";

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
                .SetProjectFile(_solution)
                .SetConfiguration(_configuration)
                .EnableNoLogo()
                .EnableNoRestore());
        });

    Target RunTests => target => target
        .DependsOn(Compile)
        .Produces(TestResultsDirectory / "**/*.trx")
        .Executes(() =>
        {
            var project = _solution.GetAllProjects("Checkout.Tests").Single();

            // Microsoft.Testing.Platform, as selected in global.json
            ProcessTasks.StartProcess(
                    ToolPathResolver.GetPathExecutable("dotcov"),
                    $"test \"{project.Path}\" --min-line 100 --min-branch 100 -- -c {_configuration} --no-build --no-restore --minimum-expected-tests 76 --report-trx --coverage-settings \"{RootDirectory / "coverage.config"}\" --coverage-output coverage.cobertura.xml",
                    RootDirectory)
                .AssertZeroExitCode();
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
