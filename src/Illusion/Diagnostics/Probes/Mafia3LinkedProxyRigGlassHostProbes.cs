using System.IO;
using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestPreparedProxyRigGlassHost(Action<string,bool> check)
    {
        var archive=new FileInfo(Path.Combine(MafiaEnvironment.CityFolder,"bc_french_east_block_b_lod0.sds")); if(!archive.Exists)return;
        static IEnumerable<ISceneHierarchyNode> NativeWalk(IEnumerable<ISceneHierarchyNode> rows)
        {foreach(var row in rows){yield return row;foreach(var child in NativeWalk(row.Children))yield return child;}}
        var setup=new CompiledSceneDocument(archive);if(setup.PreparedLinkedProxyGroups.Count!=0)return;
        try
        {
            var document=new CompiledSceneDocument(archive);
            using var gpu=new GpuContext();using var host=new D3DImageHost();var renderer=new SceneRenderer(gpu){ShowSky=false};
            typeof(ViewportControl).GetProperty("Renderer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,renderer);
            using var target=new SharedRenderTarget(gpu,192,192);
            var owner=new SceneNode("FrameResource","FrameResource",true){Source=document};host.Roots.Add(owner);
            var leaves=new List<SceneNode>();foreach(var native in document.Roots)owner.AddChild(SceneTree.BuildSceneTree(native,leaves));
            foreach(var leaf in leaves)if(leaf.Pending is {} data){var mesh=renderer.AddMesh(data);mesh.Owner=leaf;leaf.Mesh=mesh;leaf.Pending=null;}
            static IEnumerable<SceneNode> Walk(SceneNode row){yield return row;foreach(var child in row.Children)foreach(var descendant in Walk(child))yield return descendant;}
            SceneNode Frame(string id)=>Walk(owner).First(row=>row.Source is CompiledSceneDocument.CompiledFrameNode frame&&frame.SceneIdentity==id);
            var fine=Frame("guid:0F947BBBC7BC8574");var dock=fine;var frame=(CompiledSceneDocument.CompiledFrameNode)dock.Source!;
            var refusal=host.InspectCompiledLinkedProxy(fine,out var plan);if(refusal is not null||plan is null)throw new InvalidDataException(refusal);
            if(host.PrepareCompiledLinkedProxy(fine,plan)is {} rejected)throw new InvalidDataException(rejected);
            try
            {
                foreach(var mesh in renderer.Meshes)mesh.Visible=false;
                host.CarCollisionEditing.ShowShapes=true;
                var paneMesh=renderer.Meshes.First(mesh=>mesh.Owner is SceneNode row && (row.GeometrySource??row.Source) is ICompiledMeshSource source && ReferenceEquals(source.CanonicalFrame,frame) && source.GeometryBinding is {} binding && BinaryPrimitives.ReadUInt16LittleEndian(document.Graph.Data(document.Graph.Field(binding.Visual,"m_Data")!)[46..])==4);
                var center=(paneMesh.BoundsMin+paneMesh.BoundsMax)*.5f;
                renderer.Camera.Orthographic=true;renderer.Camera.OrthoHeight=4;renderer.Camera.LookAt(center+new Vector3(0,-6,2),center);
                byte[] Draw(){renderer.Render(target);return RenderTargetReadback.Read(gpu,target);}
                var before=Draw();var rig=document.VehicleRig(frame)!;int history=host.History.UndoCount;
                host.CarCollisionEditing.ShowShapes=false;
                if(host.SetLinkedProxyBone(dock,4,rig.LocalRest(4)*Matrix4x4.CreateTranslation(1,0,0))is {} editRefusal)throw new InvalidDataException(editRefusal);
                host.CarCollisionEditing.ShowShapes=true;var changed=Draw();
                check("retail static glass rig host updates physical wireframe pixels",!before.SequenceEqual(changed)&&host.History.UndoCount==history+1);
                host.History.Undo();check("retail static glass rig undo restores exact physical overlay pixels",Draw().SequenceEqual(before));
                host.History.Redo();check("retail static glass rig redo restores exact authored overlay pixels",Draw().SequenceEqual(changed));
                host.History.Undo();
            }
            finally{while(host.History.UndoCount>0)host.History.Undo();document.SaveWorkingCopy();host.History.Clear();}
        }
        finally{setup.SaveWorkingCopy();}
    }
}
