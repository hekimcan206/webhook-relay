# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Webhook authenticity | Standard Webhooks v1 (HMAC-SHA256 over id, timestamp and raw body), secret rotation with overlapping signatures |
| Replay of captured requests | ±5 minute timestamp tolerance in receivers; repeated `webhook-id`s are duplicates |
| SSRF | Resolved addresses checked at connect time (private, loopback, link-local, CGNAT, ULA, embedded IPv4 refused); redirects not followed |
| Management API | `X-Admin-Key`, constant-time comparison, minimum length enforced at start-up |
| Replay panel | Same API key; strict Content-Security-Policy; server values rendered as text only |
| Database access | The relay runs as `relay_app` (DML only; attempt log append-only). Migrations run as `relay_migrator` in a one-shot process |
| Input limits | 256 KiB payload, 1 MiB request body, validated fields; receiver response bodies are never read |
| Secrets | None in the repository. `.env.example` holds clearly marked development-only values. Signing secrets are stored in the database unencrypted (see Limitations in the README) |
