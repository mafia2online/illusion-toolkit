using System.Buffers.Binary;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Current recognized collision volumes, in the same order as the scene tree.</summary>
    public IReadOnlyList<IFrameNode> CollisionVolumes(IFrameNode owner)
        => owner is CompiledFrameNode frame && _frames.ContainsValue(frame) && !_deleted.Contains(frame)
            ? BuildCollisionNodes(frame).Select(n => n.Source).OfType<IFrameNode>().ToArray() : [];

    public IEditAction? RemoveCollision(IFrameNode owner, int volumeIndex, out string? refusal)
    {
        try
        {
            var volumes = CollisionVolumes(owner);
            if ((uint)volumeIndex >= volumes.Count) { refusal = "Collision volume index is out of range."; return null; }
            return RemoveCollision(volumes[volumeIndex], out refusal);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { refusal = ex.Message; return null; }
    }

    /// <summary>Removes one element placement and its bone-helper slot. Shared shape/physics assets remain owned
    /// by their resource, and other placements keep their original descriptors, order and bytes.</summary>
    public IEditAction? RemoveCollision(IFrameNode volume, out string? refusal)
    {
        refusal = null;
        if (volume is not CompiledCollisionNode collision || !_frames.ContainsValue(collision.Frame) || _deleted.Contains(collision.Frame))
        { refusal = "An active compiled collision volume is required."; return null; }
        try
        {
            var matches = Components(collision.Frame.Element).Where(c => c.TypeId == 0x2b7b4775)
                .Select(c => (Component: c, Definitions: Graph.Field(c, "m_ElementDefs")))
                .Where(p => p.Definitions is not null)
                .SelectMany(p => CurrentItems(p.Definitions!).Select(e => (p.Component, Definitions: p.Definitions!, Entry: e)))
                .Where(p => ReferenceEquals(Graph.Field(p.Entry, "m_ItemDesc"), collision.Reference)).ToArray();
            if (matches.Length != 1) throw new NotSupportedException("The selected placement has no unique active collision registration.");
            var (component, definitions, entry) = matches[0];
            string[] known = ["m_ElementDefs", "m_CollisionLayer", "m_MaterialDef", "m_DataFlags", "m_CustomItems",
                "m_CustomMaterials", "m_CustomItemDescs", "m_BoneCollisionHelperMap", "m_DataDependencies"];
            if (Graph.Fields(component).Keys.Any(name => !known.Contains(name)))
                throw new NotSupportedException("The collision component has unknown fields that may reference element indices.");
            foreach (string name in new[] { "m_CustomItems", "m_CustomMaterials", "m_CustomItemDescs" })
                if (Graph.Field(component, name) is not { } field || CurrentItems(field).Count != 0)
                    throw new NotSupportedException("Collision removal cannot yet remap the component's custom item/material arrays.");
            if (CurrentDescendants(entry).Any(n => n.Event == 2 && n.ObjectId != 0))
                throw new NotSupportedException("The collision placement embeds an independently registered object.");
            var before = CurrentItems(definitions).ToArray();
            ushort Bone(CompiledElement item)
            {
                if (Graph.Field(item, "m_Data") is not { DataSize: 88 } data)
                    throw new NotSupportedException("Unknown collision placement data layout.");
                return BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(data)[14..]);
            }
            var boneCounts = before.GroupBy(Bone).ToDictionary(g => g.Key, g => g.Count());
            ushort bone = Bone(entry);
            var helperMap = Graph.Field(component, "m_BoneCollisionHelperMap")
                ?? throw new NotSupportedException("Collision bone-helper map is missing.");
            var helpers = CurrentItems(helperMap).ToArray();
            var byBone = new Dictionary<ushort, (CompiledElement Helper, CompiledElement Container, CompiledElement[] Slots)>();
            foreach (var helper in helpers)
            {
                if (helper.TypeId != 0xb553f558 || Graph.Fields(helper).Keys.Any(n => n is not ("m_BoneId" or "m_Elements")) ||
                    Graph.Field(helper, "m_BoneId") is not { DataSize: 2 } id || Graph.Field(helper, "m_Elements") is not { } elements)
                    throw new NotSupportedException("Unknown collision bone-helper layout.");
                ushort helperBone = checked((ushort)Graph.Integer(id));
                var slots = CurrentItems(elements).ToArray();
                if (!boneCounts.TryGetValue(helperBone, out int count) || slots.Length != count ||
                    slots.Any(slot => slot.TypeId != 0x1fbdd215 || slot.ObjectId != 0 || CurrentDescendants(slot).Count() != 1) ||
                    !byBone.TryAdd(helperBone, (helper, elements, slots)))
                    throw new NotSupportedException("Collision helper slots do not match the active definitions, or contain serialized references.");
            }
            if (byBone.Count != boneCounts.Count) throw new NotSupportedException("A collision bone is missing its helper slots.");
            var selected = byBone[bone];
            int ordinal = before.TakeWhile(e => !ReferenceEquals(e, entry)).Count(e => Bone(e) == bone);
            var changes = new List<(CompiledElement Container, CompiledElement[] Before, CompiledElement[] After)>
            {
                (definitions, before, before.Where(e => !ReferenceEquals(e, entry)).ToArray())
            };
            if (selected.Slots.Length == 1)
                changes.Add((helperMap, helpers, helpers.Where(h => !ReferenceEquals(h, selected.Helper)).ToArray()));
            else
                changes.Add((selected.Container, selected.Slots, selected.Slots.Where((_, index) => index != ordinal).ToArray()));
            var rows = new List<(SdsFrameNode Parent, SdsFrameNode Row, int Index)>();
            void FindRows(SdsFrameNode node)
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (child.Source is CompiledCollisionNode candidate && ReferenceEquals(candidate.Reference, collision.Reference))
                        rows.Add((node, child, i));
                    FindRows(child);
                }
            }
            foreach (var root in Roots) FindRows(root);
            using var transaction = new ImportTransaction(this, null);
            var edit = new CollisionRemovalEdit(this, changes, rows);
            // Validate lengths, schemas and ownership before exposing an applied edit.
            foreach (var change in changes) SetItems(change.Container, change.After);
            foreach (var resource in changes.Select(c => Graph.Owner(c.Container)).Distinct())
                _ = resource.Rewrite(EffectivePlans().GetValueOrDefault(resource) ?? new());
            edit.Redo();
            transaction.Commit();
            return edit;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { refusal = ex.Message; return null; }
    }

    private sealed class CollisionRemovalEdit(CompiledSceneDocument document,
        IReadOnlyList<(CompiledElement Container, CompiledElement[] Before, CompiledElement[] After)> changes,
        IReadOnlyList<(SdsFrameNode Parent, SdsFrameNode Row, int Index)> rows) : IEditAction
    {
        private bool _applied;
        public void Redo()
        {
            if (_applied) return;
            foreach (var change in changes) document.SetItems(change.Container, change.After);
            foreach (var row in rows) row.Parent.Children.Remove(row.Row);
            _applied = true;
        }
        public void Undo()
        {
            if (!_applied) return;
            foreach (var change in changes.Reverse()) document.SetItems(change.Container, change.Before);
            foreach (var row in rows.OrderBy(r => r.Index))
                if (!row.Parent.Children.Contains(row.Row)) row.Parent.Children.Insert(Math.Min(row.Index, row.Parent.Children.Count), row.Row);
            _applied = false;
        }
    }
}
