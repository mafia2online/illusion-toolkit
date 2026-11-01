using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Illusion.Domain;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public async Task<NavigationDeformationResult> DeformNavigationStreamingAsync(int sectionIndex, int peerIndex,
        IReadOnlyDictionary<int, Vector3> vertices, string? copiedExecutablePath = null, CancellationToken cancellationToken = default, bool moveRepresentatives = false)
    {
        if ((uint)sectionIndex >= _navMeshes.Count || (uint)peerIndex >= _navMeshes.Count || sectionIndex == peerIndex)
            return new(null, "Select two distinct navigation sections in the same document.");
        try
        {
            var source = _navMeshes[sectionIndex]; var peer = _navMeshes[peerIndex];
            byte[][] before = [source.ToBytes(), peer.ToBytes()];
            _ = source.PrepareStreamingDeformation(peer, vertices, moveRepresentatives);
            byte[][] cooked = await NativeNavigationCooker.CookStreamingAsync(before, vertices, copiedExecutablePath, cancellationToken, moveRepresentatives: moveRepresentatives);
            if (!source.ToBytes().AsSpan().SequenceEqual(before[0]) || !peer.ToBytes().AsSpan().SequenceEqual(before[1]))
                return new(null, "A navigation section changed during the joint cook; retry the edit.");
            return new(new NavigationPairEdit(source.ApplyStreamingCook(peer, vertices, cooked[0], cooked[1], moveRepresentatives)), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }
    private sealed class NavigationPairEdit(Mafia3NavigationStreamingEdit edit) : IEditAction
    {
        public void Undo() => edit.Undo();
        public void Redo() => edit.Redo();
    }
}

public static partial class NativeNavigationCooker
{
    public static async Task<byte[][]> CookStreamingAsync(byte[][] resources, IReadOnlyDictionary<int, Vector3> vertices,
        string? copiedExecutablePath = null, CancellationToken cancellationToken = default, string? helperPath = null, bool moveRepresentatives = false,
        int? splitEdge = null, Vector3? splitPoint = null, bool network = false)
    {
        if (network ? resources.Length is < 2 or > 32 : resources.Length != 2)
            throw new ArgumentException("Select a reciprocal pair or a bounded loaded navigation network.", nameof(resources));
        if (network && splitEdge is not null) throw new NotSupportedException("Network topology splitting has a separate native contract.");
        string executable;
        if (copiedExecutablePath is not null) executable = Path.GetFullPath(copiedExecutablePath);
        else
        {
            string source = Path.Combine(MafiaEnvironment.GameRoot, "mafia3definitiveedition.exe");
            if (!File.Exists(source)) source = Path.Combine(MafiaEnvironment.PcFolder, "mafia3definitiveedition.exe");
            if (!File.Exists(source)) throw new NotSupportedException("Native navigation cooking needs the supported copied Mafia III executable.");
            string hash;
            await using (var input = File.OpenRead(source)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IllusionToolkit", "NativeCook", hash);
            Directory.CreateDirectory(cache); executable = Path.Combine(cache, "mafia3definitiveedition.exe");
            if (!File.Exists(executable)) File.Copy(source, executable);
        }
        helperPath ??= File.Exists(Path.Combine(AppContext.BaseDirectory, "Illusion.exe"))
            ? Path.Combine(AppContext.BaseDirectory, "Illusion.exe") : Path.Combine(AppContext.BaseDirectory, "Illusion.NativeCookHost.exe");
        if (!File.Exists(helperPath)) throw new NotSupportedException("The isolated native navigation cook helper is not installed.");
        using var process = new Process { StartInfo = new ProcessStartInfo(helperPath) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(helperPath)!,
        } };
        if (Path.GetFileName(helperPath).Equals("Illusion.exe", StringComparison.OrdinalIgnoreCase)) process.StartInfo.ArgumentList.Add("--native-nav-cook");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token), error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var request = new { ExecutablePath = executable, Resource = Array.Empty<byte>(), Resources = resources, Operation = network ? "navigation_streaming_network" : splitEdge is null ? "navigation_streaming" : "navigation_stream_split", MoveRepresentatives = moveRepresentatives,
                Edge = splitEdge, Position = splitPoint is { } point ? new Vertex(0, point.X, point.Y, point.Z) : null,
                Edits = vertices.Select(v => new Vertex(v.Key, v.Value.X, v.Value.Y, v.Value.Z)).ToArray() };
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string text = await output, stderr = await error;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Joint native cook worker failed without a result: " + stderr);
            using var response = JsonDocument.Parse(text); var root = response.RootElement;
            if (process.ExitCode != 0 || !root.GetProperty("Success").GetBoolean())
                throw new NotSupportedException(root.TryGetProperty("Error", out var message) ? message.GetString() : "Joint native cook was refused.");
            var result = root.GetProperty("Resources").EnumerateArray().Select(e => e.GetBytesFromBase64()).ToArray();
            if (result.Length != resources.Length) throw new InvalidDataException("Joint native cook returned an invalid section count.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { if (!process.HasExited) process.Kill(true); throw new InvalidOperationException("Joint native navigation cook timed out."); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
