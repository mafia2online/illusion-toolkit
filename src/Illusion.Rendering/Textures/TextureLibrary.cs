using System.IO;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Textures;

/// <summary>One recorded texture acquisition: the cache entry's name and the SRV handle it held at acquire
/// time. Meshes collect these in <c>GpuMesh.Create</c> and hand them back via <see cref="TextureLibrary.Release"/>
/// on dispose; the handle lets a release recognise an entry that was replaced in between (negative-cache purge)
/// and skip it instead of underflowing the new entry's count.</summary>
public readonly record struct TextureLease(string Name, nint Handle);

/// <summary>
/// GPU-texture cache keyed by .dds name: looks up the file in registered folders (extracted district
/// folders + shared ones, e.g. ground_leto), loads via <see cref="DdsTexture"/>. Not found →
/// white 1×1 (so the mesh still renders).
/// Entries are reference-counted by the meshes that acquired them (<see cref="Acquire"/>/<see cref="Release"/>):
/// when the last mesh using a texture is disposed — e.g. its district streams out — the SRV is released, so
/// VRAM no longer grows monotonically over a long whole-map roaming session.
/// Thread-safe: D3D11 device object creation is free-threaded, so the loader thread may call
/// <see cref="Acquire"/>/<see cref="AddFolder"/> while the UI thread renders; the folder list and cache
/// are guarded by one lock (file IO and texture creation happen outside it).
/// </summary>
public sealed unsafe class TextureLibrary : IDisposable
{
    private sealed class Entry
    {
        public ComPtr<ID3D11ShaderResourceView> Srv;
        public int Refs;
    }

    private readonly GpuContext _gpu;
    private readonly object _sync = new();
    private readonly List<string> _folders = new();
    private Func<string, string?>? _fallbackResolver; // name → full path when no registered folder has it
    private int _foldersVersion; // bumped by AddFolder; guards Acquire against caching a stale miss
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    // Entries dropped by Invalidate while meshes still draw with them, keyed by SRV handle: they are no
    // longer findable by name, and die when the last lease on them is returned.
    private readonly Dictionary<nint, Entry> _retired = new();
    private ComPtr<ID3D11ShaderResourceView> _white;      // immutable after the ctor
    private ComPtr<ID3D11ShaderResourceView> _flatNormal; // immutable after the ctor

    public TextureLibrary(GpuContext gpu)
    {
        _gpu = gpu;
        _white = CreateWhite();
        _flatNormal = CreateFlatNormal();
    }

    /// <summary>Snapshot of the registered lookup folders — lets a second library (a material-preview
    /// viewport, the thumbnail renderer) mirror this one's search scope.</summary>
    public IReadOnlyList<string> Folders
    {
        get { lock (_sync) return _folders.ToArray(); }
    }

    /// <summary>Last-resort name→path resolver consulted when no registered folder has the texture —
    /// how the material editor reaches textures of districts that are not loaded (a global index).
    /// The resolver must be thread-safe; a null/missing result falls back to the white placeholder.</summary>
    public void SetFallbackResolver(Func<string, string?>? resolver)
    {
        lock (_sync) _fallbackResolver = resolver;
    }

