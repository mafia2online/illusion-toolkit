"""Main-thread push export: reads the evaluated bridge meshes back and sends a push.

The payload mirrors what the toolkit sent: welded vertices + per-loop attributes,
UVs in Blender's V convention (the toolkit un-flips), _orig_index riding along as
the vertex identity the count-preserving apply keys on. Modifiers are baked
(evaluated depsgraph) and n-gons triangulated via loop_triangles.
"""

import json
import os
import uuid

import bpy
import numpy as np

from . import importer, materials, payload, protocol, server


def _guess_kind(obj) -> str:
    """What a brand-new object should be sent back as.

    A collision material carries illusion_collision_raw_id and a game material does not, so an object
    built from COL materials is a collision hull — which is exactly what a Shift+D of one looks like
    once its id has been re-minted. Anything else is an ordinary mesh.
    """
    for slot in obj.material_slots:
        if slot.material is not None and slot.material.get("illusion_collision_raw_id") is not None:
            return "collision"
    return "mesh"


def _remint_duplicate_ids(session):
    """Give every object after the first sharing an id a fresh 'new:' one. Returns how many were re-minted."""
    seen = set()
    reminted = 0
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        current = obj.get(importer.ID_PROP)
        if current is None:
            continue
        if current not in seen:
            seen.add(current)
            continue
        obj[importer.ID_PROP] = "new:" + uuid.uuid4().hex
        obj[importer.SESSION_PROP] = session
        # The kind is INHERITED where it exists: a duplicated hull keeps its materials, so re-deriving
        # would give the same answer anyway, but an object opened as collision stays collision even if
        # its materials were swapped afterwards.
        if importer.KIND_PROP not in obj.keys():
            obj[importer.KIND_PROP] = _guess_kind(obj)
        obj["illusion_meta"] = "{}"
        reminted += 1
    return reminted


