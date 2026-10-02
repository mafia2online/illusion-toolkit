"""Builds Blender materials from bridge material infos (Principled BSDF + maps)."""

import os

import bpy
import numpy as np

from . import dds

# A material the modder made here, which a push turned into a game material: unlike one the toolkit
# handed out, its texture is Blender's to replace.
AUTHORED_PROP = "illusion_authored"
# What the toolkit was last sent for that material's Base Color image, so pixels travel only when
# they changed.
SIGNATURE_PROP = "illusion_image_signature"

MAX_TEXTURE_SIZE = 2048


def build(mat_info):
    """Return the material for one mesh material info dict, reusing by game-material hash.

    The game-material identity rides in the "illusion_hash" custom property — the
    push exporter reads it back, so re-assigning a slot to another bridge material
    in Blender translates into a real material change in the toolkit. Reuse must key
    on that hash, not the display name: the game ships thousands of materials across
    many libraries and names collide, which would silently point this slot at another
    material's datablock (and push back the wrong hash).
    """
    label = mat_info.get("name") or mat_info.get("hash") or "unnamed"
    mat_hash = mat_info.get("hash") or ""
    mat_name = f"M2 {label}"

    if mat_hash:
        for existing in bpy.data.materials:
            if existing.get("illusion_hash") == mat_hash:
                return existing
    # Legacy datablock from an older session (same name, no hash yet): adopt it once.
    existing = bpy.data.materials.get(mat_name)
    if existing is not None and not existing.get("illusion_hash"):
        existing["illusion_hash"] = mat_hash
        return existing
    if existing is not None and not mat_hash:
        return existing

    # A name collision with a different hash falls through: Blender auto-suffixes the
    # new datablock (.001) — identity stays with the hash, only the display name differs.
    mat = bpy.data.materials.new(mat_name)
    mat["illusion_hash"] = mat_hash
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    principled = nodes.get("Principled BSDF")
    if principled is None:
        return mat  # unexpected default tree; bare material is still usable

    diffuse = _load_image(mat_info.get("diffuse"))
    if diffuse is not None:
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = diffuse
        tex.location = (-500, 300)
        _link(links, tex.outputs.get("Color"), principled.inputs.get("Base Color"))

    normal = _load_image(mat_info.get("normal"), non_color=True)
    if normal is not None:
        if mat_info.get("normalIsDxt5nm"):
            dds.unswizzle_dxt5nm(normal)
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = normal
        tex.location = (-700, -250)
        normal_map = nodes.new("ShaderNodeNormalMap")
        normal_map.location = (-400, -250)
        _link(links, tex.outputs.get("Color"), normal_map.inputs.get("Color"))
        _link(links, normal_map.outputs.get("Normal"), principled.inputs.get("Normal"))

    specular = _load_image(mat_info.get("specular"), non_color=True)
    if specular is not None:
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = specular
        tex.location = (-500, 30)
        target = None
        for socket_name in ("Specular IOR Level", "Specular"):  # 4.x renamed it
            target = principled.inputs.get(socket_name)
            if target is not None:
                break
        _link(links, tex.outputs.get("Color"), target)

    return mat


def build_collision(mat_info):
    """Return the reference material for one collision surface, reusing by name.

    A collision material is a PhysX surface id, not a game material: there is nothing to
    texture and no hash to re-point, so the slot carries the surface token and the overlay
    colour for display only. Pushing a slot change back is meaningless and the toolkit
    ignores it.
    """
    label = mat_info.get("name") or mat_info.get("token") or "unknown"
    mat_name = f"COL {label}"
    existing = bpy.data.materials.get(mat_name)
    if existing is not None:
        return existing

    mat = bpy.data.materials.new(mat_name)
    mat["illusion_collision_raw_id"] = mat_info.get("rawId", -1)
    mat["illusion_collision_token"] = mat_info.get("token") or ""

    color = mat_info.get("color")
    if not isinstance(color, (list, tuple)) or len(color) < 3:
        color = (0.60, 0.62, 0.66)  # catalog's unknown-surface grey
    rgba = (float(color[0]), float(color[1]), float(color[2]), 1.0)

    mat.diffuse_color = rgba  # solid-shading viewport colour
    mat.use_nodes = True
    principled = mat.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        base = principled.inputs.get("Base Color")
        if base is not None:
            base.default_value = rgba
    return mat


