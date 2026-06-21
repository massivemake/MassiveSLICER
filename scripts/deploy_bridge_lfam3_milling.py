#!/usr/bin/env python3
"""
deploy_bridge_lfam3_milling.py
──────────────────────────────
One-shot deployer: copies lfam_monitor_bridge.py and installs
lfam-monitor.service on the LFAM 3 milling cabinet RevPi (192.168.0.246).

Canonical path in MassiveSlicer repo: scripts/deploy_bridge_lfam3_milling.py
GitHub: https://github.com/MattWhite3194/MassiveSlicer

Run from the monitoring PC when the milling RevPi is reachable:
  python scripts/deploy_bridge_lfam3_milling.py --pass yourpassword
  python scripts/deploy_bridge_lfam3_milling.py --ip 192.168.0.246 --pass yourpassword

The bridge serves MILLING_IO RevPi DIO over TCP/JSON on port 8765.
Protocol matches MassiveSLICER MillingModbusClient / ExtruderBridgeClient:
  {"cmd":"ping"}                              → {"ok":true,"pong":true}
  {"cmd":"read"}                              → {"ok":true,"ts":N,"io":{...}}
  {"cmd":"write","name":"DO_01_redLamp","value":true} → {"ok":true}

Signal names match modbus_monitor.py MILLING_IO and Lfam3LiveIoCatalog.MillingPollKeys.
Standalone service — does not replace lights_management or other milling logic.
"""

from __future__ import annotations

import argparse
import json
import socket
import sys
import time

DEFAULT_IP   = "192.168.0.246"
DEFAULT_USER = "pi"
DEFAULT_PASS = "Massiveasfuck45!!"
BRIDGE_PY    = "/home/pi/lfam_monitor_bridge.py"
SERVICE_PATH = "/etc/systemd/system/lfam-monitor.service"
BRIDGE_PORT  = 8765

# modbus_monitor.py MILLING_IO — keep in sync with Lfam3LiveIoCatalog.MillingPollKeys
MILLING_IO = [
    "DI_04_gateOpenStop",
    "DI_05_SS1standstill",
    "DI_06_SS1stop",
    "DI_07_emergencyState",
    "DI_08_digitalFromKUKA",
    "DO_01_redLamp",
    "DO_02_yellowLamp",
    "DO_03_greenLamp",
]

