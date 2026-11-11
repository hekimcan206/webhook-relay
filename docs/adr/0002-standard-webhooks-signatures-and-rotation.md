# 0002: Standard Webhooks v1 signatures, secret rotation and timestamp tolerance

- Status: accepted
- Date: 2026-11-11

## Context

Receivers must be able to tell that a request came from us, was not modified, and is not a replay of an old capture.
Every webhook provider used to invent its own header and signing string, so every receiver needed custom code.
Standard Webhooks is a published specification (backed by several providers) that fixes those choices.

## Options considered

1. **A home-grown scheme** (`X-Signature: hex(hmac(body))`). Easy, but signs only the body: a captured request can be
   replayed forever, and receivers need bespoke code.
2. **mTLS to every receiver.** Strong, but most receivers are SaaS endpoints behind shared load balancers; certificate
   distribution is a large operational burden for both sides.
3. **Standard Webhooks `v1` (HMAC-SHA256)** (chosen), with `v1a` (Ed25519) left for later.

## Decision

- Headers `webhook-id`, `webhook-timestamp` (Unix seconds) and `webhook-signature`.
- Signed content is `{webhook-id}.{webhook-timestamp}.{raw body}`; the signature is `v1,` + base64(HMAC-SHA256).
- Secrets are `whsec_` + base64 of 24 to 64 random bytes; the **decoded bytes** are the HMAC key. The implementation
  reproduces the test vector used across the reference libraries (`SigningTests`).
- **Rotation:** an endpoint may hold several unexpired secrets. The relay signs with all of them and puts every
  signature, space-separated, in one header. Rotating adds a new secret and sets the old ones to expire after an overlap
  (default 24 h), so receivers can switch at their own pace with zero failed deliveries.
- **Verification** (library and all three samples): accept if any `v1` entry matches any configured secret, compare in
  constant time, skip unknown versions instead of failing, look at no more than 16 entries (bounded work), and reject
  timestamps outside ±5 minutes. The timestamp must be canonical digits: the signed string uses the header text, so a
  lenient parse (`+123`, `0123`, `1.6e9`) would accept a timestamp other than the one signed.
- **The timestamp is per attempt**, the id is per message. A retry three days later is freshly signed and passes the
  tolerance check; the unchanged id is what lets receivers drop duplicates (ADR 0005).
- **The payload is stored as text** and sent byte for byte. `jsonb` would normalise whitespace and key order, and the
  receiver would verify different bytes from the ones we signed. The demo publishes deliberately odd whitespace and
  non-ASCII text to keep this honest; one vector (`reserialised-body`) proves a re-serialised body fails.

## Consequences

- Receivers can use any Standard Webhooks library, or the 30-line samples in C#, Node and Python, which CI tests
  against vectors the relay generates in the same job (positive and negative cases).
- Secrets are stored in the database in clear text, because HMAC needs the key. Encrypting them at rest (envelope
  encryption with a KMS key) is listed under Limitations.
- `v1a` would let receivers verify with a public key, so a leaked receiver config cannot forge events. Not implemented;
  the header format already allows adding it beside `v1` without breaking existing receivers.

## Sources

- Standard Webhooks specification: https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md
- Standard Webhooks site: https://www.standardwebhooks.com/
- Overview of the scheme (headers, base64 not hex, 300 s tolerance in common libraries):
  https://webhookrelay.com/glossary/standard-webhooks/
