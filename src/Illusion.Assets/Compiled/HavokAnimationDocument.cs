using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Animations;

namespace Illusion.Assets.Compiled;

/// <summary>Persistent native animation working copy. Typed edits are in-memory history actions;
/// saving updates the native packfile and its SDS CPU reservation as one recoverable transaction.</summary>
public sealed class HavokAnimationDocument
{
    private readonly byte[] _original;
    private byte[] _current, _saved;
    private sealed class BudgetAccount(int baselineLength)
    {
        internal readonly int BaselineLength = baselineLength;
        internal IEditAction? Edit;
        internal long Bytes;
    }
    private sealed class SaveContext(string folder)
    {
        internal readonly string Folder = folder, Manifest = Path.Combine(folder, "SDSContent.xml");
        internal byte[] SavedManifest = File.ReadAllBytes(Path.Combine(folder, "SDSContent.xml"));
        internal readonly SdsResourceCarry Carry = new(folder);
        internal readonly Dictionary<string, BudgetAccount> Accounts = new(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly object ContextLock = new();
    private static readonly Dictionary<string, WeakReference<SaveContext>> Contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SaveContext? _context;
    private readonly BudgetAccount? _account;
    public string SourcePath { get; }
    public long Revision { get; private set; }
    public bool IsDirty => !_current.AsSpan().SequenceEqual(_saved);
    public IReadOnlyList<HavokAnimationClip> Clips { get; private set; }
    internal Action? SaveAfterNativeWrite { get; set; }

    public HavokAnimationDocument(string path)
    {
        SourcePath = Path.GetFullPath(path);
        if (MafiaEnvironment.ResourcesFolder is not null) CompiledProxySaveJournal.Recover();
        _original = _current = _saved = File.ReadAllBytes(SourcePath); Clips = HavokAnimationClip.Read(_current);
        string folder = Path.GetDirectoryName(SourcePath)!; string manifest = Path.Combine(folder, "SDSContent.xml");
        if (File.Exists(manifest))
        {
            lock (ContextLock)
            {
                foreach (var expired in Contexts.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()) Contexts.Remove(expired);
                if (!Contexts.TryGetValue(folder, out var weak) || !weak.TryGetTarget(out var context)
                    || !File.ReadAllBytes(manifest).AsSpan().SequenceEqual(context.SavedManifest))
                { context = new(folder); Contexts[folder] = new(context); }
                _context = context;
                if (!context.Accounts.TryGetValue(SourcePath, out _account)) context.Accounts.Add(SourcePath, _account = new(_original.Length));
            }
        }
        else if (folder.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native animation SDS working copy has no resource manifest.");
    }

    public byte[] ToArray() => _current.ToArray();
    public HavokAnimationFrame ReadFrame(int clip, int frame) => HavokAnimationAuthoring.ReadFrame(_current, clip, frame);
    public IEditAction SetTransformFrame(int clip, int frame, int track, Matrix4x4 transform)
        => Apply(HavokAnimationAuthoring.SetTransformFrame(_current, clip, frame, track, transform));
    public IEditAction SetFloatFrame(int clip, int frame, int track, float value)
        => Apply(HavokAnimationAuthoring.SetFloatFrame(_current, clip, frame, track, value));
    public IEditAction Bake(int clip, IReadOnlyList<HavokAnimationFrame> frames, float duration)
        => Apply(HavokAnimationAuthoring.Bake(_current, clip, frames, duration));

    private IEditAction Apply(byte[] next)
    {
        var decoded = HavokAnimationClip.Read(next); var edit = new AnimationEdit(this, _current, next, Clips, decoded); edit.Redo(); return edit;
    }
    private sealed class AnimationEdit(HavokAnimationDocument document, byte[] before, byte[] after,
        IReadOnlyList<HavokAnimationClip> beforeClips, IReadOnlyList<HavokAnimationClip> afterClips) : IEditAction
    {
        private bool _applied;
        private void Validate(byte[] expected)
        { if (!ReferenceEquals(document._current, expected)) throw new EditRefusedException("This native animation changed after the recorded edit."); }
        public void ValidateUndo() { if (_applied) Validate(after); }
        public void ValidateRedo() { if (!_applied) Validate(before); }
        public void Undo() { if (!_applied) return; ValidateUndo(); document._current = before; document.Clips = beforeClips; document.Revision++; _applied = false; }
        public void Redo() { if (_applied) return; ValidateRedo(); document._current = after; document.Clips = afterClips; document.Revision++; _applied = true; }
    }

    public void SaveWorkingCopy() => SaveWorkingCopies([this]);

    /// <summary>Preflights all selected animation documents and commits their payloads and shared
    /// manifests together. Clean sibling documents may be included; native budget provenance is per file.</summary>
    public static void SaveWorkingCopies(IEnumerable<HavokAnimationDocument> documents)
    {
        var requested = documents.Distinct().ToArray();
        foreach (var document in requested)
            if (!File.ReadAllBytes(document.SourcePath).AsSpan().SequenceEqual(document._saved))
                throw new EditRefusedException("A native animation file changed outside its document; reload before saving: " + Path.GetFileName(document.SourcePath));
        var contexts = requested.Select(document => document._context).OfType<SaveContext>().Distinct().ToArray();
        if (contexts.GroupBy(context => context.Folder, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new EditRefusedException("Animation documents use different revisions of the same native archive; reload them together before saving.");
        foreach (var context in contexts)
            if (!File.ReadAllBytes(context.Manifest).AsSpan().SequenceEqual(context.SavedManifest))
                throw new EditRefusedException("A native animation resource manifest changed outside its authoring context; reload before saving.");
        var dirty = requested.Where(document => document.IsDirty).GroupBy(document => document.SourcePath, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var selected = group.First();
            if (group.Any(document => !document._current.AsSpan().SequenceEqual(selected._current)))
                throw new EditRefusedException("Two native animation documents propose different bytes for the same resource.");
            return selected;
        }).ToArray();
        if (dirty.Length == 0) return;
        if (dirty.Any(document => document._context is null))
        {
            if (dirty.Length != 1) throw new NotSupportedException("Atomic native animation batch saves require extracted SDS working copies; save standalone packfiles individually.");
            var standalone = dirty[0]; AtomicFile.WriteAllBytes(standalone.SourcePath, standalone._current);
            foreach (var document in requested)
                if (document.SourcePath.Equals(standalone.SourcePath, StringComparison.OrdinalIgnoreCase) && document._current.AsSpan().SequenceEqual(standalone._current)) document._saved = document._current;
            return;
        }
        using var journal = new CompiledProxySaveJournal(dirty.Select(document => document._context!.Folder).Distinct(StringComparer.OrdinalIgnoreCase), []);
        var carryCheckpoints = contexts.Select(context => context.Carry.CaptureFlushCheckpoint()).ToArray();
        var budgets = new List<(HavokAnimationDocument Document, IEditAction? Previous, IEditAction? Next, long Growth)>();
        try
        {
            foreach (var document in dirty)
            {
                var account = document._account!; long growth = Math.Max(0, document._current.LongLength - account.BaselineLength);
                account.Edit?.Undo(); IEditAction? next = null;
                try
                {
                    if (growth > 0) next = document._context!.Carry.AddAnimationGrowthBudget(Path.GetFileName(document.SourcePath), growth, out var refusal)
                        ?? throw new NotSupportedException(refusal);
                }
                catch { account.Edit?.Redo(); throw; }
                budgets.Add((document, account.Edit, next, growth));
            }
            foreach (var document in dirty) { AtomicFile.WriteAllBytes(document.SourcePath, document._current); document.SaveAfterNativeWrite?.Invoke(); }
            foreach (var context in contexts) context.Carry.Flush();
            var snapshots = contexts.Select(context => (Context: context, Bytes: File.ReadAllBytes(context.Manifest))).ToArray(); journal.Commit();
            foreach (var snapshot in snapshots) snapshot.Context.SavedManifest = snapshot.Bytes;
            foreach (var budget in budgets) { budget.Document._account!.Edit = budget.Next; budget.Document._account.Bytes = budget.Growth; }
            foreach (var document in requested)
                if (dirty.Any(saved => saved.SourcePath.Equals(document.SourcePath, StringComparison.OrdinalIgnoreCase) && saved._current.AsSpan().SequenceEqual(document._current))) document._saved = document._current;
        }
        catch
        {
            foreach (var budget in budgets.AsEnumerable().Reverse()) { budget.Next?.Undo(); budget.Previous?.Redo(); }
            foreach (var restore in carryCheckpoints) restore(); throw;
        }
    }
}
