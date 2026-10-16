# Changelog

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
