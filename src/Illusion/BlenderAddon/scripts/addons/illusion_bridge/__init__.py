"""Illusion Bridge: receives scenes from the Illusion Toolkit (Phase A).

A background TCP server queues NDJSON control messages; a main-thread timer
pump drains the queue and dispatches them, so all bpy access stays on the
main thread.
"""

bl_info = {
    "name": "Illusion Bridge",
    "author": "Illusion Toolkit",
    "version": (0, 1, 0),
    "blender": (4, 2, 0),
    "location": "3D Viewport > Sidebar > Illusion",
    "description": "Receives scenes pushed by the Illusion Toolkit over a local bridge",
    "category": "System",
}

if "server" in locals():  # disable/enable or F8: reload submodules in place
    import importlib

    for _module in (protocol, dds, payload, server, materials, importer, exporter, panel):
        importlib.reload(_module)
else:
    from . import dds, exporter, importer, materials, panel, payload, protocol, server

import json
import os
import traceback
from datetime import datetime, timezone

import bpy
from bpy.app.handlers import persistent


# --- main-thread pump -----------------------------------------------------


def _pump():
    """Timer callback: drain queued messages; a bad message never kills it."""
    while True:
        item = server.poll()
        if item is None:
            break
        client, msg = item
        try:
            _dispatch(client, msg)
        except Exception as exc:
            # No print here: windowed Blender's redirected stdout can BLOCK the main
            # thread on Windows — the log file is the diagnostic channel.
            server.log("dispatch error:\n" + traceback.format_exc())
            server.send(protocol.make(
                protocol.ERROR,
                message=f"error handling '{msg.get('type', '?')}': {exc}",
                fatal=False), client)
    return 0.05


def _stamp_materials(resolved):
    """Record on each Blender material what the toolkit made of it.

    The hash is what later pushes send instead of the pixels. "authored" marks a material the bridge
    created — as opposed to one that merely shared its name with a game material — and the signature
    remembers which image the toolkit already has.
    """
    pending = server.state.pop("pending_signatures", None) or {}
    for entry in resolved:
        name = entry.get("name")
        material = bpy.data.materials.get(name) if name else None
        if material is None:
            continue
        if not entry.get("hash"):
            # The toolkit no longer has the material this datablock remembers: forget it, so the next
            # push sends the whole thing again.
            for key in ("illusion_hash", materials.AUTHORED_PROP, materials.SIGNATURE_PROP):
                if key in material.keys():
                    del material[key]
            server.log(f"material '{name}' is gone from the toolkit; it will be sent anew")
            continue
        material["illusion_hash"] = entry["hash"]
        if entry.get("authored"):
            material[materials.AUTHORED_PROP] = True
            signature = pending.get(name)
            if signature is not None:
                material[materials.SIGNATURE_PROP] = signature
        server.log(f"material '{name}' is game material {entry['hash']}")


def _dispatch(client, msg):
    mtype = msg.get("type")
    server.log(f"dispatch {mtype}")
    if mtype == protocol.HELLO:
        _handle_hello(client, msg)
    elif mtype == protocol.LOAD_SCENE:
        server.state["auto_push"] = bool(msg.get("autoPush", False))
        importer.handle_load_scene(client, msg)
    elif mtype == protocol.PING:
        server.send(protocol.make(protocol.PONG), client)
    elif mtype == protocol.SET_OPTIONS:
        server.state["auto_push"] = bool(msg.get("autoPush", False))
    elif mtype == protocol.BYE:
        server.close_client(client)
    elif mtype == protocol.CLEAR_SCENE:
        importer.handle_clear_scene()
    elif mtype == protocol.REQUEST_PUSH:
        pushed, deleted, new_count = exporter.export_scene("manual")
        server.log(f"request_push: {pushed} pushed, {deleted} deleted, {new_count} new")
    elif mtype == protocol.PUSH_ACK:
        applied = len(msg.get("applied") or [])
        skipped = msg.get("skipped") or []
        errors = msg.get("errors") or []
        # One line, and its COLOUR is the message: green means the toolkit took it, red means it did not.
        # The full reason goes to the log; the panel is a strip a few centimetres wide, and a paragraph in
        # it reads as noise on every push including the ones that worked.
        ok = not skipped and not errors
        if ok:
            summary = f"Pushed {applied} object(s)" if applied else "Nothing to push"
        else:
            first = errors[0] if errors else (skipped[0].get("reason") or "refused")
            summary = _shorten(str(first))
        server.state["last_push_ack"] = summary
        server.state["last_push_ok"] = ok
        _tag_redraw()
        _stamp_materials(msg.get("materials") or [])
        for skip in skipped:
            server.log(f"push skipped {skip.get('id', '?')}: {skip.get('reason', '')}")
        for error in errors:
            server.log(f"push error: {error}")
    elif mtype == protocol.ERROR:
        server.log(f"peer error: {msg.get('message', '')}")
    else:
        server.send(protocol.make(
            protocol.ERROR, message=f"unknown message type '{mtype}'", fatal=False), client)


