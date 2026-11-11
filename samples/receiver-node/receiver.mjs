// Minimal webhook receiver: verifies signatures, discards duplicates by webhook-id, exposes /stats.
// Usage: WEBHOOK_SECRETS="whsec_..." node receiver.mjs     |     node receiver.mjs verify-vectors <file>
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { verify } from "./standard-webhooks.mjs";

if (process.argv[2] === "verify-vectors") {
  process.exit(verifyVectors(process.argv[3]));
}

const secrets = (process.env.WEBHOOK_SECRETS ?? "").split(/[ ,]+/).filter(Boolean);
if (secrets.length === 0) {
  console.error("Set WEBHOOK_SECRETS to one or more whsec_ secrets.");
  process.exit(2);
}
const failFirst = Number(process.env.FAIL_FIRST_ATTEMPTS ?? 0);
const port = Number(process.env.PORT ?? 8080);
const maxBody = 1024 * 1024;

const receipts = new Map();
// Idempotency: production code stores processed ids durably (unique key, TTL > retry window) in the same transaction
// as the side effect. A Set is enough for the demo.
const processed = new Set();
const stats = { language: "node", verified: 0, rejected: 0, failedOnPurpose: 0, duplicates: 0, processed: 0 };

createServer((req, res) => {
  if (req.method === "GET" && req.url === "/stats") {
    res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(stats));
    return;
  }
  if (req.method !== "POST" || req.url !== "/webhook") {
    res.writeHead(404).end();
    return;
  }

  const chunks = [];
  let size = 0;
  req.on("data", (chunk) => {
    size += chunk.length;
    if (size > maxBody) req.destroy();
    else chunks.push(chunk);
  });
  req.on("end", () => {
    // Verify the raw bytes; JSON.parse only after the signature checks out.
    const raw = Buffer.concat(chunks);
    const id = req.headers["webhook-id"];
    const ok = verify(secrets, { id, timestamp: req.headers["webhook-timestamp"], signature: req.headers["webhook-signature"] }, raw);
    if (!ok) {
      stats.rejected++;
      res.writeHead(401).end();
      return;
    }
    stats.verified++;
    const seen = (receipts.get(id) ?? 0) + 1;
    receipts.set(id, seen);
    if (seen <= failFirst) {
      stats.failedOnPurpose++;
      res.writeHead(503).end();
      return;
    }
    if (processed.has(id)) {
      stats.duplicates++;
    } else {
      processed.add(id);
      stats.processed++;
    }
    res.writeHead(204).end();
  });
}).listen(port, () => console.log(`node receiver listening on ${port}`));

function verifyVectors(path) {
  const { vectors } = JSON.parse(readFileSync(path, "utf8"));
  let failures = 0;
  for (const v of vectors) {
    const ok = verify(
      v.receiverSecrets,
      { id: v.webhookId ?? undefined, timestamp: v.webhookTimestamp ?? undefined, signature: v.webhookSignature ?? undefined },
      Buffer.from(v.bodyBase64, "base64"),
      v.now,
    );
    if (ok !== v.expectValid) {
      failures++;
      console.log(`FAIL ${v.name}: got ${ok}`);
    }
  }
  console.log(`node: ${vectors.length - failures}/${vectors.length} vectors as expected`);
  return failures === 0 ? 0 : 1;
}
