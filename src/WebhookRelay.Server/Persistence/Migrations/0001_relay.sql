-- Endpoints are the receivers. The circuit breaker state lives on the row, so every relay instance shares one breaker
-- per endpoint and transitions are serialised by the row lock.
CREATE TABLE relay.endpoints (
    id                   uuid        PRIMARY KEY,
    url                  text        NOT NULL CHECK (url ~ '^https?://' AND length(url) <= 2048),
    description          text        NOT NULL DEFAULT '' CHECK (length(description) <= 500),
    event_types          text[]      NULL,  -- NULL: every event type
    disabled_at          timestamptz NULL,
    disabled_reason      text        NULL,
    circuit_state        text        NOT NULL DEFAULT 'closed' CHECK (circuit_state IN ('closed', 'open', 'half_open')),
    consecutive_failures integer     NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0),
    consecutive_opens    integer     NOT NULL DEFAULT 0 CHECK (consecutive_opens >= 0),
    open_until           timestamptz NULL,
    probe_lease_until    timestamptz NULL,
    created_at           timestamptz NOT NULL DEFAULT now(),
    CHECK (circuit_state <> 'open' OR open_until IS NOT NULL)
);

-- Several secrets per endpoint: during a rotation the relay signs with every unexpired one.
CREATE TABLE relay.endpoint_secrets (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    endpoint_id uuid        NOT NULL REFERENCES relay.endpoints (id) ON DELETE CASCADE,
    secret      text        NOT NULL CHECK (secret LIKE 'whsec\_%'),
    created_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NULL
);
CREATE INDEX endpoint_secrets_endpoint ON relay.endpoint_secrets (endpoint_id);

-- One row per published event. The payload is stored as text and sent byte for byte, because the signature covers the
-- exact body; re-serialising JSON (jsonb would reorder keys) would break every receiver's verification.
CREATE TABLE relay.messages (
    id              text        PRIMARY KEY CHECK (id ~ '^msg_[0-9a-f]{32}$'),
    event_type      text        NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
    payload         text        NOT NULL,
    idempotency_key text        NULL UNIQUE CHECK (length(idempotency_key) <= 200),
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- The durable queue: one row per (message, endpoint). Status machine:
--   pending -> in_flight -> delivered
--                        -> retrying -> in_flight ...
--                        -> dead (attempts exhausted, 410 Gone, or too old)
-- in_flight rows whose lease expired (the relay crashed mid-delivery) are claimable again.
CREATE TABLE relay.deliveries (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    message_id       text        NOT NULL REFERENCES relay.messages (id) ON DELETE CASCADE,
    endpoint_id      uuid        NOT NULL REFERENCES relay.endpoints (id) ON DELETE CASCADE,
    status           text        NOT NULL DEFAULT 'pending'
                                 CHECK (status IN ('pending', 'in_flight', 'retrying', 'delivered', 'dead')),
    attempts         integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    replay_count     integer     NOT NULL DEFAULT 0 CHECK (replay_count >= 0),
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    lease_until      timestamptz NULL,
    leased_by        text        NULL,
    last_status_code integer     NULL,
    last_error       text        NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    delivered_at     timestamptz NULL,
    dead_at          timestamptz NULL,
    UNIQUE (message_id, endpoint_id),
    CHECK (status <> 'in_flight' OR (lease_until IS NOT NULL AND leased_by IS NOT NULL))
);

-- Partial indexes keep the claim query cheap no matter how many delivered rows accumulate.
CREATE INDEX deliveries_due      ON relay.deliveries (next_attempt_at) WHERE status IN ('pending', 'retrying');
CREATE INDEX deliveries_leased   ON relay.deliveries (lease_until) WHERE status = 'in_flight';
CREATE INDEX deliveries_endpoint ON relay.deliveries (endpoint_id, status);
CREATE INDEX deliveries_dead     ON relay.deliveries (dead_at DESC) WHERE status = 'dead';

-- Every HTTP attempt, kept for the replay panel and for measuring the real retry schedule. Append-only.
CREATE TABLE relay.attempts (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    delivery_id bigint      NOT NULL REFERENCES relay.deliveries (id) ON DELETE CASCADE,
    attempt     integer     NOT NULL,
    replay      integer     NOT NULL DEFAULT 0,
    started_at  timestamptz NOT NULL,
    duration_ms integer     NOT NULL,
    status_code integer     NULL,
    outcome     text        NOT NULL CHECK (outcome IN ('delivered', 'retryable', 'gone')),
    error_kind  text        NULL,
    relay_id    text        NOT NULL,
    lease_lost  boolean     NOT NULL DEFAULT false
);
CREATE INDEX attempts_delivery ON relay.attempts (delivery_id, id);

-- Every breaker transition, for the panel and for measuring breaker behaviour under a flapping endpoint. Append-only.
CREATE TABLE relay.circuit_events (
    id                   bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    endpoint_id          uuid        NOT NULL REFERENCES relay.endpoints (id) ON DELETE CASCADE,
    at                   timestamptz NOT NULL DEFAULT now(),
    from_state           text        NOT NULL,
    to_state             text        NOT NULL,
    consecutive_failures integer     NOT NULL,
    open_until           timestamptz NULL
);
CREATE INDEX circuit_events_endpoint ON relay.circuit_events (endpoint_id, id);

-- The application may add attempts and breaker events but never rewrite history.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'relay_app') THEN
        REVOKE UPDATE, DELETE, TRUNCATE ON relay.attempts, relay.circuit_events FROM relay_app;
    END IF;
END
$$;
