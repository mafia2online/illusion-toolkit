using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Compiled;
using Illusion.Bridge.Payload;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyGeometry(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var doc = new CompiledSceneDocument(archive);
        if (doc.PreparedLinkedProxyGroups.Count != 0) return;
        var fineFrame = EntityCreationRows(doc.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(f => f.SceneIdentity == "guid:0F947BBBC7BC8574");
        var files = doc.Graph.Resources.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        bool ExactFiles() => files.All(p => File.Exists(p.Key) && SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value));
        CompiledMeshBinding Binding(Illusion.Domain.IFrameNode frame, int ordinal = 1) => EntityCreationRows(doc.Roots)
            .Select(n => (n.GeometrySource ?? n.Source) as ICompiledMeshSource)
            .First(s => s?.GeometryBinding is not null && ReferenceEquals(s.CanonicalFrame, frame)
                && s.SceneIdentity.EndsWith($":visual:{ordinal}", StringComparison.Ordinal))!.GeometryBinding!;
        MeshObjectPayload Export(CompiledMeshBinding binding, int lod) => binding.Export(doc, lod, out var error) ?? throw new InvalidDataException(error);
        static void EditUv(MeshObjectPayload payload)
        {
            int source = payload.LoopOrigIndex[0];
            for (int loop = 0; loop < payload.LoopOrigIndex.Length; loop++) if (payload.LoopOrigIndex[loop] == source) payload.LoopUvs[loop] += new Vector2(.125f, 0);
        }
        var source = Binding(fineFrame); int lowest = source.LodCount - 1;
        var notPrepared = Export(source, lowest); EditUv(notPrepared);
        check("linked geometry remains refused before explicit preparation", source.TryApply(notPrepared, lowest, out _) is null && ExactFiles());
        var plan = doc.InspectLinkedProxyPreparation(fineFrame, out var refusal) ?? throw new InvalidDataException(refusal);
        var preparation = doc.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
        BridgeMeshApplier.ApplyResult? applied = null;
        string packed = Path.Combine(".scratch", "proxy-geometry-durable-" + Guid.NewGuid().ToString("N"), Path.GetFileName(plan.DestinationArchive));
        try
        {
            var farFrame = doc.GetLinkedProxyTransformTargets(fineFrame).Single(); var target = Binding(farFrame);
            byte[][] NativeBytes() => doc.Graph.Resources.Select(r => r.ToArray()).ToArray();
            bool Same(byte[][] bytes) => bytes.Zip(doc.Graph.Resources, (a, r) => a.SequenceEqual(r.ToArray())).All(v => v);
            var baseline = NativeBytes();
            var colorSource = Binding(fineFrame, 0); var colorTarget = Binding(farFrame, 0);
            int colorLod = colorSource.LodCount - 1; var color = Export(colorSource, colorLod);
            int colorVertex = color.LoopOrigIndex[0];
            for (int loop = 0; loop < color.LoopOrigIndex.Length; loop++)
                if (color.LoopOrigIndex[loop] == colorVertex) color.LoopColors[loop] = new Vector4(.25f, .5f, .75f, 1);
            var colorResult = colorSource.TryApply(color, colorLod, out refusal) ?? throw new InvalidDataException(refusal);
            colorResult.ApplyNew();
            check("native BGRA color edits pair without losing logical color channels", Export(colorSource, colorLod).LoopColors.SequenceEqual(Export(colorTarget, 0).LoopColors));
            colorResult.RestoreOriginal();
            check("paired native color undo restores original bytes", Same(baseline));
            var model = doc.Graph.Nodes.First(node => doc.Graph.Items(doc.Graph.Field(node, "m_Visuals")).Any(item => ReferenceEquals(doc.Graph.Resolve(item), source.Visual)));
            var visualList = doc.Graph.Field(model, "m_Visuals")!; var item = doc.Graph.Items(visualList).First();
            var count = visualList.Values.First(); byte[] countBefore = doc.Graph.Data(count).ToArray();
            visualList.Children.Add(item); doc.Graph.Owner(count).Replace(count, BitConverter.GetBytes(BitConverter.ToUInt32(countBefore) + 1));
            try
            {
                var aliased = NativeBytes();
                check("changed or aliased native visual ownership refuses before either geometry writes", source.TryApply(notPrepared, lowest, out _) is null && Same(aliased));
            }
            finally { visualList.Children.RemoveAt(visualList.Children.Count - 1); doc.Graph.Owner(count).Replace(count, countBefore); }
            var position = Export(source, lowest); position.Positions[0] = new Vector3(float.NaN, 0, 0);
            check("paired geometry rejects nonfinite positions before native writes", source.TryApply(position, lowest, out _) is null && Same(baseline));
            var topology = Export(source, lowest); topology.LoopVertexIndices = topology.LoopVertexIndices[..^1];
            check("paired geometry rejects malformed topology atomically", source.TryApply(topology, lowest, out _) is null && Same(baseline));
            var authoredMaterial = Export(source, lowest); authoredMaterial.Materials[0].Authored = true;
            check("paired attributes retain material identity guards", source.TryApply(authoredMaterial, lowest, out _) is null && Same(baseline));
            var normal = Export(source, lowest); int normalVertex = normal.LoopOrigIndex[0];
            for (int loop = 0; loop < normal.LoopOrigIndex.Length; loop++)
                if (normal.LoopOrigIndex[loop] == normalVertex) normal.LoopNormals[loop] = Vector3.Normalize(normal.LoopNormals[loop] + new Vector3(.1f, .2f, .05f));
            var normalResult = source.TryApply(normal, lowest, out refusal) ?? throw new InvalidDataException(refusal);
            normalResult.ApplyNew();
            check("authored normals update both packed tangent frames without position changes", source.DecodeLod(lowest)!.Normals.SequenceEqual(target.DecodeLod(0)!.Normals)
                && source.DecodeLod(lowest)!.Positions.SequenceEqual(target.DecodeLod(0)!.Positions));
            normalResult.RestoreOriginal();
            check("authored normal undo is native byte exact", Same(baseline));
            var originalLow = Export(source, lowest); var originalFar = Export(target, 0);
            var high = Export(source, 0); EditUv(high);
            var highResult = source.TryApply(high, 0, out refusal) ?? throw new InvalidDataException(refusal);
            highResult.ApplyNew();
            check("higher LOD attributes leave lowest and far unchanged", highResult.AdditionalCompiledBindings.Count == 0
                && Export(source, lowest).LoopUvs.SequenceEqual(originalLow.LoopUvs) && Export(target, 0).LoopUvs.SequenceEqual(originalFar.LoopUvs));
            highResult.RestoreOriginal();
            check("higher LOD attribute undo restores native bytes", Same(baseline));
            var payload = Export(source, lowest); EditUv(payload);
            applied = source.TryApply(payload, lowest, out refusal) ?? throw new InvalidDataException(refusal);
            var duplicate = source.TryApply(payload, lowest, out refusal) ?? throw new InvalidDataException(refusal);
            check("paired bridge results conflict across the full near and far write set", applied.ConflictsWithCompiledWrites(duplicate)
                && applied.AdditionalCompiledBindings.Count == 1 && Same(baseline));
            applied.ApplyNew();
            var nearMesh = source.DecodeLod(lowest)!; var farMesh = target.DecodeLod(0)!;
            check("lowest attributes update far UV normal and tangent arrays exactly", nearMesh.UVs!.SequenceEqual(farMesh.UVs!)
                && nearMesh.Normals.SequenceEqual(farMesh.Normals) && nearMesh.Tangents!.SequenceEqual(farMesh.Tangents!)
                && nearMesh.Binormals!.SequenceEqual(farMesh.Binormals!) && nearMesh.Positions.SequenceEqual(farMesh.Positions));
            var changed = NativeBytes(); bool staleRefused = false;
            try { duplicate.ApplyNew(); } catch (InvalidDataException) { staleRefused = true; }
            check("stale paired bridge result refuses before either output changes", staleRefused && Same(changed));
            doc.SaveWorkingCopy();
            var reopened = new CompiledSceneDocument(archive);
            check("paired geometry save reopens with validated workspace mapping", reopened.PreparedLinkedProxyGroups.Count == 1 && reopened.LinkedProxyMappingWarnings.Count == 0);
            Directory.CreateDirectory(Path.GetDirectoryName(packed)!);
            using (var stream = File.Create(packed)) SdsArchive.Pack(MafiaEnvironment.ExtractedDir(new FileInfo(plan.DestinationArchive)), MafiaEnvironment.Profile)
                .Save(stream, new SdsWriteOptions { Compress = true });
            var farDocument = new CompiledSceneDocument(new FileInfo(packed));
            var independent = EntityCreationRows(farDocument.Roots).Select(n => (n.GeometrySource ?? n.Source) as ICompiledMeshSource)
                .First(s => s?.GeometryBinding?.Visual.ObjectId == target.Visual.ObjectId)!.GeometryBinding!.DecodeLod(0)!;
            check("compressed far-only paired attributes retain native geometry and tangent frames", independent.UVs!.SequenceEqual(farMesh.UVs!)
                && independent.Positions.SequenceEqual(farMesh.Positions) && independent.Tangents!.SequenceEqual(farMesh.Tangents!)
                && independent.Binormals!.SequenceEqual(farMesh.Binormals!));
            applied.RestoreOriginal(); applied = null;
            check("paired attribute undo restores every original native resource", Same(baseline));
            preparation.Undo(); doc.SaveWorkingCopy();
            check("paired geometry save undo restores every original resource and manifest", ExactFiles());
        }
        finally
        {
            if (applied is not null) applied.RestoreOriginal();
            if (doc.PreparedLinkedProxyGroups.Count != 0) { preparation.Undo(); doc.SaveWorkingCopy(); }
        }
    }
}
