using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Materials;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Formats.Materials;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyHighMaterials(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var doc = new CompiledSceneDocument(archive); if (doc.PreparedLinkedProxyGroups.Count != 0) return;
        static IEnumerable<ISceneHierarchyNode> Rows(IEnumerable<ISceneHierarchyNode> roots)
        { foreach (var row in roots) { yield return row; foreach (var child in Rows(row.Children)) yield return child; } }
        var owner = Rows(doc.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First(frame => frame.SceneIdentity == "guid:0F947BBBC7BC8574");
        var plan = doc.InspectLinkedProxyPreparation(owner, out var refusal) ?? throw new InvalidDataException(refusal);
        var prep = doc.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
        var source = Rows(doc.Roots).Select(row => row.GeometrySource ?? row.Source).OfType<ICompiledMeshSource>()
            .First(mesh => ReferenceEquals(mesh.CanonicalFrame, owner) && mesh.SceneIdentity.EndsWith(":visual:1", StringComparison.Ordinal));
        var editor = (IMaterialSlotEditor)source;
        var before = editor.GetSlotTable().Select(row => row.ToArray()).ToArray();
        var ownership = doc.EnsurePreparedLinkedProxyMaterialOwnership(owner, source.GeometryBinding!.Visual, out refusal) ?? throw new InvalidDataException(refusal);
        var catalog = MafiaMaterialCatalog.Instance; string library = catalog.LibraryOf(before[0][0])!;
        string mtl = Path.Combine(MafiaEnvironment.GameRoot, "edit", "materials", library); byte[] mtlBytes = File.ReadAllBytes(mtl);
        ulong? created = null; bool slotApplied = false, ownershipApplied = true;
        string mapping = Path.Combine(MafiaEnvironment.ResourcesFolder!, ".illusion", "linked-proxies", plan.ParentGroupId.ToString("X16") + ".json");
        try
        {
            doc.SaveWorkingCopy();
            string farFolder = MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive));
            var farFiles = Directory.GetFiles(farFolder).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)), StringComparer.OrdinalIgnoreCase);
            created = catalog.CloneMaterial(before[0][0], "itk_high_proxy_material_" + Guid.NewGuid().ToString("N"), library)
                ?? throw new InvalidDataException(catalog.LastMutationRefusal ?? "Native material clone failed.");
            var table = before.Select(row => row.ToArray()).ToArray(); table[0][0] = created.Value;
            if (!editor.SetSlotTable(table)) throw new InvalidDataException(doc.LinkedProxyMaterialRefusal); slotApplied = true;
            check("brand-new higher-only material changes the selected fine LOD without changing its lowest table", editor.GetSlotTable()[0][0] == created.Value
                && editor.GetSlotTable()[^1].SequenceEqual(before[^1]) && before[0][0] != before[^1][0]);
            if (catalog.SaveDirty(out _) is { } saved) throw new InvalidDataException(saved);
            doc.SaveWorkingCopy();
            check("higher-only material dependency carry leaves every far native resource manifest and payload byte unchanged", farFiles.All(pair =>
                SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)) && Directory.GetFiles(farFolder).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(farFiles.Keys));
            var nativeLibrary = new MaterialCollection(); nativeLibrary.LoadLibrary(mtl);
            check("new higher-only native material is persisted in the actual copied MTL", nativeLibrary.FindByHash(created.Value) is not null);
            byte[] valid = File.ReadAllBytes(mapping); var json = JsonNode.Parse(valid)!.AsObject(); var fine = json["FineMaterialTextures"]!.AsObject();
            check("persisted fine material contract names the actual new higher-only dependency", fine.ContainsKey(created.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                && !json["MaterialTextures"]!.AsObject().ContainsKey(created.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            try
            {
                fine.Remove(created.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); File.WriteAllText(mapping, json.ToJsonString());
                check("omitting a highest-only material cannot bypass native persisted coverage validation", CompiledProxyMaterialGuard.ValidateMaterialMutation(created.Value) is not null
                    && catalog.SaveDirty(out int omittedSaved) is not null && omittedSaved == 0);
            }
            finally { File.WriteAllBytes(mapping, valid); }
            var incoming = before.Select(row => row.ToArray()).ToArray();
            check("higher-only dependency remains protected even without a far material reference", CompiledProxyMaterialGuard.ValidateMaterialMutation(created.Value) is not null
                && doc.ValidatePreparedLinkedProxyMaterialTable(owner, incoming) is null);
            if (!editor.SetSlotTable(before)) throw new InvalidDataException(doc.LinkedProxyMaterialRefusal); slotApplied = false;
            ownership.Undo(); ownershipApplied = false; doc.SaveWorkingCopy();
            if (catalog.RemoveMaterial(created.Value) is null) throw new InvalidDataException(catalog.LastMutationRefusal); created = null;
            if (catalog.SaveDirty(out _) is { } undoSaved) throw new InvalidDataException(undoSaved);
            prep.Undo(); doc.SaveWorkingCopy();
            check("higher-only dependency material and ownership undo restore original MTL bytes and remove workspace mapping", File.ReadAllBytes(mtl).SequenceEqual(mtlBytes) && !File.Exists(mapping));
        }
        finally
        {
            if (slotApplied) _ = editor.SetSlotTable(before);
            if (ownershipApplied) ownership.Undo();
            if (doc.PreparedLinkedProxyGroups.Count != 0) prep.Undo(); doc.SaveWorkingCopy();
            if (created is { } leftover) { _ = catalog.RemoveMaterial(leftover); _ = catalog.SaveDirty(out _); }
            if (!File.ReadAllBytes(mtl).SequenceEqual(mtlBytes)) File.WriteAllBytes(mtl, mtlBytes);
        }
    }
}
