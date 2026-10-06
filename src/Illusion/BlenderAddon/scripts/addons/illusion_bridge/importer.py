"""Main-thread scene import: builds Blender objects from an .ilx container."""

import json
import os

import bpy
import numpy as np
from mathutils import Matrix

from . import materials, payload, protocol, server

COLLECTION_NAME = "Illusion Bridge"

ID_PROP = "illusion_id"
SESSION_PROP = "illusion_session"
KIND_PROP = "illusion_kind"
# Bone names in the toolkit's index order, on the armature object. Blender's own bone collection is not
# in creation order and renames collisions, so this is the only thing a bone INDEX may be resolved through.
BONES_PROP = "illusion_bones"


def handle_load_scene(client, msg):
    """Handle one load_scene message and reply scene_ready. Main thread only."""
    header, blocks = payload.read_container(msg.get("file", ""))
    session = header.get("session", "")

    collection = _ensure_collection()
    _clear_previous()

    built = []
    warnings = []
    # Rigs first: a skinned mesh needs its armature to exist before it can be bound to it.
    armatures = {}
    for desc in header.get("objects", []):
        if desc.get("kind") != "skeleton":
            continue
        try:
            arm = _build_armature(desc, header, blocks, session)
        except Exception as exc:
            warnings.append(f"Failed to build rig '{desc.get('id', '')}': {exc}")
            continue
        collection.objects.link(arm)
        armatures[desc.get("id", "")] = arm
        built.append(desc.get("id", ""))

    for desc in header.get("objects", []):
        obj_id = desc.get("id", "")
        kind = desc.get("kind", "")
        if kind == "skeleton":
            continue
        if kind not in ("mesh", "collision"):
            warnings.append(f"Skipped object '{obj_id}': unknown kind '{kind}'.")
            continue
        try:
            obj = _build_mesh_object(desc, header, blocks, session, warnings)
        except Exception as exc:  # one broken object must not sink the scene
            warnings.append(f"Failed to build object '{obj_id}': {exc}")
            continue
        collection.objects.link(obj)
        _bind_skin(obj, desc, header, blocks, armatures, warnings)
        built.append(obj_id)

    server.state["scene_loaded"] = True
    # The push path writes its containers beside the loaded ones and diffs against
    # the id set built here to detect deletions.
    server.state["exchange_dir"] = os.path.dirname(msg.get("file", ""))
    server.state["session"] = session
    server.state["loaded_ids"] = set(built)
    _frame_view()
    server.send(protocol.make(protocol.SCENE_READY, objects=built, warnings=warnings), client)


