#!/usr/bin/env python3
"""Drive the live MassiveSLICER control bridge.

  python3 drive.py ping|status|console|screenshot
  python3 drive.py cmd "mill status"

Blocks robot-motion commands unless --allow-motion.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.request
from pathlib import Path

MOTION = re.compile(
    r"^\s*(home|sync|move-pose|move\b|jog\b|run\b|play\b)\b",
    re.IGNORECASE,
)


def host() -> str:
    return os.environ.get("MASSIVESLICER_BRIDGE_HOST", "127.0.0.1").strip() or "127.0.0.1"


def discover_port(h: str) -> int:
    env = os.environ.get("MASSIVESLICER_BRIDGE_PORT")
    if env:
        return int(env)
    candidates = []
    local = Path(os.environ.get("LOCALAPPDATA", "")) / "MassiveSlicer" / "bridge.port"
    mac = Path.home() / "Library" / "Application Support" / "MassiveSlicer" / "bridge.port"
    for p in (local, mac):
        try:
            if p.is_file():
                candidates.append(int(p.read_text().strip().split()[0]))
        except (OSError, ValueError):
            pass
    candidates.extend(range(8723, 8729))
    seen = set()
    for port in candidates:
        if port in seen:
            continue
        seen.add(port)
        try:
            with urllib.request.urlopen(f"http://{h}:{port}/ping", timeout=1.2) as r:
                data = json.loads(r.read().decode())
            if data.get("ok") and data.get("app") == "MassiveSlicer":
                return port
        except Exception:
            continue
    raise SystemExit("bridge not answering /ping — run doctor.py")


def request(url: str, method: str = "GET", data: bytes | None = None, timeout: float = 8.0):
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        body = r.read()
        ctype = r.headers.get("Content-Type", "")
        return r.status, ctype, body


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["ping", "status", "console", "screenshot", "cmd"])
    ap.add_argument("command", nargs="?", help="console line for cmd")
    ap.add_argument("--allow-motion", action="store_true")
    ap.add_argument("--out", help="write screenshot PNG here")
    args = ap.parse_args()
    h = host()
    port = discover_port(h)
    base = f"http://{h}:{port}"

    if args.action == "ping":
        _, _, body = request(f"{base}/ping")
        sys.stdout.write(body.decode())
        return 0
    if args.action == "status":
        _, _, body = request(f"{base}/status")
        sys.stdout.write(body.decode())
        return 0
    if args.action == "console":
        _, _, body = request(f"{base}/console?n=40")
        sys.stdout.write(body.decode())
        return 0
    if args.action == "screenshot":
        _, ctype, body = request(f"{base}/screenshot?format=png", timeout=20)
        if "png" not in ctype.lower() and not body.startswith(b"\x89PNG"):
            sys.stdout.write(body.decode("utf-8", "replace"))
            return 1
        out = Path(args.out) if args.out else Path.home() / "Library" / "Application Support" / "MassiveSlicer" / "verify-evidence" / "last.png"
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(body)
        print(json.dumps({"ok": True, "path": str(out), "bytes": len(body)}))
        return 0

    if args.action == "cmd":
        if not args.command:
            print("usage: drive.py cmd \"mill status\"", file=sys.stderr)
            return 1
        if MOTION.search(args.command) and not args.allow_motion:
            print(
                json.dumps(
                    {
                        "ok": False,
                        "error": "blocked motion command",
                        "command": args.command,
                        "hint": "pass --allow-motion only if the user asked to move the robot",
                    }
                )
            )
            return 3
        payload = json.dumps({"command": args.command}).encode()
        _, _, body = request(f"{base}/command", method="POST", data=payload, timeout=30)
        sys.stdout.write(body.decode())
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
