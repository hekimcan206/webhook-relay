using Npgsql;
using NpgsqlTypes;
using WebhookRelay.Core.Breaker;
using WebhookRelay.Core.Delivery;
using WebhookRelay.Core.Retry;

namespace WebhookRelay.Server.Persistence;

/// <summary>
/// All SQL lives here. The queue is a PostgreSQL table claimed with <c>FOR UPDATE SKIP LOCKED</c>: competing relay
/// instances never block on each other's rows and never claim the same delivery while its lease is valid.
/// Every timestamp that drives scheduling comes from the database clock (<c>now()</c>), so relay instances with skewed
/// clocks still agree on what is due.
/// </summary>
internal sealed class RelayStore(NpgsqlDataSource dataSource)
{
    // Shared by the normal claim (endpoint = NULL: every closed-circuit endpoint) and the probe claim (one endpoint).
    private const string ClaimSql = """
        WITH due AS (
            SELECT d.id
            FROM relay.deliveries d
            JOIN relay.endpoints e ON e.id = d.endpoint_id
            WHERE e.disabled_at IS NULL
              AND (($4::uuid IS NULL AND e.circuit_state = 'closed') OR e.id = $4::uuid)
              AND ((d.status IN ('pending', 'retrying') AND d.next_attempt_at <= now())
                   OR (d.status = 'in_flight' AND d.lease_until < now()))
            ORDER BY d.next_attempt_at
            LIMIT $1
            FOR UPDATE OF d SKIP LOCKED)
        UPDATE relay.deliveries d
        SET status = 'in_flight', attempts = d.attempts + 1, lease_until = now() + $2, leased_by = $3
        FROM due, relay.messages m, relay.endpoints e
        WHERE d.id = due.id AND m.id = d.message_id AND e.id = d.endpoint_id
        RETURNING d.id, d.message_id, d.endpoint_id, e.url, m.event_type, m.payload, d.attempts, d.replay_count, m.created_at,
                  ARRAY(SELECT s.secret FROM relay.endpoint_secrets s
                        WHERE s.endpoint_id = d.endpoint_id AND (s.expires_at IS NULL OR s.expires_at > now())
                        ORDER BY s.id DESC)
        """;

    // ---- endpoints ---------------------------------------------------------------------------------------------

