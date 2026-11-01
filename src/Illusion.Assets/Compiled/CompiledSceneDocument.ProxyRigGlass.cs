using System.Buffers.Binary;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // Native glass stores bone IDs, not an additional bind transform. Hit-local coordinates
    // use the current visual inverse; its collision registration uses the CC helper of that ID.
    private string? ValidatePreparedRigGlass(CompiledFrameNode frame, CompiledVehicleRig rig)
    {
        try
        {
            var glass = Components(frame.Element).Where(component => component.TypeId == 0xe27be3b3).ToArray();
            if (glass.Length == 0) return null;
            if (glass.Length != 1 || Components(frame.Element).Count(component => component.TypeId == 0x2b7b4775) != 1)
                throw new NotSupportedException("Static glass requires one native glass and collision owner.");
            var component = glass[0];
            if (_frames.Values.Count(owner => Components(owner.Element).Any(candidate => ReferenceEquals(candidate, component))) != 1
                || Graph.Fields(component).Keys.Any(name => name is not ("m_GlassElements" or "m_DataFlags"))
                || Graph.Field(component,"m_DataFlags") is not { DataSize: 4 } flags || Graph.Integer(flags) != 0)
                throw new NotSupportedException("Dynamic, shared or unknown glass state requires its own rig dependency contract.");
            if (Graph.Field(component,"m_GlassElements") is not { TypeId: 0xda7bcbe6 } field)
                throw new NotSupportedException("Unknown native glass element layout.");
            var data = Graph.Data(field);
            if (data.Length < 4) throw new InvalidDataException("Truncated native glass table.");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (count > 255 || data.Length != 4L + count * 28L) throw new NotSupportedException("Unknown native glass element stride.");
            var collision = Components(frame.Element).Single(item => item.TypeId == 0x2b7b4775);
            var collisionBones = CurrentItems(Graph.Field(collision,"m_ElementDefs")!).Select(entry =>
                BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(Graph.Field(entry,"m_Data")!)[14..])).ToHashSet();
            var visuals = ProxyVisuals(frame).ToArray(); var seen = new HashSet<ushort>();
            for (int i=0;i<count;i++)
            {
                var record = data.Slice(4+i*28,28); ushort bone = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
                if (bone >= rig.Count || !seen.Add(bone) || !collisionBones.Contains(bone)
                    || BinaryPrimitives.ReadUInt32LittleEndian(record[8..]) != uint.MaxValue || BinaryPrimitives.ReadUInt16LittleEndian(record[22..]) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) is not (0 or 2)
                    || !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(record[12..])) || !float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(record[16..])))
                    throw new NotSupportedException("A glass pane lacks a verified static bone, collision helper or pristine runtime state.");
                var matches = visuals.Where(visual => Graph.Field(visual,"m_Data") is { DataSize: 88 } value
                    && BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(value)[46..]) == bone).ToArray();
                if (matches.Length != 1 || !CompiledMeshBinding.TryImmutableRigidBind(Graph,frame.Element,matches[0],out _))
                    throw new NotSupportedException("Every static glass pane requires one corresponding rigid native visual.");
            }
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        { return exception.Message; }
    }
}
