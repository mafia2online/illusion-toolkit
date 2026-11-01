using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class AuthoringTools
{
    [McpServerTool(Name = "lod_status")]
    [Description("Inspect Mafia III coarse/fine LOD groups: hierarchy, bounds, squared switch distance, native child-completer readiness and visibility. RendererVisible and RendererFallback separately report conservative coarse retention when fine geometry is not decoded; RenderVisibleMeshes also honors manual tree visibility. Read-only.")]
    public static async Task<string> LodStatus(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            IReadOnlyList<NativeLodInfo> groups = [];
            if (await ui.RunAsync(() => editor.NativeLodStatus(out groups)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, groups });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    [McpServerTool(Name = "car_place")]
    [Description("Place an independent verified Mafia III type-10 vehicle under an existing city Kit. archive is a car name or archive path in the configured game's cars folder. Copies native geometry, rig, collision, prefab and wheel templates plus textures/memory resources into the destination archives, with native resource budgets. Optional name renames the scene placement; position is world [x,y,z], defaulting to the parent position, and yaw is degrees about Z. Uses editor Undo/Redo and Save/Build. This creates an actual native vehicle entity, distinct from a traffic marker.")]
    public static async Task<string> PlaceVehicle(IEditorSession editor, IUiThreadMarshal ui, string parent, string archive,
        string? name = null, float[]? position = null, float? yaw = null)
    {
        try
        {
            string? path = null;
            if (await ui.RunAsync(() => editor.PlaceVehicle(parent, archive, name, position, yaw, out path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
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
    [Description("Edit one native triangle collision asset in a district companion, affecting ALL its instances, undoable. Pass exactly world_collision_mesh's collision-local positions array. Preserves topology, materials and shape keys and recooks bounds/compression/native BVHs, including supported containing compound instances when geometry expands. Shared stored vertices must move together; unsupported compound layouts, custom primitive variants and aliases are refused. Save/Build includes the collision companion.")]
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
    [Description("Edit fixed-topology interior NAV_PATH vertices using the validated supported game build in an isolated native cook helper. Supply document/object name, zero-based section, unique vertex indices and matching world [x,y,z] positions. Supports bounded height changes while retaining planar convex polygons, winding and conservative slope limits. Boundary/stream/blocked vertices and section bounds stay protected. Original cluster positions and every graph cost must reproduce exactly before editing; mismatches refuse unchanged. executable optionally names a copied supported game EXE; otherwise the configured executable is copied to the cook cache. Uses editor Undo/Save/Build.")]
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

    [McpServerTool(Name = "navigation_flip")]
    [Description("Flip the internal diagonal of two compatible native NAV_PATH triangles in the same cluster. Give document/object name, zero-based section and edge index. Preserves vertex/face/edge counts and native user data, recooks cluster graph costs in the isolated helper and uses normal editor Undo/Save/Build. Stream-connected patches, incompatible flags, non-coplanar or non-convex triangles are refused. executable optionally names a copied supported game EXE.")]
    public static async Task<string> FlipNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section, int edge, string? executable = null)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.FlipNavigationEdgeAsync(name, section, edge, executable));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, edge });
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

    [McpServerTool(Name = "navigation_subdivide")]
    [Description("Subdivide one compatible native NAV_PATH triangle into three using an inserted interior vertex. Give document/object name, section and face index; omit position for the centroid, or supply an interior point in the original face plane. Preserves actual streaming links and original cluster membership, recooks native graph costs, updates native storage/fixups and SDS allocation budgets, and uses normal Undo/Save/Build. Stream-connected patches and unsupported face/edge metadata are refused. executable optionally names a copied supported game EXE.")]
    public static async Task<string> SubdivideNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section,
        int face, float[]? position = null, string? executable = null)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.SubdivideNavigationFaceAsync(name, section, face, position, executable));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, face });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "navigation_extrude")]
    [Description("Extend a compatible native NAV_PATH open boundary by one connected triangle. Give document/object name, section and edge index; position optionally supplies the new coplanar outside vertex, otherwise a short outward extension is used. Rejects projected face overlap, unconnected boundary contacts, unsupported metadata and actual streamed patches. Uses the original native graph cooker and bidirectional path proof, fixes storage and RAM budgets, and supports normal Undo/Save/Build. Outside-AABB growth requires a completely isolated section. executable optionally names a copied supported game EXE.")]
    public static async Task<string> ExtrudeNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section,
        int edge, float[]? position = null, string? executable = null)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.ExtrudeNavigationBoundaryAsync(name, section, edge, position, executable));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, edge });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "navigation_stream_deform")]
    [Description("Edit a reciprocal pair of native NAV_PATH sections in one open document. Give source section and peer indices, source vertex indices and matching positions. Default mode moves shared seam counterparts together and preserves all cluster representatives. Opt in with moveRepresentatives=true to also edit interior vertices and rebuild moved representatives plus reciprocal cross-section half costs using the original native offline builder. Every external connection of a moved cluster must lead to the selected loaded peer. Both original connected graphs must reproduce exactly; topology, streaming identities, open/blocked boundaries and third-section contacts stay protected. Atomic Undo/Redo and Save/Build cover both sections. executable optionally names a supported copied EXE.")]
    public static async Task<string> DeformStreamingNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section,
        int peer, int[] vertices, float[][] positions, string? executable = null, bool moveRepresentatives = false)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.DeformNavigationStreamingAsync(name, section, peer, vertices, positions, executable, moveRepresentatives));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, peer, vertices = vertices.Length, moveRepresentatives });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "navigation_stream_split")]
    [Description("Split a compatible reciprocal native navigation seam in two loaded sections of one document. Preserves both original stream edge IDs and adds one vertex, one face, three edges and one reciprocal mesh connection in EACH section. The adjacent polygons must form closed convex planar patches without other streaming edges. position optionally supplies a point strictly inside the shared edge; otherwise its midpoint is used. Both original and new connected native graphs must reproduce, and native path queries must cross each half in both directions with every other seam blocked in scratch memory. moveRepresentatives optionally recooks fully covered cluster positions and native reciprocal costs. Normal atomic Undo/Redo, Save and compressed Build include both resources and RAM budgets.")]
    public static async Task<string> SplitStreamingNavigation(IEditorSession editor, IUiThreadMarshal ui, string name, int section, int peer,
        int edge, float[]? position = null, string? executable = null, bool moveRepresentatives = false)
    {
        try
        {
            var operation = await ui.RunAsync(() => editor.SplitNavigationStreamingAsync(name, section, peer, edge, position, executable, moveRepresentatives));
            if (await operation is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, peer, edge, moveRepresentatives });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "navigation_stream_candidates")]
    [Description("List bounded read-only geometric edge candidates for navigation_stream_split in an explicit loaded reciprocal section pair. The edit still requires exact native original/new graph reproduction and isolated bidirectional half traversal proofs. No geometry is changed.")]
    public static async Task<string> StreamingNavigationCandidates(IEditorSession editor, IUiThreadMarshal ui, string name, int section, int peer, int limit = 32)
    {
        try
        {
            int[] edges = [];
            if (await ui.RunAsync(() => editor.NavigationStreamingCandidates(name, section, peer, limit, out edges)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, section, peer, edges, nativeBaselineRequired = true });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "animation_bind")]
    [Description("Bind the current native animation preview to a Mafia III scene object's compiled render skeleton. Uses native root/name-hash bone mapping and translation locks; matching facial float slots drive morph targets at native value/100, preserving signed and overshoot weights. Set morphs=false for skeletal playback alone. Mismatched skeletons and unsupported additive facial blending are refused. Playback is transient and does not modify resources. Set enabled=false to restore the authored pose.")]
    public static async Task<string> AnimationBind(IEditorSession editor, IUiThreadMarshal ui, string name, bool enabled = true, bool morphs = true)
    {
        try
        {
            int mappedBones = 0, renderBones = 0;
            if (await ui.RunAsync(() => editor.AnimationBind(name, enabled, out mappedBones, out renderBones, morphs)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, enabled, morphs, mappedBones, renderBones });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "animation_floats")]
    [Description("Sample all named float slots of the current native animation at the current timeline time. Reference-pose slots are zero and mapped spline tracks replace them. Values retain their native signed/overshoot range; facial morph playback divides matching name-hash values by 100. Additive poses require the engine blend stack and are refused. No files are changed.")]
    public static async Task<string> AnimationFloats(IEditorSession editor, IUiThreadMarshal ui)
    {
        try
        {
            IReadOnlyList<AnimationFloatSlot> slots = [];
            if (await ui.RunAsync(() => editor.AnimationFloats(out slots)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, slots });
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