def _build_mesh_object(desc, header, blocks, session, warnings):
    arrays = payload.get_object_arrays(header, blocks, desc)
    positions = arrays.get("positions")
    indices = arrays.get("indices")
    if positions is None or indices is None:
        raise ValueError("missing 'positions' or 'indices' array")

    name = desc.get("name") or desc.get("id") or "Illusion Object"
    mesh = bpy.data.meshes.new(name)

    nv = len(positions)
    mesh.vertices.add(nv)
    mesh.vertices.foreach_set("co", positions.ravel())

    nl = len(indices)
    mesh.loops.add(nl)
    mesh.loops.foreach_set("vertex_index", indices.astype(np.int32))

    nf = nl // 3
    mesh.polygons.add(nf)
    mesh.polygons.foreach_set("loop_start", np.arange(0, nl, 3, dtype=np.int32))
    # loop_total is read-only in 4.x; consecutive loop_starts define triangle fans.
    mesh.polygons.foreach_set("use_smooth", np.ones(nf, dtype=bool))

    mesh.update(calc_edges=True)
    mesh.validate()

    meta = desc.get("meta") or {}
    kind = desc.get("kind", "mesh")
    for mat_info in meta.get("materials") or []:
        mesh.materials.append(
            materials.build_collision(mat_info) if kind == "collision" else materials.build(mat_info))

    if len(mesh.loops) != nl or len(mesh.polygons) != nf:
        warnings.append(
            f"Object '{desc.get('id', '')}': validate() altered geometry; "
            "per-corner data skipped.")
    else:
        loop_normals = arrays.get("loopNormals")
        if loop_normals is not None and len(loop_normals) == nl:
            mesh.normals_split_custom_set(loop_normals.reshape(-1, 3))

        loop_uv = arrays.get("loopUv0")
        if loop_uv is not None and len(loop_uv) == nl:
            uv = mesh.uv_layers.new(name="UVMap")
            if uv is not None:
                uv.data.foreach_set("uv", loop_uv.ravel())  # V pre-flipped by the toolkit

        orig_index = arrays.get("origIndex")
        if orig_index is not None and len(orig_index) == nl:
            attr = mesh.attributes.new("_orig_index", 'INT', 'CORNER')
            attr.data.foreach_set("value", orig_index.astype(np.int32))

        face_materials = arrays.get("faceMaterials")
        if face_materials is not None and len(face_materials) == nf and mesh.materials:
            mesh.polygons.foreach_set("material_index", face_materials.astype(np.int32))

    obj = bpy.data.objects.new(name, mesh)
    obj[ID_PROP] = desc.get("id", "")
    obj[SESSION_PROP] = session
    # Echoed back by the push exporter — this is what lets the toolkit tell a hull from a mesh.
    obj[KIND_PROP] = kind
    obj["illusion_meta"] = json.dumps(meta)  # carried back verbatim on push

    if kind == "collision":
        # Reads as reference geometry, not as something to sculpt: wireframe over the solid so
        # the hull's shape is legible against the mesh it wraps. Its shape cannot be pushed back
        # (that needs a PhysX re-cook) — only the placement transform can.
        obj.show_wire = True
        obj.show_all_edges = True

    world = desc.get("world")
    if isinstance(world, (list, tuple)) and len(world) == 16:
        # 16 floats row-major in the toolkit's row-vector convention → transpose
        # into Blender's column-vector Matrix.
        obj.matrix_world = Matrix(
            (world[0:4], world[4:8], world[8:12], world[12:16])).transposed()

    return obj


def _to_blender_matrix(values):
    """16 floats row-major in the toolkit's row-vector convention → Blender's column-vector Matrix."""
    return Matrix((values[0:4], values[4:8], values[8:12], values[12:16])).transposed()


def _build_armature(desc, header, blocks, session):
    """Build one armature from a kind='skeleton' object.

    Rest transforms are MODEL space, not relative to the parent bone, so every bone is placed
    absolutely and the parent link is set without connecting (a Mafia rig is not a chain of
    lengths — a car's doorFL sits on its hinge, nowhere near its parent's tip).
    """
    arrays = payload.get_object_arrays(header, blocks, desc)
    parents = arrays.get("boneParents")
    rest = arrays.get("boneRest")
    if parents is None or rest is None:
        raise ValueError("missing 'boneParents' or 'boneRest' array")

    meta = desc.get("meta") or {}
    names = list(meta.get("boneNames") or [])
    count = min(len(parents), len(rest), len(names) if names else len(parents))
    if not names:
        names = [f"bone{i}" for i in range(count)]

    name = desc.get("name") or "Rig"
    armature = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, armature)
    obj[ID_PROP] = desc.get("id", "")
    obj[SESSION_PROP] = session
    obj[KIND_PROP] = "skeleton"

    # Edit bones only exist while the armature is in edit mode, and that needs the object in a
    # collection and active — so link it first and unlink nothing afterwards.
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    created = []
    try:
        edit = []
        for i in range(count):
            bone = armature.edit_bones.new(names[i])
            m = _to_blender_matrix(rest[i].tolist() if hasattr(rest[i], "tolist") else list(rest[i]))
            # A bone needs a non-zero length to survive; the direction is the rest basis' Y axis and
            # the length is arbitrary — the matrix is what carries the pose.
            bone.head = (0.0, 0.0, 0.0)
            bone.tail = (0.0, 0.1, 0.0)
            bone.matrix = m
            edit.append(bone)
            # The name Blender ACTUALLY gave it: new() uniquifies a name that collides, and a rig with
            # two bones of one name is not hypothetical.
            created.append(bone.name)
        for i in range(count):
            p = int(parents[i])
            if 0 <= p < count and p != i:
                edit[i].parent = edit[p]
                edit[i].use_connect = False
    finally:
        bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.scene.collection.objects.unlink(obj)

    # The toolkit's bone ORDER, kept explicitly. armature.data.bones is not in creation order — Blender
    # keeps its own — so indexing it by a payload bone index binds vertices to the wrong bones, which
    # looks like dragging a door and watching the headlights move.
    obj[BONES_PROP] = json.dumps(created)

    world = desc.get("world")
    if isinstance(world, (list, tuple)) and len(world) == 16:
        obj.matrix_world = _to_blender_matrix(world)
    return obj


