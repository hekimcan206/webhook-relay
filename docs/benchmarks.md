# Benchmarks

Every number here was measured on the machine below and comes from the JSON in `benchmark-results/`. Medians are
reported where a scenario was repeated. Nothing is extrapolated.

## Environment

| | |
|---|---|
| CPU | 13th Gen Intel Core i7-13620H (10 cores, 16 threads), laptop |
| RAM | 15.7 GB host; Docker Desktop (WSL2) VM limited to 7.6 GB |
| OS | Windows 11 Home 25H2 |
| .NET | SDK 10.0.401, runtime 10.0.12, Release build |
| PostgreSQL | `postgres:17-alpine` in Docker (Testcontainers), default configuration (`fsync` and `synchronous_commit` on) |
| Relay and receiver | In the benchmark process on the host, talking to PostgreSQL through the Docker port mapping |
| Host load | **Not idle.** Another project's Oracle 23 and PostgreSQL containers were running and busy throughout; sampled CPU load was 34–71% before the runs |

The relay and the receiver share one process and one machine, so the receiver's work competes with the relay for CPU.
Treat the numbers as a lower bound for this design on a laptop, not as a capacity figure for a server.

How to reproduce (Docker running):

```bash
dotnet run -c Release --project benchmarks/WebhookRelay.Benchmarks -- throughput   # or latency | retry | breaker | all
dotnet run -c Release --project benchmarks/WebhookRelay.Benchmarks -- micro --filter '*'
```

## 1. Delivery throughput

**Method.** 20,000 deliveries are inserted straight into the queue with SQL (publishing cost excluded), then a relay
starts and drains them into an in-process Kestrel receiver that verifies every signature and answers 200 at once.
Throughput is deliveries divided by the time from the receiver's first to its last request (host start-up excluded).
Payload 230 bytes. Batch size 100. One warm-up run, then three runs per configuration; the median is reported.

| Endpoints | Concurrency | Before (4-statement transaction) | After (single statement) |
|---:|---:|---:|---:|
| 1 | 16 | 754 /s | **956 /s** |
| 1 | 64 | 1,457 /s | **2,064 /s** |
| 10 | 64 | 1,294 /s | **1,962 /s** |
| 10 | 128 | 1,476 /s | **2,456 /s** |

*Before* recorded every outcome in a transaction of four statements (`BEGIN`, breaker check, delivery update, attempt
insert, `COMMIT`). Each attempt therefore cost five round trips and a commit, so the common case (a 2xx on an
endpoint whose breaker is clean) became one statement with data-modifying CTEs: one round trip, one commit, same
fencing. Failures and probes still take the locked, transactional path. Raw runs: `throughput-before-single-statement-success.json`
and `throughput.json`.

What bounds it: each delivery still costs one claim (amortised over the batch) and one commit, so on this laptop the
ceiling is PostgreSQL commit latency through Docker Desktop. Batching outcome writes across deliveries is the next step
if more is needed.

## 2. Enqueue-to-delivery latency

**Method.** A relay idles with the production poll interval (1 s). A client publishes through the HTTP API at a fixed
rate (scheduled sends, not a closed loop). Latency is measured on one clock from the moment the publish request starts
to the moment the receiver gets the webhook, so it includes the API call, the database commit, the NOTIFY wake-up, the
claim and the HTTP delivery. 50 warm-up messages are excluded.

| Rate | Messages | Wake-up | p50 | p95 | p99 | max | Publish API p50 / p95 |
|---:|---:|---|---:|---:|---:|---:|---:|
| 50 /s | 1,000 | NOTIFY | **6.5 ms** | 15.4 ms | 26.7 ms | 58 ms | 3.9 / 10.4 ms |
| 200 /s | 4,000 | NOTIFY | **7.3 ms** | 21.6 ms | 55.0 ms | 136 ms | 4.4 / 15.0 ms |
| 500 /s | 6,000 | NOTIFY | **9.2 ms** | 50.0 ms | 113 ms | 187 ms | 5.6 / 24.4 ms |
| 50 /s | 1,000 | poll only | 510 ms | 976 ms | 1,018 ms | 1,043 ms | 2.8 / 7.6 ms |

LISTEN/NOTIFY cuts median latency from half the poll interval (as expected for uniform arrivals) to single-digit
milliseconds. The poll-only row is what a deployment gets with `NotifyOnPublish=false`, the escape hatch if NOTIFY's
commit-time lock ever limits publish throughput. These latency runs were taken before the single-statement change in
section 1; the change only affects work after the receiver has the message, which is not part of this measurement.

## 3. Retry schedule distribution

### Default policy (sampled, no network)

10 s × 3ⁿ⁻¹, cap 12 h, proportional jitter ±25%, 14 attempts. 100,000 draws per retry (`retry.json`):

