"""MassiveBRAIN Blender addon — live sync into MassiveSLICER.

MassiveSLICER hosts the MassiveBRAIN WebSocket server (N-key HUD → MASSIVEBRAIN,
default localhost:4547). This addon connects as a client and PUSHES geometry
using the Plasticity-bridge wire format: metres, Z-up, full meshes inline.

Install: Edit → Preferences → Add-ons → Install… → pick this file.
Panel: 3D View → N sidebar → MassiveBRAIN tab.

Pure stdlib (raw-socket RFC 6455 WebSocket client) — no pip dependencies.
"""

bl_info = {
    "name": "MassiveBRAIN Sync",
    "author": "MassiveMAKE",
    "version": (0, 1, 0),
    "blender": (3, 6, 0),
    "location": "View3D > Sidebar > MassiveBRAIN",
    "description": "Push meshes live into MassiveSLICER (MassiveBRAIN server)",
    "category": "Import-Export",
}

import base64
import os
import socket
import struct
import time

import bpy
from bpy.app.handlers import persistent

# ── Wire helpers (Plasticity bridge format: little-endian, pad-4 strings) ──────

def _u32(v):
    return struct.pack("<I", v & 0xFFFFFFFF)


def _pad4(b: bytes) -> bytes:
    return b + b"\x00" * ((4 - len(b) % 4) % 4)


def _ws_send(sock, payload: bytes):
    """One masked binary frame (client frames must be masked per RFC 6455)."""
    mask = os.urandom(4)
    masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    n = len(payload)
    hdr = b"\x82"
    if n < 126:
        hdr += bytes([0x80 | n])
    elif n < 65536:
        hdr += bytes([0x80 | 126]) + struct.pack(">H", n)
    else:
        hdr += bytes([0x80 | 127]) + struct.pack(">Q", n)
    sock.sendall(hdr + mask + masked)


def _ws_recv(sock):
    def read(n):
        buf = b""
        while len(buf) < n:
            c = sock.recv(n - len(buf))
            if not c:
                raise ConnectionError("socket closed")
            buf += c
        return buf

    b1, b2 = read(2)
    n = b2 & 0x7F
    if n == 126:
        n = struct.unpack(">H", read(2))[0]
    elif n == 127:
        n = struct.unpack(">Q", read(8))[0]
    return read(n)


def _ws_connect(host: str, port: int) -> socket.socket:
    s = socket.create_connection((host, port), timeout=5)
    key = base64.b64encode(os.urandom(16)).decode()
    req = (
        f"GET / HTTP/1.1\r\nHost: {host}:{port}\r\n"
        "Upgrade: websocket\r\nConnection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"
    )
    s.sendall(req.encode())
    resp = b""
    while b"\r\n\r\n" not in resp:
        chunk = s.recv(4096)
        if not chunk:
            raise ConnectionError("closed during WebSocket handshake")
        resp += chunk
    if b"101" not in resp.split(b"\r\n", 1)[0]:
        raise ConnectionError("server refused WebSocket upgrade")
    return s


def _object_payload(obj_id: int, name: str, verts, tris) -> bytes:
    nb = name.encode("utf-8")[:255]
    o = _u32(0)                                   # objType Solid
    o += _u32(obj_id) + _u32(1)                   # id, version
    o += _u32(0xFFFFFFFF) + _u32(0xFFFFFFFF) + _u32(0)   # parent, material, flags
    o += _u32(len(nb)) + _pad4(nb)
    o += _u32(len(verts))
    o += struct.pack(f"<{len(verts) * 3}f", *(c for v in verts for c in v))
    o += _u32(len(tris))
    o += struct.pack(f"<{len(tris) * 3}i", *(i for t in tris for i in t))
    o += _u32(0) + _u32(0) + _u32(0)              # normals / groups / face ids
    return o


