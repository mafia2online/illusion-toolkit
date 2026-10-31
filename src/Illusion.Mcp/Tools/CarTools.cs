using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Tools for building a model's own assets - a car first of all, though nothing here is tied to one: a
/// material of its own on any shader of the game's, and the checks that otherwise only the game makes.
/// </summary>
[McpServerToolType]
public sealed class CarTools
{
    private static readonly SemaphoreSlim WinterGate = new(1, 1);

    [McpServerTool(Name = "material_variant")]
    [Description("Make a model its OWN material on any shader of the game's: a copy of an existing material under a new name, with some of its textures replaced by pictures of yours. This is how a car gets its own lamp atlas, interior texture or sheet of markings - shaders the toolkit has no preset for (a material made in Blender and pushed gets one of a few plain shaders; this keeps the source's shader, flags, sampler states and every parameter). 'textures' maps a sampler - its id ('S001') or name ('NormalTexture'), as get_material_info lists them - to an image file (png; both sides powers of two). The picture's four channels are stored AS THEY ARE: DXT5 when any pixel is not opaque, else DXT1, split into the top level and the rest the way the game stores textures of 256 and up. So a car's tangent normal map is given the way the game keeps it - x in ALPHA, y in GREEN (read the stock one with inspect_sds_texture / an image tool, edit, hand it back). Each picture is written into the working copy of 'archive' under a file name no texture has in any archive that has a working copy (archives never opened are not looked into; the engine finds textures by name - never reuse a stock name for a changed picture), and the material into default.mtl at once (the previous file is kept in a backups folder beside it) - so it is refused while other material edits wait to be saved. A call that fails part way takes back what it wrote and can be made again. Then point the mesh at it: mesh_materials with the slot and the new name, per level of detail; build the archive; for Mafia II Online car_export_m2o writes the new material into the car's own .mtl. Samplers not named keep the source's textures. A copy of a car's PAINT (or chrome, glass, interior) is not treated as one until the car's PREFAB says so: the game colours, dirties and deforms the materials the prefab lists, and a copy that is not listed is drawn bare primer grey whatever colour the car is given - follow with car_material_like <car> <new name> <source name>. A winter twin is a variant of the source's own winter material under the new name + '^zima'; car_winter then swaps to it.")]
    public static async Task<string> MaterialVariant(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The material to copy, by exact name (search_materials) or by its 64-bit hash as 0x… text.")] string source,
        [Description("Name of the new material: letters, digits and '_'.")] string name,
        [Description("The archive whose working copy takes the pictures: a full path to an .sds, or a path under pc\\sds such as 'cars/my_car'.")] string archive,
        [Description("Sampler -> image file, as a JSON object: {\"NormalTexture\": \"C:\\\\work\\\\my_car_n.png\"}. May be empty for a plain copy.")] string textures = "{}")
    {
        try
        {
            Dictionary<string, string>? map;
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(textures) ? "{}" : textures);
            }
            catch (JsonException)
            {
                return ToolResult.Invalid("textures is a JSON object of sampler -> image file, e.g. {\"NormalTexture\": \"C:\\\\work\\\\n.png\"}");
            }
            if (await ui.RunAsync(() => workshop.StageBusy(archive)) is { } busy) return ToolResult.Invalid(busy);
            MaterialVariantInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.MaterialVariant(source, name, archive, map ?? [], out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(archive)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "archive_texture")]
    [Description("Give an archive a picture of ITS OWN under a texture name: an image file (png; sides powers of two; the four channels stored as they are - DXT5 when any pixel is not opaque, else DXT1, split the way the game stores textures of 256 and up) is written into the archive's working copy as that texture, replacing the archive's own copy when it already carries one (under the spelling the archive has, whatever case you give). It is for the pictures of materials that are the model's OWN: the texture of a material_variant, or the sheet of a material made by a Blender push - a push binds a material that already exists without rewriting its picture, so a redrawn sheet, or one lost when the working copy was put back to an earlier state, is stored with this. Under a STOCK texture's name it is a trap, and 'alsoCarriedBy' says when (among the archives that have a working copy - the others are not looked into): the engine keeps one picture per name, and a cloned car's own picture under the stock name was NOT the one Mafia II Online drew (seen in the game: a hearse's door emblem stayed although the clone's archive held a normal map without it). A car's paint cannot take a material of its own either (see material_variant), so something drawn in the stock paint's maps is taken off in UV, not in the picture. remove=true takes the texture out of the working copy instead (files and manifest entries) - a texture no material uses makes a multiplayer server refuse the car. Build the archive afterwards.")]
    public static async Task<string> ArchiveTexture(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The archive: a full path to an .sds, or a path under pc\\sds such as 'cars/my_car'.")] string archive,
        [Description("The texture's file name in the archive, e.g. 'ShH_n512.dds'.")] string texture,
        [Description("Full path of the image file to store under that name. Omit with remove=true.")] string? image = null,
        [Description("Take the texture out of the archive's working copy instead of writing one. Default false.")] bool remove = false)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(archive)) is { } busy) return ToolResult.Invalid(busy);
            ArchiveTextureInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.ArchiveTexture(archive, texture, image, remove, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(archive)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "material_delete")]
    [Description("Take a material the toolkit ADDED (material_variant, a Blender push, an import) out of the material library again - one that turned out not to be needed. The game's own materials are refused - and so is everything on an edition the toolkit has no list of shipped materials for, where it cannot tell. Refused while other material edits wait to be saved. The library is rewritten at once (the previous file is kept in the backups folder beside it). Meshes still pointing at the material draw with none: re-point them first (mesh_materials).")]
    public static async Task<string> MaterialDelete(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The material, by exact name.")] string name)
    {
        try
        {
            string? library = null;
            string? refused = await ui.RunAsync(() => workshop.MaterialDelete(name, out library));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, deleted = name, library });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // These tools write a car's working copy on disk. The resource editor keeps its own copy of a car on its
    // stage - a save from it would undo what was written - so a car with unsaved edits or in a Blender session
    // is refused, and one that is merely open is loaded again afterwards.

    [McpServerTool(Name = "car_lights")]
    [Description("Read a vehicle's saved native light entries and body rig bones. Mafia III reads compiled S_InitLight fields, its own numeric kinds, native light models, damage-check bones and beacon count (Last); ScaleBone is null. Mafia II reads its PREFAB light records and scale bone. Bone identifiers from this result can be passed directly to the editing tools. No files are changed; an open vehicle with unsaved edits is refused.")]
    public static async Task<string> CarLights(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarLightsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarLights(car, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_light_set")]
    [Description("Create or update one vehicle light entry. Omitted fields retain existing values; a new entry requires a kind. Mafia III copies a verified native same-kind template, using a compatible stock police-car template when available, preserves unknown fields and allocates an unused emissivity index without changing existing mesh channels. Numeric kinds are profile-specific: use car_lights results. Lamp geometry and its native emissivity/skin channels must be authored separately. Writes the working copy; busy or unsaved editors are refused. A matching resource stage reloads and its undo history is cleared; editor_build packs the changed archive.")]
    public static async Task<string> CarLightSet(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The bone name or canonical bone:index returned by car_lights.")] string bone,
        [Description("Profile-specific native kind number or supported label. Mafia III includes headlight, indicator, brake, reverse, beacon and interior; a verified native template is required. Omit to retain the kind.")] string? kind = null,
        [Description("'left' or 'right': which side's switch the light follows. A lamp in the middle is written as left. Default: kept (left for a new entry).")] string? side = null,
        [Description("Native cars_universal light model name or 0x hash; Mafia III includes car_aux_amber_glow, car_reverse_glow, car_headlight_glow and car_beacon. Omit to retain the template model.")] string? model = null,
        [Description("Bones of the rig that must be whole for the light to work - the deform bone of the part the lamp sits on - separated by commas. '-' takes them all off. Default: kept (none for a new entry).")] string? checkBones = null,
        [Description("How strongly the lit piece glows, 0 to 100 (2 on most lamps, 1.2 on some headlights). Default: kept (as the pattern for a new entry).")] double? power = null)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            string[] checks = [.. (checkBones ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            CarLightsInfo? result = null;
            bool added = false;
            string? refused = await ui.RunAsync(() => workshop.CarLightSet(car, bone, kind, side, model, checks, power, out added, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, action = added ? "added" : "changed", reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_light_remove")]
    [Description("Remove a native vehicle light entry while retaining its bone and geometry. Supports Mafia II PREFAB and Mafia III compiled S_InitLight arrays. Writes the working copy; busy or unsaved editors are refused. A matching resource stage reloads and its undo history is cleared; editor_build packs the changed archive.")]
    public static async Task<string> CarLightRemove(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The bone whose light goes.")] string bone)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarLightsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarLightRemove(car, bone, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, removed = bone, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_bone_add")]
    [Description("Add a BONE to the rig of a car's body, standing at a place in the model's space with no turn of its own - what a new lamp needs to light where it is (see car_light_set), or a new part to move on its own. A rig keeps its bones in level order, so the bone goes in after its parent's last child and every bone behind it moves up by one: every table that names bones by number is renumbered with it, and a rig that is not laid out the way the shipped cars' are is refused untouched. Nothing is skinned to the bone yet - do that in Blender AFTER this (a mesh pulled before has the old numbering: end the session and pull again). 'reach' is how far the geometry the bone will carry reaches from it. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarBoneAdd(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("Name of the new bone: plain Latin text.")] string name,
        [Description("The bone it hangs on (car_lights lists the rig's bones). A part that opens carries what hangs on its bone with it.")] string parent,
        [Description("Where it stands, model space, metres: x.")] double x,
        [Description("y (the car's nose is +y).")] double y,
        [Description("z (up).")] double z,
        [Description("How far the geometry it will carry reaches from it, metres. Default 0.1.")] double reach = 0.1)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarBoneInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarBoneAdd(car, name, parent, x, y, z, reach, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_beacon")]
    [Description("Set up native rotating-beacon bones and light registration at a model-space position. Mafia III reuses and repositions the existing casing where present, appends missing casing/lamp bones, and copies a schema-compatible native type7 template with car_beacon light model. Its parent/check bone must exist; no housing geometry is invented. Mafia II uses its stock police-car bone arrangement and refuses incompatible existing placement. Author lamp geometry through Blender afterwards. Writes the working copy transactionally; busy or unsaved editors are refused. A matching resource stage reloads and its undo history is cleared; editor_build packs the changed archive.")]
    public static async Task<string> CarBeacon(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The middle of the lamp, model space, metres: x.")] double x,
        [Description("y (the car's nose is +y).")] double y,
        [Description("z (up).")] double z,
        [Description("The bone that must be whole for the beacon to work: the deform bone of the part it sits on, e.g. 'deform_top' for a roof (car_lights lists the bones).")] string checkBone,
        [Description("How far the lamp reaches from its middle, metres. Default 0.12.")] double reach = 0.12)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarBeaconInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarBeacon(car, x, y, z, checkBone, reach, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_collisions")]
    [Description("Read the saved vehicle collision volumes with full dimensions, model-space centers and decoded model-space bounds. Mafia III reports native physical volumes grouped by canonical frame GUID and attachment bone:index; shape kinds include box, sphere, capsule, convex and triangle. Its ParentBoneId is attachment metadata and its placement matrix already contains model bind placement. Mafia II reports its separate body, glass and zone PREFAB roles. No files are changed.")]
    public static async Task<string> CarCollisions(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarCollisionsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCollisions(car, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_collision_add")]
    [Description("Add a physical volume to a vehicle component using full dimensions and model-space position in metres (positive dimensions, maximum 50). Mafia III accepts box, sphere or Z-axis capsule; body/physical selects native physical collision, while Mafia II glass/zone roles and unverified cylinders are refused. Select an exact component from car_collisions or a body bone:index. New Mafia III shape and cooked physics resources belong to the car, retaining external templates unchanged. Sphere diameters must match; capsule X/Y diameters match and total Z includes rounded caps. Mafia II also supports its native cylinder, glass and zone paths. Writes the working copy; busy or unsaved editors are refused. The matching stage reloads and its undo history is cleared; editor_build packs all changed archives.")]
    public static async Task<string> CarCollisionAdd(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The component it hangs on, by its name or its bone (car_collisions lists both) - the body's for something that is part of the shell.")] string component,
        [Description("Full size along x, metres.")] double sizeX,
        [Description("Full size along y (the car's length).")] double sizeY,
        [Description("Full size along z (height).")] double sizeZ,
        [Description("The middle, model space, metres: x.")] double x,
        [Description("y (the car's nose is +y).")] double y,
        [Description("z (up).")] double z,
        [Description("Box, sphere or capsule; Mafia II additionally supports cylinder.")] string shape = "box",
        [Description("Body by default. Mafia III also accepts physical; glass/zone retain their Mafia II meaning and are refused for Mafia III.")] string role = "body")
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarCollisionsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCollisionAdd(car, component, role, shape, sizeX, sizeY, sizeZ, x, y, z, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_collision_remove")]
    [Description("Remove one saved collision placement using its component and zero-based index from car_collisions. Mafia III also updates the native bone-helper allocation; shared shape/physics definitions remain available to other placements. Mafia II uses its PREFAB volume path. Geometry and rig bones stay. Writes the working copy; busy or unsaved editors are refused. The matching stage reloads and its undo history is cleared; editor_build packs changed archives.")]
    public static async Task<string> CarCollisionRemove(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The component, by its name or its bone.")] string component,
        [Description("Which of its collisions, counted from 0 as car_collisions lists them.")] int index)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarCollisionsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCollisionRemove(car, component, index, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_materials")]
    [Description("Which of a car's materials the GAME colours, dirties, burns and deforms. A mesh only names its materials; what the game does to one on a car is said in two lists of the car's PREFAB, by the material's hash. Rows of the first list: flags 1 - PAINTED in the car's colour (the body paint and the far level's paint, nothing else); 2 - dirt and burning (paint, chrome, glass; with the car's burnt texture where it has one); 4 - the interior. The second list names what deforms with the body (paint, chrome, glass) and in which group. 'onTheCar' says whether a mesh of the car still names that material; 'unlisted' are materials the meshes name that neither list has - fine for an engine bay or a sheet of markings, WRONG for a copy of the paint (see car_material_like). Reads the SAVED working copy (refused while the resource editor holds the car with unsaved edits); nothing is written.")]
    public static async Task<string> CarMaterials(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarMaterialsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarMaterials(car, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_material_like")]
    [Description("Tell the game to treat one of a car's materials LIKE another: the material gets every row the other has in the PREFAB's two lists (car_materials) - painted in the car's colour, dirtied, burnt, deformed with the body. This is what makes a car's OWN PAINT work: a copy of the paint material under a new name (material_variant - the car's own normal map without the stock car's emblem, its own dirt mask) is drawn bare primer grey, whatever colour the car is given, until it has the paint's rows. Do the same for a copy of the chrome, the glass or the interior, and for each winter twin ('Name^zima') you made. With like omitted the material's rows are taken out again. The winter twin of the car shares the PREFAB: run car_winter afterwards. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarMaterialLike(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The car's own material, by name or 0x... hash.")] string material,
        [Description("The material it is to be treated like - the one it was copied from - by name or 0x... hash. Omit to take the material's rows out of the lists.")] string? like = null)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarMaterialsInfo? result = null;
            int changed = 0;
            string? refused = await ui.RunAsync(() => workshop.CarMaterialLike(car, material, string.IsNullOrWhiteSpace(like) ? null : like, out changed, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, rows = changed, reloaded = changed > 0 && await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_winter")]
    [Description("Make a car's WINTER twin again from its summer car - run it last, after everything else is saved, and again after any later change. The game keeps a car twice: name.sds and name_z.sds, the one it loads when the city is under snow. On the shipped cars the two hold the SAME model - buffers, prefab, tuning, collision shapes byte for byte - and differ in three things: some material slots name a winter material in place of the summer one (paint, chrome, glass - the ones with snow and frost in them), the textures those need stand in the archive in place of the summer ones, and the effects file. So the work on a car is done ONCE, in summer: this copies the summer working copy whole over the winter one and then makes those three differences, reading them off 'reference' - the shipped car this one was made from, whose own summer and winter archives are compared slot by slot. A material the reference car does not have at all - the car's own interior, its markings - stays as it is, unless the library holds a material of its name + '^zima' (its winter twin: it is swapped for it). Textures follow the materials: a summer texture the reference's winter copy drops is taken out only when no material of the winter car names it ('texturesKept' otherwise), and a texture only the OLD winter copy carried that a winter material names is carried over ('texturesCarried'). The summer car's snow layer is the same geometry in both; winter shows it, so look at a changed roof in winter. The winter working copy is REPLACED (anything else edited there by hand is gone) - built whole beside the old one and swapped in, so a refusal or a failed write leaves the old one as it was; the summer car is only read. Refused while the resource editor has either archive with unsaved edits or in a Blender session; a winter twin on its stage is loaded again ('reloaded'). Afterwards the winter archive is on the resource editor's build list; build both archives (editor_build, or archive_build on each); car_export_m2o carries the winter twin along.")]
    public static async Task<string> CarWinter(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The summer car: its archive name under pc\\sds\\cars ('my_car'), or a full path to an .sds. Its twin is my_car_z.sds beside it (car_clone makes both).")] string car,
        [Description("The shipped car it was made from, the same way ('shubert_hearse').")] string reference)
    {
        try
        {
            string stem = car.Trim();
            if (stem.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            string twin = stem + "_z";
            // The copy is long file work: off the UI thread, so the editor does not freeze while it runs - and one
            // run at a time, since every run swaps the same folders (a client that retries must not start a second).
            if (!await WinterGate.WaitAsync(0)) return ToolResult.Invalid("car_winter is already running - wait for it to finish");
            try
            {
                if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
                if (await ui.RunAsync(() => workshop.StageBusy(twin)) is { } busyTwin) return ToolResult.Invalid(busyTwin);
                CarWinterInfo? result = null;
                string? refused = await Task.Run(() => workshop.CarWinter(car, reference, out result));
                if (refused != null) return ToolResult.Invalid(refused);
                // The twin opened and edited while the copy ran is not reloaded over its edits: what it shows is no
                // longer what is on disk, and it is said.
                if (await ui.RunAsync(() => workshop.StageBusy(twin)) is { } editedMeanwhile)
                {
                    return ToolResult.Json(new { success = true, reloaded = false, warning = editedMeanwhile, result });
                }
                return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(twin)), result });
            }
            finally
            {
                WinterGate.Release();
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_check")]
    [Description("Read-only vehicle geometry diagnostics for all native levels of detail. Supports Mafia II and compiled Mafia III. Checks referenced packed skin weights sum to 255, empty skin, distant/nonfinite positions, zero-area triangles and UV-degenerate triangles. With a reference, compare additional native UV channels, packed vertex colors and bones exclusive to each matched material. Mafia III decodes its half-UV streams and BGRA paint-mask channels directly, without requiring unique geometry. Material counterparts use exact hashes, explicit same mappings or an unambiguous shader/shared-texture match. Stock files can contain real degenerate triangles; findings report them rather than inferring corruption. notCompared identifies unmatched materials or visual LODs without decoded faces. clean is true only when the checks performed found no problems. Reads the saved working copy and refuses unsaved staged edits.")]
    public static async Task<string> CarCheck(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car,
        [Description("The car it was made from, the same way. Default: none - the comparisons against a reference are skipped.")] string? reference = null,
        [Description("Which reference material each of the car's own materials was made from: 'MyPaint=StockPaint, MyInterior=StockInterior'. Default: worked out by shader and shared textures where it can be.")] string? same = null)
    {
        try
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in (same ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] sides = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (sides.Length != 2 || sides[0].Length == 0 || sides[1].Length == 0) return ToolResult.Invalid("same is 'own=source' pairs separated by commas");
                pairs[sides[0]] = sides[1];
            }
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarCheckInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCheck(car, reference, pairs, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }
}
