using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledLinkedProxyAuthoring(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "city", "bc_french_east_block_b_lod0.sds"));
        if (!archive.Exists) return;
        var doc = new CompiledSceneDocument(archive);
        if (doc.PreparedLinkedProxyGroups.Count != 0) return; // Preserve an existing user workspace.
        var frame = EntityCreationRows(doc.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(f => f.SceneIdentity == "guid:0F947BBBC7BC8574");
        var graph = doc.Graph;
        var inputFiles = graph.Resources.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Directory.GetFiles).Concat(new[] { archive.FullName }).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)), StringComparer.OrdinalIgnoreCase);
        bool FilesExact() => inputFiles.All(p => File.Exists(p.Key) && SHA256.HashData(File.ReadAllBytes(p.Key)).AsSpan().SequenceEqual(p.Value));
        var before = graph.Resources.ToDictionary(r => r, r => (r.Revision, r.IsDirty, Bytes: SHA256.HashData(r.ToArray())));
        var plan = doc.InspectLinkedProxyPreparation(frame, out var refusal) ?? throw new InvalidDataException(refusal);
        check("proxy cost inspection is read-only and reports all fine lowest-LOD draws", plan.FineFrames == 14 && plan.AfterVisuals == 231
            && plan.AfterTriangles == 37802 && plan.AfterDraws == 235 && plan.RequiredTextures == 59
            && before.All(p => p.Key.Revision == p.Value.Revision && p.Key.IsDirty == p.Value.IsDirty
                && SHA256.HashData(p.Key.ToArray()).AsSpan().SequenceEqual(p.Value.Bytes)) && FilesExact());

        var scene = graph.Objects[0x0f947bbbc7bc8574]; var components = graph.Field(scene, "m_Components")!;
        var modelEntry = graph.Items(components).Single(e => graph.Resolve(e)?.TypeId == 0x2da22400);
        var componentCount = components.Values.First(); byte[] originalCount = graph.Data(componentCount).ToArray();
        int nodes = graph.Nodes.Count; components.Children.Add(modelEntry);
        graph.Owner(componentCount).Replace(componentCount, BitConverter.GetBytes(BinaryPrimitives.ReadUInt32LittleEndian(originalCount) + 1));
        try
        {
            check("multiple model entries refuse proxy inspection before allocating IDs", doc.InspectLinkedProxyPreparation(frame, out refusal) is null
                && refusal?.Contains("exactly one", StringComparison.Ordinal) == true && graph.Nodes.Count == nodes && FilesExact());
        }
        finally { components.Children.RemoveAt(components.Children.Count - 1); graph.Owner(componentCount).Replace(componentCount, originalCount); }

        // A registered model definition not owned by any activated fine scene is not proof of a complete family.
        var copyType = typeof(CompiledSceneDocument).GetNestedType("GeometryCopy", BindingFlags.NonPublic)!;
        var orphanCopy = Activator.CreateInstance(copyType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [doc, null, null], null)!;
        try
        {
            copyType.GetMethod("CloneDefinition")!.Invoke(orphanCopy, [graph.Resolve(modelEntry), false, null]);
            copyType.GetMethod("Finish")!.Invoke(orphanCopy, null);
            nodes = graph.Nodes.Count;
            check("unrepresented native model participants refuse before further allocation", doc.InspectLinkedProxyPreparation(frame, out refusal) is null
                && refusal?.Contains("unrepresented", StringComparison.Ordinal) == true && graph.Nodes.Count == nodes && FilesExact());
        }
        finally { copyType.GetMethod("Detach")!.Invoke(orphanCopy, null); }

        plan = doc.InspectLinkedProxyPreparation(frame, out refusal) ?? throw new InvalidDataException(refusal);
        var materialCollection = (MaterialCollection)typeof(MafiaMaterials).GetProperty("Collection", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var coarse = EntityCreationRows(doc.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(f => f.SceneIdentity == "guid:5B403B2A66404E33");
        var atlas = (Material_v63)materialCollection.FindByHash(coarse.GeometryBinding!.DecodeLod(0)!.Parts[0].MaterialHash)!;
        var replacement = atlas.CollectTextures().First(name => TextureSearchIndex.FindPath(name) is not null);
        var meshSource = EntityCreationRows(doc.Roots).Select(row => (row.GeometrySource ?? row.Source) as ICompiledMeshSource)
            .First(s => s?.GeometryBinding is not null && ReferenceEquals(s.CanonicalFrame, frame))!;
        var material = (Material_v63)materialCollection.FindByHash(meshSource.GeometryBinding!.DecodeLod(0)!.Parts[0].MaterialHash)!;
        var slot = material.Textures.First(); material.Textures[slot.Key] = new HashName(replacement);
        try
        {
            nodes = graph.Nodes.Count;
            check("reviewed cost plans reject changed native material dependencies before cloning", doc.PrepareLinkedProxyTransforms(plan, out refusal) is null
                && graph.Nodes.Count == nodes && FilesExact());
        }
        finally { material.Textures[slot.Key] = slot.Value; }

        plan = doc.InspectLinkedProxyPreparation(frame, out refusal) ?? throw new InvalidDataException(refusal);
        IEditAction? edit = null; var original = frame.LocalTransform;
        string mapPath = Path.Combine(MafiaEnvironment.ResourcesFolder!, ".illusion", "linked-proxies", plan.ParentGroupId.ToString("X16") + ".json");
        try
        {
            edit = doc.PrepareLinkedProxyTransforms(plan, out refusal) ?? throw new InvalidDataException(refusal);
            var far = doc.GetLinkedProxyTransformTargets(frame).Single();
            foreach (var changed in new[] { original * Matrix4x4.CreateTranslation(.25f, .5f, 1), Matrix4x4.CreateRotationZ(.1f) * original,
                Matrix4x4.CreateScale(1.1f, .9f, 1.2f) * original })
            {
                if (doc.ValidateLinkedProxyTransform(frame, changed) is { } reason) throw new InvalidDataException(reason);
                frame.LocalTransform = changed;
                check("prepared proxy transforms preserve exact fine/far native world matrices", frame.WorldTransform == far.WorldTransform);
                frame.LocalTransform = original;
            }
            check("preparation grants transforms only and keeps generated roots read-only", doc.ValidateSceneCollectionMutation(frame) is not null
                && doc.ValidateLinkedProxyTransform(far, far.LocalTransform) is not null);
            var box = graph.Field(graph.Objects[plan.ParentGroupId], "m_WorldAABB")!;
            byte[] oldBox = graph.Data(box).ToArray(), badBox = (byte[])oldBox.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(badBox, BinaryPrimitives.ReadSingleLittleEndian(badBox) + .01f);
            graph.Owner(box).Replace(box, badBox);
            try
            {
                check("raw native LOD bounds mutations refuse pairing before either transform changes",
                    doc.ValidateLinkedProxyTransform(frame, original * Matrix4x4.CreateTranslation(.1f, 0, 0)) is not null
                    && frame.LocalTransform == original && far.WorldTransform == frame.WorldTransform);
            }
            finally { graph.Owner(box).Replace(box, oldBox); }

            foreach (string hookName in new[] { "ProxySaveAfterNativeWrite", "ProxySaveBeforeSnapshot" })
            {
                var hook = typeof(CompiledSceneDocument).GetProperty(hookName, BindingFlags.Instance | BindingFlags.NonPublic)!;
                hook.SetValue(doc, (Action)(() => throw new IOException("Injected proxy save failure.")));
                bool failed = false;
                try { doc.SaveWorkingCopy(); } catch (IOException) { failed = true; }
                finally { hook.SetValue(doc, null); }
                check("proxy multi-resource save restores native files on " + hookName, failed && FilesExact() && !File.Exists(mapPath));
            }
            frame.LocalTransform = original * Matrix4x4.CreateTranslation(.125f, 0, 0);
            doc.SaveWorkingCopy();
            var reopened = new CompiledSceneDocument(archive);
            var loaded = EntityCreationRows(reopened.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                .First(f => f.SceneIdentity == frame.SceneIdentity);
            var loadedFar = reopened.GetLinkedProxyTransformTargets(loaded).Single();
            check("workspace mapping reopens only after native snapshots and complete family validation", reopened.PreparedLinkedProxyGroups.Contains(plan.ParentGroupId)
                && reopened.LinkedProxyMappingWarnings.Count == 0 && loaded.WorldTransform == loadedFar.WorldTransform);
            byte[] mapping = File.ReadAllBytes(mapPath); File.Delete(mapPath);
            try
            {
                var missing = new CompiledSceneDocument(archive);
                var locked = EntityCreationRows(missing.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                    .First(f => f.SceneIdentity == frame.SceneIdentity);
                check("missing workspace metadata preserves native rendering but never authorizes paired writes", missing.PreparedLinkedProxyGroups.Count == 0
                    && missing.ValidateLinkedProxyTransform(locked, locked.LocalTransform) is not null);
            }
            finally { File.WriteAllBytes(mapPath, mapping); }
            frame.LocalTransform = original; edit.Undo(); edit = null; doc.SaveWorkingCopy();
            check("undo after save restores all original native resources manifests and mapping cleanup", FilesExact() && !File.Exists(mapPath));
        }
        finally
        {
            if (edit is not null) { frame.LocalTransform = original; edit.Undo(); doc.SaveWorkingCopy(); }
        }
        // Emulate a process stopping after journal creation, without launching a GUI or touching source SDS files.
        string folder = Path.GetDirectoryName(graph.Owner(scene).Path)!;
        var journalType = typeof(CompiledSceneDocument).Assembly.GetType("Illusion.Assets.Compiled.CompiledProxySaveJournal")!;
        var journal = Activator.CreateInstance(journalType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [new[] { folder }, new[] { mapPath }], null)!;
        string manifest = Path.Combine(folder, "SDSContent.xml"), extra = Path.Combine(folder, "itk-interrupted-proxy-save.bin");
        File.WriteAllBytes(manifest, [1, 2, 3]); File.WriteAllBytes(extra, [4, 5, 6]); File.WriteAllBytes(mapPath, [7, 8, 9]);
        ((IDisposable)journalType.GetField("_lease", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!).Dispose();
        _ = new CompiledSceneDocument(archive);
        check("interrupted proxy commit recovers before parsing any native manifest", FilesExact() && !File.Exists(extra) && !File.Exists(mapPath));
    }
}
