# MassiveBRAIN — live sync server for Blender & Rhino

MassiveBRAIN turns MassiveSLICER into the **hub**: it hosts a WebSocket server
(default `localhost:4547`) and DCC addons connect to it and push geometry live —
the same working model as the Plasticity live bridge, but with the data
direction reversed (Plasticity hosts a server we *pull from*; MassiveBRAIN is a
server the DCCs *push to*).

Enable it from the N-key HUD → **MASSIVEBRAIN** (Enabled toggle, Host, Port), or
from the console: `massivebrain on|off|status`.

## Wire protocol

Identical binary format to the Plasticity bridge (already implemented and
battle-tested in `src/MassiveSlicer.App/Plasticity/PlasticityWire.cs`):

- Little-endian; every message starts with a 4-byte message type.
- Strings are length-prefixed and zero-padded to a 4-byte boundary.
- **Units: metres, Z-up** (server multiplies by `UnitScaleMm`, default 1000).

Messages the server understands:

| Type | Value | Direction | Notes |
|------|-------|-----------|-------|
| Handshake | 100 | client → server | optional trailing UTF-8 app name ("blender", "rhino") |
| Handshake ack | 100 | server → client | trailing UTF-8 `MASSIVEBRAIN` — addons verify they hit the right server |
| Transaction | 0 | client → server | carries Add/Update/Delete items |

Transaction body:

```
filename_len(u32) + filename + pad4 + version(u32) + num_items(u32)
  per item: item_len(u32) + item_data
item_data: subType(u32: Add=1, Update=2, Delete=3) + payload
Add/Update payload: num_objects(u32) + objects
object: objType(u32: Solid=0, Sheet=1) + id(u32) + version(u32) + parentId(i32)
        + materialId(i32) + flags(u32) + nameLen(u32) + name + pad4
        + nv(u32) + verts(nv*3 f32) + nf(u32) + indices(nf*3 i32)
        + nn(u32) + normals(nn*3 f32, may be 0 — server computes)
        + ng(u32) + groups(ng i32) + nfi(u32) + faceIds(nfi i32)   (groups/faceIds may be 0)
Delete payload: num_ids(u32) + ids(i32)
```

Object ids only need to be stable **per client** — the server namespaces nodes
by `(connection, id)`, so Blender and Rhino can both use their own id schemes
simultaneously. Synced objects stay in the scene when a client disconnects.

A reference client lives in the session scratchpad test
(`mb_test_client.py`) — raw-socket WebSocket, handshake, one cube Add, optional
Delete. Use it as the seed for both addons' transport layer.

## Blender addon plan (`massivebrain-blender`)

Model on `nkallen/plasticity-blender-addon`, inverted (Blender is the pusher).

1. **Transport** — pure-Python WebSocket client (stdlib `socket` + manual RFC 6455
   framing, as in the test client; no pip dependencies so install is drag-and-drop).
   Background thread with a send queue; reconnect with backoff.
2. **UI** — N-panel "MassiveBRAIN": host/port, Connect/Disconnect, "Live sync"
   toggle, per-object "Sync" checkbox (or sync a chosen collection).
3. **Change detection** — `bpy.app.handlers.depsgraph_update_post`: for updated
   objects in the synced collection, evaluate the depsgraph mesh
   (`object.evaluated_get(dg).to_mesh()`), triangulate (`calc_loop_triangles`),
   and enqueue an Update transaction. Debounce ~300 ms so sculpt strokes don't
   flood. Deletes detected by diffing the synced id set each depsgraph tick.
4. **Ids** — stable per-object int (hash of `object.session_uid`); name = object name.
5. **Units** — Blender is metres natively; apply the object's world matrix to
   vertices and send world-space metres. Z-up matches; **no axis swap**.
6. **Phases** — P1 manual "Push selected" button; P2 live depsgraph sync;
   P3 deletes + rename handling; P4 modifiers/instancing (realized).

## Rhino plugin plan (`massivebrain-rhino`)

Rhino 8 ships CPython (`ScriptEditor`); simplest path is a Python 3 plugin
sharing the Blender transport module.

1. **Transport** — same WebSocket client module, verbatim.
2. **UI** — Eto panel or simple command set: `MassiveBrainConnect`,
   `MassiveBrainSyncSelected`, `MassiveBrainLive` toggle.
3. **Change detection** — `RhinoDoc.AddRhinoObject` / `ReplaceRhinoObject` /
   `DeleteRhinoObject` events on synced objects. Meshing:
   `Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh)` (or grab the
   render mesh), then triangulate quads.
4. **Ids** — Rhino object `Guid` → stable int via hash (kept in a dict so
   Replace maps to Update, not Add); name = object name or layer.
5. **Units** — convert doc units → metres with
   `RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters)`. Rhino is
   Z-up; **no axis swap**.
6. **Phases** — P1 push-selected command; P2 live event sync; P3 deletes/undo
   handling (undo of an add fires Delete event — already covered by events).

## Repo layout (when the addons start)

```
addons/
  massivebrain_common/wire.py      # shared codec + WS client (copied into each addon)
  massivebrain-blender/__init__.py
  massivebrain-rhino/massivebrain_rhino.py
```

## Testing

- `massivebrain on` in the app console (or the N-menu toggle), then run the
  reference client. Console shows `[massivebrain] client #N connected`,
  `synced 1 object(s)`.
- Multi-client: run two clients with the same object id — nodes stay separate
  (namespaced per connection).
