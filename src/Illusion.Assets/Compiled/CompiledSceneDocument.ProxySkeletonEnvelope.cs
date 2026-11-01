using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private IEditAction? ExpandProxyRigidSkeletonEnvelope(CompiledFrameNode frame, CompiledMeshBinding binding)
    {
        var visualData = Graph.Field(binding.Visual, "m_Data");
        if (visualData?.DataSize != 88) throw new InvalidDataException("Unknown rigid visual envelope layout.");
        int bone = BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(visualData)[46..]);
        var components = Components(frame.Element).Where(component => component.TypeId == 0x0fa73185).ToArray();
        if (components.Length == 0 && bone == 0) return null;
        if (components.Length != 1) throw new InvalidDataException("Rigid envelope expansion needs exactly one immutable skeleton.");
        var reference = Graph.Field(components[0], "m_ImmutableData")!;
        var immutable = Graph.Resolve(reference) ?? throw new InvalidDataException("Unresolved rigid skeleton envelope.");
        var field = Graph.Field(immutable, "m_BoneAABBs") ?? throw new InvalidDataException("Missing native rigid skeleton joint-local bounds.");
        var bytes = Graph.Data(field).ToArray();
        if (bytes.Length < 4 || bytes.Length != 4L + BinaryPrimitives.ReadUInt32LittleEndian(bytes) * 24L
            || bone >= BinaryPrimitives.ReadUInt32LittleEndian(bytes)) throw new InvalidDataException("Malformed native rigid skeleton joint-local bounds.");
        int offset = checked(4 + bone * 24); var min = CompiledMeshDecoder.Vector(bytes.AsSpan(offset)); var max = CompiledMeshDecoder.Vector(bytes.AsSpan(offset + 12));
        if (!float.IsFinite(min.LengthSquared()) || !float.IsFinite(max.LengthSquared()) || Vector3.Min(min, max) != min)
            throw new InvalidDataException("Invalid native rigid skeleton bounds.");
        var inverse = Matrix4x4.Identity;
        if (binding.GeometryBind is { } bind && !Matrix4x4.Invert(bind, out inverse)) throw new InvalidDataException("Singular immutable rigid binding.");
        var expandedMin = min; var expandedMax = max;
        for (int level = 0; level < binding.LodCount; level++)
        {
            var mesh = binding.DecodeLod(level) ?? throw new InvalidDataException("Rigid envelope requires all native LODs.");
            foreach (var point in mesh.Positions)
            {
                var local = Vector3.Transform(point, inverse);
                if (!float.IsFinite(local.LengthSquared())) throw new InvalidDataException("Nonfinite native rigid joint position.");
                expandedMin = Vector3.Min(expandedMin, local); expandedMax = Vector3.Max(expandedMax, local);
            }
        }
        if (expandedMin == min && expandedMax == max) return null;
        var transfer = reference.Event == 2 && reference.Values.Count() > 1 ? ExternalizeProxyInlineGeometry([reference]) : null;
        var pointer = reference.Event == 2 ? reference.Values.First() : reference;
        byte[] originalPointer = Graph.Data(pointer).ToArray();
        var copy = new GeometryCopy(this);
        try
        {
            var clone = copy.CloneDefinition(immutable, false); copy.Finish();
            static void Write(byte[] data, int index, Vector3 value)
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index), value.X);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index + 4), value.Y);
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index + 8), value.Z);
            }
            Write(bytes, offset, expandedMin); Write(bytes, offset + 12, expandedMax);
            Graph.Owner(clone).Replace(Graph.Field(clone, "m_BoneAABBs")!, bytes);
            byte[] newPointer = originalPointer.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(newPointer, clone.ObjectId);
            Graph.Owner(pointer).Replace(pointer, newPointer);
            bool attached = true;
            return new ProxyGeometryAction(
                () => { if (attached) return; transfer?.Redo(); copy.Reattach(); Graph.Owner(pointer).Replace(pointer, newPointer); attached = true; },
                () => { if (!attached) return; Graph.Owner(pointer).Replace(pointer, originalPointer); copy.Detach(); transfer?.Undo(); attached = false; });
        }
        catch { copy.Detach(); transfer?.Undo(); throw; }
    }

    private sealed class ProxyGeometryAction(Action redo, Action undo, Action? validateRedo = null, Action? validateUndo = null) : IEditAction
    {
        public void ValidateRedo() => validateRedo?.Invoke();
        public void ValidateUndo() => validateUndo?.Invoke();
        public void Redo() => redo();
        public void Undo() => undo();
    }
}