def _transaction(items) -> bytes:
    fn = b"blender"
    t = _u32(0)                                   # Transaction
    t += _u32(len(fn)) + _pad4(fn)
    t += _u32(1)                                  # version
    t += _u32(len(items))
    for item in items:
        t += _u32(len(item)) + item
    return t


# ── Connection state (module-level; survives operator lifetimes) ──────────────

class _State:
    sock = None
    synced = {}        # session_uid → object name (for delete detection / UI)
    dirty = set()      # session_uids pending a push
    deleted = set()    # ids pending a delete push
    status = "Disconnected"
    last_error = ""


S = _State()


def _connected():
    return S.sock is not None


def _disconnect():
    if S.sock is not None:
        try:
            S.sock.close()
        except OSError:
            pass
    S.sock = None
    S.status = "Disconnected"


def _mesh_snapshot(obj, depsgraph):
    """World-space metres, triangulated. Returns (verts, tris) or None."""
    if obj.type != "MESH":
        return None
    ev = obj.evaluated_get(depsgraph)
    mesh = ev.to_mesh()
    try:
        mesh.calc_loop_triangles()
        mw = obj.matrix_world
        verts = [tuple(mw @ v.co) for v in mesh.vertices]     # Blender is metres, Z-up
        tris = [tuple(t.vertices) for t in mesh.loop_triangles]
        return verts, tris
    finally:
        ev.to_mesh_clear()


def _push_objects(objs, depsgraph):
    if not _connected():
        return 0
    payloads = []
    for obj in objs:
        snap = _mesh_snapshot(obj, depsgraph)
        if snap is None or not snap[1]:
            continue
        payloads.append(_object_payload(obj.session_uid, obj.name, *snap))
        S.synced[obj.session_uid] = obj.name
    if not payloads:
        return 0
    item = _u32(1) + _u32(len(payloads)) + b"".join(payloads)   # Add/Update
    try:
        _ws_send(S.sock, _transaction([item]))
        return len(payloads)
    except OSError as ex:
        S.last_error = str(ex)
        _disconnect()
        return 0


def _push_deletes(ids):
    if not _connected() or not ids:
        return
    item = _u32(3) + _u32(len(ids)) + b"".join(_u32(i) for i in ids)   # Delete
    try:
        _ws_send(S.sock, _transaction([item]))
    except OSError as ex:
        S.last_error = str(ex)
        _disconnect()


# ── Live sync: depsgraph marks dirty; a timer batches the sends ────────────────

@persistent
def _on_depsgraph(scene, depsgraph):
    wm = bpy.context.window_manager
    if not _connected() or not getattr(wm, "massivebrain_live", False):
        return
    for upd in depsgraph.updates:
        obj = upd.id
        if not isinstance(obj, bpy.types.Object):
            continue
        # Depsgraph updates carry the EVALUATED copy whose session_uid is 0 —
        # the stable uid lives on the original datablock.
        uid = obj.original.session_uid if obj.original else obj.session_uid
        if uid in S.synced and (upd.is_updated_geometry or upd.is_updated_transform):
            S.dirty.add(uid)
    # Deletes: any synced uid that no longer resolves to an object.
    alive = {o.session_uid for o in bpy.data.objects}
    for uid in list(S.synced):
        if uid not in alive:
            S.deleted.add(uid)
            del S.synced[uid]


def _flush_timer():
    if not _connected():
        return 1.0
    if S.deleted:
        _push_deletes(list(S.deleted))
        S.deleted.clear()
    if S.dirty:
        dg = bpy.context.evaluated_depsgraph_get()
        objs = [o for o in bpy.data.objects if o.session_uid in S.dirty]
        S.dirty.clear()
        n = _push_objects(objs, dg)
        if n:
            S.status = f"Live — pushed {n} update(s)"
    return 0.4      # debounce interval (seconds)


# ── Operators / UI ─────────────────────────────────────────────────────────────

