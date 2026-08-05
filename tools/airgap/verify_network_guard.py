#!/usr/bin/env python3
"""Verify that bstrings Python air-gap mode blocks WAN access but permits loopback."""

from __future__ import annotations

import importlib
import json
import os
import socket
import sys
from pathlib import Path

ENRICHMENT_ROOT = Path(__file__).resolve().parents[1] / "enrichment"
sys.path.insert(0, str(ENRICHMENT_ROOT))

bstrings_enrich = importlib.import_module("bstrings_enrich")
AirgapNetworkError = bstrings_enrich.AirgapNetworkError
enable_airgap_mode = bstrings_enrich.enable_airgap_mode


def main() -> int:
    enable_airgap_mode()
    required = {
        "HF_DATASETS_OFFLINE": "1",
        "HF_HUB_OFFLINE": "1",
        "PIP_NO_INDEX": "1",
        "TRANSFORMERS_OFFLINE": "1",
        "UV_OFFLINE": "1",
    }
    missing = {
        name: os.environ.get(name)
        for name, value in required.items()
        if os.environ.get(name) != value
    }
    if missing:
        raise RuntimeError(f"Air-gap environment is incomplete: {missing}")

    try:
        socket.getaddrinfo("example.com", 443)
    except AirgapNetworkError:
        external_blocked = True
    else:
        external_blocked = False
    if not external_blocked:
        raise RuntimeError("Air-gap mode did not block external DNS")

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        with socket.create_connection(listener.getsockname(), timeout=2):
            connection, _ = listener.accept()
            connection.close()

    print(json.dumps({"externalBlocked": True, "loopbackAllowed": True}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
