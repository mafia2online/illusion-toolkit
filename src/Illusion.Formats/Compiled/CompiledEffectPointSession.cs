namespace Illusion.Formats.Compiled;

/// <summary>Incremental isolated native point replay. Seek work is bounded per call;
/// callers can yield between calls until the requested frame is reached.</summary>
public sealed class CompiledEffectPointSession
{
    private readonly Func<CompiledEffectPointPreview> _configuration;
    private readonly CompiledGraph _graph;
    private (CompiledResource Resource, long Revision)[] _revisions = [];
    private readonly float _step;
    private readonly uint _seed;
    private CompiledEffectPointPreview.PointTimelineState _state;
    private EffectPointSnapshot? _snapshot;
    public int Frame { get; private set; }
    public EffectPointSnapshot Snapshot
    {
        get { RefreshSource(); return _snapshot ??= _state.Snapshot(); }
    }

    internal CompiledEffectPointSession(Func<CompiledEffectPointPreview> configuration, CompiledGraph graph, float step, uint seed)
    {
        _configuration = configuration; _graph = graph; _step = step; _seed = seed;
        _state = configuration().CreateTimelineState(seed);
        CaptureRevisions();
    }

    public void Reset()
    {
        _state = _configuration().CreateTimelineState(_seed);
        Frame = 0; _snapshot = null;
        CaptureRevisions();
    }

    private void CaptureRevisions() => _revisions = _graph.Resources.Select(r => (r, r.Revision)).ToArray();

    private void RefreshSource()
    {
        if (_graph.Resources.Count != _revisions.Length
            || _revisions.Where((entry, i) => !ReferenceEquals(entry.Resource, _graph.Resources[i])
                || entry.Revision != entry.Resource.Revision).Any()) Reset();
    }

    public bool AdvanceFrames(int frames, int maxFrames = 240, CancellationToken cancellationToken = default, int maxWorkMilliseconds = 25)
    {
        if (frames < 0 || frames > 36000 - Frame) throw new ArgumentOutOfRangeException(nameof(frames));
        return AdvanceToFrame(Frame + frames, maxFrames, cancellationToken, maxWorkMilliseconds);
    }

    public bool AdvanceToFrame(int target, int maxFrames = 240, CancellationToken cancellationToken = default, int maxWorkMilliseconds = 25)
    {
        if (target < 0 || target > 36000) throw new ArgumentOutOfRangeException(nameof(target));
        if (maxFrames < 1 || maxFrames > 36000) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (maxWorkMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(maxWorkMilliseconds));
        cancellationToken.ThrowIfCancellationRequested();
        long started = Environment.TickCount64;
        if (target < Frame) Reset();
        RefreshSource();
        int stop = Math.Min(target, Frame + maxFrames);
        while (Frame < stop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { _state.Step(_step); }
            catch { Reset(); throw; }
            Frame++; _snapshot = null;
            if (Environment.TickCount64 - started >= maxWorkMilliseconds) break;
        }
        return Frame == target;
    }
}
