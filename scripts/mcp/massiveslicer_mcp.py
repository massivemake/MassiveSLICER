#!/usr/bin/env python3
"""MCP stdio shim for MassiveSlicer LocalControlBridge (http://127.0.0.1:<port>).

Reads port from %LOCALAPPDATA%/MassiveSlicer/bridge.port (default 8723).

Add to ~/.grok/config.toml:
  [mcp_servers.massiveslicer]
  command = "python"
  args = ["\\\\192.168.0.191\\MassiveFILES\\Research\\LFAM\\MassiveSLICER V2\\scripts\\mcp\\massiveslicer_mcp.py"]
  enabled = true
"""

from __future__ import annotations

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


def bridge_port() -> int:
    port_file = Path(os.environ.get("LOCALAPPDATA", "")) / "MassiveSlicer" / "bridge.port"
    if port_file.is_file():
        try:
            return int(port_file.read_text(encoding="utf-8").strip())
        except ValueError:
            pass
    return int(os.environ.get("MASSIVESLICER_BRIDGE_PORT", "8723"))


def bridge_get(path: str, accept: str = "application/json") -> bytes:
    port = bridge_port()
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        headers={"Accept": accept},
        method="GET",
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read()


def bridge_post_command(command: str) -> dict[str, Any]:
    port = bridge_port()
    body = json.dumps({"command": command}).encode("utf-8")
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/command",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read().decode("utf-8"))


def bridge_post_json(path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
    port = bridge_port()
    body = json.dumps(payload or {}).encode("utf-8")
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}{path}",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.loads(resp.read().decode("utf-8"))


TOOLS = [
    {
        "name": "massiveslicer_status",
        "description": "Robot connection status, active tool/base, and live TCP pose from MassiveSlicer.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "massiveslicer_console",
        "description": "Last N lines from the MassiveSlicer in-app console.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "n": {"type": "integer", "description": "Number of lines (default 50)", "default": 50},
            },
        },
    },
    {
        "name": "massiveslicer_command",
        "description": "Run a MassiveSlicer console command (sync, scan-cal, bed-cal, scan, diag-scans, etc.).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "command": {"type": "string", "description": "Console command line"},
            },
            "required": ["command"],
        },
    },
    {
        "name": "massiveslicer_screenshot",
        "description": "Capture the full MassiveSlicer app window (toolbar, panels, console, viewport) as PNG. Returns the saved file path.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "raw": {
                    "type": "boolean",
                    "description": "If true, also note raw PNG URL (format=png)",
                    "default": False,
                },
            },
        },
    },
    {
        "name": "massiveslicer_materials_get",
        "description": (
            "Read PBR/material viewport state: shader mode, exposure, IBL, lighting, "
            "per-map layer toggles (baseColor, metallicRoughness, normal, ao, emissive, layerOverlay), "
            "factor overrides, adaptive layer preview, and which maps exist on the active print object."
        ),
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "massiveslicer_materials_set",
        "description": (
            "Update PBR/material viewport state (partial patch). Controls shaderMode "
            "(Standard, Clay, Metal, Chrome, BaseColor, NormalMap, etc.), exposure, iblIntensity, "
            "lightAzimuth/lightElevation/lightIntensity, layerPreview, layers.{baseColor,metallicRoughness,"
            "normal,ao,emissive,layerOverlay}, layerOverlayStrength (0-1), and factors "
            "{metallic,roughness,normalScale,occlusionStrength,emissive:[r,g,b]} (null to clear override)."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "shaderMode": {"type": "string"},
                "exposure": {"type": "number"},
                "iblIntensity": {"type": "number"},
                "lightAzimuth": {"type": "number"},
                "lightElevation": {"type": "number"},
                "lightIntensity": {"type": "number"},
                "layerPreview": {"type": "boolean"},
                "layerOverlayStrength": {"type": "number", "minimum": 0, "maximum": 1},
                "layers": {
                    "type": "object",
                    "properties": {
                        "baseColor": {"type": "boolean"},
                        "metallicRoughness": {"type": "boolean"},
                        "normal": {"type": "boolean"},
                        "ao": {"type": "boolean"},
                        "emissive": {"type": "boolean"},
                        "layerOverlay": {"type": "boolean"},
                    },
                },
                "factors": {
                    "type": "object",
                    "properties": {
                        "metallic": {"type": ["number", "null"]},
                        "roughness": {"type": ["number", "null"]},
                        "normalScale": {"type": ["number", "null"]},
                        "occlusionStrength": {"type": ["number", "null"]},
                        "emissive": {
                            "type": ["array", "null"],
                            "items": {"type": "number"},
                            "minItems": 3,
                            "maxItems": 3,
                        },
                    },
                },
            },
        },
    },
]


