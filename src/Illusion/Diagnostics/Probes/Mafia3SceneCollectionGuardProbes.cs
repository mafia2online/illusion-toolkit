using System.IO;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Bridge.Payload;
using Illusion.Domain;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledSceneCollectionGuards(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_bayou_block_a_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        var frames = EntityCreationRows(document.Roots).Select(row => row.Source)
            .OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct().ToArray();
        var blocked = frames.First(frame => document.ValidateSceneCollectionMutation(frame) is not null && frame.GeometryBinding is not null);
        var train = frames.Single(frame => frame.Name == "train_car_a_v1_bv01_00");
        var before = document.Graph.Resources.ToDictionary(resource => resource,
            resource => (Bytes: SHA256.HashData(resource.ToArray()), resource.Revision, resource.IsDirty));
        var paths = document.Graph.Resources.Select(resource => resource.Path)
            .Concat(document.Graph.Resources.Select(resource => Path.Combine(Path.GetDirectoryName(resource.Path)!, "SDSContent.xml")))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        bool Exact() => before.All(pair => pair.Key.Revision == pair.Value.Revision && pair.Key.IsDirty == pair.Value.IsDirty
                && SHA256.HashData(pair.Key.ToArray()).AsSpan().SequenceEqual(pair.Value.Bytes))
            && paths.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).AsSpan().SequenceEqual(pair.Value));
        check("typed coarse/fine linkage protects existing grouped frames and preserves ungrouped train authoring",
            frames.Count(frame => document.ValidateSceneCollectionMutation(frame) is not null) >= 32
            && document.ValidateSceneCollectionMutation(train) is null);
        var original = blocked.LocalTransform; var moved = original; moved.M41 += .01f;
        bool refused = false;
        try { blocked.LocalTransform = moved; }
        catch (EditRefusedException ex) { refused = ex.Message.Contains("LOD group", StringComparison.Ordinal); }
        check("linked LOD transform refuses before every resource revision or byte changes", refused && blocked.LocalTransform == original && Exact());
        var binding = blocked.GeometryBinding!;
        var apply = binding.TryApply(new MeshObjectPayload { Id = "probe:readonly", Name = blocked.Name }, 0, out var meshReason);
        var isolate = document.IsolateVisualGeometry(binding.Visual, out var isolateReason);
        check("linked LOD geometry push and isolation refuse before allocation or mutation",
            binding.DecodeLod(0) is not null && apply is null && meshReason?.Contains("LOD group", StringComparison.Ordinal) == true
            && isolate is null && isolateReason?.Contains("LOD group", StringComparison.Ordinal) == true && Exact());
        var table = blocked.GetSlotTable();
        if (table.Count > 0 && table[0].Length > 0)
        {
            table[0][0] ^= 1;
            check("linked LOD material slots remain readable but cannot be reassigned", !blocked.SetSlotTable(table) && Exact());
        }
        var duplicate = document.Duplicate(blocked, out var duplicateReason);
        check("linked LOD duplicate reparent and removal all refuse atomically", !document.CanDuplicate(blocked)
            && duplicate is null && duplicateReason?.Contains("LOD group", StringComparison.Ordinal) == true
            && !document.Reparent(blocked, train) && document.CaptureDetachment([blocked]) is null && Exact());
        var linkedRig = frames.Where(frame => document.ValidateSceneCollectionMutation(frame) is not null)
            .Select(document.VehicleRig).OfType<CompiledVehicleRig>().First();
        var bone = linkedRig.BoneSource(0); var boneOriginal = bone.LocalTransform; var boneMoved = boneOriginal; boneMoved.M41 += .01f;
        bool boneRefused = false, appendRefused = false;
        try { bone.LocalTransform = boneMoved; }
        catch (EditRefusedException ex) { boneRefused = ex.Message.Contains("LOD group", StringComparison.Ordinal); }
        try { _ = linkedRig.Append("itk_proxy_guard_probe", 0, System.Numerics.Matrix4x4.Identity, 1); }
        catch (EditRefusedException ex) { appendRefused = ex.Message.Contains("LOD group", StringComparison.Ordinal); }
        check("linked LOD bone adapters and authored rig append refuse before any resource changes",
            boneRefused && appendRefused && bone.LocalTransform == boneOriginal
            && document.ValidateSceneCollectionMutation(bone)?.Contains("LOD group", StringComparison.Ordinal) == true && Exact());
        VerifySceneCollectionHostGuards(document, blocked, train, check);
        document.SaveWorkingCopy();
        check("refused LOD authoring leaves saved compiled resources and manifests byte exact", Exact());
        var water = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city_univers", "bc_city_univers.sds"));
        if (water.Exists)
        {
            var waterDocument = new CompiledSceneDocument(water);
            var main = EntityCreationRows(waterDocument.Roots).Select(row => row.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().Distinct()
                .Single(frame => frame.Name == "bc00_water_main");
            check("verified ungrouped native water remains transform and geometry authorable",
                waterDocument.ValidateSceneCollectionMutation(main) is null && main.GeometryBinding is not null);
        }
    }
}
