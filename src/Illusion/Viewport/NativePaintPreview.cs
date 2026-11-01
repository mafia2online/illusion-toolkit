using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;
using Illusion.Mcp;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>Read-only, bounded camera-cell preview. Workers consume immutable native snapshots only.</summary>
internal sealed class NativePaintPreview(D3DImageHost host) : IDisposable
{
    private const int MaximumCells = 4096, MaximumPoints = 1_000_000, MaximumMeshes = 4096, BatchSize = 8;
    private readonly record struct Key(CompiledNativePaintField Field, CompiledPaintCellAddress Address);
    private sealed class Entry(CompiledNativePaintCell data, GpuMesh[] meshes)
    {
        internal readonly CompiledNativePaintCell Data = data;
        internal readonly GpuMesh[] Meshes = meshes;
        internal ushort Active;
        internal bool Installed;
    }
    private sealed record Generated(Key Key, CompiledNativePaintCell? Data, string? Error, int Generation);
    private readonly Dictionary<Key, Entry> _cells = [];
    private readonly Dictionary<Key, string> _failed = [];
    private readonly HashSet<Key> _budgetFailed = [];
    private sealed record SourceStamp(int Objects, long Bounds, Dictionary<CompiledResource, long> Resources);
    private readonly Dictionary<CompiledSceneDocument, SourceStamp> _versions = [];
    private Key[] _workKeys = [];
    private readonly Queue<Generated> _completed = [];
    private readonly HashSet<Key> _awaiting = [];
    private Dictionary<Key, CompiledPaintDesiredCell> _wanted = [];
    private CompiledNativePaintField[] _fields = [];
    private readonly Dictionary<CompiledNativePaintField, SceneNode?> _owners = [];
    private Task<Generated[]>? _work;
    private CancellationTokenSource _cancellation = new();
    private bool _disposed, _refresh, _dirty = true;
    private bool _retryBudget;
    private int _generation, _points, _gpuMeshes, _budgetSkipped;
    private float _desiredElapsed;
    private Vector3 _lastCamera;
    private string? _error;
    internal bool Enabled { get; private set; } = true;
    internal uint ShuffleSeed { get; private set; } = 1;
    internal Action? BeforeGpuAllocation { get; set; }

    internal PaintPreviewInfo Status => new(Enabled, ShuffleSeed, _fields.Length, _cells.Count,
        _wanted.Keys.Count(key => !_cells.ContainsKey(key) && !_failed.ContainsKey(key)),
        _cells.Values.Sum(entry => (int)entry.Active), _points, _gpuMeshes,
        _failed.Count, _budgetSkipped, _error, Refusals(), RefusedFields:
        _fields.Count(profile => profile.RenderRefusal is not null) + _versions.Keys.Sum(document => document.NativePaintRefusals.Count));

    internal void Configure(bool? enabled, uint? seed)
    {
        bool nextEnabled = enabled ?? Enabled; uint nextSeed = seed ?? ShuffleSeed;
        if (nextEnabled == Enabled && nextSeed == ShuffleSeed) return;
        Reset(); Enabled = nextEnabled; ShuffleSeed = nextSeed;
    }
    internal void InvalidateMaterials() => _refresh = true;

