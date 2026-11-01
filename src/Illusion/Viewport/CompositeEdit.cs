using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Several edits as ONE history entry: redo runs them forward, undo in reverse.
/// <para>
/// Order matters and is the caller's contract. A gizmo drag that both moves a collision placement and mints a
/// resized hull for it pushes <c>[transform, ...mints]</c>, so undo unwinds the mints first and the transform
/// last — the reverse of how they were applied. Getting that backwards would repoint a placement at a hull that
/// had already been collected.
/// </para>
/// </summary>
internal interface IPreflightEdit
{
    void ValidateUndo();
    void ValidateRedo();
}

internal sealed class CompositeEdit : INodeEdit, IPreflightEdit
{
    private readonly IEditAction[] _children;

    public CompositeEdit(IEditAction[] children) => _children = children;

    public IEnumerable<SceneNode> Nodes =>
        _children.OfType<INodeEdit>().SelectMany(c => c.Nodes).Distinct();

    public void ValidateUndo() { foreach (var child in _children.OfType<IPreflightEdit>()) child.ValidateUndo(); }
    public void ValidateRedo() { foreach (var child in _children.OfType<IPreflightEdit>()) child.ValidateRedo(); }

    public void Undo()
    {
        ValidateUndo();
        for (int i = _children.Length - 1; i >= 0; i--) _children[i].Undo();
    }

    public void Redo()
    {
        ValidateRedo();
        foreach (IEditAction child in _children) child.Redo();
    }

    public void Discard()
    {
        foreach (IEditAction child in _children) child.Discard();
    }
}
