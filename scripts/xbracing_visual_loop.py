#!/usr/bin/env python3
"""
X-bracing visual QA loop via LocalControlBridge.

  1. Kill app, (optionally) rebuild, launch
  2. Open LastWorkspacePath from prefs (most recent)
  3. Switch to toolpath view, slice
  4. Wait for "Slice complete", screenshot
  5. Write iteration artifact under scripts/xbracing_loop_out/

Usage:
  python3 scripts/xbracing_visual_loop.py              # one full cycle
  python3 scripts/xbracing_visual_loop.py --iter 3     # tag output as iter 3
  python3 scripts/xbracing_visual_loop.py --no-build   # skip dotnet build
  python3 scripts/xbracing_visual_loop.py --screenshot-only  # app already running
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PREFS = Path.home() / "Library/Application Support/MassiveSlicer/prefs.json"
PORT_FILE = Path.home() / "Library/Application Support/MassiveSlicer/bridge.port"
OUT_DIR = REPO / "scripts" / "xbracing_loop_out"
DEFAULT_PORT = 8723


def log(msg: str) -> None:
    print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)


def bridge_port() -> int:
    try:
        return int(PORT_FILE.read_text().strip())
    except Exception:
        return DEFAULT_PORT


def http_json(method: str, path: str, body: str | None = None, timeout: float = 120.0) -> dict:
    port = bridge_port()
    url = f"http://127.0.0.1:{port}{path}"
    data = body.encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode("utf-8")
    return json.loads(raw) if raw else {}


def cmd(command: str, timeout: float = 120.0) -> dict:
    return http_json("POST", "/command", json.dumps({"command": command}), timeout=timeout)


def wait_bridge(timeout_s: float = 180.0) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        try:
            r = http_json("GET", "/ping", timeout=2.0)
            if r.get("ok"):
                log(f"bridge up on port {bridge_port()}")
                return True
        except Exception:
            pass
        time.sleep(1.0)
    return False


def recent_workspace() -> str | None:
    if not PREFS.exists():
        return None
    data = json.loads(PREFS.read_text())
    path = data.get("LastWorkspacePath") or ""
    if path and Path(path).exists():
        return path
    for p in data.get("RecentWorkspaces") or []:
        if p and Path(p).exists():
            return p
    return None


def kill_app() -> None:
    log("killing MassiveSlicer.App")
    subprocess.run(
        ["pkill", "-f", "MassiveSlicer.App"],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    time.sleep(1.5)


def launch_app(no_build: bool) -> subprocess.Popen:
    env = os.environ.copy()
    env["PATH"] = "/usr/local/share/dotnet:" + env.get("PATH", "")
    if not no_build:
        log("building app (dotnet build)")
        br = subprocess.run(
            [
                "dotnet",
                "build",
                str(REPO / "src/MassiveSlicer.App/MassiveSlicer.App.csproj"),
                "-c",
                "Debug",
                "-v",
                "q",
            ],
            cwd=str(REPO),
            env=env,
            capture_output=True,
            text=True,
        )
        if br.returncode != 0:
            log("BUILD FAILED")
            print(br.stdout[-2000:] if br.stdout else "")
            print(br.stderr[-2000:] if br.stderr else "")
            sys.exit(1)
        log("build ok")

    log("launching app")
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    log_path = OUT_DIR / "app_stdout.log"
    log_f = open(log_path, "ab", buffering=0)
    # Prefer already-built binary for faster restart
    dll = (
        REPO
        / "src/MassiveSlicer.App/bin/Debug/net8.0/MassiveSlicer.App.dll"
    )
    if dll.exists():
        proc = subprocess.Popen(
            ["dotnet", str(dll)],
            cwd=str(REPO),
            env=env,
            stdout=log_f,
            stderr=log_f,
            start_new_session=True,
        )
    else:
        proc = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", str(REPO / "src/MassiveSlicer.App")],
            cwd=str(REPO),
            env=env,
            stdout=log_f,
            stderr=log_f,
            start_new_session=True,
        )
    log(f"app pid={proc.pid} (stdout → {log_path})")
    return proc


def wait_slice_complete(timeout_s: float = 600.0) -> bool:
    """Poll console + IsSlicing for completion of the *current* slice."""
    t0 = time.time()
    last_log = ""
    seen_start = False
    seen_post = False
    while time.time() - t0 < timeout_s:
        try:
            r = http_json("GET", "/console?n=50", timeout=5.0)
            lines = [x.get("text", "") for x in r.get("lines", [])]
            joined = "\n".join(lines)
            if joined != last_log:
                for ln in lines[-8:]:
                    if ln and ln not in last_log:
                        log(f"console: {ln[:160]}")
                last_log = joined
            low = joined.lower()
            if "slicing selected mesh" in low or "preparing mesh" in low or "planar: intersecting" in low:
                seen_start = True
            if "applying post-processing" in low or "x-bracing]" in low or "formbound] emit" in low:
                seen_post = True
            if seen_start and "slice failed" in low and "select a mesh" not in low:
                log("slice appears to have failed")
                return False
            if "select a mesh first" in low and not seen_start and time.time() - t0 > 3:
                log("slice rejected: no mesh selected")
                return False

            # Idle after post-process (or after long enough progress)
            st = "\n".join((cmd("viewset IsSlicing", timeout=10.0) or {}).get("output") or [])
            idle = "False" in st
            if seen_start and idle and (seen_post or time.time() - t0 > 15):
                log(f"slice idle after {time.time() - t0:.0f}s (post={seen_post})")
                return True
        except Exception as e:
            log(f"console poll: {e}")
        time.sleep(1.5)
    log("slice wait timed out")
    return False


def take_screenshot(dest: Path) -> Path | None:
    log("requesting screenshot")
    try:
        r = http_json("GET", "/screenshot", timeout=60.0)
    except Exception as e:
        log(f"screenshot failed: {e}")
        return None
    if not r.get("ok"):
        log(f"screenshot error: {r}")
        return None
    src = Path(r["path"])
    if not src.exists():
        log(f"screenshot missing: {src}")
        return None
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(src.read_bytes())
    log(f"screenshot saved → {dest} ({dest.stat().st_size} bytes)")
    # also write meta
    meta = dest.with_suffix(".json")
    meta.write_text(json.dumps({"source": str(src), "bytes": dest.stat().st_size, "at": datetime.now().isoformat()}, indent=2))
    return dest


def run_cycle(iteration: int, no_build: bool, screenshot_only: bool) -> Path | None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    shot = OUT_DIR / f"iter{iteration:02d}_{stamp}.png"

    if not screenshot_only:
        kill_app()
        launch_app(no_build=no_build)
        if not wait_bridge(180):
            log("bridge never came up")
            return None
        # let UI settle
        time.sleep(3.0)

        ws = recent_workspace()
        if not ws:
            log("no LastWorkspacePath / RecentWorkspaces found")
            return None
        log(f"opening workspace: {ws}")
        # quote path for spaces
        r = cmd(f'open "{ws}"', timeout=180.0)
        log(f"open → {r.get('output', r)}")
        # Wait for restore + any auto-reslice to settle
        time.sleep(8.0)

        # Select first mesh content object (mesh=1). Do NOT clear console — races selection.
        objs = cmd("objects", timeout=30.0)
        log(f"objects → {objs.get('output', objs)}")
        mesh_name = None
        for line in objs.get("output") or []:
            # [0] "Name"  nodes=1 mesh=1 ...
            if "mesh=1" in line and '"' in line:
                try:
                    mesh_name = line.split('"', 2)[1]
                    break
                except Exception:
                    pass
        if not mesh_name:
            log("no mesh=1 object found")
            return None
        log(f"selecting mesh: {mesh_name}")
        r = cmd(f"select {mesh_name}", timeout=30.0)
        log(f"select → {r.get('output', r)}")
        sel = cmd("selection", timeout=15.0)
        log(f"selection → {sel.get('output', sel)}")
        sel_txt = "\n".join(sel.get("output") or [])
        if "mesh=1" not in sel_txt:
            log("selection is not a mesh — abort slice")
            return None
        # Ensure SliceCommand canExecute (console select can miss HasMeshSelected)
        cmd("viewset HasMeshSelected true")
        # Wait until not already slicing (workspace open auto-reslice)
        for _ in range(180):
            st = "\n".join((cmd("viewset IsSlicing") or {}).get("output") or [])
            if "False" in st:
                break
            time.sleep(1.0)
        else:
            log("timed out waiting for auto-reslice idle")

        # Dense-enough X for diagnosis (user may still use 600 later)
        cmd("addset XBracingSpanMm 180")
        cmd("addset XBracingDepthMm 50")
        cmd("addset XBracingEnabled true")

        cmd("viewmode Toolpath")
        time.sleep(0.5)

        log("slicing…")
        r = cmd("slice", timeout=30.0)
        log(f"slice start → {r.get('output', r)}")
        start_txt = "\n".join(r.get("output") or []).lower()
        if "select a mesh first" in start_txt or "already slicing" in start_txt:
            log("slice rejected — fix flags and retry")
            cmd(f"select {mesh_name}")
            cmd("viewset HasMeshSelected true")
            time.sleep(1.0)
            r = cmd("slice", timeout=30.0)
            log(f"slice retry → {r.get('output', r)}")

        if not wait_slice_complete(900.0):
            log("slice did not complete cleanly; still capturing")
        # Drain IsSlicing
        for _ in range(60):
            st = "\n".join((cmd("viewset IsSlicing") or {}).get("output") or [])
            if "False" in st:
                break
            time.sleep(0.5)
        time.sleep(2.0)
        # Surface planner pin depths from app log
        log_path = OUT_DIR / "app_stdout.log"
        if log_path.exists():
            lines = log_path.read_text(errors="replace").splitlines()
            xb = [ln for ln in lines if "[x-bracing]" in ln]
            for ln in xb[-12:]:
                log(f"app: {ln[:180]}")

        cmd("viewmode Toolpath")
        try:
            cmd("frame")
        except Exception:
            pass
        time.sleep(1.5)
    else:
        if not wait_bridge(5):
            log("app not running for screenshot-only")
            return None

    return take_screenshot(shot)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--iter", type=int, default=1, help="iteration number for output naming")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--screenshot-only", action="store_true")
    ap.add_argument("--keep-open", action="store_true", help="do not kill app after screenshot")
    args = ap.parse_args()

    shot = run_cycle(args.iter, no_build=args.no_build, screenshot_only=args.screenshot_only)
    if shot is None:
        return 1
    print(f"SCREENSHOT={shot}")
    if not args.keep_open and not args.screenshot_only:
        # leave app open for visual inspection by default when keep-open;
        # default: keep open so agent can re-screenshot if needed
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
