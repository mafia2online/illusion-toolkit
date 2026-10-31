using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class AuthoringTools
{
    [McpServerTool(Name = "material_parameter")]
    [Description("Read or edit one material parameter by exact material name or 0x hash and native code such as C060/C061. Omit values to read. Editing uses the material editor's undo/redo and live GPU refresh, preserving vehicle paint/terrain metadata. Values must retain the existing float count; editor_save writes the copied material library.")]
    public static async Task<string> MaterialParameter(IEditorSession editor, IUiThreadMarshal ui, string material, string parameter, float[]? values = null)
    {
        try
        {
            float[] current = [];
            if (await ui.RunAsync(() => editor.MaterialParameter(material,parameter,values,out current)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, values = current });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "world_collision_meshes")]
    [Description("Page the geometry assets in the dedicated Mafia III district collision companion. Supply a document/object name. Reports native local vertex/triangle counts and instance counts; geometry editing affects ALL instances of an asset.")]
    public static async Task<string> WorldCollisionMeshes(IEditorSession editor, IUiThreadMarshal ui, string name, int offset = 0, int limit = 50)
    {
        try
        {
            IReadOnlyList<WorldCollisionInfo> meshes = []; int total = 0;
            if (await ui.RunAsync(() => editor.WorldCollisionMeshes(name,offset,limit,out meshes,out total)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, total, meshes });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "world_collision_mesh")]
    [Description("Read one world collision asset's local vertices, triangle indices and one instance world matrix. Use the returned positions array with world_collision_recook; mesh is the index from world_collision_meshes, instance defaults to zero.")]
    public static async Task<string> WorldCollisionMesh(IEditorSession editor, IUiThreadMarshal ui, string name, int mesh, int instance = 0)
    {
        try
        {
            CollisionMeshInfo? data = null;
            if (await ui.RunAsync(() => editor.WorldCollisionMesh(name,mesh,instance,out data)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, mesh = data });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "world_collision_recook")]
    [Description("Edit one native triangle collision asset in a district companion, affecting ALL its instances, undoable. Pass exactly world_collision_mesh's collision-local positions array. Preserves topology, materials and shape keys and recooks bounds/compression/native BVHs. Shared stored vertices must move together; outside-original-bounds changes, custom primitive variants and unsupported aliases are refused. Save/Build includes the collision companion.")]
    public static async Task<string> RecookWorldCollision(IEditorSession editor, IUiThreadMarshal ui, string name, int mesh, float[][] positions)
    {
        try
        {
            if (await ui.RunAsync(() => editor.RecookWorldCollision(name,mesh,positions)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "collision_remove")]
    [Description("Remove one compiled collision volume placement, undoable. Preserves shared shape/physics definitions and other placements; updates native bone helper allocation. Unknown cross-index arrays are refused. The editor Delete key uses the same path.")]
    public static async Task<string> RemoveCollision(IEditorSession editor, IUiThreadMarshal ui, string name)
    {
        try
        {
            if (await ui.RunAsync(() => editor.RemoveCollision(name)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "navigation_deform")]
    [Description("Edit fixed-topology interior NAV_PATH vertices using the validated supported game build in an isolated native cook helper. Supply document/object name, zero-based section, unique vertex indices and matching world [x,y,z] positions. Boundary/stream/blocked vertices, face planes, convexity and section bounds are protected. Original cluster positions and every graph cost must reproduce exactly before editing; mismatches refuse unchanged. executable optionally names a copied supported game EXE; otherwise the configured executable is copied to the cook cache. Uses editor Undo/Save/Build.")]
    public static async Task<string> DeformNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section,
        int[] vertices, float[][] positions, string? executable = null)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.DeformNavigationAsync(name,section,vertices,positions,executable));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, changedVertices = vertices.Length });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "navigation_translate")]
    [Description("Translate one isolated Mafia III NAV_PATH section by [x,y,z], undoable. Select an object/document with name; section is its zero-based NavigationMeshes index. Preserves topology and exact graph costs, moves vertices/cluster positions/bounds, and requests native spatial-tree rebuild. Sections with external streaming links are refused.")]
    public static async Task<string> TranslateNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section, float[] offset)
    {
        try
        {
            if (await ui.RunAsync(() => editor.TranslateNavigation(name, section, offset)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "animation_screenshot")]
    [Description("Save the current native animation preview frame to a PNG without activating a window. Open or seek a clip with animation_open first.")]
    public static async Task<string> AnimationScreenshot(IEditorSession editor, IUiThreadMarshal ui, string path)
    {
        try
        {
            if (await ui.RunAsync(() => editor.AnimationScreenshot(path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "animation_open")]
    [Description("Open the Mafia III native animation preview in a background window, select a clip and seek/play it. Supports verified Havok 2014.2 spline and interleaved transform tracks with their named skeleton; returns available clips and playback state. Uses read-only source files.")]
    public static async Task<string> AnimationOpen(IEditorSession editor, IUiThreadMarshal ui, string archive = "tables/ingame.sds",
        int clip = 0, double time = 0, bool playing = false)
    {
        try
        {
            AnimationInfo? result = null;
            if (await ui.RunAsync(() => editor.AnimationOpen(archive, clip, time, playing, out result)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, animation = result });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "collision_add")]
    [Description("Add an independent Mafia III physical collision volume under a scene object, undoable. Box size is half-extents; capsule uses size.x as radius and size.z as segment height; convex uses collision-local points. Position is local to the parent. Triangle kind uses points plus indices (up to 100000 vertices/triangles) and genuine concave Havok trees. Newly authored cooked shapes include native allocation/dependency/material metadata.")]
    public static async Task<string> AddCollision(IEditorSession editor, IUiThreadMarshal ui, string parent, string kind,
        float[] size, float[]? position = null, float[][]? points = null, ushort material = 103, ushort bone = ushort.MaxValue, uint[]? indices = null)
    {
        try
        {
            string? path = null;
            if (!kind.Equals("triangle",StringComparison.OrdinalIgnoreCase) && indices is not null) return ToolResult.Invalid("indices require triangle collision kind");
            if (await ui.RunAsync(() => kind.Equals("triangle",StringComparison.OrdinalIgnoreCase)
                ? editor.AddTriangleCollision(parent,points??[],indices??[],position,material,bone,out path)
                : editor.AddCollision(parent,kind,size,position,points,material,bone,out path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "mesh_make_unique")]
    [Description("Give one Mafia III visual independent mesh/face/vertex/index/subdivision assets, undoable. Use its scene_find path or guid:...:visual:N identity. This enables editing a shared vehicle part without changing the other parts using the original geometry.")]
    public static async Task<string> MakeMeshUnique(IEditorSession editor, IUiThreadMarshal ui, string name)
    {
        try
        {
            if (await ui.RunAsync(() => editor.MakeMeshUnique(name)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "scene_create_group")]
    [Description("Create an identity Mafia III Kit group, undoable. Omit parent for a standalone root in the single loaded document; supply an existing Kit path to nest it. The new group's path is returned.")]
    public static async Task<string> CreateGroup(IEditorSession editor, IUiThreadMarshal ui,
        string name, string? parent = null)
    {
        try
        {
            string? path = null;
            if (await ui.RunAsync(() => editor.CreateGroup(name, parent, out path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "collision_mesh")]
    [Description("Read a Mafia III collision volume's decoded local vertices, triangle indices and world matrix. Use these collision-local positions with collision_recook.")]
    public static async Task<string> CollisionMesh(IEditorSession editor, IUiThreadMarshal ui, string name)
    {
        try
        {
            CollisionMeshInfo? mesh = null;
            if (await ui.RunAsync(() => editor.CollisionMesh(name, out mesh)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, mesh });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "collision_recook")]
    [Description("Rebuild one Mafia III collision volume from collision-local [x,y,z] points, undoable. Convex input spans a volume and fits Havok's 252-vertex/255-face limits. Triangle meshes require exactly the collision_mesh vertex array and preserve topology, materials and native shape keys; vertices sharing native storage must move together, and separately cooked scale variants are refused. Creates independent cooked data and a physics dependency.")]
    public static async Task<string> RecookCollision(IEditorSession editor, IUiThreadMarshal ui,
        string name, float[][] positions)
    {
        try
        {
            if (await ui.RunAsync(() => editor.RecookCollision(name, positions)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
