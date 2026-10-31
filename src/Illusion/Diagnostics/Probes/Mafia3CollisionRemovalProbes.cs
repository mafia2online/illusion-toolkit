using System.Buffers.Binary;
using System.IO;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledCollisionRemoval(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> roots)
        {
            foreach (var root in roots) { yield return root; foreach (var child in Walk(root.Children)) yield return child; }
        }
        var owner = Walk(document.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(f => document.CollisionVolumes(f).Count > 0);
        int count = document.CollisionVolumes(owner).Count, total = document.CollisionMeshes().Count;
        var snapshot = document.Graph.Resources.ToDictionary(r => r.Path, r => File.ReadAllBytes(r.Path));
        byte[] archiveBytes = File.ReadAllBytes(archive.FullName);
        var edits = new List<IEditAction>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var edit = document.RemoveCollision(owner, 0, out var refusal) ?? throw new InvalidOperationException(refusal);
                edits.Add(edit);
                check($"collision removal {i + 1} updates canonical volumes", document.CollisionVolumes(owner).Count == count - i - 1);
                check($"collision removal {i + 1} updates overlay", document.CollisionMeshes().Count == total - i - 1);
                document.SaveWorkingCopy();
                var reload = new CompiledSceneDocument(archive);
                var reloadedOwner = Walk(reload.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
                    .First(f => f.SceneIdentity == owner.SceneIdentity);
                check($"collision removal {i + 1} survives reload", reload.CollisionVolumes(reloadedOwner).Count == count - i - 1);
                bool helpersValid = true;
                var graph = reload.Graph;
                foreach (var component in graph.Nodes.Where(n => n.TypeId == 0x2b7b4775))
                {
                    var bones = graph.Items(graph.Field(component, "m_ElementDefs"))
                        .GroupBy(e => BinaryPrimitives.ReadUInt16LittleEndian(graph.Data(graph.Field(e, "m_Data")!)[14..]))
                        .ToDictionary(g => g.Key, g => g.Count());
                    var helpers = graph.Items(graph.Field(component, "m_BoneCollisionHelperMap"));
                    helpersValid &= helpers.Count == bones.Count && helpers.All(h =>
                        bones.TryGetValue(checked((ushort)graph.Integer(graph.Field(h, "m_BoneId")!)), out int slots) &&
                        graph.Items(graph.Field(h, "m_Elements")).Count == slots);
                }
                check($"collision removal {i + 1} retains exact bone helper counts", helpersValid);
            }
            var packed = SdsArchive.Pack(MafiaEnvironment.ExtractedDir(archive), MafiaEnvironment.Profile);
            using var stream = new MemoryStream();
            packed.Save(stream, new SdsWriteOptions { Compress = true }); stream.Position = 0;
            SdsArchive.Load(stream).Extract(MafiaEnvironment.ExtractedDir(archive));
            check("collision removal compressed build/fresh extraction", new CompiledSceneDocument(archive).CollisionMeshes().Count == total - count);
            foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo();
            document.SaveWorkingCopy();
            check("collision removal exact all-resource undo", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            foreach (var edit in edits) edit.Redo();
            document.SaveWorkingCopy();
            check("collision removal redo reload", new CompiledSceneDocument(archive).CollisionMeshes().Count == total - count);
            foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo();
            document.SaveWorkingCopy();
            check("collision removal redo/undo exact", snapshot.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)));
            check("collision removal input archive untouched", File.ReadAllBytes(archive.FullName).SequenceEqual(archiveBytes));

            var cc = document.Graph.Nodes.First(n => n.TypeId == 0x2b7b4775 && document.Graph.Items(document.Graph.Field(n, "m_ElementDefs")).Count > 0);
            var helper = document.Graph.Items(document.Graph.Field(cc, "m_BoneCollisionHelperMap")).First();
            var id = document.Graph.Field(helper, "m_BoneId")!;
            var resource = document.Graph.Owner(id);
            byte[] original = document.Graph.Data(id).ToArray();
            try
            {
                resource.Replace(id, [0xfe, 0xff]);
                byte[] beforeRefusal = resource.ToArray();
                check("collision removal refuses unmatched helper references", document.RemoveCollision(owner, 0, out var why) is null && !string.IsNullOrEmpty(why));
                check("collision refusal preserves preexisting edits", resource.ToArray().SequenceEqual(beforeRefusal));
            }
            finally { resource.Replace(id, original); }
        }
        finally { foreach (var (path, bytes) in snapshot) File.WriteAllBytes(path, bytes); }
    }
}