    public async Task CreateEndpointAsync(Guid id, string url, string description, IReadOnlyList<string>? eventTypes, string secret, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ExecAsync(conn, tx, "INSERT INTO relay.endpoints (id, url, description, event_types) VALUES ($1, $2, $3, $4)", ct,
            id, url, description, P(eventTypes?.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        await ExecAsync(conn, tx, "INSERT INTO relay.endpoint_secrets (endpoint_id, secret) VALUES ($1, $2)", ct, id, secret);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<EndpointView>> ListEndpointsAsync(Guid? id, CancellationToken ct)
    {
        const string sql = """
            SELECT e.id, e.url, e.description, e.event_types, e.disabled_at IS NOT NULL, e.disabled_reason, e.circuit_state,
                   e.consecutive_failures, e.open_until,
                   (SELECT count(*) FROM relay.endpoint_secrets s WHERE s.endpoint_id = e.id AND (s.expires_at IS NULL OR s.expires_at > now())),
                   COALESCE((SELECT jsonb_object_agg(status, n) FROM (SELECT status, count(*) AS n FROM relay.deliveries d
                             WHERE d.endpoint_id = e.id GROUP BY status) c), '{}'::jsonb)::text,
                   e.created_at
            FROM relay.endpoints e
            WHERE $1::uuid IS NULL OR e.id = $1::uuid
            ORDER BY e.created_at
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(P(id, NpgsqlDbType.Uuid));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<EndpointView>();
        while (await reader.ReadAsync(ct))
        {
            var counts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(10)) ?? [];
            list.Add(new EndpointView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<string[]>(3),
                reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), (int)reader.GetInt64(9), counts,
                reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return list;
    }

    /// <summary>Adds a new secret and lets the current ones expire after <paramref name="overlap"/>. Returns false if the endpoint is unknown.</summary>
    public async Task<DateTimeOffset?> RotateSecretAsync(Guid endpointId, string newSecret, TimeSpan overlap, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT 1 FROM relay.endpoints WHERE id = $1 FOR UPDATE", conn, tx))
        {
            check.Parameters.Add(P(endpointId));
            if (await check.ExecuteScalarAsync(ct) is null)
            {
                return null;
            }
        }

        DateTimeOffset expires;
        await using (var expire = new NpgsqlCommand("""
            WITH upd AS (
                UPDATE relay.endpoint_secrets SET expires_at = LEAST(COALESCE(expires_at, 'infinity'), now() + $2)
                WHERE endpoint_id = $1 AND (expires_at IS NULL OR expires_at > now())
                RETURNING 1)
            SELECT now() + $2, (SELECT count(*) FROM upd)
            """, conn, tx))
        {
            expire.Parameters.Add(P(endpointId));
            expire.Parameters.Add(P(overlap));
            expires = new DateTimeOffset((DateTime)(await expire.ExecuteScalarAsync(ct))!);
        }

        await ExecAsync(conn, tx, "INSERT INTO relay.endpoint_secrets (endpoint_id, secret) VALUES ($1, $2)", ct, endpointId, newSecret);
        await tx.CommitAsync(ct);
        return expires;
    }

    /// <summary>Enabling also resets the breaker: an operator re-enabling an endpoint expects deliveries to resume now.</summary>
    public async Task<bool> SetEnabledAsync(Guid endpointId, bool enabled, string? reason, CancellationToken ct)
    {
        var sql = enabled
            ? """
              UPDATE relay.endpoints SET disabled_at = NULL, disabled_reason = NULL, circuit_state = 'closed',
                     consecutive_failures = 0, consecutive_opens = 0, open_until = NULL, probe_lease_until = NULL
              WHERE id = $1
              """
            : "UPDATE relay.endpoints SET disabled_at = COALESCE(disabled_at, now()), disabled_reason = $2 WHERE id = $1";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(P(endpointId));
        if (!enabled)
        {
            cmd.Parameters.Add(P(reason ?? "disabled by operator"));
        }

        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<CircuitEventView>> CircuitEventsAsync(Guid endpointId, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT at, from_state, to_state, consecutive_failures, open_until FROM relay.circuit_events
            WHERE endpoint_id = $1 ORDER BY id DESC LIMIT $2
            """);
        cmd.Parameters.Add(P(endpointId));
        cmd.Parameters.Add(P(limit));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CircuitEventView>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CircuitEventView(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return list;
    }

    // ---- publish -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Stores the message and fans it out to every enabled, subscribed endpoint in one statement and one transaction,
    /// so a message is either fully enqueued or not at all. The NOTIFY is transactional too: it is only delivered on commit.
    /// </summary>
    public async Task<PublishResult> PublishAsync(
        string messageId, string eventType, string payload, string? idempotencyKey, IReadOnlyList<Guid>? endpointIds, bool notify, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int deliveries;
        await using (var cmd = new NpgsqlCommand("""
            WITH msg AS (
                INSERT INTO relay.messages (id, event_type, payload, idempotency_key) VALUES ($1, $2, $3, $4)
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING id),
            fan AS (
                INSERT INTO relay.deliveries (message_id, endpoint_id)
                SELECT msg.id, e.id FROM msg, relay.endpoints e
                WHERE e.disabled_at IS NULL
                  AND (e.event_types IS NULL OR $2 = ANY (e.event_types))
                  AND ($5::uuid[] IS NULL OR e.id = ANY ($5::uuid[]))
                RETURNING 1)
            SELECT (SELECT count(*) FROM msg), (SELECT count(*) FROM fan)
            """, conn, tx))
        {
            cmd.Parameters.Add(P(messageId));
            cmd.Parameters.Add(P(eventType));
            cmd.Parameters.Add(P(payload));
            cmd.Parameters.Add(P(idempotencyKey, NpgsqlDbType.Text));
            cmd.Parameters.Add(P(endpointIds?.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var inserted = reader.GetInt64(0);
            deliveries = (int)reader.GetInt64(1);
            await reader.CloseAsync();

            if (inserted == 0)
            {
                // Same idempotency key seen before: answer with the original message, or 409 if the request differs.
                await using var existing = new NpgsqlCommand("""
                    SELECT m.id, m.event_type = $2 AND m.payload = $3, (SELECT count(*) FROM relay.deliveries d WHERE d.message_id = m.id)
                    FROM relay.messages m WHERE m.idempotency_key = $1
                    """, conn, tx);
                existing.Parameters.Add(P(idempotencyKey));
                existing.Parameters.Add(P(eventType));
                existing.Parameters.Add(P(payload));
                await using var r = await existing.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                var result = new PublishResult(r.GetBoolean(1) ? PublishStatus.Duplicate : PublishStatus.IdempotencyConflict, r.GetString(0), (int)r.GetInt64(2));
                await r.CloseAsync();
                await tx.CommitAsync(ct);
                return result;
            }
        }

        if (notify && deliveries > 0)
        {
            await ExecAsync(conn, tx, "SELECT pg_notify('relay_deliveries', '')", ct);
        }

        await tx.CommitAsync(ct);
        return new PublishResult(PublishStatus.Created, messageId, deliveries);
    }

    // ---- claiming ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(int limit, TimeSpan lease, string relayId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await ClaimCoreAsync(conn, null, limit, lease, relayId, null, ct);
    }

    /// <summary>
    /// For every endpoint whose open period elapsed (or whose probe holder vanished), atomically move the breaker to
    /// half-open and claim exactly one delivery as the probe. Endpoint rows are locked with SKIP LOCKED, so two relay
    /// instances can never both probe the same endpoint.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimProbesAsync(TimeSpan lease, string relayId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var candidates = new List<(Guid Id, Circuit Circuit, DateTimeOffset Now)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, circuit_state, consecutive_failures, consecutive_opens, open_until, now()
            FROM relay.endpoints
            WHERE disabled_at IS NULL
              AND ((circuit_state = 'open' AND open_until <= now()) OR (circuit_state = 'half_open' AND probe_lease_until < now()))
            ORDER BY open_until NULLS FIRST
            LIMIT 20
            FOR UPDATE SKIP LOCKED
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                candidates.Add((reader.GetGuid(0), ReadCircuit(reader, 1), reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        var probes = new List<ClaimedDelivery>();
        foreach (var (id, stored, now) in candidates)
        {
            // A half-open circuit whose probe lease expired lost its probe (relay crash): treat it as open and due.
            var circuit = stored.State == CircuitState.HalfOpen ? stored with { State = CircuitState.Open, OpenUntil = now } : stored;
            var claimed = await ClaimCoreAsync(conn, tx, 1, lease, relayId, id, ct);
            if (claimed.Count == 0)
            {
                if (stored.State == CircuitState.HalfOpen)
                {
                    await SaveCircuitAsync(conn, tx, id, stored, CircuitBreaker.AbandonProbe(stored, now), null, ct);
                }

                continue;
            }

            await SaveCircuitAsync(conn, tx, id, circuit, CircuitBreaker.StartProbe(circuit, now), now + lease, ct);
            probes.Add(claimed[0] with { IsProbe = true });
        }

        await tx.CommitAsync(ct);
        return probes;
    }

    private static async Task<IReadOnlyList<ClaimedDelivery>> ClaimCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, int limit, TimeSpan lease, string relayId, Guid? endpointId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ClaimSql, conn, tx);
        cmd.Parameters.Add(P(limit));
        cmd.Parameters.Add(P(lease));
        cmd.Parameters.Add(P(relayId));
        cmd.Parameters.Add(P(endpointId, NpgsqlDbType.Uuid));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ClaimedDelivery>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ClaimedDelivery(
                reader.GetInt64(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<string[]>(9), false));
        }

        return list;
    }

    // ---- recording outcomes ------------------------------------------------------------------------------------

    /// <summary>
    /// Applies the outcome of one attempt in one transaction: breaker transition (under the endpoint row lock), delivery
    /// state, and the attempt log. The delivery update is fenced on (status, leased_by, attempts): if this relay's lease
    /// expired and another instance re-claimed the row, the stale result is logged but does not overwrite the newer state.
    /// </summary>
    public async Task<RecordedOutcome> RecordAsync(
        ClaimedDelivery delivery, AttemptOutcome outcome, DateTimeOffset startedAt, TimeSpan duration, string relayId,
        RetryPolicy retry, CircuitOptions circuitOptions, double random01, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        if (outcome.Succeeded && !delivery.IsProbe &&
            await TryRecordCleanSuccessAsync(conn, delivery, outcome, startedAt, duration, relayId, ct) is { } fast)
        {
            return fast;
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        CircuitState? from = null, to = null;
        var needsBreaker = !outcome.Succeeded || delivery.IsProbe || await BreakerDirtyAsync(conn, tx, delivery.EndpointId, ct);
        if (needsBreaker)
        {
            Circuit current;
            DateTimeOffset now;
            await using (var cmd = new NpgsqlCommand("""
                SELECT id, circuit_state, consecutive_failures, consecutive_opens, open_until, now()
                FROM relay.endpoints WHERE id = $1 FOR UPDATE
                """, conn, tx))
            {
                cmd.Parameters.Add(P(delivery.EndpointId));
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                current = ReadCircuit(reader, 1);
                now = reader.GetFieldValue<DateTimeOffset>(5);
            }

            var next = outcome.Kind switch
            {
                OutcomeKind.Delivered => CircuitBreaker.OnSuccess(current),
                OutcomeKind.Gone => current,
                _ => CircuitBreaker.OnFailure(current, now, circuitOptions),
            };
            if (next != current)
            {
                await SaveCircuitAsync(conn, tx, delivery.EndpointId, current, next, null, ct);
                if (next.State != current.State)
                {
                    (from, to) = (current.State, next.State);
                }
            }

            if (outcome.Kind == OutcomeKind.Gone)
            {
                await ExecAsync(conn, tx, """
                    UPDATE relay.endpoints SET disabled_at = COALESCE(disabled_at, now()), disabled_reason = '410 Gone from receiver'
                    WHERE id = $1
                    """, ct, delivery.EndpointId);
            }
        }

        var (status, delay) = outcome.Kind switch
        {
            OutcomeKind.Delivered => ("delivered", TimeSpan.Zero),
            OutcomeKind.Gone => ("dead", TimeSpan.Zero),
            _ when retry.CanRetry(delivery.Attempt) => ("retrying", retry.NextDelay(delivery.Attempt, random01, outcome.RetryAfter)),
            _ => ("dead", TimeSpan.Zero),
        };

        int updated;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE relay.deliveries
            SET status = $2, lease_until = NULL, leased_by = NULL, last_status_code = $3, last_error = $4,
                next_attempt_at = CASE WHEN $2 = 'retrying' THEN now() + $5 ELSE next_attempt_at END,
                delivered_at = CASE WHEN $2 = 'delivered' THEN now() ELSE delivered_at END,
                dead_at = CASE WHEN $2 = 'dead' THEN now() ELSE NULL END
            WHERE id = $1 AND status = 'in_flight' AND leased_by = $6 AND attempts = $7
            """, conn, tx))
        {
            cmd.Parameters.Add(P(delivery.Id));
            cmd.Parameters.Add(P(status));
            cmd.Parameters.Add(P(outcome.StatusCode, NpgsqlDbType.Integer));
            cmd.Parameters.Add(P(outcome.ErrorKind, NpgsqlDbType.Text));
            cmd.Parameters.Add(P(delay));
            cmd.Parameters.Add(P(relayId));
            cmd.Parameters.Add(P(delivery.Attempt));
            updated = await cmd.ExecuteNonQueryAsync(ct);
        }

        var leaseLost = updated == 0;
        await ExecAsync(conn, tx, """
            INSERT INTO relay.attempts (delivery_id, attempt, replay, started_at, duration_ms, status_code, outcome, error_kind, relay_id, lease_lost)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """, ct,
            delivery.Id, delivery.Attempt, delivery.Replay, startedAt, (int)Math.Min(int.MaxValue, duration.TotalMilliseconds),
            P(outcome.StatusCode, NpgsqlDbType.Integer), outcome.Kind.ToString().ToLowerInvariant(), P(outcome.ErrorKind, NpgsqlDbType.Text),
            relayId, leaseLost);

        await tx.CommitAsync(ct);
        return new RecordedOutcome(leaseLost ? "lease_lost" : status, leaseLost, from, to);
    }

    /// <summary>
    /// The hot path, in one statement and one implicit transaction (one round trip, one commit): a success on an
    /// endpoint whose breaker is clean marks the delivery delivered and appends the attempt. Returns null, having
    /// changed nothing, when the breaker is not clean; the caller then takes the full transactional path.
    /// Same fencing as the full path: if the lease was lost, the attempt is logged with lease_lost and the row is untouched.
    /// </summary>
    private static async Task<RecordedOutcome?> TryRecordCleanSuccessAsync(
        NpgsqlConnection conn, ClaimedDelivery delivery, AttemptOutcome outcome, DateTimeOffset startedAt, TimeSpan duration,
        string relayId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            WITH clean AS (
                SELECT circuit_state = 'closed' AND consecutive_failures = 0 AS ok FROM relay.endpoints WHERE id = $2),
            upd AS (
                UPDATE relay.deliveries
                SET status = 'delivered', lease_until = NULL, leased_by = NULL, last_status_code = $5, last_error = NULL,
                    delivered_at = now(), dead_at = NULL
                WHERE id = $1 AND status = 'in_flight' AND leased_by = $3 AND attempts = $4 AND (SELECT ok FROM clean)
                RETURNING 1),
            ins AS (
                INSERT INTO relay.attempts (delivery_id, attempt, replay, started_at, duration_ms, status_code, outcome, relay_id, lease_lost)
                SELECT $1, $4, $6, $7, $8, $5, 'delivered', $3, NOT EXISTS (SELECT 1 FROM upd)
                WHERE (SELECT ok FROM clean)
                RETURNING lease_lost)
            SELECT (SELECT ok FROM clean), (SELECT lease_lost FROM ins)
            """, conn);
        cmd.Parameters.Add(P(delivery.Id));
        cmd.Parameters.Add(P(delivery.EndpointId));
        cmd.Parameters.Add(P(relayId));
        cmd.Parameters.Add(P(delivery.Attempt));
        cmd.Parameters.Add(P(outcome.StatusCode, NpgsqlDbType.Integer));
        cmd.Parameters.Add(P(delivery.Replay));
        cmd.Parameters.Add(P(startedAt));
        cmd.Parameters.Add(P((int)Math.Min(int.MaxValue, duration.TotalMilliseconds)));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.IsDBNull(0) || !reader.GetBoolean(0))
        {
            return null;
        }

        var leaseLost = reader.GetBoolean(1);
        return new RecordedOutcome(leaseLost ? "lease_lost" : "delivered", leaseLost, null, null);
    }

    /// <summary>
    /// Inside the full path: a success on an endpoint whose breaker is already clean needs no endpoint lock, so
    /// a single hot endpoint does not serialise all its deliveries on one row. The read is unlocked; a failure racing it
    /// may survive one interleaved success, which only makes the breaker slightly more eager to open.
    /// </summary>
    private static async Task<bool> BreakerDirtyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid endpointId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT circuit_state <> 'closed' OR consecutive_failures > 0 FROM relay.endpoints WHERE id = $1", conn, tx);
        cmd.Parameters.Add(P(endpointId));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task SaveCircuitAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid endpointId, Circuit from, Circuit to, DateTimeOffset? probeLeaseUntil, CancellationToken ct)
    {
        await ExecAsync(conn, tx, """
            UPDATE relay.endpoints
            SET circuit_state = $2, consecutive_failures = $3, consecutive_opens = $4, open_until = $5, probe_lease_until = $6
            WHERE id = $1
            """, ct,
            endpointId, StateNames.Name(to.State), to.ConsecutiveFailures, to.ConsecutiveOpens,
            P(to.OpenUntil, NpgsqlDbType.TimestampTz), P(probeLeaseUntil, NpgsqlDbType.TimestampTz));

        if (from.State != to.State)
        {
            await ExecAsync(conn, tx, """
                INSERT INTO relay.circuit_events (endpoint_id, from_state, to_state, consecutive_failures, open_until)
                VALUES ($1, $2, $3, $4, $5)
                """, ct,
                endpointId, StateNames.Name(from.State), StateNames.Name(to.State), to.ConsecutiveFailures, P(to.OpenUntil, NpgsqlDbType.TimestampTz));
        }
    }

