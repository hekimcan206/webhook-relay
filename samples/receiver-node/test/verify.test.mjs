import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { verify } from "../standard-webhooks.mjs";

// CI points VECTORS at a file the relay generated in the same job; locally the committed copy is used.
const path = process.env.VECTORS ?? fileURLToPath(new URL("../../vectors/signature-vectors.json", import.meta.url));
const { vectors, toleranceSeconds } = JSON.parse(readFileSync(path, "utf8"));

test("vector file is not empty and covers both outcomes", () => {
  assert.ok(vectors.some((v) => v.expectValid));
  assert.ok(vectors.some((v) => !v.expectValid));
});

for (const v of vectors) {
  test(`${v.expectValid ? "accepts" : "rejects"} ${v.name}`, () => {
    const ok = verify(
      v.receiverSecrets,
      { id: v.webhookId ?? undefined, timestamp: v.webhookTimestamp ?? undefined, signature: v.webhookSignature ?? undefined },
      Buffer.from(v.bodyBase64, "base64"),
      v.now,
      toleranceSeconds,
    );
    assert.equal(ok, v.expectValid);
  });
}
