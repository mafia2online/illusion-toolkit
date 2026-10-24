namespace Illusion.Domain;

/// <summary>
/// Thrown by an <see cref="IEditAction"/> whose undo or redo could not be carried out - a file it has to write
/// is locked, the thing it restores is gone. <see cref="EditHistory"/> leaves its stacks as they were when a
/// step throws, so the step is still there to be taken once the obstacle is out of the way; whoever asked for
/// the step shows the message instead of treating it as done.
/// </summary>
public sealed class EditRefusedException : Exception
{
    public EditRefusedException(string message) : base(message) { }

    public EditRefusedException(string message, Exception inner) : base(message, inner) { }
}
