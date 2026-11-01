using System.Numerics;

namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectCurve
{
    // Native scalar/vector endpoint tangents, in key-frame units (incoming points backwards).
    private static void BesselEnd(BezierKey[] keys, int i)
    {
        var key = keys[i];
        if (keys.Length == 1) { key.InTan = key.OutTan = Vector3.Zero; return; }
        if (i == 1)
        {
            float dt = keys[1].Frame - keys[0].Frame; if (dt == 0) dt = 1;
            key.InTan = (keys[0].Value - key.Value) / dt;
            if (keys.Length == 2) key.OutTan = Vector3.Zero;
            return;
        }
        float next = key.Frame - keys[i - 1].Frame; if (next == 0) next = 1;
        float previous = keys[i - 1].Frame - keys[i - 2].Frame; if (previous == 0) previous = 1;
        float ratio = previous / (next + previous);
        float left = (ratio - 1) / previous;
        float right = (2 - ratio) / next;
        key.InTan = -(left * (keys[i - 1].Value - keys[i - 2].Value) + right * (key.Value - keys[i - 1].Value));
        if (i == keys.Length - 1) key.OutTan = Vector3.Zero;
    }

    private static void BesselStart(BezierKey[] keys, int i)
    {
        var key = keys[i];
        if (keys.Length == 1) { key.InTan = key.OutTan = Vector3.Zero; return; }
        float next = keys[i + 1].Frame - key.Frame; if (next == 0) next = 1;
        if (i == keys.Length - 2)
            key.OutTan = (keys[i + 1].Value - key.Value) / next;
        else
        {
            float following = keys[i + 2].Frame - keys[i + 1].Frame; if (following == 0) following = 1;
            float ratio = next / (next + following);
            float left = (ratio + 1) / next;
            float right = ratio / following;
            key.OutTan = left * (keys[i + 1].Value - key.Value) - right * (keys[i + 2].Value - keys[i + 1].Value);
        }
        if (i == 0) key.InTan = Vector3.Zero;
    }
}