    /// <summary>Graceful shutdown: hand unfinished deliveries back immediately instead of waiting for their lease to expire.</summary>
    public async Task<int> ReleaseAsync(IReadOnlyCollection<long> deliveryIds, string relayId, CancellationToken ct)
    {
        if (deliveryIds.Count == 0)
        {
            return 0;
        }

        await using var cmd = dataSource.CreateCommand("""
            UPDATE relay.deliveries SET status = 'retrying', lease_until = NULL, leased_by = NULL, next_attempt_at = now()
            WHERE id = ANY ($1) AND status = 'in_flight' AND leased_by = $2
            """);
        cmd.Parameters.Add(P(deliveryIds.ToArray()));
        cmd.Parameters.Add(P(relayId));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Dead-letters deliveries that are too old to be useful, including ones waiting behind an open circuit.</summary>
    public async Task<int> ExpireAsync(TimeSpan maxAge, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE relay.deliveries SET status = 'dead', dead_at = now(), last_error = 'expired'
            WHERE id IN (SELECT id FROM relay.deliveries
                         WHERE status IN ('pending', 'retrying') AND created_at < now() - $1
                         LIMIT 1000 FOR UPDATE SKIP LOCKED)
            """);
        cmd.Parameters.Add(P(maxAge));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- replay panel ------------------------------------------------------------------------------------------

    private const string DeliveryColumns = """
        d.id, d.message_id, d.endpoint_id, e.url, m.event_type, d.status, d.attempts, d.replay_count, d.next_attempt_at,
        d.last_status_code, d.last_error, d.created_at, d.delivered_at, d.dead_at
        """;

    public async Task<IReadOnlyList<DeliveryView>> ListDeliveriesAsync(string? status, Guid? endpointId, long? beforeId, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {DeliveryColumns}
            FROM relay.deliveries d
            JOIN relay.messages m ON m.id = d.message_id
            JOIN relay.endpoints e ON e.id = d.endpoint_id
            WHERE ($1::text IS NULL OR d.status = $1) AND ($2::uuid IS NULL OR d.endpoint_id = $2) AND ($3::bigint IS NULL OR d.id < $3)
            ORDER BY d.id DESC
            LIMIT $4
            """);
        cmd.Parameters.Add(P(status, NpgsqlDbType.Text));
        cmd.Parameters.Add(P(endpointId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P(beforeId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(P(limit));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DeliveryView>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadDelivery(reader));
        }

        return list;
    }

    public async Task<DeliveryDetail?> GetDeliveryAsync(long id, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        DeliveryView view;
        string payload;
        await using (var cmd = new NpgsqlCommand($"""
            SELECT {DeliveryColumns}, m.payload
            FROM relay.deliveries d
            JOIN relay.messages m ON m.id = d.message_id
            JOIN relay.endpoints e ON e.id = d.endpoint_id
            WHERE d.id = $1
            """, conn))
        {
            cmd.Parameters.Add(P(id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            view = ReadDelivery(reader);
            payload = reader.GetString(14);
        }

        var attempts = new List<AttemptView>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT attempt, replay, started_at, duration_ms, status_code, outcome, error_kind, relay_id, lease_lost
            FROM relay.attempts WHERE delivery_id = $1 ORDER BY id
            """, conn))
        {
            cmd.Parameters.Add(P(id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                attempts.Add(new AttemptView(reader.GetInt32(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7), reader.GetBoolean(8)));
            }
        }

        return new DeliveryDetail(view, payload, attempts);
    }

    /// <summary>
    /// Replay re-queues a delivery with a fresh attempt budget. The webhook-id is unchanged, so a receiver that already
    /// processed the message recognises the replay as a duplicate. In-flight and pending deliveries cannot be replayed.
    /// </summary>
    public async Task<ReplayStatus> ReplayAsync(long id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH target AS (SELECT id, status FROM relay.deliveries WHERE id = $1 FOR UPDATE),
            upd AS (
                UPDATE relay.deliveries d
                SET status = 'pending', attempts = 0, replay_count = d.replay_count + 1, next_attempt_at = now(), dead_at = NULL
                FROM target WHERE d.id = target.id AND target.status IN ('dead', 'retrying', 'delivered')
                RETURNING d.id)
            SELECT (SELECT count(*) FROM target), (SELECT count(*) FROM upd), pg_notify('relay_deliveries', '')
            """);
        cmd.Parameters.Add(P(id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.GetInt64(0) == 0 ? ReplayStatus.NotFound : reader.GetInt64(1) == 0 ? ReplayStatus.NotReplayable : ReplayStatus.Replayed;
    }

    /// <summary>Bulk replay of the dead-letter queue (optionally for one endpoint), typically after the receiver is fixed.</summary>
    public async Task<int> ReplayDeadAsync(Guid? endpointId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH upd AS (
                UPDATE relay.deliveries
                SET status = 'pending', attempts = 0, replay_count = replay_count + 1, next_attempt_at = now(), dead_at = NULL
                WHERE status = 'dead' AND ($1::uuid IS NULL OR endpoint_id = $1)
                RETURNING id)
            SELECT count(*), pg_notify('relay_deliveries', '') FROM upd
            """);
        cmd.Parameters.Add(P(endpointId, NpgsqlDbType.Uuid));
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyDictionary<string, long>> StatusCountsAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT status, count(*) FROM relay.deliveries GROUP BY status");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var counts = new Dictionary<string, long> { ["pending"] = 0, ["in_flight"] = 0, ["retrying"] = 0, ["delivered"] = 0, ["dead"] = 0 };
        while (await reader.ReadAsync(ct))
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static DeliveryView ReadDelivery(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7),
        r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12),
        r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13));

    private static Circuit ReadCircuit(NpgsqlDataReader r, int offset) => new(
        StateNames.ParseCircuit(r.GetString(offset)), r.GetInt32(offset + 1), r.GetInt32(offset + 2),
        r.IsDBNull(offset + 3) ? null : r.GetFieldValue<DateTimeOffset>(offset + 3));

    private static NpgsqlParameter P(object? value, NpgsqlDbType? type = null)
    {
        var p = new NpgsqlParameter { Value = value ?? DBNull.Value };
        if (type is { } t)
        {
            p.NpgsqlDbType = t;
        }

        return p;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var arg in args)
        {
            cmd.Parameters.Add(arg as NpgsqlParameter ?? P(arg));
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
