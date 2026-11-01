using Illusion.Assets;
using Illusion.Mcp;

namespace Illusion.Viewport;

internal sealed partial class DistrictStreamer
{
    private NativePaintPreview? _nativePaintPreview;
    internal NativePaintPreview PaintPreview => _nativePaintPreview ??= new(_host);
    private void TickNativePaint(float dt)
    {
        if (MafiaEnvironment.Profile.ArchiveVersion == 20) PaintPreview.Tick(dt);
    }
    internal void InvalidateNativePaintMaterials() => _nativePaintPreview?.InvalidateMaterials();
    internal PaintPreviewInfo ConfigureNativePaint(bool? enabled, uint? seed)
    {
        PaintPreview.Configure(enabled, seed); PaintPreview.Tick(0); return PaintPreview.Status;
    }
}
