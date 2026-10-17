# Changelog

## 6.0.2

Unreleased.

- Align the applications with telemetry 28.0.0 and SemanticConventions 13.1.2.
- Consume qyl and orchestration 15.0.3, plus the canonical testing receiver 3.0.3.
- Keep API contracts 14.0.1; its contract revision is unchanged.
- Format the runner's minimum test count independently of the machine's locale.

## 6.0.1

- Consume qyl and orchestration 15.0.2 so services can flush final exports before their dependencies stop.
- Consume API contracts 14.0.1, which preserves the existing contract revision.
- Stop owned services before restarting the recorder, finish fully received exports after producer disconnects and retain failed forwards with their actual stage and error category.
- Shut down the Collector gracefully and keep its native exit code and forced-termination status beside the captured telemetry.
- Verify real client disconnects, forwarding failures and Collector shutdown. Document the upstream duplicate-scope-attribute diagnostics instead of suppressing them.

## 6.0.0

- Align qyl and orchestration at 15.0.0, telemetry at 27.0.0, contracts at 14.0.0
  and the canonical testing library at 3.0.2.
- Replace the duplicated OTLP decoder and span model with the published testing
  receiver and records. Keep every original export, typed signal and actual
  collector response in retained evidence.
- Wait for client/server evidence rather than a quiet interval. Preserve caller
  cancellation, separate invocation traces, original MSTest results and discovery locations.
- Record an actual order counter and business span event, and verify correlated
  named logs plus native HTTP/runtime metric types, units and resource identity.
- Use native application attribute names and types with SemanticConventions 13.1.1.
- Verify the Collector retains harmless exception messages and stack traces, alongside
  the original captured exception event.
- Keep coverage gaps visible without blanket library percentage thresholds;
  require passing native invariants, valid coverage data and clean inspections.

## 5.0.2

- Return HTTP 400 and a protobuf diagnostic for malformed trace exports instead
  of reporting them as server failures.
- Record forwarded spans only after qyl accepts their export. Failed and partial
  exports cannot satisfy local architecture assertions.
- Add regression tests for both forwarding failures and document the v5.0.1 error
  records without filtering startup probes or deliberate application failures.

## 5.0.1

- Export the test host's spans through qyl so persisted traces include the HTTP
  client parent and completed test root, not only the service spans.
- Stamp telemetry with the release version instead of the default `1.0.0`.
- Check persisted parent links and service versions in regression tests.
- Keep telemetry enabled in the API failure cases and require their error spans
  and error logs to reach qyl.

## 5.0.0

- Promote the qyl metrics and logs integration to stable after the 4.9.0 release
  source passes all 88 tests and both 100% coverage gates.

## 4.9.0

- Forward traces, metrics and logs to an isolated, published qyl 13.2.0 collector.
  Keep its database and output for inspection and stop it with the test run.
- Add typed qyl queries for trace-correlated logs and time-windowed metric buckets.
  Preserve pagination, units, aggregation and query failures.
- Exercise real structured application logs and qyl's automatic HTTP metrics,
  plus collector startup, cancellation, disposal and query failure cases.

## 4.8.3

- Build the test project directly so its child probes use the selected configuration
  in a clean checkout.
- Make the OTLP fixture's internal attribute helper private.

## 4.8.2

- Require 100% line and branch coverage when `./build.sh` runs the 76 tests through
  globally installed DotCov. Keep each run's coverage reports and existing source scope.
- Fix naming and style issues reported by JetBrains inspections in the build project.

## 4.8.1

- Test missing server-address features, empty bound-address collections and activity
  tag values whose text conversion returns null.
- Check OTLP span-kind mapping at both numeric extremes as well as every supported kind.
- Simplify span-kind mapping, export-route registration and startup exception handling
  while preserving their behavior and removing redundant compiler paths.

Validation: all 76 tests pass, with no skipped tests. All 467 reported lines and
293 branches are covered, with unchanged coverage scope and no new exclusions.
JetBrains inspections report zero warnings, errors or suggestions.

## 4.8.0

- Expand the MSTest suite from 8 to 67 tests covering architecture rules, OTLP decoding,
  API failures, process cleanup, cancellation and test-runner failure reporting.
- Reject invalid protobuf fields, truncated values, overflowing integers and oversized
  lengths with explicit input errors.
- Report shared-store architecture violations before checking individual store names.
- Retain original assertion failures and verify them through separate MSTest processes.

Validation: all 67 tests pass, with no skipped tests. Coverage is 468/468 lines (100%)
and 290/296 branches (97.97%). The six remaining branch outcomes stay visible in the
report. Coverage scope and exclusions are unchanged. JetBrains inspections report
zero warnings, errors or suggestions for both the solution and the failure-probe project.