def send(msg: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()


def tool_result(req_id: Any, text: str) -> None:
    send({
        "jsonrpc": "2.0",
        "id": req_id,
        "result": {
            "content": [{"type": "text", "text": text}],
        },
    })


def tool_error(req_id: Any, message: str) -> None:
    send({
        "jsonrpc": "2.0",
        "id": req_id,
        "error": {"code": -32000, "message": message},
    })


def handle_tools_call(req_id: Any, name: str, arguments: dict[str, Any]) -> None:
    try:
        if name == "massiveslicer_status":
            data = json.loads(bridge_get("/status").decode("utf-8"))
            tool_result(req_id, json.dumps(data, indent=2))

        elif name == "massiveslicer_console":
            n = int(arguments.get("n", 50))
            data = json.loads(bridge_get(f"/console?n={n}").decode("utf-8"))
            lines = [ln.get("text", "") for ln in data.get("lines", [])]
            tool_result(req_id, "\n".join(lines))

        elif name == "massiveslicer_command":
            cmd = str(arguments.get("command", "")).strip()
            if not cmd:
                tool_error(req_id, "command is required")
                return
            data = bridge_post_command(cmd)
            out = data.get("output", [])
            tool_result(req_id, json.dumps(data, indent=2) + ("\n\n" + "\n".join(out) if out else ""))

        elif name == "massiveslicer_screenshot":
            data = json.loads(bridge_get("/screenshot").decode("utf-8"))
            if not data.get("ok"):
                tool_error(req_id, data.get("error", "screenshot failed"))
                return
            path = data.get("path", "")
            extra = ""
            if arguments.get("raw"):
                port = bridge_port()
                extra = f"\nraw: http://127.0.0.1:{port}/screenshot?format=png"
            tool_result(req_id, f"Screenshot saved: {path} ({data.get('bytes', 0)} bytes){extra}")

        elif name == "massiveslicer_materials_get":
            data = json.loads(bridge_get("/materials").decode("utf-8"))
            tool_result(req_id, json.dumps(data, indent=2))

        elif name == "massiveslicer_materials_set":
            patch = {k: v for k, v in arguments.items() if v is not None}
            data = bridge_post_json("/materials", patch)
            tool_result(req_id, json.dumps(data, indent=2))

        else:
            tool_error(req_id, f"unknown tool: {name}")

    except urllib.error.URLError as ex:
        tool_error(req_id, f"MassiveSlicer bridge not reachable on port {bridge_port()}: {ex}")
    except Exception as ex:  # noqa: BLE001
        tool_error(req_id, str(ex))


def main() -> None:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue

        req_id = msg.get("id")
        method = msg.get("method", "")

        if method == "initialize":
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "massiveslicer", "version": "1.1.0"},
                },
            })
        elif method == "notifications/initialized":
            pass
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": req_id, "result": {"tools": TOOLS}})
        elif method == "tools/call":
            params = msg.get("params", {})
            handle_tools_call(req_id, params.get("name", ""), params.get("arguments") or {})
        elif method == "ping":
            send({"jsonrpc": "2.0", "id": req_id, "result": {}})
        else:
            if req_id is not None:
                send({
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "error": {"code": -32601, "message": f"Method not found: {method}"},
                })


if __name__ == "__main__":
    main()