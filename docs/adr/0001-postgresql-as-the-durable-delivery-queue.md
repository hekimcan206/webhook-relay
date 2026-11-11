# 0001: PostgreSQL as the durable delivery queue (SKIP LOCKED claims with leases)

- Status: accepted
- Date: 2026-11-11

## Context

A webhook relay accepts an event from a producer and must get it to every subscribed endpoint, however long the
endpoint is down, without losing it if the relay itself crashes. That needs a durable queue with:

1. **Atomic enqueue with fan-out.** A published event becomes one delivery per subscribed endpoint, all or nothing.
2. **Exclusive claims across instances.** Two relay processes must not send the same delivery at the same time.
3. **Crash recovery.** A delivery claimed by a process that dies must become claimable again, automatically.
4. **Per-delivery scheduling.** Each delivery has its own next-attempt time (backoff), and breakers pause whole endpoints.
5. **Queryability.** The replay panel lists dead letters, filters by endpoint, and shows every attempt.

## Options considered

1. **RabbitMQ / a broker with delayed messages and a DLX.** Strong at (2) and (3), but per-message scheduling over days
   needs plugins or TTL-queue ladders, pausing one endpoint while others flow needs a queue per endpoint, and (5) means
   a second store for history anyway. Two systems to operate and keep consistent with the producer's database.
2. **Kafka.** Ordered partitions are the wrong shape: one slow endpoint blocks its partition (head-of-line), and a
   per-message retry schedule needs retry topics. Excellent for the producer side, not for per-endpoint redelivery.
3. **Redis streams / sorted sets.** Fast, but durability depends on AOF settings, and history plus replay queries
   are awkward.
4. **PostgreSQL table claimed with `SELECT ... FOR UPDATE SKIP LOCKED`** (chosen). PostgreSQL documents `SKIP LOCKED` as
   intended for queue-like tables accessed by multiple consumers; rows locked by another transaction are skipped
   instead of waited on.

## Decision

- `relay.messages` stores each event once; `relay.deliveries` is the queue, one row per (message, endpoint), created in
  the same statement as the message (`INSERT ... RETURNING` chained through CTEs), so (1) holds by construction.
- A worker claims due rows with `FOR UPDATE OF d SKIP LOCKED` and, in the same statement, sets `status = 'in_flight'`,
  `attempts = attempts + 1`, `lease_until = now() + lease` and `leased_by = <instance id>`. The claim transaction
  commits immediately; the HTTP call happens outside any transaction, so no row lock is held across the network.
- Rows `in_flight` with an expired lease are claimable again: that is the crash recovery path (3). The lease must be at
  least twice the request timeout (validated at start-up) so a live attempt is never re-claimed.
- Recording an outcome is fenced on `(status = 'in_flight', leased_by = me, attempts = n)`. If the lease expired and
  another instance re-claimed the row, the stale result is written to the attempt log with `lease_lost = true` but does
  not overwrite the newer state.
- All scheduling timestamps come from the database clock (`now()`), never from the relay hosts, so instances with
  skewed clocks agree on what is due.
- Partial indexes (`WHERE status IN ('pending','retrying')`, `WHERE status = 'in_flight'`) keep the claim cost independent
  of how many delivered rows accumulate.
- An idle worker sleeps until `NOTIFY relay_deliveries` (sent in the publish transaction, so only on commit) or the
  poll interval, whichever comes first. Retries that come due are found by the poll.

## Consequences

- One database to back up, monitor and reason about; history and queue state are queryable with SQL.
- Attempt-count increments at claim time, so a delivery that crashes the relay every time (a poison message) still runs
  out of attempts instead of looping forever.
- Throughput is bounded by commit rate: every attempt is two transactions (claim is amortised over a batch, the outcome
  is one transaction per attempt). Measured numbers are in `docs/benchmarks.md`; this is far below a broker's ceiling and
  is the main thing to revisit for very high volume (batched outcome writes, or a broker in front).
- `NOTIFY` takes a global lock at commit; at very high publish rates it can serialise commits. It can be turned off
  (`Relay:Delivery:NotifyOnPublish=false`), trading latency for the poll interval (measured in `docs/benchmarks.md`).
- Retries are picked up on the next poll, so their timing has up to one poll interval of extra delay.
- No global ordering: deliveries to one endpoint can arrive out of order (concurrent attempts, retries). Receivers must
  not rely on order; events carry their own timestamps or versions (see ADR 0005).

## Sources

- PostgreSQL documentation, `SELECT`, "The Locking Clause" (`SKIP LOCKED` for queue-like tables):
  https://www.postgresql.org/docs/17/sql-select.html#SQL-FOR-UPDATE-SHARE
- PostgreSQL documentation, `NOTIFY`: https://www.postgresql.org/docs/17/sql-notify.html