def _load_image(path, non_color=False):
    """Load an image datablock; None when the path is absent or unreadable."""
    if not path or not os.path.isfile(path):
        return None
    try:
        image = bpy.data.images.load(path, check_existing=True)
    except RuntimeError:
        return None
    if non_color:
        image.colorspace_settings.name = 'Non-Color'
    return image


def _link(links, output, input_socket):
    if output is not None and input_socket is not None:
        links.new(output, input_socket)


def base_color_image(material):
    """The image feeding the Principled BSDF's Base Color, or None.

    Looks through the colour-adjusting nodes people put in between (Hue/Saturation, a Mix, curves):
    those are not baked — the image itself is what travels — but they must not hide it.
    """
    principled = _principled(material)
    return None if principled is None else _image_behind(principled.inputs.get("Base Color"))


def normal_map_image(material):
    """The image behind the Normal Map node feeding the Principled BSDF's Normal, or None.

    Only a tangent-space Normal Map node counts: a Bump node's height map is a different thing, and
    sending it as a normal map would light the surface from nowhere.
    """
    principled = _principled(material)
    socket = None if principled is None else principled.inputs.get("Normal")
    if socket is None or not socket.is_linked:
        return None
    node = socket.links[0].from_node
    if node.type != 'NORMAL_MAP':
        return None
    return _image_behind(node.inputs.get("Color"))


def specular_image(material):
    """The image feeding the Principled BSDF's specular level, or None."""
    socket = _specular_socket(material)
    return None if socket is None else _image_behind(socket)


def specular_level(material):
    """The specular level as a plain value (Blender's default is 0.5); None when a texture drives it."""
    socket = _specular_socket(material)
    return None if socket is None or socket.is_linked else float(socket.default_value)


def roughness(material):
    """The roughness as a plain value; None when a texture drives it."""
    principled = _principled(material)
    socket = None if principled is None else principled.inputs.get("Roughness")
    return None if socket is None or socket.is_linked else float(socket.default_value)


def alpha_use(material):
    """How the material uses alpha: None when it is opaque, else (mode, image, channel, value).

    mode is "blend" for a translucent surface and "clip" for a cut-out — the two things the game can do
    with a diffuse texture's alpha. Which one is read off the material's own render setting, the one
    that already decides how Blender draws it: Blended is a blend, anything else (Dithered, the
    default) is a cut-out.

    The alpha itself comes from whatever feeds the Principled BSDF's Alpha: an image's Alpha output
    (channel "A"), an image's Color used as a mask (channel "L"), or — with nothing plugged in — the
    plain value, which only makes sense as a blend. A value of 1 with nothing plugged in is opaque.
    """
    principled = _principled(material)
    socket = None if principled is None else principled.inputs.get("Alpha")
    if socket is None:
        return None
    blended = (getattr(material, "surface_render_method", None) == 'BLENDED'
               or getattr(material, "blend_method", None) == 'BLEND')
    if socket.is_linked:
        link = socket.links[0]
        node = link.from_node
        if node.type == 'TEX_IMAGE' and node.image is not None:
            image, channel = node.image, ("A" if link.from_socket.name == "Alpha" else "L")
        else:
            image, channel = _image_behind(socket), "L"
            if image is None:
                return None
        return ("blend" if blended else "clip", image, channel, None)
    value = float(socket.default_value)
    if value >= 0.999:
        return None
    return ("blend", None, None, value)


