using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledWorldCollisionArchive
{
    private sealed record Binding(CompiledResource Resource,CompiledElement Data,int Variant,Vector3 Min,Vector3 Max);
    private readonly Dictionary<MeshData,Binding> _bindings=[];
    private bool _needsFlush;
    public bool IsDirty => _needsFlush;
    public bool HasSavedChanges { get; private set; }
    public long Revision { get; private set; }
    private void RegisterEditableMesh(MeshData mesh,CompiledResource resource,CompiledElement data,int variant) => _bindings.Add(mesh,new(resource,data,variant,
        mesh.Positions.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min),mesh.Positions.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max)));

    /// <summary>Changes one native geometry asset and ALL its world instances, preserving triangle topology.</summary>
    public IEditAction? ReplaceMeshPositions(MeshData mesh,IReadOnlyList<Vector3> positions,out string? refusal)
    {
        refusal=null;
        if(!_bindings.TryGetValue(mesh,out var binding)){refusal="Select a concave geometry asset owned by this district collision archive.";return null;}
        if(positions.Any(v=>v.X<binding.Min.X||v.Y<binding.Min.Y||v.Z<binding.Min.Z||v.X>binding.Max.X||v.Y>binding.Max.Y||v.Z>binding.Max.Z))
        {refusal="World collision replacements must remain inside the original geometry bounds until external baked compound proxies can be refitted.";return null;}
        try
        {
            byte[] before=binding.Resource.Data(binding.Data).ToArray();
            byte[] after=HavokMeshWriter.ReplacePositions(before,binding.Variant,positions);
            if(before.AsSpan().SequenceEqual(after)){refusal="No collision vertices changed.";return null;}
            var decoded=HavokMeshData.ReadVariant(after,binding.Variant)??throw new InvalidDataException("Recooked world collision variant is unavailable.");
            if(decoded.Vertices.Length!=mesh.Positions.Length||!decoded.Indices.AsSpan().SequenceEqual(mesh.Indices))throw new InvalidDataException("World recooking changed fixed triangle topology.");
            var action=new MeshAction(this,binding,mesh,before,after,mesh.Positions.ToArray(),decoded.Vertices);
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

    private sealed class MeshAction(CompiledWorldCollisionArchive owner,Binding binding,MeshData mesh,byte[] before,byte[] after,Vector3[] original,Vector3[] updated):IEditAction
    {
        private bool _applied;
        private void Apply(byte[] expected,byte[] data,Vector3[] positions)
        {
            if(!binding.Resource.Data(binding.Data).SequenceEqual(expected))throw new InvalidOperationException("World collision resource changed outside this edit history; refusing to overwrite another edit.");
            binding.Resource.Replace(binding.Data,data);positions.CopyTo(mesh.Positions,0);owner._needsFlush=true;owner.Revision++;
        }
        public void Undo(){if(!_applied)return;Apply(after,before,original);_applied=false;}
        public void Redo(){if(_applied)return;Apply(before,after,updated);_applied=true;}
    }
}
