using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxySkinHost(Action<string,bool> check)
    {
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_french_east_block_b_lod0.sds"));if(!archive.Exists)return;
        var doc=new CompiledSceneDocument(archive);if(doc.PreparedLinkedProxyGroups.Count!=0)return;
        string source=Path.Combine(MafiaEnvironment.ResourcesFolder!,"sds_retail","city_crash","bc_city_crash.sds","Generic_4029_6E1E5C7CA4DCBFA3.ires.compiled");
        if(!File.Exists(source))return;
        var files=doc.Graph.Resources.Select(resource=>Path.GetDirectoryName(resource.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(Directory.GetFiles)
            .ToDictionary(path=>path,path=>SHA256.HashData(File.ReadAllBytes(path)),StringComparer.OrdinalIgnoreCase);
        using var gpu=new GpuContext();using var host=new D3DImageHost();var renderer=new SceneRenderer(gpu){ShowSky=false};
        typeof(ViewportControl).GetProperty("Renderer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,renderer);using var target=new SharedRenderTarget(gpu,192,192);
        var document=new SceneNode("FrameResource","FrameResource",true){Source=doc};host.Roots.Add(document);
        var leaves=new List<SceneNode>();foreach(var native in doc.Roots)document.AddChild(SceneTree.BuildSceneTree(native,leaves));
        foreach(var leaf in leaves)if(leaf.Pending is{}data){var mesh=renderer.AddMesh(data);mesh.Owner=leaf;leaf.Mesh=mesh;leaf.Pending=null;}
        host.Tree.MeshCount=renderer.Meshes.Count;
        static IEnumerable<SceneNode> Walk(SceneNode row){yield return row;foreach(var child in row.Children)foreach(var descendant in Walk(child))yield return descendant;}
        var owner=Walk(document).First(row=>row.Source is CompiledSceneDocument.CompiledFrameNode frame&&frame.SceneIdentity=="guid:0F947BBBC7BC8574");
        var refusal=host.InspectCompiledLinkedProxy(owner,out var plan);if(refusal is not null||plan is null)throw new InvalidDataException(refusal);
        if(host.PrepareCompiledLinkedProxy(owner,plan)is{}prepare)throw new InvalidDataException(prepare);
        try
        {
            var groupRefusal=host.CreateLinkedProxyGroup(owner,"native_skin_host",out var parent);if(groupRefusal is not null||parent is null)throw new InvalidDataException(groupRefusal);
            foreach(var mesh in renderer.Meshes)mesh.Visible=false;
            var originalMeshes=renderer.Meshes.ToArray();int originalCount=host.Tree.MeshCount,history=host.History.UndoCount,objects=doc.ObjectCount;
            var native=doc.Graph.Resources.ToDictionary(resource=>resource,resource=>(Hash:SHA256.HashData(resource.ToArray()),resource.Revision,resource.IsDirty));long revision=doc.NativeLods!.BoundsRevision;
            host.Selection.SetSelection([parent],parent);int attempts=0;host.BeforePreparedSkinGpuAllocation=index=>{attempts=index;if(index==2)throw new InvalidOperationException("Injected second skin GPU allocation failure.");};
            string? failed;try{failed=host.ImportLinkedProxySkin(owner,parent,source,0x06A9E8CFD6F580AA,out _);}finally{host.BeforePreparedSkinGpuAllocation=null;}
            check("prepared skin second GPU failure leaves native epochs tree selection and history untouched",failed is not null&&attempts==2&&doc.ObjectCount==objects&&host.Tree.MeshCount==originalCount
                &&host.History.UndoCount==history&&ReferenceEquals(host.Selection.Active,parent)&&renderer.Meshes.SequenceEqual(originalMeshes)&&doc.NativeLods.BoundsRevision==revision
                &&native.All(pair=>pair.Key.Revision==pair.Value.Revision&&pair.Key.IsDirty==pair.Value.IsDirty&&SHA256.HashData(pair.Key.ToArray()).SequenceEqual(pair.Value.Hash)));
            var importRefusal=host.ImportLinkedProxySkin(owner,parent,source,0x06A9E8CFD6F580AA,out var imported);if(importRefusal is not null||imported is null)throw new InvalidDataException(importRefusal);
            var fine=(CompiledSceneDocument.CompiledFrameNode)imported.Source!;var far=(CompiledSceneDocument.CompiledFrameNode)doc.GetLinkedProxyTransformTargets(fine).Single();
            GpuMesh Mesh(CompiledSceneDocument.CompiledFrameNode frame)=>renderer.Meshes.First(mesh=>mesh.Owner is SceneNode row&&(row.GeometrySource??row.Source)is ICompiledMeshSource sourceMesh&&ReferenceEquals(sourceMesh.CanonicalFrame,frame));
            var near=Mesh(fine);var distant=Mesh(far);var center=(near.BoundsMin+near.BoundsMax)*.5f;var direction=Vector3.Normalize(new Vector3(3,-4,2));
            renderer.Camera.Orthographic=true;renderer.Camera.OrthoHeight=6;renderer.Camera.LookAt(center+direction*6,center);
            host.Selection.Select(null);
            byte[] Draw(){renderer.Render(target);return RenderTargetReadback.Read(gpu,target);}
            var baseline=Draw();
            check("prepared skin import installs canonical five-bone tree and near far GPU memberships",renderer.Meshes.Count==originalMeshes.Length+2&&ReferenceEquals(imported.Parent,parent)
                &&Walk(imported).Count(row=>row.Kind=="Bone")==5&&near.RenderVisible&&!distant.RenderVisible);
            var rig=doc.VehicleRig(fine)!;
            if(host.SetLinkedProxyBone(imported,2,rig.LocalRest(2)*Matrix4x4.CreateTranslation(1.2f,0,.2f))is{}poseRefusal)throw new InvalidDataException(poseRefusal);
            var posed=Draw();check("prepared weighted bone authoring changes actual near GPU skin pixels",!posed.SequenceEqual(baseline));
            renderer.Camera.LookAt(center+direction*1500,center);Mesh(far).Visible=false;var emptyFar=Draw();Mesh(far).Visible=true;var farPixels=Draw();
            check("authored weighted far counterpart renders under native far visibility",!Mesh(fine).RenderVisible&&Mesh(far).RenderVisible&&renderer.DrawnMeshes>0&&!farPixels.SequenceEqual(emptyFar));
            renderer.Camera.LookAt(center+direction*6,center);host.History.Undo();check("prepared weighted GPU rig undo restores exact near pixels",Draw().SequenceEqual(baseline));
            host.History.Redo();check("prepared weighted GPU rig redo restores exact authored pixels",Draw().SequenceEqual(posed));host.History.Undo();
            doc.SaveWorkingCopy();var fresh=new CompiledSceneDocument(archive);check("host imported skin independently reopens a valid complete family",fresh.LinkedProxyMappingWarnings.Count==0);
            host.History.Undo();check("prepared skin host undo removes both GPU roots and restores original order",renderer.Meshes.SequenceEqual(originalMeshes)&&host.Tree.MeshCount==originalCount&&!host.Tree.IsInScene(imported));
            host.History.Redo();check("prepared skin host redo restores both native GPU rows and canonical bone tree",host.Tree.IsInScene(imported)&&Walk(imported).Count(row=>row.Kind=="Bone")==5&&renderer.Meshes.Count==originalMeshes.Length+2);
        }
        finally
        {
            host.BeforePreparedSkinGpuAllocation=null;while(host.History.UndoCount>0)host.History.Undo();doc.SaveWorkingCopy();host.History.Clear();
        }
        check("prepared skin host history undo restores all original resources and manifests",files.All(pair=>File.Exists(pair.Key)&&SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)));
    }
}
