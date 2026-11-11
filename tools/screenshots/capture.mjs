// Captures the replay panel after the compose demo: adds an endpoint whose port is closed (connection refused), so the
// panel shows an open circuit and retrying deliveries next to the demo's delivered traffic. Synthetic data only.
import { chromium } from "playwright";

const relay = process.env.RELAY_URL ?? "http://relay:8080/";
const key = process.env.ADMIN_KEY ?? "admin-dev-only-key";
const out = process.env.OUT_DIR ?? "/out";
const headers = { "X-Admin-Key": key, "content-type": "application/json" };
const post = (path, body) => fetch(new URL(`api/v1/${path}`, relay), { method: "POST", headers, body: JSON.stringify(body) }).then((r) => r.json());

const broken = await post("endpoints", { url: "http://receiver-node:9/hook", description: "closed port" });
for (let i = 0; i < 12; i++) {
  await post("messages", { eventType: "invoice.paid", payload: { sequence: i, amount: "42.00" }, endpointIds: [broken.id] });
}
await new Promise((r) => setTimeout(r, 9000)); // let the breaker trip and a probe fail

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1, colorScheme: "light" });
await page.goto(relay);
await page.fill("#admin-key", key);
await page.click("#key-form button");
await page.waitForSelector("#endpoints tbody tr td.url");
await page.click("#tabs button[data-status='retrying']");
await page.waitForSelector("#deliveries tbody tr.clickable");
await page.locator("#deliveries tbody tr.clickable").first().click();
await page.waitForSelector("#deliveries tr.detail");
await page.waitForTimeout(500);
await page.screenshot({ path: `${out}/replay-panel.png`, fullPage: true });
await browser.close();
console.log("wrote replay-panel.png");
