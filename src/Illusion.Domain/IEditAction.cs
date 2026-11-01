namespace Illusion.Domain;

/// <summary>One reversible scene edit. <see cref="Undo"/> restores the before-state, <see cref="Redo"/> the after.</summary>
public interface IEditAction
{
    /// <summary>Optional read-only preflight used before any child of a composite changes state.</summary>
    void ValidateUndo() { }
    void ValidateRedo() { }
    void Undo();
    void Redo();

    /// <summary>Called when this edit is permanently dropped from the history (redo branch cleared by a new edit,
    /// pruned on unload, or scene reset), so it can release resources it holds while APPLIED — e.g. a delete's
    /// detached-but-still-alive GPU meshes. Default: nothing to release.</summary>
    void Discard() { }
}
