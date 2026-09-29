#!/usr/bin/env python3
"""Read-only: is this MassiveSLICER instance worth driving?

Exit 0 = ping ok. Exit 2 = app not running. Exit 1 = error.
Does not launch the GUI. Does not POST /command (that can move a robot).
"""
from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path

PREFERRED = range(8723, 8729)


def port_files() -> list[Path]:
    home = Path.home()
    return [
        Path(os.environ.get("LOCALAPPDATA", "")) / "MassiveSlicer" / "bridge.port",
        home / "Library" / "Application Support" / "MassiveSlicer" / "bridge.port",
        home / ".local" / "share" / "MassiveSlicer" / "bridge.port",
    ]


def read_port_file() -> int | None:
    for p in port_files():
        if not p.suffix or not p.parent.name:
            continue
        try:
            if p.is_file():
                n = int(p.read_text().strip().split()[0])
                if n:
                    return n
        except (OSError, ValueError):
            continue
    return None


def host() -> str:
    return os.environ.get("MASSIVESLICER_BRIDGE_HOST", "127.0.0.1").strip() or "127.0.0.1"


def get(url: str, timeout: float = 2.0) -> tuple[int, str]:
    req = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")
    except Exception as e:
        return 0, str(e)


def try_ping(h: str, port: int) -> dict | None:
    code, body = get(f"http://{h}:{port}/ping", timeout=1.5)
    if code != 200:
        return None
    try:
        data = json.loads(body)
    except json.JSONDecodeError:
        return None
    if data.get("ok") and data.get("app") == "MassiveSlicer":
        data["_port"] = port
        return data
    return None


def main() -> int:
    h = host()
    ports: list[int] = []
    filed = read_port_file()
    if filed:
        ports.append(filed)
    for p in PREFERRED:
        if p not in ports:
            ports.append(p)

    ping = None
    for p in ports:
        ping = try_ping(h, p)
        if ping:
            break

    report: dict = {
        "ok": False,
        "host": h,
        "port_file": filed,
        "lan_env": os.environ.get("MASSIVESLICER_BRIDGE_LAN"),
    }

    if ping is None:
        report["error"] = "MassiveSLICER bridge not answering /ping"
        report["hint"] = (
            "Mac: cd the NAS tree && bash run.sh. "
            "Shop: Release rebuild, console must show [bridge]. "
            "Do not exec the apphost on MassiveFILES (noexec SMB)."
        )
        print(json.dumps(report, indent=2))
        return 2

    port = int(ping["_port"])
    report["ping"] = {k: v for k, v in ping.items() if not str(k).startswith("_")}
    report["port"] = port

    scode, sbody = get(f"http://{h}:{port}/status", timeout=3)
    status = None
    if scode == 200:
        try:
            status = json.loads(sbody)
        except json.JSONDecodeError:
            report["status_raw"] = sbody[:500]
    report["status"] = status
    report["ok"] = True
    report["worth_driving"] = True
    if status and status.get("connected"):
        report["motion_warning"] = (
            "Robot connected. POST /command can move it. "
            "Drive only console reads (mill status, krlpost, outliner-tree) "
            "unless the user asked for motion."
        )
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
