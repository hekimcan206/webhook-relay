# Threat model

Scope: the relay service (API, delivery worker, replay panel), its PostgreSQL database, and the path to receivers.
Method: assets, actors and trust boundaries, then STRIDE per boundary with the mitigation that exists **in this
repository** and what is left to the deployment.

## Assets

| Asset | Why it matters |
|---|---|
| Endpoint signing secrets (`whsec_`) | Anyone holding one can forge webhooks that a receiver accepts. |
| Event payloads | May contain business data (orders, invoices). All demo and test data is synthetic. |
| Delivery state and attempt log | Loss or tampering breaks the at-least-once guarantee and the audit trail. |
| The admin key | Grants endpoint management, publishing, replay and payload reads. |
| The relay's network position | It can reach internal services that tenants cannot (SSRF). |

## Actors

- **Producer / operator** (trusted, holds the admin key): registers endpoints, publishes, replays.
- **Receiver owner** (semi-trusted): controls the endpoint's responses, timing and DNS.
- **Network attacker** between relay and receiver (untrusted): can observe or replay plain-http traffic.
- **Internet attacker** reaching the relay's HTTP port (untrusted).

## Trust boundaries

1. Producer → relay API (admin key).
2. Relay → PostgreSQL (least-privilege role).
3. Relay → receiver (signed requests over the internet).
4. Operator browser → replay panel.

## STRIDE

| # | Threat | Boundary | Mitigation here | Left to the deployment |
|---|---|---|---|---|
| S1 | Forged webhooks reach receivers | 3 | HMAC-SHA256 Standard Webhooks signatures over id, timestamp and raw body; samples verify before parsing. | Receivers must actually verify (guidance in README). |
| S2 | Caller impersonates the operator | 1 | `X-Admin-Key` compared in constant time on SHA-256 digests; minimum 16 characters, start-up fails otherwise. | Real identity (OIDC), per-producer keys, rotation; TLS termination. |
| T1 | Body modified in transit | 3 | Signature covers the exact bytes; payload stored as text, never re-serialised. | https endpoints (`Relay:RequireHttps=true`). |
| T2 | Attempt history rewritten | 2 | Application role has only SELECT/INSERT on `attempts` and `circuit_events`; DDL only via the migrator role; tested. | Database audit logging, backups. |
| R1 | Operator denies a replay | 1 | Each replay increments `replay_count`; attempts record the replay number and relay instance. | Operator identity in the log (needs real auth). |
| I1 | Captured request replayed later | 3 | ±5 minute timestamp tolerance; receivers also drop repeated `webhook-id`s. | — |
| I2 | Secrets leak from the database or logs | 2 | Secrets never logged (`WebhookSecret.ToString()` is redacted); returned only at creation and rotation; DB access limited to the app role. | **Not encrypted at rest** (HMAC needs the key): envelope encryption with a KMS is the fix. |
| I3 | SSRF: relay used to reach internal services or cloud metadata | 3 | Connect-time egress policy on the resolved IP (private, loopback, link-local, CGNAT, ULA, embedded IPv4); no redirects; registration rejects literal internal addresses; tested end to end. | An egress proxy / network policy as a second layer. |
| I4 | Internal trace ids leak to receivers | 3 | `traceparent`/baggage propagation disabled on the webhook client. | — |
| I5 | Payloads exposed through the panel | 4 | Panel requires the admin key; key kept in `sessionStorage` only; strict CSP (`default-src 'self'`), `nosniff`, `no-referrer`, no framing. | TLS; restrict panel access by network. |
| I6 | Stored XSS via tenant-controlled strings (URL, event type, error) in the panel | 4 | Panel writes server values with `textContent` only; CSP blocks inline script. | — |
| D1 | Receiver holds workers (slow headers, endless body) | 3 | Per-attempt timeout, connect timeout, headers-only read, bounded concurrency per instance. | — |
| D2 | One dead endpoint starves others | 3 | Per-endpoint circuit breaker; open circuits are not claimed. | Per-endpoint concurrency limits (not implemented). |
| D3 | Huge payloads or floods on the publish API | 1 | 256 KiB payload limit, 1 MiB request limit, validation of every field. | Rate limiting at the gateway. |
| D4 | Receiver sets `Retry-After: 31536000` | 3 | Retry-After honoured only up to `MaxDelay`. | — |
| D5 | Poison message crashes the relay repeatedly | 2 | Attempts counted at claim time, so a crashing delivery still exhausts its budget. | Crash-loop alerting. |
| E1 | SQL injection | 2 | Parameterised SQL everywhere; no string-built SQL with input. | — |
| E2 | Container escape / privilege | — | Chiseled, non-root image (verified in CI); no shell in the runtime image. | Read-only root filesystem, seccomp profile. |

## Privacy (KVKK / GDPR)

The relay forwards whatever producers publish and keeps payloads in `relay.messages` indefinitely in this version.
For personal data (KVKK, the Turkish data protection law; GDPR), a deployment needs a retention job (delete delivered
messages after N days; attempts and deliveries cascade) and should prefer thin events (ids, not personal fields) that
receivers resolve with an authenticated API call. All data in this repository is synthetic.
