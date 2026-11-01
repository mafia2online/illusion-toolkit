using System.IO;
using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePlacementRest(Action<string, bool> check)
    {
        var rig = new SkeletonData
        {
            OwnerName = "native placement rest", Bones = [new("root", -1, Matrix4x4.CreateRotationY(.3f)),
                new("tip", 0, Matrix4x4.CreateRotationX(-.4f) * Matrix4x4.CreateTranslation(.2f, .1f, .2f))],
            InverseBind = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(-.1f, 0, 0)],
            World = Matrix4x4.Identity, Attachments = []
        };
        var positions = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0) };
        var weights = new[] { 64 / 255f, 191 / 255f, 0, 0, 128 / 255f, 126 / 255f, 0, 0, 200 / 255f, 55 / 255f, 0, 0 };
        var indices = new byte[] { 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0 };
        MeshData Source(byte[]? bones = null) => new()
        {
            Name = "native static rig", World = Matrix4x4.Identity, Positions = positions, Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX], Binormals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            UVs = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], Colors = [Vector4.One, Vector4.One, Vector4.One],
            Indices = [0, 1, 2], Parts = [new(0, 3, null)], Skeleton = rig, BoneIndices = bones ?? indices, BoneWeights = weights, LiveRest = [Matrix4x4.CreateRotationY(.2f),
                Matrix4x4.CreateRotationX(-.3f) * Matrix4x4.CreateTranslation(.1f, .2f, .3f)]
        };
        var source = Source(); var rest = CompiledPlacementRestMesh.Bake(source);
        check("native placement rest bake preserves channels/material identity and removes only dynamic skin dependency", !rest.IsSkinned
            && ReferenceEquals(rest.Parts, source.Parts) && ReferenceEquals(rest.UVs, source.UVs) && ReferenceEquals(rest.Colors, source.Colors)
            && rest.Positions.Length == source.Positions.Length && !rest.Positions.SequenceEqual(source.Positions));
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 96, 96);
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 4; renderer.Camera.LookAt(new(0, -2, 4), Vector3.Zero);
        renderer.AddMesh(source); renderer.Render(target); var skinned = RenderTargetReadback.Read(gpu, target);
        renderer.Clear(); renderer.AddMesh(rest); renderer.Render(target); var baked = RenderTargetReadback.Read(gpu, target);
        check("static placement rest geometry matches four-influence skinned GPU pixels with nonidentity bones and unnormalized weights",
            skinned.SequenceEqual(baked) && skinned.Chunk(4).Select(pixel => Convert.ToHexString(pixel)).Distinct().Count() > 1);
        var invalid = indices.ToArray(); invalid[0] = 127; bool refused = false;
        try { _ = CompiledPlacementRestMesh.Bake(Source(invalid)); } catch (NotSupportedException) { refused = true; }
        check("invalid used rest influence refuses without changing source channels", refused && source.Positions.SequenceEqual(positions)
            && source.BoneWeights!.SequenceEqual(weights) && source.BoneIndices!.SequenceEqual(indices));
        var archive = new FileInfo(Path.Combine(Illusion.Assets.MafiaEnvironment.ArchiveFolder, "city_crash", "bc_city_crash.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive);
        check("actual global placement snapshot admits all941 verified static rig placements", document.NativeTranslocatorPlacementCount == 410046
            && document.NativeTranslocatorRenderedPlacements == 396422 && document.NativeTranslocatorRestPosePlacements == 941 && !document.NativeTranslocatorRefusals.Keys.Any(reason => reason.Contains("Skinned native placement", StringComparison.Ordinal)));
        var changes = document.Graph.NodesOfType(0x1A24D577).SelectMany(root => document.Graph.Items(document.Graph.Field(root, "m_LocalModifications")))
            .SelectMany(mod => document.Graph.Items(document.Graph.Field(mod, "m_ChildrenMods")).Prepend(document.Graph.Field(mod, "m_RootMods")))
            .SelectMany(mod => document.Graph.Items(document.Graph.Field(mod, "m_ComponentMods"))).ToArray();
        check("all27325 actual component changes are strictly confined to native light data or projection textures", changes.Length == 27325
            && changes.All(change => CompiledPlacementLightChanges.IsGeometryIndependent(document.Graph, change)));
        check("light-only placement geometry is retained with explicit unsupported-light diagnostics", document.NativeTranslocatorLightOverridePlacements == 14566
            && document.NativeTranslocatorUnsupportedLightChanges > 0);
        var original = document.Graph.Owner(changes[0]);
        var independent = CompiledResource.FromBytes(original.ToArray()); var graph = new CompiledGraph([independent]);
        var copy = independent.Elements.First(element => element.TypeId == 0xA901241C && element.Event == 2);
        var component = graph.Field(copy, "m_ComponentType")!; var saved = graph.Data(component).ToArray();
        independent.Replace(component, BitConverter.GetBytes(0x360B668Du));
        bool rejectsHierarchy = !CompiledPlacementLightChanges.IsGeometryIndependent(graph, copy);
        independent.Replace(component, saved);
        var diffs = graph.Resolve(graph.Field(copy, "m_Diffs"))!;
        var compressed = graph.Field(diffs, "m_CompressedOperations")!; saved = graph.Data(compressed).ToArray();
        var altered = saved.ToArray(); BitConverter.GetBytes(0xA5E4ADE6ADCE844FUL).CopyTo(altered, 0); independent.Replace(compressed, altered);
        check("light exception rejects hierarchy component and scene transform attribute changes", rejectsHierarchy
            && !CompiledPlacementLightChanges.IsGeometryIndependent(graph, copy));
        independent.Replace(compressed, saved);
        check("native light classification is read-only and independent-copy mutations restore exact bytes", CompiledPlacementLightChanges.IsGeometryIndependent(graph, copy)
            && independent.ToArray().SequenceEqual(original.ToArray()));
        check("native static rig placements preserve every compiled working resource", document.Graph.Resources.All(resource => !resource.IsDirty
            && resource.ToArray().SequenceEqual(File.ReadAllBytes(resource.Path))));
    }
}