def with_alpha(pixels, width, height, diffuse, alpha):
    """The diffuse image's RGBA8 pixels with the alpha the material means written into the fourth byte.

    `pixels` is what image_rgba8 returned for `diffuse`; `alpha` is what alpha_use returned. An image
    that is its own alpha source is left as it is; a separate mask is brought to the diffuse image's
    size first.
    """
    _, image, channel, value = alpha
    if image is None:
        out = pixels.copy()
        out[:, 3] = int(round(max(0.0, min(1.0, value)) * 255.0))
        return out
    if image == diffuse and channel == "A":
        return pixels
    packed = image_rgba8(image)
    if packed is None:
        return pixels
    mask, mask_width, mask_height = packed
    mask = mask.reshape(mask_height, mask_width, 4).astype(np.float32)
    mask = _resample(_resample(mask, width, axis=1), height, axis=0)
    if channel == "A":
        coverage = mask[..., 3]
    else:
        coverage = mask[..., 0] * 0.2126 + mask[..., 1] * 0.7152 + mask[..., 2] * 0.0722
    out = pixels.copy()
    out[:, 3] = np.clip(coverage + 0.5, 0.0, 255.0).astype(np.uint8).reshape(-1)
    return out


def _principled(material):
    tree = getattr(material, "node_tree", None)
    if tree is None:
        return None
    return next((n for n in tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)


def _specular_socket(material):
    principled = _principled(material)
    if principled is None:
        return None
    for name in ("Specular IOR Level", "Specular"):  # 4.x renamed it
        socket = principled.inputs.get(name)
        if socket is not None:
            return socket
    return None


def _image_behind(socket):
    for _ in range(8):
        if socket is None or not socket.is_linked:
            return None
        node = socket.links[0].from_node
        if node.type == 'TEX_IMAGE':
            return node.image
        socket = next((i for i in node.inputs if i.is_linked and i.type == 'RGBA'), None)
    return None


def image_signature(image):
    """A cheap identity for an image's pixels; None for one with unsaved paint (always resend)."""
    if image.is_dirty:
        return None
    path = bpy.path.abspath(image.filepath, library=image.library) if image.filepath else ""
    try:
        stamp = os.path.getmtime(path) if path else 0
    except OSError:
        stamp = 0
    return f"{image.name}|{path}|{stamp}|{image.size[0]}x{image.size[1]}"


def image_rgba8(image):
    """Read an image as (pixels, width, height): uint8 RGBA, rows top-down, power-of-two sides.

    The game's textures are block-compressed with a full MIP chain, which wants powers of two, so
    anything else is resampled to the nearest one. That happens here on the pixel array rather than
    through Image.scale() on a copy: copying an image with unsaved paint (or a generated one) gives
    back its blank original, and the modder's own datablock must not be resized under them.
    Returns None for an image with no pixels.
    """
    width, height = image.size
    if width == 0 or height == 0:
        return None
    pixels = np.empty(width * height * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    # Blender stores rows bottom-up; a .dds runs top-down.
    pixels = pixels.reshape(height, width, 4)[::-1]

    target_width, target_height = _power_of_two(width), _power_of_two(height)
    pixels = _resample(_resample(pixels, target_width, axis=1), target_height, axis=0)

    if image.is_float and image.colorspace_settings.name != 'Non-Color':
        # Float buffers are scene-linear; the game samples its albedo as sRGB.
        rgb = np.clip(pixels[..., :3], 0.0, 1.0)
        pixels = pixels.copy()
        pixels[..., :3] = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * np.power(rgb, 1.0 / 2.4) - 0.055)
    rgba = np.clip(pixels * 255.0 + 0.5, 0.0, 255.0).astype(np.uint8)
    return rgba.reshape(-1, 4), target_width, target_height


def _resample(pixels, size, axis):
    """Linear resample of one axis to `size` texels (texel centres aligned)."""
    current = pixels.shape[axis]
    if current == size:
        return pixels
    position = (np.arange(size) + 0.5) * current / size - 0.5
    low = np.floor(position)
    weight = (position - low).astype(np.float32)
    first = np.clip(low.astype(np.int64), 0, current - 1)
    second = np.clip(first + 1, 0, current - 1)
    shape = [1, 1, 1]
    shape[axis] = size
    weight = weight.reshape(shape)
    return np.take(pixels, first, axis=axis) * (1.0 - weight) + np.take(pixels, second, axis=axis) * weight


def _power_of_two(size):
    nearest = 1 << max(2, int(round(np.log2(size))))
    return min(MAX_TEXTURE_SIZE, nearest)
