using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledWorldCollisionArchive
{
    private sealed record Binding(CompiledResource Resource,CompiledElement Data,int Variant);
    private readonly Dictionary<MeshData,Binding> _bindings=[];
    private bool _needsFlush;
    public bool IsDirty => _needsFlush;
    public bool HasSavedChanges { get; private set; }
    public long Revision { get; private set; }
    private void RegisterEditableMesh(MeshData mesh,CompiledResource resource,CompiledElement data,int variant) => _bindings.Add(mesh,new(resource,data,variant));

    /// <summary>Changes one native geometry asset and ALL its world instances, preserving triangle topology.</summary>
    public IEditAction? ReplaceMeshPositions(MeshData mesh,IReadOnlyList<Vector3> positions,out string? refusal)
    {
        refusal=null;
        if(!_bindings.TryGetValue(mesh,out var binding)){refusal="Select a concave geometry asset owned by this district collision archive.";return null;}
        try
        {
            byte[] before=binding.Resource.Data(binding.Data).ToArray();
            byte[] after=HavokMeshWriter.ReplacePositions(before,binding.Variant,positions);
            if(before.AsSpan().SequenceEqual(after)){refusal="No collision vertices changed.";return null;}
            after=HavokCompoundBoundsWriter.Refit(before,after,binding.Variant).Bytes;
            if(before.AsSpan().SequenceEqual(after)){refusal="No collision vertices changed.";return null;}
            var decoded=HavokMeshData.ReadVariant(after,binding.Variant)??throw new InvalidDataException("Recooked world collision variant is unavailable.");
            if(decoded.Vertices.Length!=mesh.Positions.Length||!decoded.Indices.AsSpan().SequenceEqual(mesh.Indices))throw new InvalidDataException("World recooking changed fixed triangle topology.");
            var action=new MeshAction(this,binding,mesh,before,after,mesh.Positions.ToArray(),decoded.Vertices,mesh.Normals.ToArray(),WorldCollisionNormals(decoded.Vertices,decoded.Indices));
            action.Redo();return action;
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidDataException or NotSupportedException or OverflowException)
        {refusal=ex.Message;return null;}
    }

    /// <summary>Flushes edited extracted resources. The containing SDS must subsequently be rebuilt.</summary>
    public void SaveWorkingCopy()
    {
        if(!_needsFlush)return;
        foreach(var resource in _bindings.Values.Select(b=>b.Resource).Distinct())resource.Save();
        _needsFlush=false;HasSavedChanges=true;
    }

    private static Vector3[] WorldCollisionNormals(IReadOnlyList<Vector3> positions,uint[] indices)
    {
        var normals=new Vector3[positions.Count];
        for(int t=0;t<indices.Length;t+=3)
        {
            int a=(int)indices[t],b=(int)indices[t+1],c=(int)indices[t+2];
            var normal=Vector3.Cross(positions[b]-positions[a],positions[c]-positions[a]);
            normals[a]+=normal;normals[b]+=normal;normals[c]+=normal;
        }
        for(int n=0;n<normals.Length;n++)
        {
            float length=normals[n].LengthSquared();
            normals[n]=float.IsFinite(length)&&length>1e-12f?Vector3.Normalize(normals[n]):Vector3.UnitZ;
        }
        return normals;
    }

    private sealed class MeshAction(CompiledWorldCollisionArchive owner,Binding binding,MeshData mesh,byte[] before,byte[] after,Vector3[] original,Vector3[] updated,Vector3[] oldNormals,Vector3[] newNormals):IEditAction
    {
        private bool _applied;
        private void Apply(byte[] expected,byte[] data,Vector3[] positions,Vector3[] normals)
        {
            if(!binding.Resource.Data(binding.Data).SequenceEqual(expected))throw new InvalidOperationException("World collision resource changed outside this edit history; refusing to overwrite another edit.");
            binding.Resource.Replace(binding.Data,data);positions.CopyTo(mesh.Positions,0);normals.CopyTo(mesh.Normals,0);owner._needsFlush=true;owner.Revision++;
        }
        public void Undo(){if(!_applied)return;Apply(after,before,original,oldNormals);_applied=false;}
        public void Redo(){if(_applied)return;Apply(before,after,updated,newNormals);_applied=true;}
    }
}
