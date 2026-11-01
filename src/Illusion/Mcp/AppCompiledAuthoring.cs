using System.Numerics;
using System.IO;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? AnimationScreenshot(string path)
    {
        if (!Path.IsPathRooted(path)) return "give the full PNG output path";
        if (ResourceWindow?.AnimationPreview is not { } preview) return "animation_open first";
        try { preview.SaveFrame(path); return null; }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return ex.Message; }
    }

    public string? MakeMeshUnique(string name)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "end the Blender session before isolating geometry";
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        return host.MakeCompiledMeshUnique(node!);
    }

    public string? PlaceVehicle(string parent, string archive, string? name, float[]? position, float? yaw, out string? path)
    {
        path = null;
        if (position is not null && (position.Length != 3 || position.Any(v => !float.IsFinite(v))) || yaw is { } angle && !float.IsFinite(angle))
            return "Vehicle position and yaw must be finite.";
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, parent, out var row) is { } refusal) return refusal;
        string source = Path.IsPathRooted(archive) ? archive : Path.Combine(Assets.MafiaEnvironment.ArchiveFolder,
            archive.Contains('/') || archive.Contains('\\') ? archive : Path.Combine("cars", archive));
        if (!source.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) source += ".sds";
        Vector3? at = position is null ? null : new(position[0], position[1], position[2]);
        refusal = host.ObjectImporting.ImportVehicle(row!, new FileInfo(source), name, at, yaw, out var created);
        if (refusal is null) path = PathOf(created!);
        return refusal;
    }

    public string? AddCollision(string parent, string kind, float[] size, float[]? position, float[][]? points,
        ushort material, ushort bone, out string? path)
    {
        path = null; position ??= [0,0,0];
        if (size is not { Length: 3 } || size.Any(v => !float.IsFinite(v)) || position is not { Length: 3 } || position.Any(v => !float.IsFinite(v)))
            return "size and position must contain three finite numbers";
        if (points is not null && (points.Length > 100000 || points.Any(p => p is not { Length: 3 } || p.Any(v => !float.IsFinite(v)))))
            return "points must contain finite [x,y,z] values";
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "end the Blender session before creating collision";
        if (Resolve(host, parent, out var node) is { } unresolved) return unresolved;
        var refusal = host.AddCompiledCollision(node!, kind, new Vector3(size[0],size[1],size[2]), new Vector3(position[0],position[1],position[2]),
            points?.Select(p => new Vector3(p[0],p[1],p[2])).ToArray(), material, out var created, bone);
        if (refusal is not null) return refusal;
        path = PathOf(created!); return null;
    }

    public string? AddTriangleCollision(string parent, float[][] positions, uint[] indices, float[]? position,
        ushort material, ushort bone, out string? path)
    {
        path = null; position ??= [0,0,0];
        if (positions is null || positions.Length is < 3 or > 100000 || positions.Any(p=>p is not { Length: 3 } || p.Any(v=>!float.IsFinite(v)))
            || indices is null || indices.Length is < 3 or > 300000 || indices.Length % 3 != 0 || indices.Any(i=>i>=positions.Length))
            return "triangle collision needs 3-100000 finite local positions and 1-100000 indexed triangles";
        if (position is not { Length: 3 } || position.Any(v=>!float.IsFinite(v))) return "position needs three finite values";
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.IsLoading || host.BridgeEditedCount > 0) return "wait for loading and end the Blender session before creating collision";
        if (Resolve(host,parent,out var node) is { } refusal) return refusal;
        refusal = host.AddCompiledCollision(node!,"triangle",Vector3.One,new(position[0],position[1],position[2]),
            positions.Select(p=>new Vector3(p[0],p[1],p[2])).ToArray(),material,out var created,bone,indices);
        if (refusal is null) path=PathOf(created!);
        return refusal;
    }

    public string? CreateGroup(string name, string? parent, out string? path)
    {
        path = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "end the Blender session before creating a group";
        SceneNode? under = null;
        if (!string.IsNullOrWhiteSpace(parent) && Resolve(host, parent, out under) is { } unresolved) return unresolved;
        var refusal = host.CreateCompiledGroup(name, under, out var created);
        if (refusal is not null) return refusal;
        path = PathOf(created!);
        return null;
    }

    public string? CollisionMesh(string name, out CollisionMeshInfo? mesh)
    {
        mesh = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        if (node!.Source is not IFrameNode frame || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "select a compiled collision volume";
        if (document.CollisionMesh(frame) is not { } data)
            return document.CollisionWarnings.Count == 0 ? "this collision volume could not be decoded"
                : string.Join("; ", document.CollisionWarnings);
        var world = data.World;
        mesh = new CollisionMeshInfo(node.Name, data.Positions.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(), data.Indices,
            [world.M11,world.M12,world.M13,world.M14,world.M21,world.M22,world.M23,world.M24,
                world.M31,world.M32,world.M33,world.M34,world.M41,world.M42,world.M43,world.M44]);
        return null;
    }

    public string? MaterialParameter(string material, string parameter, float[]? values, out float[] current)
    {
        current = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        ulong? hash = material.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(material[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong parsed)
                ? parsed : Assets.MafiaMaterials.FindHashByName(material);
        if (hash is null) return "unknown material";
        var before = host.MaterialCatalog.GetParameter(hash.Value, parameter);
        if (before is null) return "unknown material parameter";
        if (values is not null)
        {
            if (values.Any(value => !float.IsFinite(value)) || values.Length != before.Count) return "values must match the parameter's float count and be finite";
            if (!host.SetMaterialParameter(hash.Value, parameter, values)) return "the material parameter could not be changed";
        }
        current = host.MaterialCatalog.GetParameter(hash.Value, parameter)!.ToArray();
        return null;
    }

    public string? RemoveCollision(string name)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        return host.RemoveCompiledCollision(node!);
    }

    public Task<string?> DeformNavigationAsync(string name, int section, int[] vertices, float[][] positions, string? executable)
    {
        if (vertices is null || positions is null || vertices.Length != positions.Length || vertices.Length is < 1 or > 100000
            || vertices.Any(index=>index<0) || vertices.Distinct().Count()!=vertices.Length
            || positions.Any(p=>p is not { Length: 3 } || p.Any(value=>!float.IsFinite(value))))
            return Task.FromResult<string?>("vertices and finite [x,y,z] positions must have the same nonzero count and unique indices");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host,name,out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        var edits = vertices.Select((index,i)=>(index,position:new Vector3(positions[i][0],positions[i][1],positions[i][2])))
            .ToDictionary(pair=>pair.index,pair=>pair.position);
        return host.DeformCompiledNavigationAsync(node!,section,edits,executable);
    }

    public Task<string?> DeformNavigationStreamingAsync(string name, int section, int peer, int[] vertices, float[][] positions, string? executable, bool moveRepresentatives = false)
    {
        if (section < 0 || peer < 0 || section == peer || vertices is null || positions is null || vertices.Length != positions.Length
            || vertices.Length is < 1 or > 100000 || vertices.Any(i => i < 0) || vertices.Distinct().Count() != vertices.Length
            || positions.Any(p => p is not { Length: 3 } || p.Any(v => !float.IsFinite(v))))
            return Task.FromResult<string?>("Select distinct nonnegative section indices and unique vertices with matching finite [x,y,z] positions.");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host, name, out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        var edits = vertices.Select((index, i) => (index, position: new Vector3(positions[i][0], positions[i][1], positions[i][2])))
            .ToDictionary(p => p.index, p => p.position);
        return host.DeformCompiledNavigationStreamingAsync(node!, section, peer, edits, executable, moveRepresentatives);
    }

    public Task<string?> SplitNavigationStreamingAsync(string name, int section, int peer, int edge, float[]? position, string? executable, bool moveRepresentatives = false)
    {
        if (section < 0 || peer < 0 || section == peer || edge < 0 || position is not null && (position.Length != 3 || position.Any(v => !float.IsFinite(v))))
            return Task.FromResult<string?>("Select distinct nonnegative sections and edge; position requires three finite coordinates.");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host, name, out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        Vector3? point = position is null ? null : new(position[0], position[1], position[2]);
        return host.SplitCompiledNavigationStreamingAsync(node!, section, peer, edge, point, executable, moveRepresentatives);
    }

    public string? NavigationStreamingCandidates(string name, int section, int peer, int limit, out int[] edges)
    {
        edges = [];
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } refusal) return refusal;
        var document = node!.Source as CompiledSceneDocument ?? node.OwningDocumentNode()?.Source as CompiledSceneDocument;
        if (document is null || (uint)section >= document.NavigationMeshes.Count || (uint)peer >= document.NavigationMeshes.Count || section == peer)
            return "Select two distinct navigation sections in an open Mafia III document.";
        try { edges = document.NavigationMeshes[section].StreamingEdgeSplitCandidates(document.NavigationMeshes[peer], limit).ToArray(); return null; }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { return ex.Message; }
    }

    public string? TranslateNavigation(string name, int section, float[] offset)
    {
        if (offset is not { Length: 3 } || offset.Any(value => !float.IsFinite(value))) return "offset needs three finite numbers";
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        return host.TranslateCompiledNavigation(node!, section, new Vector3(offset[0], offset[1], offset[2]));
    }

    public Task<string?> FlipNavigationEdgeAsync(string name, int section, int edge, string? executable)
    {
        if (section < 0 || edge < 0) return Task.FromResult<string?>("Section and edge indices must be nonnegative.");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host, name, out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        return host.FlipCompiledNavigationEdgeAsync(node!, section, edge, executable);
    }
    public Task<string?> SubdivideNavigationFaceAsync(string name, int section, int face, float[]? position, string? executable)
    {
        if (section < 0 || face < 0 || position is not null && (position.Length != 3 || position.Any(v => !float.IsFinite(v))))
            return Task.FromResult<string?>("Section and face indices must be nonnegative; position needs three finite coordinates.");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host, name, out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        Vector3? point = position is null ? null : new(position[0], position[1], position[2]);
        return host.SubdivideCompiledNavigationFaceAsync(node!, section, face, point, executable);
    }
    public Task<string?> ExtrudeNavigationBoundaryAsync(string name, int section, int edge, float[]? position, string? executable)
    {
        if (section < 0 || edge < 0 || position is not null && (position.Length != 3 || position.Any(v => !float.IsFinite(v))))
            return Task.FromResult<string?>("Section and edge indices must be nonnegative; position needs three finite coordinates.");
        if (TargetHost is not { } host) return Task.FromResult<string?>(TargetNotOpen);
        if (Resolve(host, name, out var node) is { } refusal) return Task.FromResult<string?>(refusal);
        Vector3? point = position is null ? null : new(position[0], position[1], position[2]);
        return host.ExtrudeCompiledNavigationBoundaryAsync(node!, section, edge, point, executable);
    }

    public string? RecookCollision(string name, float[][] positions)
    {
        if (positions is null || positions.Length is < 3 or > 100000
            || positions.Any(p => p is not { Length: 3 } || p.Any(v => !float.IsFinite(v))))
            return "positions needs 4–100000 finite [x,y,z] collision-local points";
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.BridgeEditedCount > 0) return "end the Blender session before recooking collision";
        if (Resolve(host, name, out var node) is { } unresolved) return unresolved;
        if (node!.Source is not IFrameNode frame || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "select a compiled convex or triangle collision volume";
        var edit = document.RecookCollision(frame, positions.Select(p => new Vector3(p[0], p[1], p[2])).ToArray(), out var refusal);
        if (edit is null) return refusal ?? "the collision could not be cooked";
        host.KeepCompiledMutation(node, edit);
        return null;
    }
}
