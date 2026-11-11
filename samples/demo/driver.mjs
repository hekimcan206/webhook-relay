// docker compose demo driver: registers the three sample receivers, publishes synthetic events, waits for the queue
// to drain, replays one delivery, and checks each receiver's own counters. Exit code 0 only if every receiver
// processed every message, rejected nothing, and saw the replay as a duplicate of a webhook-id it already had.
const relay = process.env.RELAY_URL ?? "http://localhost:59480/";
const key = process.env.ADMIN_KEY ?? "admin-dev-only-key";
const messages = Number(process.env.MESSAGES ?? 60);
const receivers = [
  { name: "csharp", url: "http://receiver-csharp:8080/webhook", stats: "http://receiver-csharp:8080/stats", secret: process.env.SECRET_CSHARP },
  { name: "node", url: "http://receiver-node:8080/webhook", stats: "http://receiver-node:8080/stats", secret: process.env.SECRET_NODE },
  { name: "python", url: "http://receiver-python:8080/webhook", stats: "http://receiver-python:8080/stats", secret: process.env.SECRET_PYTHON },
];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(path, init = {}) {
  const res = await fetch(new URL(`api/v1/${path}`, relay), {
    ...init,
    headers: { "X-Admin-Key": key, "content-type": "application/json" },
  });
  if (!res.ok) throw new Error(`${init.method ?? "GET"} ${path}: ${res.status} ${await res.text()}`);
  return res.status === 202 && res.headers.get("content-length") === "0" ? null : res.json().catch(() => null);
}

async function waitFor(what, check, timeoutMs = 90_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      if (await check()) return;
    } catch {
      // not up yet
    }
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}`);
    await sleep(500);
  }
}

const started = Date.now();
await waitFor("relay", async () => (await fetch(new URL("health/ready", relay))).ok);
for (const r of receivers) await waitFor(r.name, async () => (await fetch(r.stats)).ok);

// Idempotent setup: re-running the demo against the same database reuses the endpoints.
const existing = await api("endpoints");
for (const r of receivers) {
  const found = existing.find((e) => e.url === r.url);
  r.id = found ? found.id : (await api("endpoints", { method: "POST", body: JSON.stringify({ url: r.url, secret: r.secret, description: `${r.name} sample` }) })).id;
}

const before = Object.fromEntries(await Promise.all(receivers.map(async (r) => [r.name, await (await fetch(r.stats)).json()])));
const runId = Date.now().toString(36);
const types = ["invoice.paid", "order.created", "customer.updated"];
for (let i = 0; i < messages; i++) {
  await api("messages", {
    method: "POST",
    body: JSON.stringify({
      eventType: types[i % types.length],
      payload: { sequence: i, amount: (100 + i * 7.25).toFixed(2), currency: "TRY", note: "synthetic demo event · çğıöşü" },
      idempotencyKey: `demo-${runId}-${i}`,
    }),
  });
}
// The same idempotency key again: accepted as a duplicate, not enqueued twice.
const dup = await api("messages", { method: "POST", body: JSON.stringify({ eventType: types[0], payload: { sequence: 0, amount: "100.00", currency: "TRY", note: "synthetic demo event · çğıöşü" }, idempotencyKey: `demo-${runId}-0` }) });

const drained = async () => {
  const s = (await api("stats")).deliveries;
  return s.pending + s.in_flight + s.retrying === 0;
};
await waitFor("queue to drain", drained);

// Replay one delivered message to the node receiver: it must arrive with the same webhook-id and be detected.
const [delivered] = await api(`deliveries?status=delivered&endpointId=${receivers[1].id}&limit=1`);
await api(`deliveries/${delivered.id}/replay`, { method: "POST" });
await sleep(300);
await waitFor("replay", drained);

const after = Object.fromEntries(await Promise.all(receivers.map(async (r) => [r.name, await (await fetch(r.stats)).json()])));
const delta = (name, field) => after[name][field] - before[name][field];
const failures = [];
for (const r of receivers) {
  if (delta(r.name, "processed") !== messages) failures.push(`${r.name} processed ${delta(r.name, "processed")} of ${messages}`);
  if (delta(r.name, "rejected") !== 0) failures.push(`${r.name} rejected ${delta(r.name, "rejected")} signatures`);
}
if (delta("node", "duplicates") !== 1) failures.push(`node saw ${delta("node", "duplicates")} duplicates, expected the 1 replay`);
if (delta("python", "failedOnPurpose") !== Math.floor(messages / 3)) failures.push("python did not force a retry of every third message");
if (!dup?.duplicate) failures.push("idempotent publish was not reported as a duplicate");

const stats = (await api("stats")).deliveries;
console.log(JSON.stringify({
  ok: failures.length === 0,
  messages,
  seconds: Math.round((Date.now() - started) / 100) / 10,
  relay: stats,
  receivers: Object.fromEntries(receivers.map((r) => [r.name, {
    processed: delta(r.name, "processed"),
    duplicatesDetected: delta(r.name, "duplicates"),
    retriedOnPurpose: delta(r.name, "failedOnPurpose"),
    rejected: delta(r.name, "rejected"),
  }])),
  failures,
}));
process.exit(failures.length === 0 ? 0 : 1);
