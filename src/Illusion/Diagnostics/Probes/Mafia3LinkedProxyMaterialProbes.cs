using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Materials;
using Illusion.Domain;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyMaterials(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var doc = new CompiledSceneDocument(archive);
        if (doc.PreparedLinkedProxyGroups.Count != 0) return;
        var fine = EntityCreationRows(doc.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(f => f.SceneIdentity == "guid:0F947BBBC7BC8574");
        var plan = doc.InspectLinkedProxyPreparation(fine, out var refusal) ?? throw new InvalidDataException(refusal);
        var collection = (MaterialCollection)typeof(MafiaMaterials).GetProperty("Collection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var source = EntityCreationRows(doc.Roots).Select(n => (n.GeometrySource ?? n.Source) as ICompiledMeshSource)
            .First(s => s?.GeometryBinding is not null && ReferenceEquals(s.CanonicalFrame, fine))!;
        ulong hash = source.GeometryBinding!.DecodeLod(0)!.Parts[0].MaterialHash;
        var material = (Material_v63)collection.FindByHash(hash)!; var texture = material.Textures.First();
        var catalog = MafiaMaterialCatalog.Instance;
        var library = collection.Libraries.Values.First(lib => lib.Materials.ContainsKey(hash));
        var oldFlags = material.Flags;
        var parameter = material.Parameters.First(p => p.Paramaters.Length > 0 && p.Paramaters.All(float.IsFinite));
        float[] oldParameter = parameter.Paramaters.ToArray();
        // External absolute archives share the current material workspace even though their SDS path is elsewhere.
        string external = Path.GetFullPath(Path.Combine(".scratch", "proxy-material-external-" + Guid.NewGuid().ToString("N")));
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string relative in new[] { "city/bc_french_east_block_b_lod0.sds", "city/bc_french_east_block_b_lod1.sds",
                "city/bc_french_east_block_b_lod2.sds", "city_crash/bc_city_crash.sds" })
            {
                string from = Path.Combine(MafiaEnvironment.ArchiveFolder, relative), to = Path.Combine(external, "sds_retail", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(from, to);
                extracted.Add(MafiaEnvironment.ExtractedDir(new FileInfo(to)));
            }
            var other = new CompiledSceneDocument(new FileInfo(Path.Combine(external, "sds_retail/city/bc_french_east_block_b_lod0.sds")));
            var otherFine = EntityCreationRows(other.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                .First(f => f.SceneIdentity == fine.SceneIdentity);
            var otherPlan = other.InspectLinkedProxyPreparation(otherFine, out refusal) ?? throw new InvalidDataException(refusal);
            var otherEdit = other.PrepareLinkedProxyTransforms(otherPlan, out refusal) ?? throw new InvalidDataException(refusal);
            try
            {
                check("external absolute unsaved proxies protect the current material workspace", !other.SourceArchive.FullName.StartsWith(MafiaEnvironment.ArchiveFolder, StringComparison.OrdinalIgnoreCase)
                    && CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is not null && !catalog.SetTexture(hash, "S000", "itk_external_missing_texture"));
            }
            finally { otherEdit.Undo(); }
        }
        finally
        {
            foreach (string folder in extracted.Where(Directory.Exists)) Directory.Delete(folder, true);
            if (Directory.Exists(external)) Directory.Delete(external, true);
        }
        var files = doc.Graph.Resources.Select(r => r.Path).Concat(doc.Graph.Resources.Select(r => Path.Combine(Path.GetDirectoryName(r.Path)!, "SDSContent.xml")))
            .Append(library.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        bool Exact() => files.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).AsSpan().SequenceEqual(p.Value));
        IEditAction? edit = null; ulong? cloned = null;
        string path = Path.Combine(MafiaEnvironment.ResourcesFolder!, ".illusion", "linked-proxies", plan.ParentGroupId.ToString("X16") + ".json");
        try
        {
            edit = doc.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
            check("unsaved prepared proxies protect global material texture edits", CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is not null
                && !catalog.SetTexture(hash, "S000", "itk_missing_texture") && catalog.LastMutationRefusal is not null && Exact());
            check("prepared material identity and sampler mutations refuse without writing", catalog.RemoveMaterial(hash) is null
                && catalog.RenameMaterial(hash, "itk_forbidden_rename") is null && !catalog.AddSampler(hash, "S999")
                && catalog.RemoveSampler(hash, "S000") is null && !catalog.RestoreSampler(hash, new object()) && Exact());
            cloned = catalog.CloneMaterial(hash, "itk_proxy_guard_" + Guid.NewGuid().ToString("N"), Path.GetFileName(library.Name));
            check("unrelated material copies remain texture-editable", cloned is { } clone
                && catalog.SetTexture(clone, "S000", texture.Value.String) && catalog.RemoveMaterial(clone) is not null);
            cloned = null;
            check("prepared material flags remain editable without changing texture dependencies", catalog.SetFlags(hash, (MaterialFlags)((uint)oldFlags ^ 1))
                && CompiledProxyMaterialGuard.ValidateMaterialSave() is null);
            catalog.SetFlags(hash, oldFlags);
            var changedParameter = (float[])oldParameter.Clone(); changedParameter[0] += .001f;
            check("parameter-only edits remain usable without changing dependencies", catalog.SetParameter(hash, parameter.ID, changedParameter)
                && CompiledProxyMaterialGuard.ValidateMaterialSave() is null && catalog.SaveDirty(out _) is null);
            if (!parameter.Paramaters.SequenceEqual(oldParameter)) catalog.SetParameter(hash, parameter.ID, oldParameter);
            check("parameter and flags undo save preserves the source library", catalog.SaveDirty(out _) is null);
            doc.SaveWorkingCopy();
            byte[] mapping = File.ReadAllBytes(path);
            var written = files.Keys.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            material.Textures[texture.Key] = new HashName("itk_missing_far_dependency");
            try
            {
                bool nativeRefused = false;
                try { doc.SaveWorkingCopy(); } catch (InvalidOperationException) { nativeRefused = true; }
                var reopened = new CompiledSceneDocument(archive);
                check("direct catalog bypass refuses MTL-only and native saves and invalidates mapping authoring", catalog.SaveDirty(out int saved) is not null
                    && saved == 0 && nativeRefused && reopened.PreparedLinkedProxyGroups.Count == 0 && reopened.LinkedProxyMappingWarnings.Count > 0
                    && written.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).AsSpan().SequenceEqual(p.Value)));
            }
            finally { material.Textures[texture.Key] = texture.Value; }
            edit.Undo(); edit = null; // The persisted mapping must protect the material even without an active prepared document.
            check("persisted proxy mappings protect material-only editing with no active prepared state", doc.PreparedLinkedProxyGroups.Count == 0
                && CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is not null && !catalog.SetTexture(hash, "S000", "itk_missing_far_dependency"));
            foreach (string field in new[] { "Pairs", "Files" })
            {
                var malformed = JsonNode.Parse(mapping)!.AsObject(); malformed[field] = null; File.WriteAllText(path, malformed.ToJsonString());
                var reopened = new CompiledSceneDocument(archive);
                check("null " + field + " metadata refuses safely without crashing document load", reopened.PreparedLinkedProxyGroups.Count == 0
                    && reopened.LinkedProxyMappingWarnings.Count > 0 && CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is not null);
            }
            var partial = JsonNode.Parse(mapping)!.AsObject(); partial["MaterialTextures"]!.AsObject().Remove(hash.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(path, partial.ToJsonString());
            check("omitted material signatures cannot bypass persisted native coverage validation", CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is not null
                && catalog.SaveDirty(out int omittedSaved) is not null && omittedSaved == 0);
            File.WriteAllBytes(path, mapping); doc.SaveWorkingCopy();
            check("unpreparing and saving releases material guards with native and MTL bytes restored", Exact() && !File.Exists(path)
                && CompiledProxyMaterialGuard.ValidateMaterialMutation(hash) is null);
        }
        finally
        {
            material.Textures[texture.Key] = texture.Value; material.Flags = oldFlags;
            if (!parameter.Paramaters.SequenceEqual(oldParameter)) catalog.SetParameter(hash, parameter.ID, oldParameter);
            if (cloned is { } leftover) catalog.RemoveMaterial(leftover);
            if (edit is not null) { edit.Undo(); doc.SaveWorkingCopy(); }
        }
    }
}