def export_scene(reason):
    """Export every surviving bridge object and send one push message.

    Returns (pushed_count, deleted_count, new_count). Raises on a hard failure
    (no bridge scene, no exchange directory); per-object problems are printed
    and the object is left out of the push.
    """
    exchange_dir = server.state.get("exchange_dir")
    if not exchange_dir or not os.path.isdir(exchange_dir):
        raise RuntimeError("No bridge scene was loaded in this session")
    session = server.state.get("session", "")
    server.log(f"export_scene({reason}) start")

    # A mesh dropped into the bridge collection without an id is a NEW object: mint it a
    # "new:" id so the toolkit creates a frame object for it, and track it from now on.
    collection = bpy.data.collections.get(importer.COLLECTION_NAME)
    new_count = 0
    if collection is not None:
        for obj in collection.objects:
            if obj.type == 'MESH' and importer.ID_PROP not in obj.keys():
                obj[importer.ID_PROP] = "new:" + uuid.uuid4().hex
                obj[importer.SESSION_PROP] = session
                obj[importer.KIND_PROP] = _guess_kind(obj)
                obj["illusion_meta"] = "{}"
                new_count += 1

    # Shift+D is how anyone makes a second one of something, and Blender's duplicate copies custom
    # properties — so the copy arrives wearing the ORIGINAL's id. Left alone, both objects resolve to
    # the same placement and the second silently overwrites the first. Whoever holds the id longest is
    # arbitrary, so keep the first occurrence and re-mint the rest as new objects, preserving the kind
    # they inherited: a duplicated collision hull is still a collision hull.
    new_count += _remint_duplicate_ids(session)

    survivors = [o for o in bpy.data.objects
                 if importer.ID_PROP in o.keys() and o.type == 'MESH']
    # What gets PUSHED is the meshes; what counts as still PRESENT is every object carrying an id. A rig is
    # an ARMATURE, so scoping presence to meshes reported it deleted on the first push after every load — and
    # a deletion the toolkit acts on takes the whole model, its bones and its attachments with it.
    present_ids = {o[importer.ID_PROP] for o in bpy.data.objects if importer.ID_PROP in o.keys()}
    deleted = sorted(server.state.get("loaded_ids", set()) - present_ids)

    # A posed armature must NOT bake into the geometry that goes home. The toolkit keeps the pose in the
    # rig's own transforms (the rig is pushed separately), so a mesh evaluated with its Armature modifier
    # live would send the door's vertices in their swung-open position AND move the bone — applying the
    # same motion twice. Modifiers are muted for the evaluation and restored right after.
    muted = []
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        for modifier in obj.modifiers:
            if modifier.type == 'ARMATURE' and modifier.show_viewport:
                modifier.show_viewport = False
                muted.append(modifier)

    server.log(f"export_scene: {len(survivors)} survivors, evaluating depsgraph")
    depsgraph = bpy.context.evaluated_depsgraph_get()
    server.log("export_scene: depsgraph ready")
    objects = []
    blocks = []
    pushed_ids = []
    # One block per image however many slots and objects use it, and the signatures to stamp once the
    # toolkit confirms it took the pixels.
    image_refs = {}
    signatures = {}
    try:
        for obj in survivors:
            try:
                server.log(f"export_scene: exporting {obj.name}")
                entry = _export_object(obj, depsgraph, blocks, image_refs, signatures)
            except Exception as exc:
                server.log(f"push: failed to export '{obj.get(importer.ID_PROP, obj.name)}': {exc}")
                continue
            objects.append(entry)
            pushed_ids.append(entry["id"])

        # The rigs. A bone posed in Blender is what the toolkit stores as that bone's rest transform, so a
        # door swung open here comes back as a door swung open there. Sent whether or not it was touched —
        # the toolkit compares against what it exported and only records what actually moved.
        for arm in [o for o in bpy.data.objects
                    if importer.ID_PROP in o.keys() and o.type == 'ARMATURE']:
            try:
                entry = _export_armature(arm, blocks)
            except Exception as exc:
                server.log(f"push: failed to export rig '{arm.get(importer.ID_PROP, arm.name)}': {exc}")
                continue
            objects.append(entry)
            pushed_ids.append(entry["id"])
    finally:
        for modifier in muted:
            modifier.show_viewport = True

    if not pushed_ids and not deleted and new_count == 0:
        server.log("export_scene: nothing to push")
        return 0, 0, 0

    counter = server.state.get("push_counter", 0) + 1
    server.state["push_counter"] = counter
    path = os.path.join(exchange_dir, f"push_{counter:04d}.ilx")
    server.log("export_scene: writing container")
    payload.write_container(path, session, objects, blocks)
    server.log("export_scene: container written, sending push")
    server.state["pending_signatures"] = signatures

    server.send(protocol.make(
        protocol.PUSH,
        file=path,
        reason=reason,
        objects=pushed_ids,
        deleted=list(deleted),
        newObjects=new_count))
    server.log("export_scene: push sent")
    # Deletions are reported exactly once — the toolkit acts on this push, so the
    # baseline forgets them (re-sending would delete-fail forever after).
    server.state["loaded_ids"] = present_ids
    return len(pushed_ids), len(deleted), new_count


def _export_armature(obj, blocks):
    """Read one rig back: per bone, where it stands NOW in armature space.

    The toolkit stores a bone's placement as its rest transform, and posing is how anyone moves a
    bone in Blender — so a pose bone's armature-space matrix IS the rest transform to send back. Bones
    go in the toolkit's own index order (BONES_PROP), which is not Blender's.
    """
    try:
        bone_names = json.loads(obj.get(importer.BONES_PROP) or "[]")
    except ValueError:
        bone_names = []
    if not bone_names:
        raise RuntimeError("rig carries no bone order")

    rest = np.zeros((len(bone_names), 16), dtype=np.float32)
    parents = np.full(len(bone_names), -1, dtype=np.int32)
    index_of = {name: i for i, name in enumerate(bone_names)}
    for i, name in enumerate(bone_names):
        pose_bone = obj.pose.bones.get(name)
        if pose_bone is None:
            raise RuntimeError(f"bone '{name}' is gone from the rig")
        rest[i] = _row_major(pose_bone.matrix)
        parent = pose_bone.parent
        if parent is not None:
            parents[i] = index_of.get(parent.name, -1)

    return {
        "kind": "skeleton",
        "id": obj[importer.ID_PROP],
        "name": obj.name,
        "parentId": None,
        "world": _row_major(obj.matrix_world),
        "local": _row_major(obj.matrix_local),
        "meta": {"boneNames": bone_names},
        "arrays": {
            "boneParents": _add_block(blocks, "i32", 1, len(bone_names), parents),
            "boneRest": _add_block(blocks, "f32", 16, len(bone_names), rest),
        },
    }


