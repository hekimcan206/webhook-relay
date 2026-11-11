# 0003: Exponential backoff with proportional jitter, Retry-After, and a dead-letter queue

- Status: accepted
- Date: 2026-11-11

## Context

Endpoints fail for seconds (deploys), minutes (incidents) or days (abandoned integrations). Retrying too eagerly
hammers a struggling receiver and wastes relay capacity; retrying too slowly delays recovery. When an endpoint comes back
after an outage, thousands of deliveries that failed at the same moment must not all retry in the same second.

## Options considered

**Schedule**

1. *Fixed interval.* Either too aggressive for long outages or too slow for blips.
2. *Fixed table* (the Standard Webhooks example: 5 s, 5 min, 30 min, 2 h, 5 h, 10 h, 14 h, 20 h, 24 h). Clear, but not
   tunable per deployment without editing code.
3. *Exponential with a cap* (chosen): `base × multiplier^(n−1)`, capped. Defaults 10 s × 3, cap 12 h, 14 attempts: the
   nominal schedule is 10 s, 30 s, 90 s, 4.5 min, 13.5 min, 40.5 min, 2 h, 6 h, then 12 h: the last retry lands about 2.9 days in nominally (2.7 days median once jitter and the cap are applied),
   close to the spec's example (about 3.1 days).

**Jitter**

1. *None.* A herd that failed together retries together, forever.
2. *Full jitter* (uniform in [0, d]). AWS's analysis found it does the least total work under contention, but an
   individual retry can fire almost immediately after a failure, and the schedule is hard to explain to a receiver.
3. *Decorrelated jitter.* Spreads well, but the next delay depends on the previous draw, which complicates a stateless,
   database-scheduled design.
4. *Proportional jitter* (chosen): uniform in [0.75 d, 1.25 d]. Keeps the schedule recognisable ("about 90 s") while
   spreading a herd across half the nominal interval. Full jitter is available as a setting.

## Decision

- `RetryPolicy` is pure: the random draw is a parameter, so property tests check every delay lies within its bounds and
  never exceeds the cap, and the benchmark samples the real distribution without waiting days.
- Only 2xx is success. 410 Gone disables the endpoint and dead-letters the delivery (spec guidance). Everything else
  (timeouts, refused or reset connections, 3xx, 4xx, 5xx) is retried. Redirects are not followed (ADR 0006).
- `Retry-After` on 429/503 is honoured when it asks for **more** time than the backoff, never less, and never past the
  cap, so a misbehaving receiver cannot park a message for a year.
- When attempts are exhausted the delivery becomes `dead`: the dead-letter queue is the same table, queryable and
  replayable from the panel/API (one delivery or every dead delivery of an endpoint). Replay resets the attempt budget,
  keeps the webhook-id, and is recorded (`replay_count`, and per attempt).
- Deliveries older than `MaxMessageAge` (default 4 days) are dead-lettered even with attempts left, so a delivery
  waiting behind an open circuit does not wait forever.

## Consequences

- The measured herd test (10,000 deliveries failing together, retry 3): no jitter puts all 10,000 in one second;
  proportional jitter peaks at 254 per second spread over 46 seconds; full jitter at 134 per second over 90 seconds
  (exact numbers in `docs/benchmarks.md`).
- Retries are found by the worker's poll, so the observed gap is the drawn delay plus up to one poll interval; the
  benchmark measures that overshoot.

## Sources

- Marc Brooker, "Exponential Backoff And Jitter", AWS Architecture Blog:
  https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
- Standard Webhooks specification, retry schedule and 410 handling:
  https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md
- RFC 9110 §10.2.3, Retry-After: https://www.rfc-editor.org/rfc/rfc9110#section-10.2.3
