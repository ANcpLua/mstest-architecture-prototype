# Architecture tests with qyl

The prototype runs Orders and Payments as separate .NET processes. MSTest architecture
attributes select the services and decide whether their stores are independent or shared.
Architecture assertions inspect the spans those processes export over OTLP/HTTP.
The same exports reach an isolated qyl collector, where tests query persisted logs
and metrics through the platform's API.

```csharp
[Microservices]
[Service<Orders.Api.EntryPoint>("orders-api")]
[Service<Payments.Api.EntryPoint>("payments-api")]
public sealed class CheckoutTests
{
    // SystemUnderTest.Current.Client<Orders.Api.EntryPoint>() reaches the running service.
    // SystemUnderTest.Current.TraceAsync() returns the test's exported trace.
    // SystemUnderTest.Current.Telemetry queries the qyl collector.
}
```

The tests use MSTest SDK 4.5.1, Microsoft.Testing.Platform 2.5.1, Microsoft
coverage 18.12.0 and qyl 13.2.0. Restore downloads the published qyl runtime for
the SDK's operating system and architecture; no sibling checkout is needed.

Run the tests, coverage gate and inspections with the .NET SDK pinned in
`global.json` and globally installed DotCov 4.7.0 or later:

```sh
./build.sh --configuration Release
```

The gate restores the JetBrains tool pinned in `.config/dotnet-tools.json`, then builds
the test project and its references in the selected configuration. It runs all 92
tests through global `dotcov`, requires 100% line and branch coverage, and runs
InspectCode against their build outputs. It fails on
test failures, coverage below either threshold, or inspection warnings, errors or
suggestions. The report is `artifacts/jb-inspections.xml`.
`.editorconfig` promotes enabled analyzer suggestions, including MSTest's cancellation
guidance, to build warnings, which the project treats as errors.

To run just the tests:

```sh
dotnet test --project tests/Checkout.Tests/Checkout.Tests.csproj --report-trx --results-directory TestResults
```

The test command builds the referenced API projects. The fixture composes their compiled
executables through `Qyl.Orchestration` 13.1.0. qyl reserves distinct loopback ports, passes
service references, waits for each `/health` response to match its child's process ID, and
owns process startup and shutdown. The fixture supplies the architecture's store names.
Startup failures include the child's recent output. Failed startup and assembly cleanup
stop the child processes. Only the selected services are launched.

`Qyl.Telemetry.Hosting` 25.0.3 supplies `builder.AddQyl()`, automatic HTTP instrumentation
and OTLP export. Its transitive SemanticConventions 10.0.1 package supplies `Spans.DbClient`
for the simulated in-memory store writes. Tests receive telemetry on an isolated local
endpoint, with a short export interval and sampling enabled. The harness forwards all three
signals to the published qyl collector and keeps its DuckDB database and output under
`qyl-data/<run>` beside the test assembly. Each collector uses its own loopback port
and database. The test host also uses `AddQyl()` and exports directly to that collector,
so its HTTP client spans and completed test root are present in the stored trace.
The `Version` in `Directory.Build.props` supplies the services' `service.version`.
The stores demonstrate ownership through spans; they are not persistent
databases.

The package's `qyl-telemetry` guide documents the orchestrator integration and the
child environment settings used here for the OTLP endpoint and HTTP/protobuf protocol.

Aspire's AppHost, SDK and testing package have been removed. `Qyl.Orchestration` contains
the supervisor shared with the qyl CLI; the prototype has no custom service launcher and
uses its published NuGet package. No sibling clone or copied orchestration source is needed.
`Qyl.Api.Contracts` supplies the collector query response types used by the harness.

The checkout tests cover microservices, a distributed monolith and a single-service monolith,
including the exported qyl store spans and the orders-to-payments call. Hosting tests cover
process isolation, service selection, shutdown, startup cancellation and a missing dependency's
startup error. Native `[DataRow]` cases verify that each payment invocation receives
its own trace. A discovery test launches the test runner with `--list-tests json` and checks
the original file and line for every architecture case, including each data row. MSTest reads
these locations from the original `[TestMethod]` during discovery; the execution wrapper does not need to copy
them or suppress a caller-information inspection.

`Architecture.Testing` uses MSTest's `TestClassAttribute`, `TestMethodAttribute.ExecuteAsync`
and `AssemblyFixtureProvider` extension points. Its wrapper adds architecture verification
and trace diagnostics while preserving the method attribute's results. MSTest supplies
data-row execution, duration reporting, discovery and the underlying test outcomes.

Regression tests also cover invalid architecture declarations, store ownership, malformed
OTLP payloads, receiver startup failure, API fallbacks and payment failures. Three deliberately
failing cases live in `tests/Architecture.Probes`. The main suite runs each in a separate
MSTest process and checks its exit code and TRX diagnostic, including preservation of an
original assertion failure. The parent project builds the probes and owns their assertions.
Run `Checkout.Tests` for a passing suite; the solution also lists the probe sources for browsing.

## Logs and metrics

The service applications use `AddQyl()` for all three signals. They emit structured
application logs through `ILogger`; qyl also records HTTP metrics automatically.
The harness stops and joins the collector during assembly cleanup.

`TraceAsync()` returns a trace with a public `Id`. Use that ID to read its logs:

```csharp
var system = SystemUnderTest.Current;
var trace = await system.TraceAsync(cancellationToken);
var logs = await system.Telemetry.LogsAsync(trace.Id, cancellationToken);
```

Log queries follow every continuation page and retain severity, body, attributes,
resource and trace identity from `Qyl.Api.Contracts`. HTTP errors, invalid cursors
and incomplete OTLP acknowledgements fail visibly.

The receiver records exported spans only after qyl accepts the export. Malformed
trace protobuf receives HTTP 400 with the decoder's diagnostic in a protobuf status
message. Startup probes and deliberately failed requests remain in telemetry;
[the v5.0.1 error audit](docs/telemetry-errors.md) explains their causes and the
receiver defects corrected in v5.0.2.

Metrics describe an aggregation window, not one request. Query a metric name with
an explicit start, end and aggregation:

```csharp
var metrics = await system.Telemetry.QueryMetricAsync(
    "http.server.request.duration", start, end,
    MetricAggregation.Count, cancellationToken);
```

The result retains qyl's units, series, buckets and truncation flag. Export and
persistence are asynchronous: an early read can be empty. The integration tests
wait for actual log records or nonempty metric values with a bounded deadline.

## Coverage

Use DotCov 4.7.0 or later. `dotcov test` flushes pending Microsoft coverage counters
when the expected startup-error child terminates. `coverage.config` measures all three
source assemblies, including their child processes. Both line and branch coverage
must reach 100%; `./build.sh` enforces these thresholds without coverage exclusions.

```sh
mkdir -p artifacts
dotcov test tests/Checkout.Tests/Checkout.Tests.csproj --min-line 100 --min-branch 100 --format json -- \
  -c Release --minimum-expected-tests 92 --report-trx \
  --coverage-settings coverage.config --coverage-output coverage.cobertura.xml \
  > artifacts/coverage.json 2> artifacts/coverage.stderr
```

DotCov writes each run's XML, test results and log into a fresh `TestResults/<run>`
directory, named on stderr. Keep these with the JSON and diagnostics. All three
assemblies and all 16 production C# source files should appear. Uncovered lines and
partial branches identify behavior the tests have not exercised.