def _find_armature(obj):
    """The rig this mesh is skinned to, however it is attached.

    The importer sets BOTH links — it parents the mesh to the armature and gives it an Armature modifier —
    but only one of them survives everything a modeller does. Joining, duplicating or re-linking an object
    can drop the parent while the modifier stays. Looking only at the parent meant the weights were silently
    not exported, which has no symptom of its own: the toolkit reports "nothing changed" and new geometry
    keeps the skin of whatever vertex was nearest.
    """
    if obj.parent is not None and obj.parent.type == 'ARMATURE':
        return obj.parent
    for modifier in obj.modifiers:
        if modifier.type == 'ARMATURE' and modifier.object is not None:
            return modifier.object
    return None


def _export_skin(obj, me, n_verts, blocks, arrays):
    """Send the vertex groups home as bone influences, four per vertex.

    Without this the toolkit has no idea which bone a vertex belongs to, and geometry added in Blender
    takes the skin of whatever vertex happens to be NEAREST — put a new part on the hood and it rides the
    left door because the door was closer. The groups are named after the bones (that is how they were
    built on import), so a name is what identifies a bone; indices go out in the toolkit's own bone order,
    which is the one it carries on the armature as BONES_PROP.

    Silent no-op for a mesh with no rig, and for any vertex in no group at all — the toolkit falls back to
    what it already knows for those, which is better than an empty skin.
    """
    armature = _find_armature(obj)
    if armature is None or not obj.vertex_groups:
        return
    try:
        bone_names = json.loads(armature.get(importer.BONES_PROP) or "[]")
    except ValueError:
        return
    if not bone_names:
        return

    bone_of_name = {name: i for i, name in enumerate(bone_names)}
    bone_of_group = {}
    for group in obj.vertex_groups:
        bone = bone_of_name.get(group.name)
        if bone is not None and bone <= 255:
            bone_of_group[group.index] = bone
    if not bone_of_group:
        return

    # The evaluated mesh carries the groups; fall back to the object's own when it does not (and only
    # when the counts still line up, or the two would be talking about different vertices).
    source = me
    if n_verts and not any(v.groups for v in me.vertices[:1]):
        if len(obj.data.vertices) == n_verts:
            source = obj.data

    ids = np.zeros((n_verts, 4), dtype=np.uint8)
    weights = np.zeros((n_verts, 4), dtype=np.float32)
    for vertex in source.vertices:
        influences = [(bone_of_group[g.group], g.weight)
                      for g in vertex.groups
                      if g.group in bone_of_group and g.weight > 0.0]
        if not influences:
            continue
        # The format holds four; the heaviest four are the ones that matter, renormalized so they still
        # sum to one — the game's blend assumes it.
        influences.sort(key=lambda pair: -pair[1])
        del influences[4:]
        total = sum(weight for _, weight in influences)
        if total <= 0.0:
            continue
        for slot, (bone, weight) in enumerate(influences):
            ids[vertex.index][slot] = bone
            weights[vertex.index][slot] = weight / total

    arrays["boneIndices"] = _add_block(blocks, "u8", 4, n_verts, ids)
    arrays["boneWeights"] = _add_block(blocks, "f32", 4, n_verts, weights)


