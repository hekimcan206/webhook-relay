// A from-scratch Standard Webhooks v1 verifier using only node:crypto.
// HMAC-SHA256 over `${webhook-id}.${webhook-timestamp}.${raw body}`, keyed with the base64-decoded part of the
// whsec_ secret, compared in constant time against every v1 entry of the webhook-signature header.
import { createHmac, timingSafeEqual } from "node:crypto";

const CANONICAL_TIMESTAMP = /^(0|[1-9][0-9]{0,14})$/;

/**
 * @param {string[]} secrets   whsec_ secrets (several during a rotation)
 * @param {{id?: string, timestamp?: string, signature?: string}} headers
 * @param {Buffer} rawBody     the body bytes exactly as received
 * @param {number} nowSeconds  current Unix time in seconds
 * @param {number} toleranceSeconds
 */
export function verify(secrets, headers, rawBody, nowSeconds = Math.floor(Date.now() / 1000), toleranceSeconds = 300) {
  const { id, timestamp, signature } = headers;
  if (!id || !timestamp || !signature) return false;
  if (!CANONICAL_TIMESTAMP.test(timestamp)) return false;
  if (Math.abs(nowSeconds - Number(timestamp)) > toleranceSeconds) return false;

  const signed = Buffer.concat([Buffer.from(`${id}.${timestamp}.`, "utf8"), rawBody]);
  const entries = signature.split(" ").filter(Boolean);
  for (const secret of secrets) {
    const key = Buffer.from(secret.startsWith("whsec_") ? secret.slice(6) : secret, "base64");
    const expected = Buffer.from(createHmac("sha256", key).update(signed).digest("base64"), "ascii");
    for (const entry of entries) {
      const comma = entry.indexOf(",");
      if (comma < 0 || entry.slice(0, comma) !== "v1") continue;
      const candidate = Buffer.from(entry.slice(comma + 1), "ascii");
      if (candidate.length === expected.length && timingSafeEqual(candidate, expected)) return true;
    }
  }
  return false;
}
