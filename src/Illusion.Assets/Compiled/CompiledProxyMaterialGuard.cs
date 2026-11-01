using System.Security.Cryptography;
using System.Text.Json;
using Illusion.Formats;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Compiled;

/// <summary>Global material safety for loaded and persisted prepared proxies, including MTL-only saves.</summary>
public static class CompiledProxyMaterialGuard
{
    private static readonly object Sync = new();
    private sealed record TrackedDocument(WeakReference<CompiledSceneDocument> Reference, string Workspace);
    private static readonly List<TrackedDocument> Documents = [];
    internal static void Track(CompiledSceneDocument document)
    {
        lock (Sync)
        {
            Documents.RemoveAll(item => !item.Reference.TryGetTarget(out _));
            if (!Documents.Any(item => item.Reference.TryGetTarget(out var existing) && ReferenceEquals(existing, document)))
                Documents.Add(new(new(document), Path.GetFullPath(MafiaEnvironment.ResourcesFolder!)));
        }
    }

    /// <summary>Refuses texture/sampler or identity changes for a referenced material. Color/flag edits do not call this gate.</summary>
    public static string? ValidateMaterialMutation(ulong materialHash)
    {
        var pins = Collect(out string? error);
        if (error is not null) return error;
        return pins.Any(pin => pin.Hash == materialHash) ? $"Material {materialHash:X16} is referenced by a prepared linked proxy. "
            + "Its texture slots and identity are read-only until proxy preparation is undone and saved; automatic texture carry refresh is not supported." : null;
    }

    /// <summary>Must run before any global material library is written, even if no scene document is dirty.</summary>
    public static string? ValidateMaterialSave()
    {
        var pins = Collect(out string? error);
        if (error is not null) return error;
        foreach (var pin in pins)
            if (TextureSignature(pin.Hash) != pin.Signature)
                return $"Material {pin.Hash:X16} changed texture slots or identity after linked proxy preparation. "
                    + "Undo that material change before saving; the far archive's carried textures have not been rebuilt.";
        return null;
    }

    internal static string? TextureSignature(ulong hash)
    {
        MafiaMaterials.EnsureLoaded();
        if (MafiaMaterials.Collection?.FindByHash(hash) is not Material_v63 material) return null;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Hash = material.GetMaterialHash(), Name = material.MaterialName.String,
            Slots = material.Textures.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { Slot = pair.Key, pair.Value.Hash, Name = pair.Value.String }).ToArray()
        })));
    }

    private sealed record Pin(ulong Hash, string Signature);
    private static List<Pin> Collect(out string? error)
    {
        error = null; var pins = new List<Pin>();
        if (!MafiaEnvironment.IsInitialized || MafiaEnvironment.Profile.Variant != GameVariant.MafiaIII) return pins;
        try
        {
            CompiledSceneDocument[] documents;
            lock (Sync)
            {
                Documents.RemoveAll(item => !item.Reference.TryGetTarget(out _));
                string workspace = Path.GetFullPath(MafiaEnvironment.ResourcesFolder!);
                documents = Documents.Where(item => item.Workspace.Equals(workspace, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Reference.TryGetTarget(out var doc) ? doc : null).OfType<CompiledSceneDocument>().ToArray();
            }
            foreach (var document in documents)
                pins.AddRange(document.PreparedProxyMaterialSignatures().Select(pair => new Pin(pair.Key, pair.Value)));
            if (!Directory.Exists(CompiledProxySaveJournal.Workspace)) return pins;
            foreach (string path in Directory.GetFiles(CompiledProxySaveJournal.Workspace, "*.json"))
            {
                pins.AddRange(CompiledProxyMaterialMapVerifier.Read(path).Select(pair => new Pin(pair.Key, pair.Value)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { error = "Prepared proxy material safety could not be verified. " + ex.Message + " Restore or validate its workspace mapping before editing texture dependencies."; }
        return pins;
    }
}