    public void AddFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        lock (_sync)
        {
            if (_folders.Contains(folder)) return;
            _folders.Add(folder);
            _foldersVersion++;

            // Textures that were not found earlier and cached as the "white placeholder" may live in this
            // new folder. Drop their negative cache, otherwise Acquire() would return white forever (a bug
            // with incremental loading: a mesh references a .dds whose folder is added after the mesh).
            // Refs on a purged white entry are abandoned deliberately: white is never disposed, and a later
            // Release recognises the replaced entry by its handle and skips it.
            var misses = _cache.Where(kv => kv.Value.Srv.Handle == _white.Handle).Select(kv => kv.Key).ToList();
            foreach (string name in misses) _cache.Remove(name);
        }
    }

    /// <summary>
    /// Forgets a cached texture whose FILE was rewritten, so the next <see cref="Acquire"/> reads it again.
    /// Meshes that already hold the old view keep drawing with it until they rebind or are disposed — the
    /// view stays alive for exactly as long as their leases do.
    /// </summary>
    public void Invalidate(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_sync)
        {
            if (!_cache.Remove(name, out Entry? entry)) return;
            if (entry.Srv.Handle == _white.Handle) return; // a cached miss — nothing to keep alive
            _retired[(nint)entry.Srv.Handle] = entry;
        }
    }

    /// <summary>Resolves a texture and records the acquisition in <paramref name="leases"/> (nothing is
    /// recorded for an empty name or an uncached miss). Pass the leases back via <see cref="Release"/> when
    /// the owning mesh is disposed.</summary>
    public ComPtr<ID3D11ShaderResourceView> Acquire(string? name, List<TextureLease> leases)
    {
        if (string.IsNullOrEmpty(name)) return _white;

        string[] folders;
        Func<string, string?>? resolver;
        int version;
        lock (_sync)
        {
            if (_cache.TryGetValue(name, out Entry? cached))
            {
                cached.Refs++;
                leases.Add(new TextureLease(name, (nint)cached.Srv.Handle));
                return cached.Srv;
            }
            folders = _folders.ToArray(); // snapshot: file IO must not run under the lock
            resolver = _fallbackResolver;
            version = _foldersVersion;
        }

        ComPtr<ID3D11ShaderResourceView> srv = default;
        foreach (string folder in folders)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                srv = DdsTexture.Load(_gpu, File.ReadAllBytes(path));
            }
            catch
            {
                srv = default;
            }
            if (srv.Handle != null) break;
        }

        // No registered folder has it — ask the fallback resolver (the global mirror index), so the
        // material editor shows real textures even when their district is not loaded.
        if (srv.Handle == null && resolver?.Invoke(name) is { } resolved && File.Exists(resolved))
        {
            try
            {
                srv = DdsTexture.Load(_gpu, File.ReadAllBytes(resolved));
            }
            catch
            {
                srv = default;
            }
        }

        lock (_sync)
        {
            // Another caller may have loaded the same name while we did IO — keep the first, drop ours.
            if (_cache.TryGetValue(name, out Entry? existing))
            {
                if (srv.Handle != null && srv.Handle != existing.Srv.Handle) srv.Dispose();
                existing.Refs++;
                leases.Add(new TextureLease(name, (nint)existing.Srv.Handle));
                return existing.Srv;
            }
            if (srv.Handle == null)
            {
                // A folder registered while we searched may contain this texture: caching the stale miss
                // would repoison the negative cache right after AddFolder purged it, whitening the texture
                // for the whole session. Return white UNCACHED — the next Acquire retries with the new folder.
                if (version != _foldersVersion) return _white;
                srv = _white; // not found / failed — white
            }
            _cache[name] = new Entry { Srv = srv, Refs = 1 };
            leases.Add(new TextureLease(name, (nint)srv.Handle));
            return srv;
        }
    }

    /// <summary>Like <see cref="Acquire"/>, but for a normal-map slot: a missing/failed texture returns the
    /// flat-normal texel (tangent-space (0,0,1)) instead of white, so the shader decodes it to the plain
    /// vertex normal rather than garbage. Named textures still go through the normal cache/folders.</summary>
    public ComPtr<ID3D11ShaderResourceView> AcquireNormalOrFlat(string? name, List<TextureLease> leases)
    {
        if (string.IsNullOrEmpty(name)) return _flatNormal;
        var srv = Acquire(name, leases);
        return srv.Handle == _white.Handle ? _flatNormal : srv;
    }

    /// <summary>Returns a mesh's recorded acquisitions. An entry whose last user is gone is dropped from the
    /// cache and its SRV released (the white placeholder is never disposed). A lease whose entry was replaced
    /// in the meantime (negative-cache purge + reload) is recognised by its handle and skipped.</summary>
    public void Release(List<TextureLease> leases)
    {
        lock (_sync)
        {
            foreach (TextureLease lease in leases)
            {
                if (_retired.TryGetValue(lease.Handle, out Entry? retired))
                {
                    if (--retired.Refs > 0) continue;
                    _retired.Remove(lease.Handle);
                    retired.Srv.Dispose();
                    continue;
                }
                if (!_cache.TryGetValue(lease.Name, out Entry? entry)) continue;
                if ((nint)entry.Srv.Handle != lease.Handle) continue;
                if (--entry.Refs > 0) continue;
                _cache.Remove(lease.Name);
                if (entry.Srv.Handle != _white.Handle) entry.Srv.Dispose();
            }
        }
        leases.Clear();
    }

    private ComPtr<ID3D11ShaderResourceView> CreateWhite()
    {
        uint pixel = 0xFFFFFFFF; // 1×1 opaque white RGBA
        ComPtr<ID3D11ShaderResourceView> srv = DdsTexture.CreateImmutableSrv(
            _gpu, &pixel, 4, 1, 1, Format.FormatR8G8B8A8Unorm);
        // The white placeholder must never be null (Get() falls back to it), so fail loudly if it could not be made.
        if (srv.Handle == null) throw new InvalidOperationException("Failed to create the 1×1 white placeholder texture.");
        return srv;
    }

    private ComPtr<ID3D11ShaderResourceView> CreateFlatNormal()
    {
        uint pixel = 0xFFFF8080; // RGBA (128,128,255,255) → unpacks to tangent-space normal (0,0,1)
        ComPtr<ID3D11ShaderResourceView> srv = DdsTexture.CreateImmutableSrv(
            _gpu, &pixel, 4, 1, 1, Format.FormatR8G8B8A8Unorm);
        if (srv.Handle == null) throw new InvalidOperationException("Failed to create the 1×1 flat-normal placeholder texture.");
        return srv;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (Entry entry in _cache.Values)
            {
                if (entry.Srv.Handle != _white.Handle) entry.Srv.Dispose();
            }
            _cache.Clear();
            foreach (Entry entry in _retired.Values) entry.Srv.Dispose();
            _retired.Clear();
        }
        _flatNormal.Dispose();
        _white.Dispose();
    }
}