    internal void Tick(float dt)
    {
        if (_disposed || host.Rnd is null) return;
        try
        {
            PollSources();
            if (!Enabled) return;
            _desiredElapsed += dt;
            var camera = host.Rnd.Camera.Position;
            if (_dirty || camera != _lastCamera && _desiredElapsed >= .05f) BuildDesired(camera);
            ObserveWork();
            var budget = Stopwatch.StartNew();
            while (_completed.Count > 0 && budget.ElapsedMilliseconds < 4)
            {
                var result = _completed.Dequeue(); _awaiting.Remove(result.Key);
                if (result.Generation != _generation || !_wanted.ContainsKey(result.Key)) continue;
                if (result.Error is { } error) { _failed[result.Key] = error; _error = error; continue; }
                Commit(result.Key, result.Data!); _dirty = true;
            }
            if (_dirty) BuildDesired(camera);
            foreach (var pair in _cells.ToArray())
            {
                var entry = pair.Value;
                bool visible = _owners.GetValueOrDefault(pair.Key.Field)?.IsVisible ?? true;
                foreach (var mesh in entry.Meshes) mesh.Visible = visible;
                if (entry.Data.FullCount == 0) continue;
                var count = CompiledPaintCellSelection.ActiveCount(pair.Key.Field.Context, pair.Key.Address, camera,
                    checked((ushort)entry.Data.FullCount), entry.Active);
                if (count.Updated && count.Count != entry.Active)
                {
                    try { SetActive(entry, count.Count); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { Remove(pair.Key); _failed[pair.Key] = _error = exception.Message; }
                }
            }
            StartWork();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { _error = exception.Message; }
    }

    private void PollSources()
    {
        var documents = new HashSet<CompiledSceneDocument>();
        var rows = new List<SceneNode>();
        void Walk(SceneNode row)
        {
            if (row.Source is CompiledSceneDocument document) documents.Add(document);
            rows.Add(row);
            foreach (var child in row.Children) Walk(child);
        }
        foreach (var root in host.Roots) Walk(root);
        foreach (var stale in _versions.Keys.Where(document => !documents.Contains(document)).ToArray()) _versions.Remove(stale);
        foreach (var document in documents)
        {
            bool known = _versions.TryGetValue(document, out var previous);
            bool changed = known && (previous!.Objects != document.ObjectCount || previous.Bounds != (document.NativeLods?.BoundsRevision ?? 0)
                || previous.Resources.Count != document.Graph.Resources.Count
                || document.Graph.Resources.Any(resource => !previous.Resources.TryGetValue(resource, out long revision) || resource.Revision != revision));
            if (_refresh || changed) document.RefreshNativePaintFields();
            if (!known || changed || _refresh)
                _versions[document] = new(document.ObjectCount, document.NativeLods?.BoundsRevision ?? 0,
                    document.Graph.Resources.ToDictionary(resource => resource, resource => resource.Revision));
        }
        _refresh = false;
        var fields = documents.SelectMany(document => document.NativePaintFields)
            .OrderBy(field => field.SourceArchive, StringComparer.OrdinalIgnoreCase).ThenBy(field => field.RootId)
            .DistinctBy(field => (field.SourceArchive.ToUpperInvariant(), field.RootId)).ToArray();
        if (!_fields.SequenceEqual(fields))
        {
            _fields = fields; _dirty = true; _owners.Clear();
            foreach (var field in fields)
                _owners[field] = rows.FirstOrDefault(row => row.Kind != "Lod"
                    && (row.GeometrySource ?? row.Source) is ICompiledMeshSource { GeometryBinding: { } binding,
                        CanonicalFrame: CompiledSceneDocument.CompiledFrameNode frame }
                    && frame.SceneIdentity == $"guid:{field.OwnerSceneId:X16}"
                    && row.OwningDocumentNode()?.Source is CompiledSceneDocument owner
                    && owner.Graph.Field(binding.Visual, "m_Data") is { DataSize: 88 } data
                    && BinaryPrimitives.ReadUInt64LittleEndian(owner.Graph.Data(data)[56..]) == field.OwnerVisualHash);
        }
    }

    private void BuildDesired(Vector3 camera)
    {
        var requests = new Dictionary<Key, CompiledPaintDesiredCell>();
        foreach (var field in _fields.Where(field => field.RenderRefusal is null))
        {
            var existing = _cells.Keys.Where(key => ReferenceEquals(key.Field, field)).Select(key => key.Address).ToArray();
            bool Populated(CompiledPaintCellAddress address) => _cells.TryGetValue(new(field, address), out var entry) && entry.Data.FullCount > 0;
            foreach (var desired in CompiledPaintCellSelection.GetDesiredCells(field.Context, camera, Populated, existing, 65536))
            {
                var address = desired.Address; var matrix = field.Context.CellMatrix(address.X, address.Y, address.Z, address.Level);
                var center = matrix.Translation; var half = field.Context.Size(address.Level) * .5f;
                var min = center - half; var max = center + half;
                if (max.X < field.BoundsMin.X || min.X > field.BoundsMax.X || max.Y < field.BoundsMin.Y || min.Y > field.BoundsMax.Y
                    || max.Z < field.BoundsMin.Z || min.Z > field.BoundsMax.Z) continue;
                requests[new(field, address)] = desired;
            }
        }
        _budgetSkipped = Math.Max(0, requests.Count - MaximumCells);
        _wanted = requests.OrderBy(pair => CompiledPaintCellSelection.Distance(pair.Key.Field.Context, pair.Key.Address, camera))
            .ThenBy(pair => pair.Key.Address.Level).Take(MaximumCells).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var key in _cells.Keys.Where(key => !_wanted.ContainsKey(key)).ToArray()) Remove(key);
        foreach (var key in _failed.Keys.Where(key => !_wanted.ContainsKey(key)).ToArray()) _failed.Remove(key);
        _budgetFailed.RemoveWhere(key => !_wanted.ContainsKey(key));
        if (_retryBudget)
        {
            foreach (var key in _budgetFailed) _failed.Remove(key);
            _budgetFailed.Clear(); _retryBudget = false;
        }
        _lastCamera = camera; _desiredElapsed = 0; _dirty = false;
    }

    private void StartWork()
    {
        if (_work is not null) return;
        var keys = _wanted.Where(pair => !pair.Value.Retained && !_cells.ContainsKey(pair.Key) && !_failed.ContainsKey(pair.Key)
            && !_awaiting.Contains(pair.Key)).Select(pair => pair.Key).Take(BatchSize).ToArray();
        if (keys.Length == 0) return;
        foreach (var key in keys) _awaiting.Add(key);
        _workKeys = keys;
        int generation = _generation; uint seed = ShuffleSeed; var token = _cancellation.Token;
        _work = Task.Run(() => keys.Select(key =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var address = key.Address;
                var data = key.Field.GenerateCell(address.X, address.Y, address.Z, address.Level, seed, ushort.MaxValue, token);
                return new Generated(key, data, data.RenderRefusal, generation);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return new Generated(key, null, exception.Message, generation); }
        }).ToArray(), token);
    }

    private void ObserveWork()
    {
        if (_work is not { IsCompleted: true } task) return;
        _work = null;
        var keys = _workKeys; _workKeys = [];
        try { foreach (var result in task.GetAwaiter().GetResult()) _completed.Enqueue(result); }
        catch (OperationCanceledException) { foreach (var key in keys) _awaiting.Remove(key); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { foreach (var key in keys) { _awaiting.Remove(key); _failed[key] = exception.Message; } _error = exception.Message; }
    }

    private void Commit(Key key, CompiledNativePaintCell data)
    {
        if (data.FullCount is < 0 or > ushort.MaxValue || data.FullCount == 0 && data.Meshes.Count > 0)
        { _failed[key] = _error = "Native paint cell has an invalid point layout."; return; }
        if (_points + data.FullCount > MaximumPoints || _gpuMeshes + data.Meshes.Count > MaximumMeshes)
        {
            _budgetFailed.Add(key);
            _failed[key] = _error = "Native paint preview exceeds its explicit point or GPU mesh budget."; return;
        }
        var created = new List<GpuMesh>();
        try
        {
            foreach (var source in data.Meshes)
            {
                if (source.Instances?.Length != data.FullCount) throw new InvalidDataException("Native paint mesh point ordering is incomplete.");
                source.NativeLod = key.Field.OwnerLod;
                BeforeGpuAllocation?.Invoke(); created.Add(host.Rnd!.CreateMeshGpu(source));
            }
            var entry = new Entry(data, created.ToArray());
            var count = CompiledPaintCellSelection.ActiveCount(key.Field.Context, key.Address, host.Rnd!.Camera.Position,
                checked((ushort)data.FullCount), 0);
            SetActive(entry, count.Count);
            foreach (var mesh in created) host.Rnd.AttachMesh(mesh);
            _cells.Add(key, entry); _points += data.FullCount; _gpuMeshes += created.Count; host.Tree.MeshCount += created.Count;
        }
        catch (Exception exception)
        {
            foreach (var mesh in created) { host.Rnd!.DetachMeshes([mesh]); mesh.Dispose(); }
            if (exception is OutOfMemoryException) throw;
            _failed[key] = _error = exception.Message;
        }
    }

    private void SetActive(Entry entry, ushort count)
    {
        if (entry.Installed && entry.Active == count) return;
        for (int index = 0; index < entry.Meshes.Length; index++)
        {
            var source = entry.Data.Meshes[index];
            host.Rnd!.UpdateInstances(entry.Meshes[index], source.Instances!.AsSpan(0, count).ToArray(),
                source.InstanceDrawDistances is null ? null : source.InstanceDrawDistances.AsSpan(0, count).ToArray(),
                source.InstanceDrawOrigins is null ? null : source.InstanceDrawOrigins.AsSpan(0, count).ToArray());
        }
        entry.Active = count; entry.Installed = true;
    }

    private void Remove(Key key)
    {
        var entry = _cells[key];
        host.Rnd?.DetachMeshes(entry.Meshes); foreach (var mesh in entry.Meshes) mesh.Dispose();
        host.Tree.MeshCount = Math.Max(0, host.Tree.MeshCount - entry.Meshes.Length);
        _points -= entry.Data.FullCount; _gpuMeshes -= entry.Meshes.Length; _cells.Remove(key);
        if (entry.Data.FullCount > 0 || entry.Meshes.Length > 0) _retryBudget = true;
    }

    internal void Reset()
    {
        if (_disposed) return;
        _generation++; _cancellation.Cancel(); _cancellation.Dispose(); _cancellation = new();
        if (_work is { } task) _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _work = null; _workKeys = [];
        foreach (var key in _cells.Keys.ToArray()) Remove(key);
        _completed.Clear(); _awaiting.Clear(); _failed.Clear(); _wanted.Clear(); _versions.Clear(); _owners.Clear(); _fields = [];
        _budgetFailed.Clear(); _retryBudget = false;
        _error = null; _budgetSkipped = 0; _dirty = true;
    }
    private IReadOnlyDictionary<string, int> Refusals() => _versions.Keys.SelectMany(document => document.NativePaintRefusals.Values)
        .Concat(_fields.Select(field => field.RenderRefusal).OfType<string>()).Concat(_failed.Values)
        .GroupBy(reason => reason, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    public void Dispose() { if (_disposed) return; Reset(); _cancellation.Dispose(); _disposed = true; }
}
