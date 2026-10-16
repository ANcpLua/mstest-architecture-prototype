# Architecture tests with qyl

Orders and Payments run as separate .NET processes. Native MSTest architecture
attributes choose the services and declare whether their stores should be independent
or shared. Architecture checks inspect the spans the actual processes export.
The same exports reach an isolated qyl collector for persistence assertions.

```csharp
[Microservices]
[Service<Orders.Api.EntryPoint>("orders-api")]
[Service<Payments.Api.EntryPoint>("payments-api")]
public sealed class CheckoutTests
{
    // SystemUnderTest.Current.Client<Orders.Api.EntryPoint>() reaches the service.
    // SystemUnderTest.Current.TraceAsync() returns this invocation's trace.
    // SystemUnderTest.Current.Telemetry reads the collector's stored records.
}
```

This checkout uses .NET SDK 10.0.401, MSTest SDK 4.5.1, Microsoft.Testing.Platform
2.5.1 and Microsoft coverage 18.12.0. Its published dependencies are qyl and
`Qyl.Orchestration` 14.0.0, `Qyl.Telemetry.Hosting` 26.0.0,
`Qyl.Api.Contracts` 14.0.0 and `Qyl.Telemetry.Testing` 1.1.2. The instrumentation
stack supplies SemanticConventions 12.0.0 and OpenTelemetry SDK/exporter 1.20.0.
Restore downloads the runtime for the SDK's OS and architecture; no sibling clone
or private feed is needed.

## Run

With global DotCov 4.7.0 or later installed:

```sh
./build.sh --configuration Release
```

The build runs all 73 native test cases through global DotCov and requires a real
coverage report with line and branch data. Coverage percentages are informational,
following the owner's removal of blanket library thresholds. Test failures, missing
or ambiguous coverage, and JetBrains warnings, errors or suggestions fail the build.
`artifacts/coverage.json`, `artifacts/coverage.stderr` and
`artifacts/jb-inspections.xml` retain the results. No coverage exclusions were added.

To run only the tests:

```sh
dotnet test --project tests/Checkout.Tests/Checkout.Tests.csproj --report-trx --results-directory TestResults
```

The test project builds its API and child-probe references in the selected
configuration. The pinned JetBrains tool is restored from `.config/dotnet-tools.json`.
Enabled compiler and analyzer suggestions are warnings, and warnings fail compilation.

## Processes and invocation lifetime

`Qyl.Orchestration` reserves loopback endpoints, passes service references, checks
that `/health` returns the child's process ID, and owns startup, cancellation and
shutdown. Startup errors retain child output. Only the selected services start.
The in-memory store spans demonstrate ownership; the stores are not databases.

`Architecture.Testing` uses native `TestClassAttribute`,
`TestMethodAttribute.ExecuteAsync` and `AssemblyFixtureProvider` extension points.
The wrapper delegates to the original method attribute and retains its result objects,
earlier failures and native data-row behavior. Each invocation has a separate trace.
A real discovery subprocess checks the original source file and line for every
architecture case. MSTest owns navigation and duration reporting.

Three deliberately failing cases in `tests/Architecture.Probes` test missing telemetry,
invalid composition and original assertion failures. Passing parent tests run each
through native MSTest and assert its exit code and TRX diagnostic. Startup failures
and expected application errors remain in telemetry.

## Capture and persistence

`Qyl.Telemetry.Testing` owns OTLP decoding and the canonical trace, log and metric
models. The prototype has no copied decoder or flattened span model. A thin local
relay captures every protobuf export, forwards it to qyl, and returns the collector's
actual status, content type and response body. Nonempty valid acknowledgements are
preserved. Rejected payloads and diagnostics remain in the capture.

The test process uses the official SDK for invocation roots and HTTP client spans.
Application processes use `AddQyl()` for traces, logs and metrics. Exporter forwarding
is suppressed from instrumentation to prevent export loops. Architecture assertions
wait for matching client/server evidence with a bounded deadline; ongoing background
spans do not require a quiet period. Stored-data tests await the actual persisted
record separately.

`TestTrace.Telemetry.Capture` retains every captured record and original payload.
The trace view selects its trace ID. Its call graph uses unique span identities while
raw retransmissions remain visible. Database identities retain system, server and
namespace rather than merging stores with the same name.

Each collector retains its DuckDB database, process output and `capture.json` under
`qyl-data/<run>` beside the test assembly. The capture includes original exports and
both capture and collector responses. Assembly cleanup attaches these evidence files,
stops services, joins the receiver and collector, and releases their ports.

## Traces, logs and metrics

A successful order emits a `checkout.order.created` span event and increments the
monotonic `checkout.orders.created` counter. Its structured `OrderCreated` log retains
the same trace, server span and service instance. Payment and failure logs retain their
named events as well. Custom application event names are supported.

Tests check typed values, ordered span events, timestamps, trace flags, resource and
scope metadata. The raw capture keeps exception message and stack trace. The collector's
existing exception-event persistence policy keeps `exception.type`; tests distinguish
that stored policy from the original exported event.

Native HTTP server/client and runtime CPU/memory metrics are checked for their actual
units, exported shapes and producer identity. Process metrics belong to a resource,
not to a nearby request. The simulated store does not produce database-client metrics;
real database instrumentation is exercised by AutoInstrumentation's consumer lanes.

```csharp
var system = SystemUnderTest.Current;
var trace = await system.TraceAsync(cancellationToken);
var logs = await system.Telemetry.LogsAsync(trace.Id, cancellationToken);
```

Log reads follow every continuation page. qyl's dashboard applies severity, exact
service and message filters before pagination, so older errors remain accessible.
Historical unknown times stay unknown. The [earlier error audit](docs/telemetry-errors.md)
is retained as release history.

Metric aggregates require an explicit name, time range and aggregation. Responses
retain units, buckets, truncation and numeric-representation metadata. Original metric
points are available through the qyl API and the complete capture, including native
histograms, exact integers, summaries and exemplars.

## Coverage

`coverage.config` measures all three source assemblies, including child processes.
DotCov flushes buffered Microsoft coverage counters when the expected startup-error
child terminates. Each run writes fresh reports and the native test log into its own
`TestResults/<run>` directory. Uncovered lines and partial branches remain in the report.

```sh
dotcov test tests/Checkout.Tests/Checkout.Tests.csproj --min-line 0 --min-branch 0 --format json -- \
  -c Release --minimum-expected-tests 73 --report-trx \
  --coverage-settings coverage.config --coverage-output coverage.cobertura.xml \
  > artifacts/coverage.json 2> artifacts/coverage.stderr
```

With both percentage thresholds disabled, DotCov reports its documented exit 4.
The build accepts that informational outcome only after the native tests pass and
coverage data is present and unambiguous. Other tool failures still fail the build.
