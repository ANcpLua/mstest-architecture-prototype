# Architecture tests with qyl

The prototype runs Orders and Payments as separate .NET processes. MSTest architecture
attributes select the services and decide whether their stores are independent or shared.
The assertions inspect the spans that those processes actually export over OTLP/HTTP.

```csharp
[Microservices]
[Service<Orders.Api.EntryPoint>("orders-api")]
[Service<Payments.Api.EntryPoint>("payments-api")]
public sealed class CheckoutTests
{
    // SystemUnderTest.Current.Client<Orders.Api.EntryPoint>() reaches the running service.
    // SystemUnderTest.Current.TraceAsync() returns the test's exported trace.
}
```

The tests use MSTest SDK 4.5.1, Microsoft.Testing.Platform 2.5.1 and Microsoft
coverage 18.12.0.

Run the tests and inspections with the .NET SDK pinned in `global.json`:

```sh
./build.sh
```

The gate restores the JetBrains tool pinned in `.config/dotnet-tools.json`, runs all
eight tests, and runs InspectCode against their build outputs. It fails on inspection
warnings, errors or suggestions; the report is `artifacts/jb-inspections.xml`.
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
endpoint, with a short export interval and sampling enabled. No external collector or
database is needed. The stores demonstrate ownership through spans; they are not persistent
databases.

The package's `qyl-telemetry` guide documents the orchestrator integration and the
child environment settings used here for the OTLP endpoint and HTTP/protobuf protocol.

Aspire's AppHost, SDK and testing package have been removed. `Qyl.Orchestration` contains
the supervisor shared with the qyl CLI; the prototype has no custom service launcher and
uses its published NuGet package. No sibling clone or copied orchestration source is needed.
`qyl-api-schema` describes collector APIs, which this local trace receiver does not call.

The checkout tests cover microservices, a distributed monolith and a single-service monolith,
including the exported qyl store spans and the orders-to-payments call. Hosting tests cover
process isolation, service selection, shutdown, startup cancellation and a missing dependency's
startup error. A discovery test launches the test runner with `--list-tests json` and checks
the original file and line for every architecture test. MSTest reads these locations from
the original `[TestMethod]` during discovery; the execution wrapper does not need to copy
them or suppress a caller-information inspection.

## Coverage

Use DotCov 4.7.0 or later. `dotcov test` flushes pending Microsoft coverage counters
when the expected startup-error child terminates. `coverage.config` measures all three
source assemblies, including their child processes. The repository has no coverage
threshold policy, so `--min-line 0` leaves the gate disabled (exit 4).

```sh
mkdir -p artifacts
dotcov test tests/Checkout.Tests/Checkout.Tests.csproj --min-line 0 --format json -- \
  -c Release --minimum-expected-tests 8 --report-trx \
  --coverage-settings coverage.config --coverage-output coverage.cobertura.xml \
  > artifacts/coverage.json
```

DotCov writes each run's XML, test results and log into a fresh `TestResults/<run>`
directory, named on stderr. Keep these with the JSON and diagnostics. All three
assemblies and all 14 tracked C# source files should appear. Uncovered lines and
partial branches identify behavior the tests have not exercised.