def _describe_authored(material, entry, blocks, image_refs, signatures):
    """Add what the toolkit needs to turn a material made in Blender into a game material.

    A material the toolkit handed out is identified by its hash and nothing else. One made here has no
    hash until a push creates it, and afterwards keeps the right to replace its own textures — so its
    images ride along the first time, and again whenever anything about it changed. They travel
    TOGETHER: the toolkit packs the normal and the specular map into one texture and picks the shader
    by which maps exist, so it needs the whole material, not the part that moved.
    """
    game_hash = material.get("illusion_hash")
    if game_hash and not material.get(materials.AUTHORED_PROP):
        return
    entry["authored"] = True
    diffuse = materials.base_color_image(material)
    if diffuse is None:
        return
    normal = materials.normal_map_image(material)
    specular = materials.specular_image(material)
    level = materials.specular_level(material)
    roughness = materials.roughness(material)
    alpha = materials.alpha_use(material)

    # Unsaved paint on any image has no cheap identity, so it resends every time (signature None).
    parts = [materials.image_signature(i) for i in (diffuse, normal, specular) if i is not None]
    values = [f"n={normal is not None}", f"s={specular is not None}", f"level={level}", f"rough={roughness}"]
    alpha_key = ""
    if alpha is not None:
        # Only a material that uses alpha gains a term, so an opaque one keeps the signature it was
        # stamped with before alpha existed and is not resent for nothing.
        mode, mask, channel, value = alpha
        if mask is not None and mask != diffuse:
            parts.append(materials.image_signature(mask))
        alpha_key = f"alpha={mode}:{channel}:{value}:{mask.name if mask is not None else ''}"
        values.append(alpha_key)
    signature = None if any(p is None for p in parts) else "|".join(parts + values)
    if game_hash and signature is not None and signature == material.get(materials.SIGNATURE_PROP):
        return

    for key, image in (("diffuseImage", diffuse), ("normalImage", normal), ("specularImage", specular)):
        if image is None:
            continue
        # The same image is a different texture once a material writes its own alpha into it.
        with_alpha = key == "diffuseImage" and alpha is not None
        ref_key = f"{image.name}|{alpha_key}" if with_alpha else image.name
        ref = image_refs.get(ref_key)
        if ref is None:
            packed = materials.image_rgba8(image)
            if packed is None:
                if key == "diffuseImage":
                    return
                continue
            pixels, width, height = packed
            if with_alpha:
                pixels = materials.with_alpha(pixels, width, height, image, alpha)
            ref = {
                "name": image.name,
                "width": width,
                "height": height,
                "block": _add_block(blocks, "u8", 4, width * height, pixels),
            }
            image_refs[ref_key] = ref
        entry[key] = ref
    if alpha is not None:
        entry["alphaMode"] = alpha[0]
    if level is not None:
        entry["specularLevel"] = level
    if roughness is not None:
        entry["roughness"] = roughness
    signatures[material.name] = signature


