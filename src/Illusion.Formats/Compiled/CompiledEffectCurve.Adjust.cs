using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectCurve
{
    // Original scalar CompAdjBesselBezPoints101A23920. Tangents are slopes in frame units;
    // incoming slopes point backwards, and lengths are intentionally unchanged in this helper.
    private static void AdjustBessel(BezierKey[] keys, int i)
    {
        var key = keys[i]; int before = key.Frame - keys[(i + keys.Length - 1) % keys.Length].Frame;
        if (i == 0) before += keys[^1].Frame - keys[0].Frame;
        int after = keys[(i + 1) % keys.Length].Frame - key.Frame;
        if (i == keys.Length - 1) after += keys[^1].Frame - keys[0].Frame;
        if (before == 0 || after == 0 || i == 0 || i == keys.Length - 1)
        {
            if (key.OutMode == 0) { if (i == 0) BesselStart(keys, i); else key.OutTan = Vector3.Zero; }
            if (key.InMode == 0) { if (i == keys.Length - 1) BesselEnd(keys, i); else key.InTan = Vector3.Zero; }
            return;
        }
        if (key.InMode != 0 && key.OutMode == 0)
        {
            if (key.InMode is 1 or 3 or 4 or 5) key.OutTan = -key.InTan;
            else if (key.InMode == 2) key.OutTan = Vector3.Zero;
            return;
        }
        if (key.InMode == 0 && key.OutMode != 0)
        {
            if (key.OutMode is 1 or 3 or 4 or 5) key.InTan = -key.OutTan;
            else if (key.OutMode == 2) key.InTan = Vector3.Zero;
            return;
        }
        if (key.InMode != 0 || key.OutMode != 0) return;
        float left = before, right = after, weight = left / (left + right);
        float leftCoefficient = (1 - weight) / left, rightCoefficient = weight / right;
        var slope = leftCoefficient * (key.Value - keys[i - 1].Value) + rightCoefficient * (keys[i + 1].Value - key.Value);
        key.InTan = -slope; key.OutTan = slope;
    }

    // Original scalar CompAdjFlatBezPoints101A23E30. The monotonic flattening factor uses
    // double-precision pow(...,4) after explicitly rounded float time weights.
    private static void AdjustFlat(BezierKey[] keys, int i)
    {
        var key = keys[i]; int before = key.Frame - keys[(i + keys.Length - 1) % keys.Length].Frame;
        if (i == 0) before += keys[^1].Frame - keys[0].Frame;
        int after = keys[(i + 1) % keys.Length].Frame - key.Frame;
        if (i == keys.Length - 1) after += keys[^1].Frame - keys[0].Frame;
        if (before == 0 || after == 0 || i == 0 || i == keys.Length - 1)
        {
            if (key.OutMode == 6) key.OutTan = Vector3.Zero;
            if (key.InMode == 6) key.InTan = Vector3.Zero;
            return;
        }
        if (key.InMode != 6 && key.OutMode == 6)
        {
            if (key.InMode is 0 or 1 or 3 or 4 or 5) key.OutTan = -key.InTan;
            else if (key.InMode == 2) key.OutTan = Vector3.Zero;
            return;
        }
        if (key.InMode == 6 && key.OutMode != 6)
        {
            if (key.OutMode is 0 or 1 or 3 or 4 or 5) key.InTan = -key.OutTan;
            else if (key.OutMode == 2) key.InTan = Vector3.Zero;
            return;
        }
        if (key.InMode != 6 || key.OutMode != 6) return;
        float left = before, right = after, weight = left / (left + right);
        float previousPowerBase = 2 - (1 - weight), nextPowerBase = 2 - weight;
        for (int component = 0; component < 3; component++)
        {
            float previous = key.Value[component] - keys[i - 1].Value[component];
            float next = keys[i + 1].Value[component] - key.Value[component];
            float slope = 0;
            if (!((previous >= 0 && next <= 0) || (previous <= 0 && next >= 0)))
            {
                slope = (previous + next) / (left + right);
                float factor;
                if (MathF.Abs(next) < MathF.Abs(previous))
                    factor = previous == 0 ? 0 : (float)(Math.Pow(previousPowerBase, 4) * (double)MathF.Abs(next) / MathF.Abs(previous));
                else factor = next == 0 ? 0 : (float)(Math.Pow(nextPowerBase, 4) * (double)MathF.Abs(previous) / MathF.Abs(next));
                slope *= MathF.Min(factor, 1);
            }
            key.InTan[component] = -slope; key.OutTan[component] = slope;
            key.InTanLen[component] = key.OutTanLen[component] = BitConverter.UInt32BitsToSingle(0x3EAAAA9F);
        }
    }
}
