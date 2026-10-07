using Illusion.Domain.Properties;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Editing of a frame object's data properties (the property panel): applies a committed value to the underlying
/// object, records it as one undoable edit on the shared history, and marks the owning document dirty so Save /
/// Build pick it up. A rename also updates the scene-tree row. Sits beside <see cref="TransformEditController"/>
/// and shares its <see cref="Domain.EditHistory"/>.
/// </summary>
internal sealed class PropertyEditController
{
    private readonly D3DImageHost _host;

    public PropertyEditController(D3DImageHost host) => _host = host;

    /// <summary>Applies a property edit, records it for undo and marks the document modified (no-op when unchanged).</summary>
    public void Commit(SceneNode node, PropertyDescriptor descriptor, object? before, object? after)
    {
        if (descriptor.Set is null || Equals(before, after)) return;
        SetAndKeepLinks(node, descriptor, after);
        PropagateNameEdit(node, descriptor);
        _host.Editing.History.Push(new PropertyEdit(this, node, descriptor, before, after));
        _host.Persistence.MarkFrameModified(node);
        _host.CarCollisionEditing.RefreshOverlay(); // a resized shape has to redraw at its new size
        _host.RaiseSelectionPropertiesChanged();    // refresh the panel values + the header title
    }

    // Undo/redo re-applies the recorded value. Skips a node that left the scene (streaming unload) — a defensive
    // backstop to the history pruning. Re-selects the node so the change is visible and the panel rebuilds.
    private void ApplyRecorded(SceneNode node, PropertyDescriptor descriptor, object? value)
    {
        if (descriptor.Set is null || !_host.Tree.IsInScene(node)) return;
        SetAndKeepLinks(node, descriptor, value);
        PropagateNameEdit(node, descriptor);       // update the tree row/name BEFORE re-select so the rebuilt panel is correct
        _host.Persistence.MarkFrameModified(node); // undo/redo re-dirties the frame vs. the last save
        _host.Selection.SetSelection(new[] { node }, node);
        _host.CarCollisionEditing.RefreshOverlay();
        _host.RaiseSelectionPropertiesChanged();
    }

    // The collision an imported object was given is tied to it by its NAME (Sds.ImportLinks) — the only handle
    // a static object has — so a rename takes the record along, and so does the undo of one, which comes
    // through here as well. Only when the record is this object's: with two objects of a name (one renamed
    // onto a name that was taken) the record under it belongs to one of them, and whose it is is asked
    // BEFORE the name changes — by which of them it fits, not by there being a namesake. Asked by namesake
    // alone, an object that had brought its record onto a taken name could not take it away again.
    private void SetAndKeepLinks(SceneNode node, PropertyDescriptor descriptor, object? value)
    {
        string? old = null, dir = null;
        if (descriptor.Id == "Base.Name" && descriptor.Get() is HashNameValue was && _host.OwnsImportLinks(node, out string kept))
        {
            old = was.Name;
            dir = kept;
        }
        descriptor.Set!(value);
        if (old != null && dir != null && descriptor.Get() is HashNameValue now)
        {
            // Not over a record the new name already has: this object then gives up its tie, and its record
            // stays under the old name for the undo to find.
            Assets.Sds.ImportLinks.Rename(dir, old, now.Name);
        }
    }

    // Side effects of a name / on-name-table edit: the visible tree-row name and the name-table dirty flag (those
    // fields live in the FrameNameTable, not the frame stream, so the next save must rewrite it).
    private void PropagateNameEdit(SceneNode node, PropertyDescriptor descriptor)
    {
        if (descriptor.Id == "Base.Name" && descriptor.Get() is HashNameValue hn)
            node.Name = hn.Name;
        if (descriptor.Id is "Base.Name" or "Base.IsOnFrameTable")
            _host.Persistence.MarkNameTableDirty(node);
        // An actor's row is titled by its entity name, and a rename the pack REFUSED (empty, or already taken
        // by another actor) must not retitle the row either — so the row takes whatever the actor ended up
        // with, not what was typed.
        if (descriptor.Id == "Actor.Entity" && descriptor.Get() is string entity && entity.Length > 0)
            node.Name = entity;
    }

    // Prunable by the streamer (INodeEdit) like a transform edit, so unloading a district drops its edits.
    private sealed class PropertyEdit : INodeEdit
    {
        private readonly PropertyEditController _owner;
        private readonly SceneNode _node;
        private readonly PropertyDescriptor _descriptor;
        private readonly object? _before;
        private readonly object? _after;

        public PropertyEdit(PropertyEditController owner, SceneNode node, PropertyDescriptor descriptor,
            object? before, object? after)
        {
            _owner = owner;
            _node = node;
            _descriptor = descriptor;
            _before = before;
            _after = after;
        }

        public IEnumerable<SceneNode> Nodes { get { yield return _node; } }
        public void Undo() => _owner.ApplyRecorded(_node, _descriptor, _before);
        public void Redo() => _owner.ApplyRecorded(_node, _descriptor, _after);
    }
}
