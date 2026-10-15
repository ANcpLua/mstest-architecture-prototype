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

Run with the .NET SDK pinned in `global.json`:

```sh
dotnet test --project tests/Checkout.Tests/Checkout.Tests.csproj --report-trx --results-directory TestResults
```

The test command builds the referenced API projects. The fixture composes their compiled
executables through `Qyl.Orchestration` 13.1.0. qyl reserves distinct loopback ports, passes
service references, waits for each `/health` response to match its child's process ID, and
owns process startup and shutdown. The fixture supplies the architecture's store names.
Startup failures include the child's recent output. Failed startup and assembly cleanup
stop the child processes. Only the selected services are launched.

`Qyl.Telemetry.Hosting` 25.0.2 supplies `builder.AddQyl()`, automatic HTTP instrumentation
and OTLP export. Its transitive SemanticConventions 10.0.1 package supplies `Spans.DbClient`
for the simulated in-memory store writes. Tests receive telemetry on an isolated local
endpoint, with a short export interval and sampling enabled. No external collector or
database is needed. The stores demonstrate ownership through spans; they are not persistent
databases.

Aspire's AppHost, SDK and testing package have been removed. `Qyl.Orchestration` contains
the supervisor shared with the qyl CLI; the prototype has no custom process launcher and
uses its published NuGet package. No sibling clone or copied orchestration source is needed.
`qyl-api-schema` describes collector APIs, which this local trace receiver does not call.

The checkout tests cover microservices, a distributed monolith and a single-service monolith,
including the exported qyl store spans and the orders-to-payments call. Hosting tests cover
process isolation, service selection, shutdown and a missing dependency's startup error.