| After failed attempt | Nominal | p5 | p50 | p95 |
|---:|---:|---:|---:|---:|
| 1 | 10 s | 7.8 s | 10.0 s | 12.3 s |
| 2 | 30 s | 23.2 s | 30.0 s | 36.7 s |
| 3 | 90 s | 69.8 s | 90.0 s | 110.2 s |
| 4 | 4.5 min | 3.5 min | 4.5 min | 5.5 min |
| 5 | 13.5 min | 10.5 min | 13.5 min | 16.5 min |
| 6 | 40.5 min | 31.4 min | 40.5 min | 49.6 min |
| 7 | 2.0 h | 1.6 h | 2.0 h | 2.5 h |
| 8 | 6.1 h | 4.7 h | 6.1 h | 7.4 h |
| 9–13 | 12 h (cap) | 9.3 h | 12 h | 12 h |

The cap is applied after jitter, so from retry 9 the upper half of the band collapses onto 12 h. The time from the
first attempt to the last retry is 61.2 h (p5), **65.5 h (p50)**, 69.0 h (p95): about 2.7 days, similar to the
Standard Webhooks example schedule.

### Herd spreading

10,000 deliveries that failed in the same instant (an endpoint outage), at retry 3 (nominal 90 s):

| Jitter | Peak retries in one second | Spread over |
|---|---:|---:|
| none | 10,000 | 1 s |
| proportional ±25% (default) | **254** | 46 s |
| full (uniform 0–d) | 134 | 90 s |

### Observed schedule

The same policy scaled down (200 ms × 2ⁿ⁻¹, cap 3.2 s, 6 attempts) against a receiver that always answers 503, 200
deliveries, poll interval 100 ms. Gaps between consecutive attempts as seen by the receiver:

| After attempt | Configured band | min | p50 | p95 | max | below band |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 150–250 ms | 155 | 241 | 364 | 579 | 0 |
| 2 | 300–500 ms | 319 | 471 | 635 | 861 | 0 |
| 3 | 600–1,000 ms | 624 | 863 | 1,084 | 1,179 | 0 |
| 4 | 1,200–2,000 ms | 1,231 | 1,745 | 2,050 | 2,216 | 0 |
| 5 | 2,400–3,200 ms | 2,437 | 3,212 | 3,310 | 3,361 | 0 |

No retry ever fired early. Retries fire late by up to roughly one poll interval plus claim and connection time
(p95 overshoot 50–135 ms past the top of the band), because a due retry is found by the next poll, not by a timer.
With the production poll interval of 1 s and delays of seconds to hours this is negligible except for the first retry.

## 4. Circuit breaker under a flapping endpoint

**Method.** The receiver alternates 3 s up (200) and 3 s down (503). A client publishes 40 messages/s for 30 s
(1,200 messages). Retry 500 ms × 2, cap 8 s, 30 attempts. Breaker: 5 consecutive failures, open 1 s doubling to 8 s.
Each configuration ran twice; both runs are shown (`breaker.json`).

| | Breaker on (run 1 / 2) | Breaker off (run 1 / 2) |
|---|---:|---:|
| Messages delivered | 1,200 / 1,200 | 1,200 / 1,200 |
| Dead letters | 0 / 0 | 0 / 0 |
| HTTP attempts | 1,232 / 1,230 | 2,586 / 2,534 |
| **Attempts against the down endpoint** | **32 / 30** | **1,386 / 1,334** |
| Attempts per message | 1.03 / 1.02 | 2.15 / 2.11 |
| Times opened / closed | 10 / 5 (both runs) | — |
| Time open | 16.5 s / 16.2 s | — |
| Latency p50 | 537 / 503 ms | 140 / 137 ms |
| Latency p95 | 3.23 / 3.15 s | 3.93 / 3.96 s |
| Latency max | 3.7 / 3.7 s | 21.4 / 4.4 s |
| Drain after publishing stopped | 0.7 / 0.5 s | 7.0 / 2.5 s |

Reading it honestly:

- The breaker cut attempts against the down endpoint by **about 44×** (31 vs 1,360 on average). Those are the requests
  that hammer a struggling receiver and hold relay worker slots.
- It costs median latency: messages published during a down phase wait for the open period and the probe instead of
  being retried on their own short schedule, so p50 rises from about 140 ms to about 520 ms.
- The tail improves: without the breaker, messages that failed several times back off to multi-second delays and some
  land in the next down phase again (max 21 s in run 1); with the breaker, everything waiting is released together as
  soon as a probe succeeds.
- Each down phase opened the circuit twice: once on the first failures, and once more when the 1 s probe found the
  endpoint still down (the open period then doubled), which matches the configured behaviour.

## 5. Signing cost (BenchmarkDotNet)

ShortRun job, so error bars are wide; `benchmark-results/micro.md` has the full table.

| Operation | 1 KiB body | 64 KiB body | Allocated |
|---|---:|---:|---:|
| Sign, one secret | 1.43 µs | 30.6 µs | 944 B |
| Sign, two secrets (rotation) | 2.78 µs | 61.5 µs | 2,128 B |
| Verify | 1.58 µs | 30.8 µs | 432 B |
| Verify a two-signature header | 1.89 µs | 32.4 µs | 680 B |

Signing is not a bottleneck: at 2,000 deliveries/s it uses about 3 ms of CPU per second for 1 KiB payloads.

## Not measured

- Multiple relay instances against one database (correctness is tested, throughput is not).
- A dedicated database server or tuned PostgreSQL; everything ran on default settings in Docker Desktop.
- Receivers on another machine or across a real network.