BRIDGE_SCRIPT = r'''#!/usr/bin/env python3
"""
LFAM 3 Milling Cabinet Monitoring Bridge  —  v1.0
Serves RevPi DIO (MILLING_IO) over TCP/JSON on port 8765.
Deployed by deploy_bridge_lfam3_milling.py
"""
import json, socket, threading, time, warnings
warnings.filterwarnings("ignore")

PORT          = 8765
POLL_INTERVAL = 1.0

IO_NAMES = [
    "DI_04_gateOpenStop",
    "DI_05_SS1standstill",
    "DI_06_SS1stop",
    "DI_07_emergencyState",
    "DI_08_digitalFromKUKA",
    "DO_01_redLamp",
    "DO_02_yellowLamp",
    "DO_03_greenLamp",
]

WRITABLE = {
    "DO_01_redLamp",
    "DO_02_yellowLamp",
    "DO_03_greenLamp",
}


class MonitoringBridge:
    def __init__(self):
        self._cache = {}
        self._lock  = threading.Lock()
        self._rpi   = None
        self._running = True

    def _init_rpi(self):
        try:
            import revpimodio2
            self._rpi = revpimodio2.RevPiModIO(autorefresh=False)
        except Exception as e:
            print(f"[milling-bridge] RevPi init failed: {e}")
            self._rpi = None

    def _read_io(self):
        if self._rpi is None:
            return {}
        try:
            self._rpi.readprocimg()
        except Exception:
            pass
        out = {}
        for name in IO_NAMES:
            try:
                out[name] = bool(self._rpi.io[name].value)
            except Exception:
                out[name] = None
        return out

    def _write_io(self, name, value):
        if self._rpi is None:
            return False, "RevPi not initialised"
        if name not in WRITABLE:
            return False, f"{name} is read-only"
        try:
            self._rpi.io[name].value = bool(value)
            self._rpi.writeprocimg()
            return True, None
        except Exception as e:
            return False, str(e)

    def _poll_loop(self):
        self._init_rpi()
        while self._running:
            try:
                snap = {"ts": int(time.time()), "io": self._read_io()}
                with self._lock:
                    self._cache = snap
            except Exception as e:
                print(f"[milling-bridge] poll error: {e}")
            time.sleep(POLL_INTERVAL)

    def get_snapshot(self):
        with self._lock:
            return dict(self._cache)

    def _handle_client(self, conn, addr):
        try:
            conn.settimeout(10)
            buf = b""
            while b"\n" not in buf and len(buf) < 4096:
                chunk = conn.recv(256)
                if not chunk:
                    break
                buf += chunk
            if not buf.strip():
                return
            try:
                req = json.loads(buf.decode("utf-8", errors="replace").strip())
            except Exception:
                conn.sendall(b'{"ok":false,"error":"invalid json"}\n')
                return
            cmd = req.get("cmd", "")
            if cmd == "ping":
                conn.sendall(b'{"ok":true,"pong":true}\n')
            elif cmd == "read":
                snap = self.get_snapshot()
                conn.sendall((json.dumps({"ok": True, **snap}) + "\n").encode())
            elif cmd == "write":
                name = req.get("name")
                value = req.get("value")
                if name is None or value is None:
                    conn.sendall(b'{"ok":false,"error":"missing name or value"}\n')
                else:
                    ok, err = self._write_io(name, value)
                    resp = {"ok": ok}
                    if err:
                        resp["error"] = err
                    conn.sendall((json.dumps(resp) + "\n").encode())
            else:
                conn.sendall(b'{"ok":false,"error":"unknown command"}\n')
        except Exception as e:
            try:
                conn.sendall((json.dumps({"ok": False, "error": str(e)}) + "\n").encode())
            except Exception:
                pass
        finally:
            try:
                conn.close()
            except Exception:
                pass

    def run(self):
        poll = threading.Thread(target=self._poll_loop, daemon=True, name="milling-bridge-poll")
        poll.start()
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", PORT))
        srv.listen(10)
        print(f"[milling-bridge] listening on 0.0.0.0:{PORT}")
        while self._running:
            try:
                conn, addr = srv.accept()
                threading.Thread(
                    target=self._handle_client, args=(conn, addr), daemon=True
                ).start()
            except Exception as e:
                if self._running:
                    print(f"[milling-bridge] accept error: {e}")


if __name__ == "__main__":
    bridge = MonitoringBridge()
    try:
        bridge.run()
    except KeyboardInterrupt:
        bridge._running = False
'''

SERVICE_UNIT = """[Unit]
Description=LFAM 3 Milling Cabinet Monitoring Bridge
After=network.target revpi-device-setup.service
Wants=network-online.target

[Service]
Type=simple
Restart=always
RestartSec=5
User=pi
WorkingDirectory=/home/pi
ExecStart=/usr/bin/python3 /home/pi/lfam_monitor_bridge.py
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
"""


def wait_for_host(ip: str, port: int = 22, retries: int = 6, delay: int = 5) -> bool:
    for i in range(retries):
        try:
            with socket.create_connection((ip, port), timeout=5):
                return True
        except OSError:
            if i < retries - 1:
                print(f"  [{i + 1}/{retries}] {ip}:{port} not reachable, retrying in {delay}s...")
                time.sleep(delay)
    return False


def bridge_exchange(ip: str, payload: dict, port: int = BRIDGE_PORT, timeout: float = 8.0) -> dict:
    with socket.create_connection((ip, port), timeout=timeout) as s:
        s.sendall((json.dumps(payload) + "\n").encode())
        buf = b""
        while b"\n" not in buf and len(buf) < 131072:
            chunk = s.recv(4096)
            if not chunk:
                break
            buf += chunk
    return json.loads(buf.decode("utf-8", errors="replace").strip())


