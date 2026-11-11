# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-11

### Added

- Standard Webhooks v1 signing and verification library (`WebhookRelay.Signing`): `webhook-id`, `webhook-timestamp`,
  `webhook-signature`, HMAC-SHA256 with `whsec_` secrets, multiple signatures during rotation, ±5 minute timestamp
  tolerance, constant-time comparison, bounded header parsing. Reproduces the reference test vector.
- Delivery policy (`WebhookRelay.Core`): exponential backoff with proportional or full jitter and Retry-After support,
  a per-endpoint circuit breaker state machine with growing open periods, attempt classification per the
  specification (2xx success, 410 disables, everything else retried), and an egress policy for SSRF protection.
- Relay service: PostgreSQL delivery queue claimed with `FOR UPDATE SKIP LOCKED` and leases, crash recovery through
  lease expiry, fenced outcome recording, LISTEN/NOTIFY wake-ups, fan-out by event type, producer idempotency keys,
  a dead-letter queue, secret rotation, endpoint enable/disable, an append-only attempt log and circuit event log.
- Replay panel (static page over the admin API) and replay API for single deliveries and the whole dead-letter queue.
- Receiver samples in C#, Node and Python, each a from-scratch verifier tested in CI against signature vectors the
  relay generates in the same job, plus idempotent processing by `webhook-id`.
- Fault-injection integration tests: receivers that time out, return 5xx, close the connection or are slow; mixed
  faults; two relay instances on one queue; the relay process killed hard twice mid-delivery.
- Benchmark harness: delivery throughput, enqueue-to-delivery latency (NOTIFY vs polling), retry schedule
  distribution, and circuit breaker behaviour under a flapping endpoint; BenchmarkDotNet for signing cost.
- `docker compose up` demo with three verifying receivers and a driver that checks the outcome; CI (build, tests,
  coverage, vulnerable-package audit, npm audit, pip-audit, samples, container, compose demo) and CodeQL.

### Performance

- Successful attempts on a healthy endpoint are recorded in a single statement (one round trip, one commit) instead
  of a four-statement transaction; see `docs/benchmarks.md` for the measured effect.

[1.0.0]: https://github.com/hekimcan206/webhook-relay/releases/tag/v1.0.0
