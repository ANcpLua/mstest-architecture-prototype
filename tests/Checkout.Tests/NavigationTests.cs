using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Checkout.Tests;

/// <summary>Checks the source locations reported by MSTest discovery for architecture tests.</summary>
/// <param name="testContext">The runner context that supplies cancellation for this test.</param>
[TestClass]
public sealed class NavigationTests(TestContext testContext)
{
    /// <summary>Every architecture test navigates to its original attribute in the checkout test source.</summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task DiscoveryRetainsOriginalSourceLocations()
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { typeof(NavigationTests).Assembly.Location, "--list-tests", "json" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start MSTest discovery.");

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(testContext.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(testContext.CancellationToken);
            await process.WaitForExitAsync(testContext.CancellationToken);
            Assert.AreEqual(0, process.ExitCode, await error);

            using var report = JsonDocument.Parse(await output);
            var discovered = report.RootElement.GetProperty("tests").EnumerateArray().ToArray();
            Type[] testClasses = [typeof(CheckoutAsMicroservices), typeof(CheckoutAsDistributedMonolith), typeof(PaymentsAsMonolith)];

            foreach (var testClass in testClasses)
            {
                var methods = testClass.GetMethods().Where(method => method.IsDefined(typeof(TestMethodAttribute))).ToArray();
                Assert.IsNotEmpty(methods);
                foreach (var method in methods)
                {
                    var original = method.GetCustomAttribute<TestMethodAttribute>()!;
                    Assert.Contains("CheckoutTests.cs", original.DeclaringFilePath);
                    Assert.IsNotNull(original.DeclaringLineNumber);
                    Assert.IsTrue(original.DeclaringLineNumber > 0);

                    var test = discovered.Single(node =>
                        node.GetProperty("type").GetProperty("typeName").GetString() == testClass.Name &&
                        node.GetProperty("type").GetProperty("methodName").GetString() == method.Name);
                    var location = test.GetProperty("location");
                    Assert.AreEqual(original.DeclaringFilePath, location.GetProperty("file").GetString());
                    Assert.AreEqual(original.DeclaringLineNumber, location.GetProperty("lineStart").GetInt32());
                    Assert.AreEqual(original.DeclaringLineNumber, location.GetProperty("lineEnd").GetInt32());
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
