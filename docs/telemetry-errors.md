# v5.0.1 telemetry error audit

The released-source run on 16 October 2026, 12:38:27–12:39:42 CEST, passed all
89 tests. Its six collector databases contained 524 traces, 1,208 spans, 2,205 logs
and 31 metric names. This audit examines all 81 error spans and both error logs,
including the three intentionally failing child-process probes.

| Recorded error | Spans | Cause |
| --- | ---: | --- |
| HTTP 404 | 44 | `QylStoresCompletedTestRootAndClient` polls for the completed trace before the test host's batch export arrives. All 44 belong to this test. |
| Connection error | 31 | Loopback readiness requests during service or collector startup, including tests that deliberately fail startup. |
| HTTP 500 from failed orders | 4 | Client and server spans for the two `InvalidPaymentResponsesFailTheOrder` cases. |
| HTTP 502 from payments | 1 | The upstream failure deliberately injected by one of those payment tests. |
| HTTP 500 from malformed OTLP | 1 | The receiver let a protobuf decoding exception become a server error. The input is deliberately malformed; the response status was a defect. |

The two error logs are the failed-order requests: one `InvalidOperationException`
for an invalid payment response and one `HttpRequestException` for the injected
upstream failure. Their trace IDs match those tests. They are expected evidence
that application failures reach qyl.

Connection errors were matched to test execution intervals and loopback
destinations. Nine came from `CollectorHasItsOwnEndpointAndCanBeDisposedTwice`.
The others occurred while services were starting or in the explicit missing
collector, missing dependency and cached startup failure cases. These requests
can fail before a child binds its port; the health probe retries until readiness
or the expected startup failure. They are retained as observed requests.

Counts are spans, not failed tests or distinct requests. One request can produce
both client and server spans. Batching and startup timing affect these counts
between runs, so a clean test run does not require zero error records.

## Receiver fixes in v5.0.2

The malformed trace request now receives HTTP 400 with the original decoder
diagnostic encoded as `google.rpc.Status.message`. This follows the
[OTLP bad-data contract](https://opentelemetry.io/docs/specs/otlp/#bad-data).
The invalid payload is neither forwarded nor recorded locally. The regression
test checks the response status, content type, diagnostic bytes and unchanged
recorded and forwarded span counts.

The audit also found that the receiver recorded spans before awaiting qyl's
acknowledgement. A failed or partial export could therefore satisfy a local
architecture assertion without reaching qyl. Recording now happens after the
export succeeds. Two regression cases inject a transport failure and a partial
acknowledgement; both must remain server failures and leave the recorder empty.
These deliberately failed exports also remain visible in telemetry.

All three regression cases failed against v5.0.1 behavior and passed with the
receiver fixes. No coverage exclusions, sampling changes, telemetry filters or
log-level changes were added.

The demonstrated defects were in this prototype's forwarding receiver. The
audited records do not show a qyl collector or instrumentation defect: qyl
recorded the actual probe responses and application failures.