class MB_OT_connect(bpy.types.Operator):
    bl_idname = "massivebrain.connect"
    bl_label = "Connect"
    bl_description = "Connect to the MassiveBRAIN server in MassiveSLICER"

    def execute(self, ctx):
        wm = ctx.window_manager
        try:
            sock = _ws_connect(wm.massivebrain_host, wm.massivebrain_port)
            _ws_send(sock, _u32(100) + b"blender")            # Handshake + app name
            ack = _ws_recv(sock)
            if ack[:4] != _u32(100) or b"MASSIVEBRAIN" not in ack:
                sock.close()
                self.report({"ERROR"}, "Server is not MassiveBRAIN")
                return {"CANCELLED"}
            sock.settimeout(10)
            S.sock = sock
            S.status = f"Connected to {wm.massivebrain_host}:{wm.massivebrain_port}"
            S.last_error = ""
            if not bpy.app.timers.is_registered(_flush_timer):
                bpy.app.timers.register(_flush_timer, first_interval=0.4)
        except OSError as ex:
            self.report({"ERROR"}, f"Connect failed: {ex}")
            S.last_error = str(ex)
            return {"CANCELLED"}
        return {"FINISHED"}


class MB_OT_disconnect(bpy.types.Operator):
    bl_idname = "massivebrain.disconnect"
    bl_label = "Disconnect"

    def execute(self, ctx):
        _disconnect()
        return {"FINISHED"}


class MB_OT_push_selected(bpy.types.Operator):
    bl_idname = "massivebrain.push_selected"
    bl_label = "Push Selected"
    bl_description = "Send the selected mesh objects to MassiveSLICER (further edits sync live)"

    @classmethod
    def poll(cls, ctx):
        return _connected() and any(o.type == "MESH" for o in ctx.selected_objects)

    def execute(self, ctx):
        dg = ctx.evaluated_depsgraph_get()
        n = _push_objects([o for o in ctx.selected_objects if o.type == "MESH"], dg)
        S.status = f"Pushed {n} object(s); {len(S.synced)} linked"
        return {"FINISHED"}


class MB_PT_panel(bpy.types.Panel):
    bl_idname = "MB_PT_panel"
    bl_label = "MassiveBRAIN"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "MassiveBRAIN"

    def draw(self, ctx):
        wm = ctx.window_manager
        col = self.layout.column()
        row = col.row()
        row.prop(wm, "massivebrain_host", text="Host")
        row.prop(wm, "massivebrain_port", text="Port")
        if _connected():
            col.operator("massivebrain.disconnect", icon="UNLINKED")
        else:
            col.operator("massivebrain.connect", icon="LINKED")
        col.operator("massivebrain.push_selected", icon="EXPORT")
        col.prop(wm, "massivebrain_live", text="Live sync")
        col.separator()
        col.label(text=S.status)
        col.label(text=f"Linked objects: {len(S.synced)}")
        if S.last_error:
            col.label(text=S.last_error, icon="ERROR")


CLASSES = (MB_OT_connect, MB_OT_disconnect, MB_OT_push_selected, MB_PT_panel)


def register():
    bpy.types.WindowManager.massivebrain_host = bpy.props.StringProperty(
        name="Host", default="localhost")
    bpy.types.WindowManager.massivebrain_port = bpy.props.IntProperty(
        name="Port", default=4547, min=1, max=65535)
    bpy.types.WindowManager.massivebrain_live = bpy.props.BoolProperty(
        name="Live sync", default=True)
    for c in CLASSES:
        bpy.utils.register_class(c)
    if _on_depsgraph not in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.append(_on_depsgraph)


def unregister():
    _disconnect()
    if _on_depsgraph in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.remove(_on_depsgraph)
    if bpy.app.timers.is_registered(_flush_timer):
        bpy.app.timers.unregister(_flush_timer)
    for c in CLASSES:
        bpy.utils.unregister_class(c)
    del bpy.types.WindowManager.massivebrain_host
    del bpy.types.WindowManager.massivebrain_port
    del bpy.types.WindowManager.massivebrain_live


if __name__ == "__main__":
    register()
