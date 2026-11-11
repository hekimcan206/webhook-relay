// Replay panel: a thin client over the admin API. Every value from the server is written with textContent, never
// innerHTML, because URLs, event types and error strings are tenant-controlled.
"use strict";

const state = { key: sessionStorage.getItem("relay-admin-key") || "", status: "dead", timer: null, open: new Set() };
const $ = (sel) => document.querySelector(sel);

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v);
  }
  for (const child of children) node.append(child instanceof Node ? child : document.createTextNode(String(child ?? "")));
  return node;
}

async function api(path, options = {}) {
  const response = await fetch(`api/v1/${path}`, {
    ...options,
    headers: { "X-Admin-Key": state.key, "Content-Type": "application/json", ...(options.headers || {}) },
  });
  if (response.status === 401) throw new Error("The admin key was rejected.");
  if (!response.ok && response.status !== 202) throw new Error(`${response.status} ${response.statusText}`);
  return response.status === 202 || response.status === 204 ? null : response.json();
}

const fmtTime = (iso) => (iso ? new Date(iso).toLocaleString() : "");
const circuitBadge = (s) => el("span", { class: `badge ${s === "closed" ? "ok" : s === "open" ? "bad" : "warn"}` }, s.replace("_", "-"));
const statusBadge = (s) =>
  el("span", { class: `badge ${s === "delivered" ? "ok" : s === "dead" ? "bad" : "warn"}` }, s.replace("_", " "));

async function refresh() {
  if (!state.key) return;
  try {
    const [stats, endpoints, deliveries] = await Promise.all([
      api("stats"),
      api("endpoints"),
      api(`deliveries?limit=100${state.status ? `&status=${state.status}` : ""}`),
    ]);
    renderCounts(stats.deliveries);
    renderEndpoints(endpoints);
    await renderDeliveries(deliveries);
    $("#status").textContent = `Updated ${new Date().toLocaleTimeString()} · relay ${stats.relayId}`;
  } catch (error) {
    $("#status").textContent = error.message;
  }
}

function renderCounts(counts) {
  const order = ["pending", "in_flight", "retrying", "delivered", "dead"];
  $("#counts").replaceChildren(
    ...order.map((k) => el("div", { class: "count" }, el("b", {}, counts[k] ?? 0), el("span", { class: "muted" }, k.replace("_", " ")))),
  );
}

function renderEndpoints(endpoints) {
  const rows = endpoints.map((e) => {
    const d = e.deliveries || {};
    const action = e.disabled || e.circuitState !== "closed"
      ? el("button", { onclick: async () => { await api(`endpoints/${e.id}/enable`, { method: "POST" }); refresh(); } }, "Enable & reset")
      : "";
    return el("tr", {},
      el("td", { class: "url", title: e.url }, e.url, e.disabled ? el("div", { class: "muted small" }, `disabled: ${e.disabledReason}`) : ""),
      el("td", {}, circuitBadge(e.circuitState), e.openUntil ? el("div", { class: "muted small" }, `until ${fmtTime(e.openUntil)}`) : ""),
      el("td", { class: "num" }, e.consecutiveFailures),
      el("td", { class: "num" }, d.pending ?? 0),
      el("td", { class: "num" }, d.retrying ?? 0),
      el("td", { class: "num" }, d.dead ?? 0),
      el("td", { class: "num" }, d.delivered ?? 0),
      el("td", {}, action));
  });
  $("#endpoints tbody").replaceChildren(...(rows.length ? rows : [el("tr", {}, el("td", { colspan: 8, class: "muted" }, "No endpoints yet."))]));
}

async function renderDeliveries(deliveries) {
  const rows = [];
  for (const d of deliveries) {
    const replayable = ["dead", "retrying", "delivered"].includes(d.status);
    rows.push(el("tr", { class: "clickable", onclick: () => toggle(d.id) },
      el("td", { class: "num" }, d.id),
      el("td", {}, el("code", {}, d.messageId)),
      el("td", {}, d.eventType),
      el("td", { class: "url", title: d.endpointUrl }, d.endpointUrl),
      el("td", {}, statusBadge(d.status), d.replayCount ? el("span", { class: "muted small" }, ` replayed ×${d.replayCount}`) : ""),
      el("td", { class: "num" }, d.attempts),
      el("td", {}, d.lastStatusCode ?? d.lastError ?? ""),
      el("td", {}, fmtTime(d.createdAt)),
      el("td", {}, replayable
        ? el("button", { onclick: async (ev) => { ev.stopPropagation(); await api(`deliveries/${d.id}/replay`, { method: "POST" }); refresh(); } }, "Replay")
        : "")));
    if (state.open.has(d.id)) rows.push(await detailRow(d.id));
  }
  $("#deliveries tbody").replaceChildren(...(rows.length ? rows : [el("tr", {}, el("td", { colspan: 9, class: "muted" }, "Nothing here."))]));
}

async function detailRow(id) {
  const detail = await api(`deliveries/${id}`);
  const list = el("table", {},
    el("thead", {}, el("tr", {}, ...["Attempt", "Replay", "Started", "ms", "Status", "Outcome", "Error", "Relay"].map((h) => el("th", {}, h)))),
    el("tbody", {}, ...detail.attempts.map((a) => el("tr", {},
      el("td", { class: "num" }, a.attempt), el("td", { class: "num" }, a.replay), el("td", {}, fmtTime(a.startedAt)),
      el("td", { class: "num" }, a.durationMs), el("td", {}, a.statusCode ?? ""), el("td", {}, a.outcome),
      el("td", {}, a.errorKind ?? ""), el("td", {}, el("code", {}, a.relayId))))));
  return el("tr", { class: "detail" }, el("td", { colspan: 9 },
    el("div", { class: "small muted" }, "Payload"), el("pre", {}, el("code", {}, detail.payload.slice(0, 2000))), list));
}

function toggle(id) {
  state.open.has(id) ? state.open.delete(id) : state.open.add(id);
  refresh();
}

$("#key-form").addEventListener("submit", (ev) => {
  ev.preventDefault();
  state.key = $("#admin-key").value;
  sessionStorage.setItem("relay-admin-key", state.key);
  refresh();
});

$("#tabs").addEventListener("click", (ev) => {
  const button = ev.target.closest("button[data-status]");
  if (!button) return;
  state.status = button.dataset.status;
  for (const b of document.querySelectorAll("#tabs button")) b.setAttribute("aria-selected", String(b === button));
  refresh();
});

$("#replay-all").addEventListener("click", async () => {
  if (!confirm("Re-queue every dead-lettered delivery?")) return;
  const result = await api("deliveries/replay-dead", { method: "POST", body: "{}" });
  $("#status").textContent = `Re-queued ${result.replayed} deliveries.`;
  refresh();
});

if (state.key) $("#admin-key").value = state.key;
refresh();
state.timer = setInterval(refresh, 3000);
