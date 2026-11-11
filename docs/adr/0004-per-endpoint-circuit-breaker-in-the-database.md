# 0004: A per-endpoint circuit breaker, stored on the endpoint row

- Status: accepted
- Date: 2026-11-11

## Context

Backoff is per delivery. When an endpoint goes down with 5,000 deliveries queued, each of them still gets tried on
its own schedule: thousands of doomed requests, connection attempts and timeouts that hold worker slots other endpoints
need, and every one of those attempts burns that delivery's retry budget. What is wanted is: notice the endpoint is
down, stop sending to it, check occasionally with a single request, and resume when it answers.

## Options considered

1. **Polly / Microsoft.Extensions.Http.Resilience breaker in process.** Mature, but state lives in one process: with
   three relay instances there are three breakers that open and probe independently, and a restart forgets everything.
   It also short-circuits by throwing at call time, after the delivery was already claimed and its attempt counted.
2. **Failure-rate breaker over a sliding window** (e.g. open when more than 50% of the last 60 s failed, with a minimum
   throughput). Better for high-volume endpoints with background noise, but needs per-endpoint windows shared across
   instances (a counter table or Redis), more state than this project warrants.
3. **Consecutive-failure breaker persisted on the endpoint row** (chosen), with the transitions as a pure state machine
   (`CircuitBreaker` in `WebhookRelay.Core`) applied under the row lock.

## Decision

- States `closed → open → half_open → closed|open`. `FailureThreshold` consecutive failures open it (default 5). The open
  period starts at `OpenDuration` (30 s) and doubles on each failed probe up to `MaxOpenDuration` (10 min), so a flapping
  endpoint is probed less and less often. A success closes it and forgets the history.
- **The claim query only takes deliveries of closed-circuit endpoints.** Deliveries behind an open circuit are not
  claimed, not attempted and **do not consume attempts**: an outage of hours does not dead-letter a backlog that would
  succeed after recovery. `MaxMessageAge` still bounds how long they wait.
- **One probe across all instances:** a separate claim step locks endpoint rows whose open period ended
  (`FOR UPDATE SKIP LOCKED`), moves each to `half_open` with a probe lease, and claims exactly one due delivery as the
  probe in the same transaction. If the prober dies, the probe lease expires and another instance probes.
- Outcome recording applies `OnSuccess`/`OnFailure` under `SELECT ... FOR UPDATE` on the endpoint row. A success on an
  already-clean endpoint (the common case) skips the lock, so one hot endpoint does not serialise all its deliveries
  on one row; the cost is that a failure racing that unlocked read may survive one interleaved success.
- Every transition is appended to `relay.circuit_events` (application role: insert and select only), for the panel and
  for the flapping-endpoint measurement.
- Operators can force a reset (`POST /endpoints/{id}/enable`).

## Consequences

- Measured under a 3 s up / 3 s down flapping endpoint (see `docs/benchmarks.md`): with the breaker, far fewer attempts
  hit the endpoint while it is down, at the price of higher delivery latency for messages that wait out an open period.
- **A known blind spot, found by the compose demo:** a receiver that rejects the *first* attempt of every message (but
  accepts retries) keeps the breaker open indefinitely, because the probe is usually a delivery that has never been
  tried. The demo receiver was changed to fail every third first attempt, and the behaviour is documented here rather
  than hidden. A failure-rate window (option 2) or preferring already-attempted deliveries as probes would address it.
- Consecutive failures are a blunt signal on very high-volume endpoints where a small error rate is normal; option 2
  is the upgrade path.

## Sources

- Martin Fowler, "CircuitBreaker": https://martinfowler.com/bliki/CircuitBreaker.html
- Microsoft, "Circuit Breaker pattern" (Azure Architecture Center):
  https://learn.microsoft.com/azure/architecture/patterns/circuit-breaker
- Standard Webhooks specification (disable endpoints after repeated failures):
  https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md
