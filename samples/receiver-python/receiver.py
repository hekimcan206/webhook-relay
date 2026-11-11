"""Minimal webhook receiver: verifies signatures, discards duplicates by webhook-id, exposes /stats.

Usage: WEBHOOK_SECRETS="whsec_..." python receiver.py     |     python receiver.py verify-vectors <file>
"""

import base64
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from standard_webhooks import verify

MAX_BODY = 1024 * 1024


def verify_vectors(path: str) -> int:
    with open(path, encoding="utf-8") as f:
        vectors = json.load(f)["vectors"]
    failures = 0
    for v in vectors:
        ok = verify(
            v["receiverSecrets"],
            v["webhookId"],
            v["webhookTimestamp"],
            v["webhookSignature"],
            base64.b64decode(v["bodyBase64"]),
            now=v["now"],
        )
        if ok != v["expectValid"]:
            failures += 1
            print(f"FAIL {v['name']}: got {ok}")
    print(f"python: {len(vectors) - failures}/{len(vectors)} vectors as expected")
    return 0 if failures == 0 else 1


class State:
    def __init__(self, secrets, fail_first):
        self.secrets = secrets
        self.fail_first = fail_first
        self.lock = threading.Lock()
        self.receipts = {}
        # Idempotency: production code stores processed ids durably (unique key, TTL > retry window) in the same
        # transaction as the side effect. A set is enough for the demo.
        self.processed = set()
        self.stats = {"language": "python", "verified": 0, "rejected": 0, "failedOnPurpose": 0, "duplicates": 0, "processed": 0}


def make_handler(state: State):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):  # keep stdout quiet
            pass

        def do_GET(self):
            if self.path != "/stats":
                self.send_response(404)
                self.end_headers()
                return
            with state.lock:
                body = json.dumps(state.stats).encode()
            self.send_response(200)
            self.send_header("content-type", "application/json")
            self.send_header("content-length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            length = int(self.headers.get("content-length", "0"))
            if self.path != "/webhook" or length > MAX_BODY:
                self.send_response(404 if self.path != "/webhook" else 413)
                self.end_headers()
                return
            # Verify the raw bytes; json.loads only after the signature checks out.
            raw = self.rfile.read(length)
            webhook_id = self.headers.get("webhook-id")
            ok = verify(state.secrets, webhook_id, self.headers.get("webhook-timestamp"), self.headers.get("webhook-signature"), raw)
            with state.lock:
                if not ok:
                    state.stats["rejected"] += 1
                    status = 401
                else:
                    state.stats["verified"] += 1
                    state.receipts[webhook_id] = state.receipts.get(webhook_id, 0) + 1
                    if state.receipts[webhook_id] <= state.fail_first:
                        state.stats["failedOnPurpose"] += 1
                        status = 503
                    elif webhook_id in state.processed:
                        state.stats["duplicates"] += 1
                        status = 204
                    else:
                        state.processed.add(webhook_id)
                        state.stats["processed"] += 1
                        status = 204
            self.send_response(status)
            self.end_headers()

    return Handler


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "verify-vectors":
        return verify_vectors(sys.argv[2])
    secrets = [s for s in os.environ.get("WEBHOOK_SECRETS", "").replace(",", " ").split(" ") if s]
    if not secrets:
        print("Set WEBHOOK_SECRETS to one or more whsec_ secrets.", file=sys.stderr)
        return 2
    state = State(secrets, int(os.environ.get("FAIL_FIRST_ATTEMPTS", "0")))
    port = int(os.environ.get("PORT", "8080"))
    print(f"python receiver listening on {port}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", port), make_handler(state)).serve_forever()
    return 0


if __name__ == "__main__":
    sys.exit(main())
