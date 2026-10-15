using System;
using System.Linq;
using System.Xml.Linq;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using Fallout.Solutions;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

sealed class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Default);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution]
    readonly Solution Solution;

    AbsolutePath TestResultsDirectory => RootDirectory / "TestResults";

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNet("tool restore");
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoLogo()
                .EnableNoRestore());
        });

    Target RunTests => _ => _
        .DependsOn(Restore)
        .Produces(TestResultsDirectory / "*.trx")
        .Executes(() =>
        {
            TestResultsDirectory.CreateOrCleanDirectory();
            var project = Solution.GetAllProjects("Checkout.Tests").Single();

            // Microsoft.Testing.Platform, as selected in global.json
            DotNet($"test --project {project.Path} -c {Configuration} --no-restore --results-directory {TestResultsDirectory} --minimum-expected-tests 8 --report-trx");
        });

    Target InspectCode => _ => _
        .DependsOn(RunTests)
        .Executes(() =>
        {
            var artifacts = RootDirectory / "artifacts";
            artifacts.CreateDirectory();
            var reportPath = artifacts / "jb-inspections.xml";
            DotNet($"tool run jb -- inspectcode \"{RootDirectory / "Prototype.slnx"}\" --no-build --no-updates --jobs=4 --severity=SUGGESTION --format=Xml --output=\"{reportPath}\" --caches-home=\"{artifacts / "jb-cache"}\" --properties=Configuration={Configuration}");

            var report = XDocument.Load(reportPath);
            var issues = report.Root?.Element("Issues")
                ?? throw new InvalidOperationException($"InspectCode produced no Issues section in {reportPath}.");
            var findings = issues.Descendants("Issue").ToArray();
            if (findings.Length > 0)
                throw new InvalidOperationException($"InspectCode reported {findings.Length} warnings, errors or suggestions. See {reportPath}.");

            Console.WriteLine("InspectCode: no warnings, errors or suggestions.");
        });

    Target Default => _ => _
        .DependsOn(InspectCode);
}
