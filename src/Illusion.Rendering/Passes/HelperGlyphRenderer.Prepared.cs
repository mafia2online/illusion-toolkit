using Illusion.Domain;

namespace Illusion.Rendering.Passes;

internal sealed unsafe partial class HelperGlyphRenderer
{
    /// <summary>Allocates a replacement before changing the live district. History retains its own
    /// COM references, so subsequent live-pose/transform overlay refreshes cannot invalidate undo.</summary>
    internal IEditAction PrepareDistrict(object key, HelperGlyphRenderData? data)
    {
        District? before = _districts.TryGetValue(key, out var current) ? current : null; District? after = null;
        if (data is { SegmentCount: > 0 })
        {
            var segments = data.Segments.Select(segment => new OverlaySegment
            {
                Anchor = segment.Anchor, OffsetA = segment.OffsetA, OffsetB = segment.OffsetB, Extent = segment.Extent,
                Rgba = OverlaySegments.Pack(segment.Color), Flags = (uint)(segment.ScreenSized ? OverlaySegmentFlags.ScreenSized : OverlaySegmentFlags.World)
            }).ToArray();
            after = new District { Vb = _pass.CreateBuffer(segments), SegmentCount = (uint)segments.Length, GlyphCount = data.GlyphCount, HiddenCount = data.HiddenCount };
        }
        else if (data is { HiddenCount: > 0 }) after = new District { HiddenCount = data.HiddenCount };
        Retain(before); return new DistrictEdit(this, key, before, after);
    }

    private static void Retain(District? district)
    { if (district is { } value && value.Vb.Handle != null) value.Vb.Handle->AddRef(); }

    private sealed class DistrictEdit(HelperGlyphRenderer owner, object key, District? before, District? after) : IEditAction
    {
        private bool _applied, _discarded;
        private void Validate()
        {
            if (_discarded) throw new EditRefusedException("The recorded skeleton overlay was discarded.");
        }
        public void ValidateUndo() { if (_applied) Validate(); }
        public void ValidateRedo() { if (!_applied) Validate(); }
        public void Redo() { if (_applied) return; ValidateRedo(); owner.RemoveDistrict(key); Retain(after); if (after is { } value) owner._districts.Add(key, value); _applied = true; }
        public void Undo() { if (!_applied) return; ValidateUndo(); owner.RemoveDistrict(key); Retain(before); if (before is { } value) owner._districts.Add(key, value); _applied = false; }
        public void Discard()
        {
            if (_discarded) return; _discarded = true;
            before?.Vb.Dispose(); after?.Vb.Dispose();
        }
    }
}