def verify_bridge(ip: str) -> None:
    print(f"\n  Testing bridge on {ip}:{BRIDGE_PORT}...")
    try:
        ping = bridge_exchange(ip, {"cmd": "ping"})
        if not ping.get("pong"):
            print(f"  Bridge ping unexpected: {ping}")
            return
        print(f"  Bridge ping OK")

        snap = bridge_exchange(ip, {"cmd": "read"})
        if not snap.get("ok"):
            print(f"  Bridge read failed: {snap}")
            return
        io = snap.get("io", {})
        present = [k for k in MILLING_IO if k in io]
        print(f"  Bridge read OK — {len(present)}/{len(MILLING_IO)} MILLING_IO keys present")
        for key in MILLING_IO:
            if key in io:
                print(f"    {key}: {io[key]}")
            else:
                print(f"    {key}: (missing)")
    except Exception as e:
        print(f"  Bridge verification FAILED: {e}")
        print("  Service may still be starting, or revpimodio2 could not open the process image.")


def deploy(ip: str, user: str, password: str) -> None:
    try:
        import paramiko
    except ImportError:
        print("ERROR: paramiko not installed. Run: pip install paramiko")
        sys.exit(1)

    print(f"\nDeploying lfam-monitor bridge to LFAM 3 milling cabinet @ {ip}")
    print("Waiting for SSH to be reachable...")
    if not wait_for_host(ip, 22):
        print(f"ERROR: {ip}:22 still unreachable after retries.")
        print("Make sure the milling RevPi (RevPi130866) is powered on and on the LAN.")
        sys.exit(1)

    print("Connecting via SSH...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(ip, username=user, password=password, timeout=20, banner_timeout=30)
    print("  Connected.")

    sftp = ssh.open_sftp()
    print(f"  Uploading bridge script to {BRIDGE_PY}...")
    with sftp.open(BRIDGE_PY, "w") as f:
        f.write(BRIDGE_SCRIPT)
    sftp.close()

    def run(cmd: str) -> str:
        _, stdout, stderr = ssh.exec_command(cmd, timeout=30)
        out = stdout.read().decode().strip()
        err = stderr.read().decode().strip()
        if out:
            print(f"    {out}")
        if err and "Warning" not in err:
            print(f"    STDERR: {err}")
        return out

    print("  Setting bridge script permissions...")
    run(f"chmod +x {BRIDGE_PY}")

    print(f"  Writing systemd unit to {SERVICE_PATH}...")
    run(f"sudo tee {SERVICE_PATH} > /dev/null << 'EOSVC'\n{SERVICE_UNIT}\nEOSVC")

    print("  Reloading systemd + enabling + starting lfam-monitor.service...")
    run("sudo systemctl daemon-reload")
    run("sudo systemctl enable lfam-monitor.service")
    run("sudo systemctl restart lfam-monitor.service")

    print("  Waiting 3s for service to start...")
    time.sleep(3)

    print("  Checking service status...")
    run("sudo systemctl is-active lfam-monitor.service")
    run("sudo journalctl -u lfam-monitor.service -n 12 --no-pager")

    ssh.close()
    verify_bridge(ip)

    print("\nDeployment complete.")
    print(f"LFAM 3 milling cabinet now serves MILLING_IO on {ip}:{BRIDGE_PORT}.")
    print("MassiveSLICER Live I/O Phase 3 will poll millIp from lfam3.json when the panel is expanded.")


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--ip",   default=DEFAULT_IP,   help=f"Milling RevPi IP (default {DEFAULT_IP})")
    p.add_argument("--user", default=DEFAULT_USER, help=f"SSH user (default {DEFAULT_USER})")
    p.add_argument("--pass", dest="password", default=DEFAULT_PASS,
                   help="SSH password (default from lfam_settings.json LFAM 3 profile)")
    return p.parse_args()


if __name__ == "__main__":
    args = parse_args()
    deploy(args.ip, args.user, args.password)