def _shorten(text, limit=64):
    """One short line for the panel — the whole reason is in the log."""
    text = " ".join(text.split())
    if len(text) <= limit:
        return text
    cut = text[:limit].rsplit(" ", 1)[0]
    return (cut or text[:limit]) + "..."


def _tag_redraw():
    """Repaint the sidebar. A panel only redraws when Blender feels like it, so a status written on an
    incoming message would otherwise sit invisible until the mouse happened to move over the strip —
    which reads exactly like the push having done nothing."""
    try:
        for window in bpy.context.window_manager.windows:
            for area in window.screen.areas:
                if area.type == 'VIEW_3D':
                    area.tag_redraw()
    except AttributeError:
        pass  # no window manager yet (headless / during load)


def _handle_hello(client, msg):
    current = server.owner()
    if current is not None and current.alive and current is not client:
        server.send(protocol.make(
            protocol.HELLO_DENIED,
            owner=current.session or "",
            reason="another toolkit session is connected"), client)
        server.close_client(client)
        return
    server.claim_owner(client, str(msg.get("session", "")))
    server.send(protocol.make(
        protocol.HELLO_ACK,
        blenderVersion=bpy.app.version_string,
        addonVersion=protocol.ADDON_VERSION,
        protocolVersion=protocol.PROTOCOL_VERSION), client)


@persistent
def _on_load_post(_filepath):
    """Opening another .blend discards the bridge scene — tell the toolkit."""
    if server.state.get("scene_loaded"):
        server.state["scene_loaded"] = False
        server.send(protocol.make(protocol.SCENE_LOST, reason="file_opened"))
    _subscribe_mode_watch()  # msgbus subscriptions do not survive a file load


# --- auto-push on leaving Edit Mode --------------------------------------

_MSGBUS_OWNER = object()
_last_modes = {}
_auto_push_pending = False


def _subscribe_mode_watch():
    """(Re)subscribe the Object.mode watcher — the Tab-out auto-push trigger."""
    bpy.msgbus.clear_by_owner(_MSGBUS_OWNER)
    bpy.msgbus.subscribe_rna(
        key=(bpy.types.Object, "mode"),
        owner=_MSGBUS_OWNER,
        args=(),
        notify=_on_mode_change,
        options={'PERSISTENT'},
    )


def _on_mode_change():
    """Fires after a mode-switch operator completes; EDIT→OBJECT on a bridge object pushes."""
    try:
        obj = bpy.context.object
        if obj is None or importer.ID_PROP not in obj.keys():
            return
        previous = _last_modes.get(obj.name)
        _last_modes[obj.name] = obj.mode
        if previous == 'EDIT' and obj.mode == 'OBJECT' and server.state.get("auto_push"):
            _schedule_auto_push()
    except Exception:
        server.log("mode watch error:\n" + traceback.format_exc())


@persistent
def _on_undo_redo(_scene):
    """msgbus misses undo-driven mode changes — re-check the cached mode map."""
    _on_mode_change()


def _schedule_auto_push():
    """Debounced push (rapid Tab-in/out coalesces into one)."""
    global _auto_push_pending
    if _auto_push_pending:
        return
    _auto_push_pending = True

    def fire():
        global _auto_push_pending
        _auto_push_pending = False
        if server.is_connected() and server.state.get("scene_loaded"):
            try:
                exporter.export_scene("auto")
            except Exception:
                server.log("auto push failed:\n" + traceback.format_exc())
        return None

    bpy.app.timers.register(fire, first_interval=0.5)


# --- discovery file -------------------------------------------------------


def _discovery_path():
    base = os.environ.get("APPDATA") or os.path.join(
        os.path.expanduser("~"), "AppData", "Roaming")
    return os.path.join(base, "Illusion", "bridge.json")


def _write_discovery(port):
    path = _discovery_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    endpoint = {
        "port": port,
        "pid": os.getpid(),
        "blenderVersion": bpy.app.version_string,
        "addonVersion": protocol.ADDON_VERSION,
        "startedUtc": datetime.now(timezone.utc).isoformat(),
    }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(endpoint, fh)


def _delete_discovery():
    try:
        os.remove(_discovery_path())
    except OSError:
        pass


# --- addon lifecycle ------------------------------------------------------


def register():
    port = server.start()
    _write_discovery(port)
    panel.register()
    if not bpy.app.timers.is_registered(_pump):
        bpy.app.timers.register(_pump, first_interval=0.1, persistent=True)
    if _on_load_post not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_on_load_post)
    for handler_list in (bpy.app.handlers.undo_post, bpy.app.handlers.redo_post):
        if _on_undo_redo not in handler_list:
            handler_list.append(_on_undo_redo)
    _subscribe_mode_watch()


def unregister():
    bpy.msgbus.clear_by_owner(_MSGBUS_OWNER)
    for handler_list in (bpy.app.handlers.undo_post, bpy.app.handlers.redo_post):
        if _on_undo_redo in handler_list:
            handler_list.remove(_on_undo_redo)
    if _on_load_post in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_on_load_post)
    if bpy.app.timers.is_registered(_pump):
        bpy.app.timers.unregister(_pump)
    panel.unregister()
    server.stop()
    _delete_discovery()