def _bind_skin(obj, desc, header, blocks, armatures, warnings):
    """Turn a mesh's bone influences into vertex groups and bind it to its armature."""
    meta = desc.get("meta") or {}
    rig_id = meta.get("skeletonId")
    if not rig_id:
        return
    armature = armatures.get(rig_id)
    if armature is None:
        warnings.append(f"Object '{desc.get('id', '')}': its rig '{rig_id}' is not in this scene.")
        return

    arrays = payload.get_object_arrays(header, blocks, desc)
    indices = arrays.get("boneIndices")
    weights = arrays.get("boneWeights")
    if indices is None or weights is None:
        return

    # Bone names in the TOOLKIT's index order (see BONES_PROP), never armature.data.bones' own order.
    try:
        bone_names = json.loads(armature.get(BONES_PROP) or "[]")
    except Exception:
        bone_names = []
    if not bone_names:
        warnings.append(f"Object '{desc.get('id', '')}': its rig has no bone order; skin not bound.")
        return

    groups = {}
    for v in range(min(len(indices), len(weights))):
        for k in range(4):
            w = float(weights[v][k])
            if w <= 0.0:
                continue
            b = int(indices[v][k])
            if b >= len(bone_names):
                continue
            group = groups.get(b)
            if group is None:
                group = groups[b] = obj.vertex_groups.new(name=bone_names[b])
            group.add((v,), w, 'REPLACE')

    # Parenting keeps the mesh's own matrix as its offset FROM the armature — and that matrix is already the
    # model's world, the same one the armature stands at. Left alone the mesh is carried there twice. On a
    # car nobody sees it (a car's model sits at the identity); a person's model is turned a quarter turn —
    # its geometry is stored along the spine — so the body arrived lying down beside a rig that stood, and
    # a push from that scene read as "the object was moved". The offset is what is left of the mesh's world
    # once the armature's is taken out: nothing, for a mesh on its own rig.
    world = obj.matrix_world.copy()
    obj.parent = armature
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.matrix_basis = armature.matrix_world.inverted_safe() @ world
    modifier = obj.modifiers.new(name="Illusion Rig", type='ARMATURE')
    modifier.object = armature
    modifier.use_vertex_groups = True
    # Envelopes deform by PROXIMITY, ignoring the weights entirely — with them on, moving a door drags
    # whatever body geometry happens to be near it. The game's skin is the four weights and nothing else.
    modifier.use_bone_envelopes = False


def handle_clear_scene():
    """The toolkit ended the edit session — despawn the bridge objects; Blender stays up."""
    _clear_previous()
    server.state["scene_loaded"] = False
    server.state["loaded_ids"] = set()


def _ensure_collection():
    collection = bpy.data.collections.get(COLLECTION_NAME)
    if collection is None:
        collection = bpy.data.collections.new(COLLECTION_NAME)
    scene_children = bpy.context.scene.collection.children
    if collection.name not in scene_children:
        scene_children.link(collection)
    return collection


def _clear_previous():
    """Remove every object a previous bridge load created, plus its mesh or armature data."""
    for obj in [o for o in bpy.data.objects if ID_PROP in o]:
        mesh = obj.data if obj.type == 'MESH' else None
        armature = obj.data if obj.type == 'ARMATURE' else None
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh is not None and mesh.users == 0:
            bpy.data.meshes.remove(mesh)
        if armature is not None and armature.users == 0:
            bpy.data.armatures.remove(armature)


def _frame_view():
    """Best-effort: frame the loaded scene in the first 3D viewport."""
    try:
        for window in bpy.context.window_manager.windows:
            for area in window.screen.areas:
                if area.type != 'VIEW_3D':
                    continue
                region = next((r for r in area.regions if r.type == 'WINDOW'), None)
                if region is None:
                    continue
                with bpy.context.temp_override(window=window, area=area, region=region):
                    bpy.ops.view3d.view_all()
                return
    except Exception:
        pass  # pure nicety; never fail the import over it
