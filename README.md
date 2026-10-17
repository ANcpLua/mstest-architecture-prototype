# Architecture tests with qyl

**Archived successor notice:** the retained architecture fixtures, Orders/Payments
applications and telemetry scenarios moved to
[Qyl.Telemetry.Tests](https://github.com/ANcpLua/Qyl.Telemetry.Tests).
Active development continues in its shared support, unit, integration and
end-to-end projects. The documentation below describes this historical prototype.

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
2.5.1 and Microsoft coverage 18.12.0. Its dependency pins are qyl and
`Qyl.Orchestration` 15.0.3, `Qyl.Telemetry.Hosting` 28.0.0,
`Qyl.Api.Contracts` 14.0.1 and `Qyl.Telemetry.Testing` 3.0.3. The instrumentation
stack supplies SemanticConventions 13.1.2 and OpenTelemetry SDK/exporter 1.20.0.
Restore downloads the runtime for the SDK's OS and architecture; no sibling clone
or private feed is needed.

## Run

With global DotCov 4.7.0 or later installed:

```sh
./build.sh --configuration Release
```

By default the build runs all 78 native test cases through global DotCov and requires
a real coverage report with line and branch data. Coverage percentages are informational.
Test failures, missing or ambiguous coverage, and JetBrains warnings, errors or
suggestions fail the build.
`artifacts/coverage.json`, `artifacts/coverage.stderr` and
`artifacts/jb-inspections.xml` retain the results. No coverage exclusions were added.

For a focused change, pass `--TestFilter '<MSTest filter>' --MinimumExpectedTests <count>`.
The minimum must match the intended scope; a filter that discovers too few tests fails.

To run only the tests:

```sh
dotnet test --project tests/Checkout.Tests/Checkout.Tests.csproj --report-trx --results-directory TestResults
```

The test project builds its API and child-probe references in the selected
configuration. The pinned JetBrains tool is restored from `.config/dotnet-tools.json`.
Enabled compiler and analyzer suggestions are warnings, and warnings fail compilation.

## Processes and invocation lifetime

The automatic assembly fixture registers a run-owned session without launching
processes. Architecture tests start its recorder and collector lazily; ordinary unit
tests do not acquire those dependencies. Service caches and the current invocation
belong to that consuming assembly's session. Tests that exercise the recorder
directly start it explicitly; receiver-only tests do not start the collector.

`Qyl.Orchestration` reserves loopback endpoints, passes service references, checks
that `/health` returns the child's process ID, and owns startup, cancellation and
shutdown. Only the selected services start. The in-memory store spans demonstrate
ownership; the stores are not databases.

`Architecture.Testing` uses native `TestClassAttribute`,
`TestMethodAttribute.ExecuteAsync` and `AssemblyFixtureProvider` extension points.
The wrapper delegates once to the original method attribute and retains its result
objects, existing failures and native data-row behavior. Evidence and cleanup
failures are attached to the original results; they do not replace an existing
assertion failure. The same diagnostic applies to every result returned by an
executor, rather than being assigned arbitrarily to its last result. An executor
exception remains the primary exception if scope cleanup also fails.

The internal executor declares its dependency on MSTest 4.5.1's experimental
`TestContext.Current` API. No diagnostic is suppressed and consuming tests keep
their existing syntax. Its native invocation token flows through startup and
evidence waits. Resource cleanup deliberately remains independent of an already
cancelled invocation so owned processes are still joined.

Each invocation has a separate trace. A real discovery subprocess checks the
original source file and line for every architecture case. MSTest owns navigation
and duration reporting.

The default evidence wait requires observed store activity for every declared
brick and matching server spans for observed HTTP calls to those bricks. Verification
distinguishes conformance, a violation and insufficient evidence. A partial trace
cannot establish that multiple services own or share their stores. A successful
verdict describes the observed scenario, not every possible execution.

For a different scenario, `TraceAsync(trace => /* required evidence */, token)` waits
for an explicit predicate over the trace and its complete captured telemetry. Use
that form for operations that do not touch stores, background business events,
logs or metrics. Metrics require a producer identity and measurement window; they
are not individual trace events. An explicit read predicate does not waive final
architecture verification. An HTTP response or server span alone does not
establish that all telemetry has arrived. Each wait is bounded and cancellable.

Shutdown attempts every owned resource even when another cleanup step fails.
Captured exports, delivery records, source inventories and Collector shutdown logs
are attached to the native test report even when shutdown reports an error.

Three deliberately failing cases in `tests/Architecture.Probes` test missing telemetry,
invalid composition and original assertion failures. Passing parent tests run each
through native MSTest and assert its exit code and TRX diagnostic. Startup failures
and expected application errors remain in telemetry.

## Capture and persistence

`Qyl.Telemetry.Testing` owns OTLP decoding and the canonical trace, log and metric
models. The prototype has no copied decoder or flattened span model. A thin local
relay captures every protobuf export, forwards it to qyl, and returns the collector's
actual status, content type and response body. Nonempty valid acknowledgements are
preserved. Fully received exports finish even if the producer disconnects. Failed
forwards retain the completed stages and failure category; they are never recorded
as successful Collector responses. Rejected payloads and diagnostics remain in the capture.

OpenTelemetry .NET 1.20.0 can repeat identical HTTP attributes from logging state
and scopes ([upstream issue](https://github.com/open-telemetry/opentelemetry-dotnet/issues/4324)).
Testing reports these nonconforming records as partial rejections and retains their
original payloads. The Collector coalesces identical values and reports that normalization;
conflicting values remain errors. Decoded capture counts can therefore differ from
stored counts without implying that the original export was lost.

The test process uses the official SDK for invocation roots and HTTP client spans.
Application processes use `AddQyl()` for traces, logs and metrics. Exporter forwarding
is suppressed from instrumentation to prevent export loops. Architecture assertions
wait for the declared store evidence and matching client/server spans with a bounded
deadline; ongoing background spans do not require a quiet period. Stored-data tests await the actual persisted
record separately.

`TestTrace.Telemetry.Capture` retains every captured record and original payload.
The trace view selects its trace ID. Its call graph uses unique span identities while
raw retransmissions remain visible. Database identities retain system, server and
namespace rather than merging stores with the same name.

Each collector retains its DuckDB database, process output and `capture.json` under
`qyl-data/<run>` beside the test assembly. The capture includes original exports and
both capture and collector responses. Assembly cleanup attaches these evidence files,
stops services before draining the receiver, then shuts down the Collector and releases
their ports. `shutdown.json` records the Collector's exit code and whether shutdown
required forced termination.

## Traces, logs and metrics

A successful order emits a `checkout.order.created` span event and increments the
monotonic `checkout.orders.created` counter. Its structured `OrderCreated` log retains
the same trace, server span and service instance. Payment and failure logs retain their
named events as well. Custom application event names are supported.

Tests check typed values, ordered span events, timestamps, trace flags, resource and
scope metadata. The raw capture keeps the original exception event. Tests verify that
the Collector retains harmless exception messages, stack traces and type; its bounded
sanitizer redacts embedded credentials before persistence.

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
  -c Release --minimum-expected-tests 78 --report-trx \
  --coverage-settings coverage.config --coverage-output coverage.cobertura.xml \
  > artifacts/coverage.json 2> artifacts/coverage.stderr
```

With both percentage thresholds disabled, DotCov reports its documented exit 4.
The build accepts that informational outcome only after the native tests pass and
coverage data is present and unambiguous. Other tool failures still fail the build.

## Releases

After committing and pushing the version and changelog to `main`, run
`./release.sh <x.y.z>`. It archives that exact commit and runs the native tests,
global DotCov and JetBrains checks from the extracted source before creating the tag
and GitHub release. The release includes the source archive and coverage/inspection
reports; `artifacts/release-<x.y.z>/` records the commit, complete gate log and retained
source path for inspecting every telemetry phase.
To verify only affected tests, append `--filter '<MSTest filter>' --minimum-tests <count>`.
The release includes `test-scope.txt` so its verification scope is explicit.
