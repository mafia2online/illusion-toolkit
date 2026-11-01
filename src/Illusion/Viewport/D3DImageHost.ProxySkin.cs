using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal Action<int>? BeforePreparedSkinGpuAllocation { get; set; }
    internal string? ImportLinkedProxySkin(SceneNode owner,SceneNode parent,string resource,ulong guid,out SceneNode? created)
    {
        created=null;
        if(IsLoading||Rnd is null||BridgeEditedCount>0)return "Wait for loading and end the Blender session before importing a prepared skin.";
        if(!Tree.IsInScene(owner)||!Tree.IsInScene(parent)||owner.Source is not IFrameNode source||parent.Source is not IFrameNode target
            ||owner.OwningDocumentNode()is not{Source:CompiledSceneDocument document}documentNode||!ReferenceEquals(parent.OwningDocumentNode(),documentNode))
            return "Select a prepared fine owner and a Kit in the same document.";
        var resources=document.Graph.Resources.Select(native=>native.CaptureRevertedMutationCheckpoint()).ToArray();
        var bounds=document.NativeLods?.CaptureRevertedBoundsCheckpoint();var cache=document.CaptureRevertedRigCacheCheckpoint();var carries=document.CaptureRevertedCarryCheckpoint();
        var mutation=document.ImportPreparedSkinnedProp(resource,guid,source,target,out var refusal);
        if(mutation is null)return refusal??"The native prepared skin could not be imported.";
        var meshes=new List<GpuMesh>();IEditAction? rigs=null;
        SceneNode row;SceneNode[] additional;
        try
        {
            var leaves=new List<SceneNode>();row=SceneTree.BuildSceneTree(mutation.Root,leaves);
            additional=mutation.AdditionalRoots.Select(native=>SceneTree.BuildSceneTree(native,leaves)).ToArray();
            foreach(var leaf in leaves)if(leaf.Pending is{}pending)
            {
                BeforePreparedSkinGpuAllocation?.Invoke(meshes.Count+1);
                var mesh=Rnd.CreateMeshGpu(pending);meshes.Add(mesh);mesh.Owner=leaf;leaf.Mesh=mesh;leaf.Pending=null;
            }
            var targets=new[]{mutation.Root}.Concat(mutation.AdditionalRoots).Select(native=>native.Source).OfType<IFrameNode>().ToHashSet();
            BeforePreparedSkinGpuAllocation?.Invoke(meshes.Count+1);
            rigs=Streamer.PrepareCompiledRigAuthoringRefresh(documentNode,targets,new[]{row}.Concat(additional).ToArray());
        }
        catch(Exception exception)
        {
            mutation.Detach();foreach(var restore in resources)restore();bounds?.Invoke();cache();carries();
            foreach(var mesh in meshes)mesh.Dispose();rigs?.Discard();
            if(exception is OutOfMemoryException)throw;
            return "The prepared skin could not be displayed: "+exception.Message;
        }
        var edit=new PreparedSkinDisplay(this,parent,documentNode,row,additional,mutation,meshes.ToArray(),rigs);
        edit.Install();History.Push(edit);created=row;return null;
    }

    private sealed class PreparedSkinDisplay(D3DImageHost host,SceneNode parent,SceneNode document,SceneNode row,SceneNode[] additional,
        IFrameDuplication native,GpuMesh[] meshes,IEditAction rigs):INodeEdit,IPreflightEdit
    {
        private bool _applied=true;
        public IEnumerable<SceneNode> Nodes=>new[]{row}.Concat(additional);
        public void ValidateUndo(){native.ValidateDetach();rigs.ValidateUndo();}
        public void ValidateRedo()
        {
            if(!host.Tree.IsInScene(parent)||!host.Tree.IsInScene(document))throw new EditRefusedException("The prepared skin's owning Kit is no longer in the scene.");
            native.ValidateReattach();rigs.ValidateRedo();
        }
        internal void Install()=>Attach();
        private void Attach()
        {
            parent.AddChild(row);foreach(var root in additional)document.AddChild(root);
            foreach(var mesh in meshes)host.Rnd!.AttachMesh(mesh);host.Tree.MeshCount+=meshes.Length;rigs.Redo();
            host.Persistence.MarkFrameModified(row);host.Selection.SetSelection([row],row);host.RaiseSceneChanged();_applied=true;
        }
        public void Undo()
        {
            if(!_applied)return;ValidateUndo();host.Selection.Select(null);native.Detach();rigs.Undo();
            host.Rnd!.DetachMeshes(meshes);host.Tree.MeshCount-=meshes.Length;parent.Children.Remove(row);foreach(var root in additional)document.Children.Remove(root);
            host.Persistence.MarkFrameModified(parent);host.RaiseSceneChanged();_applied=false;
        }
        public void Redo(){if(_applied)return;ValidateRedo();native.Reattach();Attach();}
        public void Discard(){rigs.Discard();if(!_applied)foreach(var mesh in meshes)mesh.Dispose();}
    }
}
