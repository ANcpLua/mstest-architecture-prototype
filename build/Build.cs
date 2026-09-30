using System.Linq;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using Fallout.Solutions;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

class Build : FalloutBuild
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
        .DependsOn(Compile)
        .Produces(TestResultsDirectory / "*.trx")
        .Executes(() =>
        {
            TestResultsDirectory.CreateOrCleanDirectory();
            var project = Solution.GetAllProjects("Checkout.Tests").Single();

            // Microsoft.Testing.Platform, as selected in global.json
            DotNet($"test --project {project.Path} -c {Configuration} --no-build --results-directory {TestResultsDirectory} --minimum-expected-tests 1 --report-trx");
        });

    Target Default => _ => _
        .DependsOn(RunTests);
}
