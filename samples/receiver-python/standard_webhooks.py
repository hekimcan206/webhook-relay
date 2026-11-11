"""A from-scratch Standard Webhooks v1 verifier using only the standard library.

HMAC-SHA256 over "{webhook-id}.{webhook-timestamp}.{raw body}", keyed with the base64-decoded part of the whsec_
secret, compared in constant time against every v1 entry of the webhook-signature header.
"""

import base64
import hashlib
import hmac
import re
import time
from typing import Iterable, Optional

_CANONICAL_TIMESTAMP = re.compile(r"^(0|[1-9][0-9]{0,14})$")


def verify(
    secrets: Iterable[str],
    webhook_id: Optional[str],
    timestamp: Optional[str],
    signature_header: Optional[str],
    raw_body: bytes,
    now: Optional[int] = None,
    tolerance_seconds: int = 300,
) -> bool:
    if not webhook_id or not timestamp or not signature_header:
        return False
    if not _CANONICAL_TIMESTAMP.match(timestamp):
        return False
    now = int(time.time()) if now is None else now
    if abs(now - int(timestamp)) > tolerance_seconds:
        return False

    signed = f"{webhook_id}.{timestamp}.".encode("utf-8") + raw_body
    entries = [e for e in signature_header.split(" ") if e]
    for secret in secrets:
        key = base64.b64decode(secret[len("whsec_"):] if secret.startswith("whsec_") else secret)
        expected = base64.b64encode(hmac.new(key, signed, hashlib.sha256).digest())
        for entry in entries:
            version, _, candidate = entry.partition(",")
            if version == "v1" and hmac.compare_digest(candidate.encode("ascii", "replace"), expected):
                return True
    return False
