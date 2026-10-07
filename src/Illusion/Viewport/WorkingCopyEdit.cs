using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// An edit that is already in an archive's working copy when it is made — a tuning value, an effect — and
/// whose undo and redo write the working copy again. Each of those writes has to put the archive back on the
/// build list, exactly as the edit itself did: a Build takes the archive off the list, and an undo after it
/// used to change the working copy with nothing left to say the game's archive was now behind it — the next
/// Build packed nothing, and the game kept the value that had been undone.
/// </summary>
internal sealed class WorkingCopyEdit(IEditAction inner, Action enlist) : IEditAction
{
    public void Undo()
    {
        inner.Undo();
        enlist();
    }

    public void Redo()
    {
        inner.Redo();
        enlist();
    }

    public void Discard() => inner.Discard();
}
