namespace Illusion.Formats.Frames;

// TODO(smell): process-wide counter — every FrameEntry in the process shares this sequence. Taken atomically,
// so documents parsed on two threads at once (the prop catalog scans archives in the background while the
// editor loads a district) never hand out the same id; scope it per FrameResource when the frame graph gets
// a construction context.
public static class RefManager
{
    //set to 10 because the first 10 are placeholders for render assets.
    private static int _currentRefID = 10;

    public static int GetNewRefID()
    {
        return Interlocked.Increment(ref _currentRefID);
    }
}
