using System.Numerics;
using Illusion.Domain;

namespace Illusion.Rendering.Passes;

internal sealed unsafe partial class LineOverlayRenderer
{
    internal IEditAction PrepareDistrict(object key, IReadOnlyList<Vector3> beforeVertices, IReadOnlyList<Vector4> beforeColors,
        IReadOnlyList<Vector3> afterVertices, IReadOnlyList<Vector4> afterColors)
    {
        District? Build(IReadOnlyList<Vector3> vertices,IReadOnlyList<Vector4> colors)
        {
            if(vertices.Count<2)return null;
            var segments = OverlaySegments.FromLineList(vertices, colors);
            return new District { Vb = _pass.CreateBuffer(segments), SegmentCount = (uint)segments.Length };
        }
        var before=Build(beforeVertices,beforeColors);
        try { return new PreparedEdit(this,key,before,Build(afterVertices,afterColors)); }
        catch { before?.Vb.Dispose(); throw; }
    }
    private static void Retain(District? district)
    { if (district is { } value && value.Vb.Handle != null) value.Vb.Handle->AddRef(); }
    private sealed class PreparedEdit(LineOverlayRenderer owner, object key, District? before, District? after) : IEditAction
    {
        private bool _applied, _discarded;
        private void Validate() { if (_discarded) throw new EditRefusedException("The recorded collision overlay was discarded."); }
        public void ValidateUndo() { if (_applied) Validate(); }
        public void ValidateRedo() { if (!_applied) Validate(); }
        public void Redo() { if (_applied) return; ValidateRedo(); owner.RemoveDistrict(key); Retain(after); if (after is { } value) owner._districts.Add(key,value); _applied=true; }
        public void Undo() { if (!_applied) return; ValidateUndo(); owner.RemoveDistrict(key); Retain(before); if (before is { } value) owner._districts.Add(key,value); _applied=false; }
        public void Discard() { if (_discarded) return; _discarded=true; before?.Vb.Dispose(); after?.Vb.Dispose(); }
    }
}
