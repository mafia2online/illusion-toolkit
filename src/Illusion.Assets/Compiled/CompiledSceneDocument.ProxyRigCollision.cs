using System.Buffers.Binary;
using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // Native C_CollisionComponent::ActivateElement copies model-bind CollMatrix directly.
    // Bone IDs drive helper/enable grouping; static CC has no skeleton-update callback.
    // Dynamic/gameplay/glass components are excluded by the outer rig component contract.
    private string? ValidatePreparedRigCollision(CompiledFrameNode frame, CompiledVehicleRig rig)
    {
        try
        {
            foreach (var component in Components(frame.Element).Where(component => component.TypeId == 0x2b7b4775))
            {
                if (_frames.Values.Count(owner => Components(owner.Element).Any(candidate => ReferenceEquals(candidate, component))) != 1)
                    throw new NotSupportedException("The static collision component is shared by multiple scene owners.");
                string[] known = ["m_ElementDefs","m_CollisionLayer","m_MaterialDef","m_DataFlags","m_CustomItems","m_CustomMaterials","m_CustomItemDescs","m_BoneCollisionHelperMap","m_DataDependencies"];
                if (Graph.Fields(component).Keys.Any(field => !known.Contains(field))) throw new NotSupportedException("Unknown collision fields may depend on authored bone transforms.");
                foreach (string name in new[] { "m_CustomItems", "m_CustomMaterials", "m_CustomItemDescs" })
                    if (Graph.Field(component, name) is not { } field || CurrentItems(field).Count != 0)
                        throw new NotSupportedException("Custom collision items/materials require their own rig attachment rewrite.");
                var entries = CurrentItems(Graph.Field(component, "m_ElementDefs") ?? throw new InvalidDataException("Missing collision definitions."));
                var counts = new Dictionary<ushort, int>();
                foreach (var entry in entries)
                {
                    if (Graph.Field(entry, "m_Data") is not { DataSize: 88 } data || Graph.Resolve(Graph.Field(entry, "m_ItemDesc")) is not { } shape
                        || shape.TypeId is not (0x63ee7798 or 0x4fd32ee0 or 0x48430eeb or 0x7a0b5196) || CollisionGeometry(shape) is null)
                        throw new NotSupportedException("A collision placement lacks a verified native shape or model-bind matrix.");
                    ushort bone = BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(data)[14..]);
                    if (bone != ushort.MaxValue && bone >= rig.Count) throw new InvalidDataException("Collision attachment bone exceeds the native skeleton.");
                    if (!Matrix4x4.Invert(ReadMatrix(Graph.Data(data).Slice(24, 48)), out _)) throw new InvalidDataException("A collision placement is singular.");
                    counts[bone] = counts.GetValueOrDefault(bone) + 1;
                }
                var helpers = CurrentItems(Graph.Field(component, "m_BoneCollisionHelperMap") ?? throw new InvalidDataException("Missing collision helper map."));
                var seen = new HashSet<ushort>();
                foreach (var helper in helpers)
                {
                    if (helper.TypeId != 0xb553f558 || Graph.Fields(helper).Keys.Any(name => name is not ("m_BoneId" or "m_Elements"))
                        || Graph.Field(helper, "m_BoneId") is not { DataSize: 2 } id || Graph.Field(helper, "m_Elements") is not { } slots)
                        throw new NotSupportedException("Unknown collision bone-helper layout.");
                    ushort bone = checked((ushort)Graph.Integer(id)); var items = CurrentItems(slots);
                    if (!seen.Add(bone) || !counts.TryGetValue(bone, out int count) || items.Count != count
                        || items.Any(item => item.TypeId != 0x1fbdd215 || item.ObjectId != 0 || CurrentDescendants(item).Count() != 1))
                        throw new NotSupportedException("Collision helper slots do not correspond to the native bone attachments.");
                }
                if (!seen.SetEquals(counts.Keys)) throw new InvalidDataException("Collision helper map has incomplete bone coverage.");
            }
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { return exception.Message; }
    }

    private void UpdatePreparedRigCollision(CompiledFrameNode frame, CompiledVehicleRig rig, Matrix4x4[] previousModels,
        Dictionary<CompiledElement, byte[]> before, Dictionary<CompiledElement, byte[]> after)
    {
        if (ValidatePreparedRigCollision(frame, rig) is { } refusal) throw new NotSupportedException(refusal);
        foreach (var component in Components(frame.Element).Where(component => component.TypeId == 0x2b7b4775))
            foreach (var entry in CurrentItems(Graph.Field(component, "m_ElementDefs")!))
            {
                var data = Graph.Field(entry, "m_Data")!; byte[] bytes = Graph.Data(data).ToArray();
                ushort bone = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
                if (bone == ushort.MaxValue || previousModels[bone] == rig.ModelRest(bone)) continue;
                if (!Matrix4x4.Invert(previousModels[bone], out var inverse)) throw new InvalidDataException("The previous collision bind bone is singular.");
                var delta = inverse * rig.ModelRest(bone);
                if (!CompiledMeshBinding.IsRigidBind(delta)) throw new NotSupportedException("A static collision bind delta must remain a finite proper rigid transform.");
                before.TryAdd(data, bytes.ToArray());
                WriteMatrix(ReadMatrix(bytes.AsSpan(24, 48)) * delta).CopyTo(bytes, 24);
                after[data] = bytes; Graph.Owner(data).Replace(data, bytes);
            }
    }
}
