using System.Numerics;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? AnimationAuthoring(string operation, int frame, int track, float[]? localMatrix, float? value, out AnimationAuthoringInfo? result)
    {
        result = null;
        if (ResourceWindow?.AnimationPreview is not { } preview) return "Open a native animation clip first.";
        try
        {
            float[]? sampledMatrix = null; float? sampledFloat = null;
            switch (operation)
            {
                case "inspect": break;
                case "read_transform":
                    var sampled = preview.ReadAnimationTransformFrame(frame,track);
                    sampledMatrix = [sampled.M11,sampled.M12,sampled.M13,sampled.M14,sampled.M21,sampled.M22,sampled.M23,sampled.M24,sampled.M31,sampled.M32,sampled.M33,sampled.M34,sampled.M41,sampled.M42,sampled.M43,sampled.M44]; break;
                case "read_float": sampledFloat = preview.ReadAnimationFloatFrame(frame,track); break;
                case "transform":
                    if (localMatrix is not { Length: 16 } m || m.Any(v => !float.IsFinite(v))) return "Provide16 finite row-major local matrix values.";
                    preview.SetAnimationTransformFrame(frame,track,new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15])); break;
                case "float":
                    if (value is not { } number || !float.IsFinite(number)) return "Provide one finite raw native float value.";
                    preview.SetAnimationFloatFrame(frame,track,number); break;
                case "undo": preview.UndoAnimation(); break;
                case "redo": preview.RedoAnimation(); break;
                case "save": preview.SaveAnimations(); break;
                default: return "Operation must be inspect, read_transform, read_float, transform, float, undo, redo or save.";
            }
            var state = preview.AnimationAuthoringState(); var bindings = preview.AnimationAuthoringBindings();
            result = new(state.Path,state.Clip,state.Frames,state.TransformTracks,state.FloatTracks,state.Dirty) { Skeleton = bindings.Skeleton, BlendHint = bindings.BlendHint, TransformTrackNames = bindings.Transforms, FloatTrackNames = bindings.Floats, LocalMatrix = sampledMatrix, Value = sampledFloat }; return null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        { return "Native animation authoring: " + ex.Message; }
    }
}
