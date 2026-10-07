namespace Illusion.Mcp;

/// <summary>
/// The last things the editor said through its notice banner, kept so a client that cannot see the
/// banner can still read them. A notice lives on screen for five seconds; the outcome of a Blender push —
/// what was applied, what was refused and why — is said nowhere else.
/// <para>
/// One log for the application rather than one per window, for the same reason the MCP server is the
/// application's: the editor window is replaced as the user moves between the launcher and the editor,
/// and a question asked just after must still find what was said just before. Notices arrive on protocol
/// and background threads, so every access is locked.
/// </para>
/// </summary>
internal static class EditorNoticeLog
{
    private const int Capacity = 200;

    private static readonly object Gate = new();
    private static readonly Queue<EditorNotice> Entries = new();

    public static void Add(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (Gate)
        {
            Entries.Enqueue(new EditorNotice(DateTime.Now, isError, message));
            while (Entries.Count > Capacity) Entries.Dequeue();
        }
    }

    /// <summary>The most recent <paramref name="count"/> notices, oldest first.</summary>
    public static IReadOnlyList<EditorNotice> Last(int count)
    {
        lock (Gate)
        {
            return Entries.Skip(Math.Max(0, Entries.Count - count)).ToList();
        }
    }
}
