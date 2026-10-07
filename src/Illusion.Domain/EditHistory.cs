namespace Illusion.Domain;

/// <summary>
/// Linear undo/redo stack for scene edits (currently object transforms — gizmo drags and numeric-field commits).
/// Pushing a new edit clears the redo branch, like every mainstream editor; the stack is cleared on scene reset.
/// Raises <see cref="Changed"/> so the Edit menu / hotkeys can refresh their enabled state.
/// </summary>
public sealed class EditHistory
{
    private readonly List<IEditAction> _undo = new();
    private readonly List<IEditAction> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised whenever the undo/redo availability may have changed.</summary>
    public event Action? Changed;

    /// <summary>Records a freshly-applied edit and drops the redo branch (discarding its actions).</summary>
    public void Push(IEditAction action)
    {
        _undo.Add(action);
        IEditAction[] dropped = TakeRedo();
        Changed?.Invoke();
        foreach (IEditAction a in dropped) a.Discard();
    }

    /// <summary>
    /// Drops the redo branch alone, leaving the undo stack — what a change that did NOT come off this stack
    /// does to it.
    ///
    /// <para>
    /// A Blender push is the case: it rewrites the very geometry a redo would put its snapshot back over, so
    /// every action on the branch is now a recipe for a scene that no longer exists. The undo stack stays,
    /// because it is the way back out and it is what a modder reaches for when a push went wrong.
    /// </para>
    /// </summary>
    public void ClearRedo()
    {
        IEditAction[] dropped = TakeRedo();
        if (dropped.Length == 0) return;
        Changed?.Invoke();
        foreach (IEditAction a in dropped) a.Discard();
    }

    // Snapshot-then-clear so a throwing Discard cannot leave a stale action on the redo stack.
    private IEditAction[] TakeRedo()
    {
        IEditAction[] dropped = _redo.ToArray();
        _redo.Clear();
        return dropped;
    }

    /// <summary>How many edits can be undone — what <see cref="SquashSince"/> is given to mark a point.</summary>
    public int UndoCount => _undo.Count;

    /// <summary>
    /// Folds every edit pushed since the undo stack held <paramref name="count"/> into one, built by
    /// <paramref name="combine"/> from them in the order they were pushed — so a command that went through
    /// several editors is still one Ctrl+Z. Nothing happens when fewer than two were pushed.
    /// </summary>
    public void SquashSince(int count, Func<IEditAction[], IEditAction> combine)
    {
        ArgumentNullException.ThrowIfNull(combine);
        if (count < 0 || _undo.Count - count < 2) return;
        IEditAction[] edits = [.. _undo.Skip(count)];
        _undo.RemoveRange(count, edits.Length);
        _undo.Add(combine(edits));
        Changed?.Invoke();
    }

    /// <summary>Reverts the most recent edit (no-op when empty).</summary>
    public void Undo()
    {
        if (_undo.Count == 0) return;
        IEditAction a = _undo[^1];
        a.Undo(); // may throw — the stacks stay untouched so the edit is not lost from history
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(a);
        Changed?.Invoke();
    }

    /// <summary>Re-applies the most recently undone edit (no-op when empty).</summary>
    public void Redo()
    {
        if (_redo.Count == 0) return;
        IEditAction a = _redo[^1];
        a.Redo(); // may throw — the stacks stay untouched so the edit is not lost from history
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(a);
        Changed?.Invoke();
    }

    /// <summary>Drops all history (scene reset — the nodes it references are gone), discarding every action.</summary>
    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        foreach (IEditAction a in _undo) a.Discard();
        foreach (IEditAction a in _redo) a.Discard();
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops every edit matching <paramref name="match"/> from both stacks (e.g. edits whose object is
    /// being unloaded by streaming), discarding each, so undo/redo never targets a node that has left the scene.</summary>
    public void RemoveWhere(Predicate<IEditAction> match)
    {
        int removed = DropWhere(_undo, match) + DropWhere(_redo, match);
        if (removed > 0) Changed?.Invoke();
    }

    private static int DropWhere(List<IEditAction> list, Predicate<IEditAction> match)
    {
        int removed = 0;
        for (int i = list.Count - 1; i >= 0; i--)
            if (match(list[i])) { list[i].Discard(); list.RemoveAt(i); removed++; }
        return removed;
    }
}
