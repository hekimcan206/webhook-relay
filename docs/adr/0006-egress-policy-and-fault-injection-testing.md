# 0006: Egress (SSRF) policy and how delivery guarantees are tested

- Status: accepted
- Date: 2026-11-11

## Context

Two concerns that both come from "the relay makes HTTP requests to URLs chosen by someone else":

1. **Server-side request forgery.** A tenant registers `http://169.254.169.254/latest/meta-data/` or
   `http://localhost:5432/` and the relay, sitting inside the network, POSTs to it and maybe reveals timing or
   status codes in the replay panel.
2. **Proving the guarantees.** At-least-once, bounded duplicates and breaker behaviour are claims about failure modes.
   Mocked HTTP handlers cannot produce a half-written response, a reset socket or a killed process.

## Options considered

**Egress**

1. *Validate the URL at registration only.* Defeated by DNS: `evil.example` resolves to a public IP at registration and
   to `127.0.0.1` later (rebinding), and by redirects.
2. *An egress proxy (e.g. Smokescreen) in front of the relay.* The strongest option in production; an extra component.
3. *Check the resolved address at connect time, in the relay* (chosen), plus early validation at registration for
   clear errors, plus no redirect following. Documented as compatible with option 2.

**Testing**

1. *Mock `HttpMessageHandler`.* Fast, but cannot reset a TCP connection, time out mid-response, or crash the relay.
2. *Toxiproxy between relay and receivers.* Good for network faults (used in another project of this portfolio); it does
   not model receiver behaviour such as "processed, then answered too late".
3. *Real receivers with scripted behaviour plus real process kills* (chosen).

## Decision

- `SocketsHttpHandler.ConnectCallback` resolves the host, keeps only public unicast addresses (rejecting loopback,
  RFC 1918, link-local including cloud metadata, CGNAT, multicast, IPv6 ULA/link-local, and IPv4 embedded in
  IPv4-mapped, NAT64 and 6to4 forms) and connects to those addresses directly, so the hostname is never re-resolved.
  `AllowAutoRedirect = false`; 3xx counts as a failure. `AllowPrivateNetworks` exists only for local demos and tests.
- Outbound requests carry no `traceparent`/baggage: receivers are third parties.
- The response body is never read (headers only), so an endless or huge body cannot hold a worker slot.
- `TestReceiver` (tests/WebhookRelay.TestKit) is a real Kestrel server that verifies every signature and records
  receipts and processed ids separately; behaviours: respond with any status (with Retry-After), delay (beyond the
  relay's timeout = processed but response lost), abort the connection, or any function of the request.
- Fault-injection tests assert from the receiver's side (ids processed, duplicates, invalid signatures) and from the
  relay's side (statuses, attempt log), so a bug in one cannot hide behind the other.
- The crash test starts the real relay binary as a child process and kills it with `Process.Kill` (TerminateProcess /
  SIGKILL): no shutdown hooks, leases left behind. It runs in CI on every push.

## Consequences

- SSRF via direct IPs, DNS names that resolve internally, and redirects is blocked in the default configuration
  (tested at unit level for the address classes and end to end for `localhost`).
- The tests take about a minute on a laptop and on CI runners (PostgreSQL container plus real sockets). Timing-based
  assertions use generous margins; test classes run serially.
- Not covered: TLS-level faults and slow-loris style receivers that trickle response headers byte by byte (the request
  timeout bounds them, but there is no dedicated test).

## Sources

- OWASP, "Server Side Request Forgery Prevention Cheat Sheet":
  https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html
- Stripe, Smokescreen (egress proxy): https://github.com/stripe/smokescreen
- .NET API: `SocketsHttpHandler.ConnectCallback`:
  https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.connectcallback
