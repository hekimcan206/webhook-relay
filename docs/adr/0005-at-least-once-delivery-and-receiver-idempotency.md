# 0005: At-least-once delivery; receivers deduplicate by webhook-id

- Status: accepted
- Date: 2026-11-11

## Context

A webhook crosses a network to a system we do not control. The relay can never know whether a receiver processed a
request whose response was lost: the receiver may have committed and then timed out, or the relay may have crashed
after the 2xx arrived but before it recorded it. Every delivery system picks which way to err.

## Options considered

1. **At-most-once:** mark delivered before sending. A crash or a lost request silently drops events. Unacceptable for
   payment- or order-style events.
2. **"Exactly-once":** impossible across an HTTP boundary without the receiver's cooperation; systems that claim it are
   at-least-once plus deduplication somewhere.
3. **At-least-once with a stable id per message** (chosen). The relay only marks a delivery delivered after a 2xx, in a
   transaction after the call, and every retry, replay and post-crash redelivery carries the same `webhook-id`.

## Decision

The guarantee, as tested:

- **No loss.** A delivery leaves the queue only as `delivered` (2xx seen) or `dead` (attempts exhausted, 410, or too
  old), and dead letters are kept for replay. A relay killed mid-delivery leaves `in_flight` rows that are re-claimed
  when their lease expires (`CrashRecoveryTests` kills the relay process hard, twice, with 300 messages in flight).
- **Bounded duplicates.** A duplicate needs an attempt whose effect happened but whose result was not recorded:
  a receiver timeout, a lost response, or a relay crash. Each relay crash duplicates at most the deliveries it held in
  flight (at most `MaxConcurrency`); each timed-out attempt duplicates at most once. The tests assert exactly these
  bounds from the receiver's side.
- **Detectable duplicates.** Every copy carries the original `webhook-id` and a fresh, valid signature.

Guidance for receivers (in the README and implemented in all three samples):

1. Verify the signature over the raw body first, then parse.
2. Use `webhook-id` as an idempotency key: record it in the **same transaction** as the side effect, with a unique
   constraint, and keep it at least as long as the retry window (about 3 days with default settings; 4 days with the
   default maximum message age). A cache with a 5-minute TTL, as sometimes suggested, does not cover multi-day retries.
3. Answer 2xx quickly and do slow work asynchronously; a receiver that times out turns its own slowness into duplicates.
4. Do not rely on ordering. Concurrent attempts and retries reorder events; use versions or timestamps in the payload.
5. Answer 410 only if the endpoint should be disabled permanently.

## Consequences

- Simple, standard semantics that every Standard Webhooks receiver already expects.
- Receivers must store processed ids; the samples show the rule with an in-memory set and say so.
- The measured duplicate rate under hard kills and fault mixes is in `docs/benchmarks.md` and the test suite.

## Sources

- Standard Webhooks specification, "Idempotency": https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md
- Stripe documentation, "Handle duplicate events" (webhook endpoints may receive the same event more than once):
  https://docs.stripe.com/webhooks#handle-duplicate-events
