using System.Security.Cryptography;
using System.Xml.Linq;
using Illusion.Assets.Sds;
using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed class ProxySlotTextureCarry(IEditAction edit, string signature, string[] sourceFiles)
    {
        internal IEditAction Edit { get; } = edit;
        internal string Signature { get; } = signature;
        internal string[] SourceFiles { get; } = sourceFiles;
        internal bool Applied { get; set; } = true;
    }
    private readonly Dictionary<(ulong Parent, ulong Material, string Destination), ProxySlotTextureCarry> _proxySlotCarries = [];

    /// <summary>No-write history preflight. Ownership may currently be undone: a preceding
    /// composition child restores it before the actual table write.</summary>
    public string? ValidatePreparedLinkedProxyMaterialTable(IFrameNode source, IReadOnlyList<ulong[]> table)
    {
        var canonical = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (canonical is not CompiledFrameNode frame) return "Choose a canonical compiled fine object.";
        var state = _linkedProxyStates.Values.SingleOrDefault(s => s.Pairs.ContainsKey(frame));
        if (state is null) return ValidateSceneCollectionMutation(source);
        if (ValidateProxyMaterialSignatures(state) is { } material) return material;
        if (ValidateProxyInvariants(state) is { } invariant) return invariant;
        var allowed = _proxySlotDependencies.TryGetValue(state.Parent, out var history) ? history
            : state.MaterialTextures.Concat(state.FineMaterialTextures).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value);
        foreach (ulong hash in table.SelectMany(row => row).Distinct())
        {
            var signature = CompiledProxyMaterialGuard.TextureSignature(hash);
            if (signature is null || allowed.TryGetValue(hash, out var expected) && signature != expected)
                return "The target material is missing or its texture identity changed after this slot edit.";
            foreach (var carry in _proxySlotCarries.Where(pair => pair.Key.Parent == state.Parent && pair.Key.Material == hash).Select(pair => pair.Value))
            {
                if (carry.Signature != signature) return "The target material's carried texture identity changed.";
                foreach (string path in carry.SourceFiles)
                    if (!File.Exists(path)) return "A carried material source payload is missing: " + Path.GetFileName(path);
            }
        }
        return null;
    }

    private ProxySlotTextureCarry CarryProxySlotMaterial(ProxyRuntimeState state, ulong hash, string destination)
    {
        var signature = CompiledProxyMaterialGuard.TextureSignature(hash) ?? throw new NotSupportedException($"Native material {hash:X16} is unavailable.");
        if (_proxySlotCarries.TryGetValue((state.Parent, hash, destination), out var existing))
        {
            if (existing.Signature != signature) throw new NotSupportedException("The carried material's texture identity changed.");
            return existing;
        }
        MafiaMaterials.EnsureLoaded();
        var material = MafiaMaterials.Collection?.FindByHash(hash) as Material_v63 ?? throw new NotSupportedException("Select an existing native Mafia III material.");
        var required = material.CollectTextures().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => TextureSearchIndex.FindPath(name) ?? throw new NotSupportedException("Copy the native texture dependency before assigning: " + name))
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase); var edits = new List<IEditAction>();
        string workspace = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".scratch"));
        string temporary = Path.Combine(workspace, "proxy-slot-carry-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string targetFolder in new[] { Path.GetFullPath(destination) })
            {
                if (!_vehicleResources.TryGetValue(targetFolder, out var manager)) _vehicleResources.Add(targetFolder, manager = new SdsResourceCarry(targetFolder));
                foreach (string source in required.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string manifest = Path.Combine(source, "SDSContent.xml"); var xml = XElement.Load(manifest); sourceFiles.Add(manifest);
                    foreach (var row in xml.Elements("ResourceEntry").ToArray())
                    {
                        string file = row.Element("File")?.Value ?? throw new InvalidDataException("Missing native texture resource filename.");
                        if (Path.GetFileName(file) != file) throw new InvalidDataException("A texture resource filename escapes its folder.");
                        if (row.Element("Type")?.Value != "MemFile" && !required.Contains(Path.GetFullPath(Path.Combine(source, file)))) row.Remove();
                    }
                    if (!xml.Elements("ResourceEntry").Any()) throw new InvalidDataException("The native texture manifest has no selected payloads.");
                    string filtered = Path.Combine(temporary, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(filtered);
                    foreach (var row in xml.Elements("ResourceEntry"))
                    {
                        string file = row.Element("File")!.Value; sourceFiles.Add(Path.Combine(source, file));
                        File.Copy(Path.Combine(source, file), Path.Combine(filtered, file));
                    }
                    xml.Save(Path.Combine(filtered, "SDSContent.xml"));
                    edits.Add(manager.AddFrom(filtered, out var refusal) ?? throw new NotSupportedException(refusal));
                }
            }
            return new ProxySlotTextureCarry(new ProxyCarryEdit(edits), signature, sourceFiles.ToArray());
        }
        catch { foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo(); throw; }
        finally
        {
            if (!temporary.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid texture staging directory.");
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private void AcceptProxySlotSourceFiles(ProxyRuntimeState state, IEnumerable<ProxySlotTextureCarry> carries)
    {
        var paths = carries.SelectMany(carry => carry.SourceFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        state.Files = state.Files.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in paths) state.FileHashes.TryAdd(path, SHA256.HashData(File.ReadAllBytes(path)));
    }
}
