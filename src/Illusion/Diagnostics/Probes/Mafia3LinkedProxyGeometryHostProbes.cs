using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestLinkedProxyGeometryHost(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        if (document.PreparedLinkedProxyGroups.Count != 0) return;
        using var gpu = new GpuContext(); using var host = new D3DImageHost(); var renderer = new SceneRenderer(gpu);
        typeof(ViewportControl).GetProperty("Renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, renderer);
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document }; host.Roots.Add(owner);
        var leaves = new List<SceneNode>();
        foreach (var native in document.Roots) owner.AddChild(SceneTree.BuildSceneTree(native, leaves));
        foreach (var leaf in leaves) if (leaf.Pending is { } pending)
        { var mesh = renderer.AddMesh(pending); mesh.Owner = leaf; leaf.Mesh = mesh; leaf.Pending = null; }
        host.Tree.MeshCount = renderer.Meshes.Count;
        var fine = owner.Children.Single(row => row.Source is CompiledSceneDocument.CompiledFrameNode frame && frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var originalMeshes = renderer.Meshes.ToArray(); var originalRows = owner.Children.ToArray();
        var originalBytes = document.Graph.Resources.ToDictionary(r => r, r => SHA256.HashData(r.ToArray()));
        var originalFiles = document.Graph.Resources.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var refusal = host.InspectCompiledLinkedProxy(fine, out var plan);
        if (refusal is not null || plan is null) throw new InvalidDataException(refusal);
        if (host.PrepareCompiledLinkedProxy(fine, plan) is { } rejected) throw new InvalidDataException(rejected);
        var frameSource = (IFrameNode)fine.Source!; var farFrame = document.GetLinkedProxyTransformTargets(frameSource).Single();
        SceneNode Row(IFrameNode frame, int lod, int ordinal = 1) => renderer.Meshes.Select(m => m.Owner).OfType<SceneNode>().Single(row =>
            (row.GeometrySource ?? row.Source) is ICompiledMeshSource source && ReferenceEquals(source.CanonicalFrame, frame)
            && source.SceneIdentity.EndsWith($":visual:{ordinal}", StringComparison.Ordinal) && row.Lod == lod);
        var fineBinding = EntityCreationRows(document.Roots).Select(n => (n.GeometrySource ?? n.Source) as ICompiledMeshSource)
            .First(s => s?.GeometryBinding is not null && ReferenceEquals(s.CanonicalFrame, frameSource) && s.SceneIdentity.EndsWith(":visual:1", StringComparison.Ordinal))!.GeometryBinding!;
        int lowest = fineBinding.LodCount - 1;
        var fineRow = Row(frameSource, lowest); var farRow = Row(farFrame, 0);
        var beforeMeshes = renderer.Meshes.ToArray(); var oldFine = fineRow.Mesh; var oldFar = farRow.Mesh;
        var baseline = document.Graph.Resources.ToDictionary(r => r, r => SHA256.HashData(r.ToArray()));
        bool BytesExact(Dictionary<Illusion.Formats.Compiled.CompiledResource, byte[]> snapshots) => snapshots.All(p => SHA256.HashData(p.Key.ToArray()).SequenceEqual(p.Value));
        var revisions = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.Revision);
        var expanded = fineBinding.Export(document, lowest, out refusal) ?? throw new InvalidDataException(refusal);
        for (int index = 0; index < expanded.Positions.Length; index++) expanded.Positions[index] += new Vector3(12, 0, 0);
        var replacement = fineBinding.TryApply(expanded, lowest, out refusal) ?? throw new InvalidDataException(refusal);
        int replacementAllocations = 0; bool replacementRefused = false;
        host.GeometryEditing.BeforeGeometryGpuAllocation = () =>
        { if (++replacementAllocations == 2) throw new InvalidOperationException("Injected replacement GPU allocation failure."); };
        try { host.GeometryEditing.ApplyPushBatch([new(fineRow, replacement)], [], [], null); }
        catch (InvalidOperationException) { replacementRefused = true; }
        finally { host.GeometryEditing.BeforeGeometryGpuAllocation = null; }
        check("requantized proxy replacement allocation failure preserves native revisions all LOD GPUs order and history", replacementRefused
            && replacementAllocations == 2 && replacement.AdditionalCompiledLodMeshes.Count > 0 && BytesExact(baseline)
            && revisions.All(pair => pair.Key.Revision == pair.Value) && beforeMeshes.SequenceEqual(renderer.Meshes)
            && ReferenceEquals(oldFine, fineRow.Mesh) && ReferenceEquals(oldFar, farRow.Mesh) && host.History.CanUndo && !host.History.CanRedo);
        var payload = fineBinding.Export(document, lowest, out refusal) ?? throw new InvalidDataException(refusal);
        int vertex = payload.LoopOrigIndex[0];
        for (int loop = 0; loop < payload.LoopOrigIndex.Length; loop++) if (payload.LoopOrigIndex[loop] == vertex) payload.LoopUvs[loop] += new Vector2(.125f, 0);
        var first = fineBinding.TryApply(payload, lowest, out refusal) ?? throw new InvalidDataException(refusal);
        var second = fineBinding.TryApply(payload, lowest, out refusal) ?? throw new InvalidDataException(refusal);
        bool mixedRefused = false;
        try { host.GeometryEditing.ApplyPushBatch([new(fineRow, first), new(fineRow, second)], [], [], null); }
        catch (EditRefusedException) { mixedRefused = true; }
        check("mixed paired bridge writes refuse before native GPU or history changes", mixedRefused && BytesExact(baseline)
            && beforeMeshes.SequenceEqual(renderer.Meshes) && ReferenceEquals(oldFine, fineRow.Mesh) && ReferenceEquals(oldFar, farRow.Mesh) && !host.History.CanRedo);
        int allocations = 0; bool allocationRefused = false;
        host.GeometryEditing.BeforeGeometryGpuAllocation = () => { if (++allocations == 2) throw new InvalidOperationException("Injected second GPU allocation failure."); };
        try { host.GeometryEditing.ApplyPushBatch([new(fineRow, first)], [], [], null); }
        catch (InvalidOperationException) { allocationRefused = true; }
        finally { host.GeometryEditing.BeforeGeometryGpuAllocation = null; }
        check("far GPU allocation failure leaves both native outputs renderer order and history untouched", allocationRefused && allocations == 2
            && BytesExact(baseline) && beforeMeshes.SequenceEqual(renderer.Meshes) && ReferenceEquals(oldFine, fineRow.Mesh)
            && ReferenceEquals(oldFar, farRow.Mesh) && !host.History.CanRedo);

        var otherBinding = EntityCreationRows(document.Roots).Select(n => (n.GeometrySource ?? n.Source) as ICompiledMeshSource)
            .First(s => s?.GeometryBinding is not null && ReferenceEquals(s.CanonicalFrame, frameSource) && s.SceneIdentity.EndsWith(":visual:0", StringComparison.Ordinal))!.GeometryBinding!;
        int otherLod = otherBinding.LodCount - 1; var otherRow = Row(frameSource, otherLod, 0);
        var otherPayload = otherBinding.Export(document, otherLod, out refusal) ?? throw new InvalidDataException(refusal);
        int otherVertex = otherPayload.LoopOrigIndex[0];
        for (int loop = 0; loop < otherPayload.LoopOrigIndex.Length; loop++) if (otherPayload.LoopOrigIndex[loop] == otherVertex) otherPayload.LoopUvs[loop] += new Vector2(.125f, 0);
        var other = otherBinding.TryApply(otherPayload, otherLod, out refusal) ?? throw new InvalidDataException(refusal);
        var fields = (IReadOnlyList<CompiledElement>)first.GetType().GetProperty("CompiledWriteFields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first)!;
        var field = fields.First(f => f.Event == 4 && f.DataSize > 100); var originalField = document.Graph.Data(field).ToArray();
        var invalidField = originalField.ToArray(); invalidField[^1] ^= 1; document.Graph.Owner(field).Replace(field, invalidField);
        var injected = document.Graph.Resources.ToDictionary(r => r, r => SHA256.HashData(r.ToArray())); bool staleApply = false;
        try { host.GeometryEditing.ApplyPushBatch([new(otherRow, other), new(fineRow, first)], [], [], null); }
        catch (InvalidDataException) { staleApply = true; }
        finally { document.Graph.Owner(field).Replace(field, originalField); }
        check("two nonconflicting results preflight stale native input before any batch write", staleApply && beforeMeshes.SequenceEqual(renderer.Meshes)
            && BytesExact(baseline) && !first.ConflictsWithCompiledWrites(other) && injected.Count == baseline.Count);
        host.GeometryEditing.ApplyPushBatch([new(otherRow, other), new(fineRow, first)], [], [], null);
        var batchMeshes = renderer.Meshes.ToArray(); var changedField = document.Graph.Data(field).ToArray();
        invalidField = changedField.ToArray(); invalidField[^1] ^= 1; document.Graph.Owner(field).Replace(field, invalidField);
        injected = document.Graph.Resources.ToDictionary(r => r, r => SHA256.HashData(r.ToArray())); bool staleUndo = false;
        try { host.History.Undo(); } catch (InvalidDataException) { staleUndo = true; }
        check("multi-action undo preflights every native output before any GPU or history swap", staleUndo && BytesExact(injected)
            && batchMeshes.SequenceEqual(renderer.Meshes) && host.History.CanUndo && !host.History.CanRedo);
        document.Graph.Owner(field).Replace(field, changedField); host.History.Undo();
        check("multi-action successful undo restores both complete geometry pairs", BytesExact(baseline) && beforeMeshes.SequenceEqual(renderer.Meshes));

        var beforeTransform = ((IFrameNode)fine.Source!).LocalTransform;
        var afterTransform = beforeTransform * Matrix4x4.CreateTranslation(.25f, 0, 0);
        var beforeFineWorld = oldFine!.World; var beforeFarWorld = oldFar!.World;
        host.GeometryEditing.ApplyPushBatch([new(fineRow, first)], [new(fine, beforeTransform, afterTransform)], [], null);
        var changedFine = fineRow.Mesh; var changedFar = farRow.Mesh;
        var farBinding = ((ICompiledMeshSource)(farRow.GeometrySource ?? farRow.Source)!).GeometryBinding!;
        var near = fineBinding.DecodeLod(lowest)!; var far = farBinding.DecodeLod(0)!;
        check("real editor bridge application swaps both native proxy GPUs in one history entry", !ReferenceEquals(oldFine, changedFine)
            && !ReferenceEquals(oldFar, changedFar) && near.UVs!.SequenceEqual(far.UVs!) && near.Tangents!.SequenceEqual(far.Tangents!));
        check("combined bridge geometry and ObjectMode transform refresh both GPU worlds", changedFine!.World.Translation == beforeFineWorld.Translation + new Vector3(.25f, 0, 0)
            && changedFar!.World.Translation == beforeFarWorld.Translation + new Vector3(.25f, 0, 0));
        var collection = (MaterialCollection)typeof(MafiaMaterials).GetProperty("Collection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var material = (Material_v63)collection.FindByHash(near.Parts[0].MaterialHash)!; var texture = material.Textures.First();
        material.Textures[texture.Key] = new HashName("injected_not_carried");
        var materialMeshes = renderer.Meshes.ToArray(); var materialBytes = document.Graph.Resources.ToDictionary(r => r, r => SHA256.HashData(r.ToArray()));
        bool materialRefused = false;
        try { host.History.Undo(); } catch (InvalidDataException) { materialRefused = true; } catch (EditRefusedException) { materialRefused = true; }
        finally { material.Textures[texture.Key] = texture.Value; }
        check("material-refused combined undo preserves both GPUs native bytes and history", materialRefused && BytesExact(materialBytes)
            && materialMeshes.SequenceEqual(renderer.Meshes) && ReferenceEquals(changedFine, fineRow.Mesh) && ReferenceEquals(changedFar, farRow.Mesh)
            && host.History.CanUndo && !host.History.CanRedo);
        host.History.Undo();
        check("paired editor geometry undo restores exact GPU draw order and native bytes", ReferenceEquals(oldFine, fineRow.Mesh)
            && ReferenceEquals(oldFar, farRow.Mesh) && beforeMeshes.SequenceEqual(renderer.Meshes) && BytesExact(baseline));
        host.History.Redo();
        check("paired editor geometry redo reuses both GPU replacements", ReferenceEquals(changedFine, fineRow.Mesh) && ReferenceEquals(changedFar, farRow.Mesh));
        check("combined bridge redo restores paired GPU worlds", changedFine.World.Translation == beforeFineWorld.Translation + new Vector3(.25f, 0, 0)
            && changedFar!.World.Translation == beforeFarWorld.Translation + new Vector3(.25f, 0, 0));
        host.History.Undo(); host.History.Undo();
        check("geometry then preparation undo restores all original native rows and GPU instances", originalRows.SequenceEqual(owner.Children)
            && originalMeshes.SequenceEqual(renderer.Meshes) && BytesExact(originalBytes));
        host.History.Redo(); host.History.Redo();
        check("preparation then geometry redo reacquires live proxy state and restores both GPUs", ReferenceEquals(changedFine, fineRow.Mesh)
            && ReferenceEquals(changedFar, farRow.Mesh) && document.ValidateLinkedProxyTransform(frameSource, afterTransform) is null);
        document.SaveWorkingCopy();
        var reopened = new CompiledSceneDocument(archive);
        check("redoing the complete preparation geometry lifecycle saves and reopens valid native mappings", reopened.PreparedLinkedProxyGroups.Count == 1
            && reopened.LinkedProxyMappingWarnings.Count == 0 && document.ValidateLinkedProxyTransform(frameSource, afterTransform) is null);
        host.History.Undo(); host.History.Undo(); document.SaveWorkingCopy();
        check("complete lifecycle redo save undo restores original native files and GPU instances", BytesExact(originalBytes)
            && originalFiles.All(pair => File.Exists(pair.Key) && SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value))
            && originalRows.SequenceEqual(owner.Children) && originalMeshes.SequenceEqual(renderer.Meshes));
        host.History.Clear();
    }
}