def _export_object(obj, depsgraph, blocks, image_refs, signatures):
    """Read one evaluated mesh into payload arrays; appends blocks, returns the header entry."""
    if obj.mode == 'EDIT':
        obj.update_from_editmode()  # commit the live BMesh before evaluating

    ob_eval = obj.evaluated_get(depsgraph)
    me = ob_eval.to_mesh()
    try:
        me.calc_loop_triangles()
        tris = me.loop_triangles
        n_tris = len(tris)
        n_loops = len(me.loops)
        n_verts = len(me.vertices)

        tri_loops = np.empty(n_tris * 3, dtype=np.int32)
        tris.foreach_get("loops", tri_loops)
        tri_polys = np.empty(n_tris, dtype=np.int32)
        tris.foreach_get("polygon_index", tri_polys)

        positions = np.empty(n_verts * 3, dtype=np.float32)
        me.vertices.foreach_get("co", positions)
        positions = positions.reshape(n_verts, 3)

        loop_vi = np.empty(n_loops, dtype=np.int32)
        me.loops.foreach_get("vertex_index", loop_vi)

        # Split normals per corner (4.1+); fall back to vertex normals when absent.
        loop_normals = np.empty(n_loops * 3, dtype=np.float32)
        try:
            me.corner_normals.foreach_get("vector", loop_normals)
            loop_normals = loop_normals.reshape(n_loops, 3)
        except (AttributeError, RuntimeError):
            vertex_normals = np.empty(n_verts * 3, dtype=np.float32)
            me.vertices.foreach_get("normal", vertex_normals)
            loop_normals = vertex_normals.reshape(n_verts, 3)[loop_vi]

        uv_layer = me.uv_layers.active
        if uv_layer is not None:
            loop_uv = np.empty(n_loops * 2, dtype=np.float32)
            uv_layer.data.foreach_get("uv", loop_uv)
            loop_uv = loop_uv.reshape(n_loops, 2)
        else:
            loop_uv = np.zeros((n_loops, 2), dtype=np.float32)

        attr = me.attributes.get("_orig_index")
        if attr is not None and attr.domain == 'CORNER' and attr.data_type == 'INT':
            orig = np.empty(n_loops, dtype=np.int32)
            attr.data.foreach_get("value", orig)
        else:
            orig = np.full(n_loops, -1, dtype=np.int32)

        face_mats = np.zeros(len(me.polygons), dtype=np.int32)
        if len(me.polygons):
            me.polygons.foreach_get("material_index", face_mats)

        arrays = {
            "positions": _add_block(blocks, "f32", 3, n_verts, positions),
            "indices": _add_block(blocks, "u32", 1, n_tris * 3, loop_vi[tri_loops]),
            "loopNormals": _add_block(blocks, "f32", 3, n_tris * 3, loop_normals[tri_loops]),
            "loopUv0": _add_block(blocks, "f32", 2, n_tris * 3, loop_uv[tri_loops]),
            "origIndex": _add_block(blocks, "i32", 1, n_tris * 3, orig[tri_loops]),
            "faceMaterials": _add_block(blocks, "u16", 1, n_tris, face_mats[tri_polys]),
        }
        _export_skin(obj, me, n_verts, blocks, arrays)
    finally:
        ob_eval.to_mesh_clear()

    try:
        meta = json.loads(obj.get("illusion_meta", "") or "{}")
    except ValueError:
        meta = {}

    # The material SET is live state, not import-time state: the user may have re-pointed a slot
    # at another bridge material (or added/removed slots). Identity = the illusion_hash prop for a
    # game material, and illusion_collision_raw_id for a collision surface — a collision material
    # has no illusion_hash, so without the second field the surface a face was painted with is
    # simply lost on the way back and cannot be fed to the cooker.
    slot_materials = []
    for slot in obj.material_slots:
        material = slot.material
        entry = {
            "hash": (material.get("illusion_hash") if material else None) or None,
            "name": material.name if material else None,
        }
        # Spelled "rawId" so it lands straight back in CollisionMaterialInfo.RawId — the same field the
        # toolkit sent out. Absent for ordinary game materials, and a collision surface id is never 0
        # (the .col section bias subtracts 2), so 0 reads unambiguously as "not a collision surface".
        raw_id = material.get("illusion_collision_raw_id") if material else None
        if raw_id is not None:
            entry["rawId"] = int(raw_id)
        elif material is not None:
            _describe_authored(material, entry, blocks, image_refs, signatures)
        slot_materials.append(entry)
    if slot_materials:
        meta["materials"] = slot_materials

    return {
        # Echo the kind stamped at import. Hard-coding "mesh" here would send a collision hull
        # back as a mesh, and the toolkit would route it into the geometry-apply path that
        # cannot handle it.
        "kind": obj.get(importer.KIND_PROP, "mesh"),
        "id": obj[importer.ID_PROP],
        "name": obj.name,
        "parentId": None,
        "world": _row_major(obj.matrix_world),
        "local": _row_major(obj.matrix_local),
        "meta": meta,
        "arrays": arrays,
    }


def _add_block(blocks, dtype_str, components, count, data):
    blocks.append((dtype_str, components, count, data))
    return len(blocks) - 1


def _row_major(matrix):
    """Blender column-vector Matrix → the toolkit's 16 row-vector floats."""
    t = matrix.transposed()
    return [value for row in t for value in row]
