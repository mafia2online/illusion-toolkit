using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Illusion.Domain;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public sealed record NavigationDeformationResult(IEditAction? Edit, string? Refusal);

    /// <summary>Cooks planar-face interior vertex edits in a separate process. The source graph must
    /// first be reproduced exactly by the reviewed native ABI. Returned edits are already applied.</summary>
    public async Task<NavigationDeformationResult> DeformNavigationInteriorAsync(int sectionIndex,
        IReadOnlyDictionary<int, Vector3> vertices, string? copiedExecutablePath = null, CancellationToken cancellationToken = default)
    {
        if ((uint)sectionIndex >= _navMeshes.Count) return new(null, "Navigation section index is out of range.");
        try
        {
            var file = _navMeshes[sectionIndex];
            byte[] before = file.ToBytes();
            _ = file.PrepareInteriorDeformation(vertices);
            byte[] cooked = await NativeNavigationCooker.CookAsync(before, vertices, copiedExecutablePath, cancellationToken);
            if (!before.AsSpan().SequenceEqual(file.ToBytes())) return new(null, "Navigation changed while the native cook was running; retry the edit.");
            return new(new NavigationTranslationEdit(file.ApplyInteriorCook(cooked)), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }
}

/// <summary>JSON transport only. Native PE bytes are mapped exclusively by the isolated child.</summary>
public static partial class NativeNavigationCooker
{
    private sealed record Vertex(int Index, float X, float Y, float Z);
    private sealed record Request(string ExecutablePath, byte[] Resource, Vertex[] Edits, string? Operation = null, int? Edge = null, int? Face = null, Vertex? Position = null);
    private sealed record Response(bool Success, byte[]? Resource, string? Error);
    public static async Task<byte[]> CookAsync(byte[] resource, IReadOnlyDictionary<int, Vector3> vertices,
        string? copiedExecutablePath = null, CancellationToken cancellationToken = default, string? helperPath = null, int? flipEdge = null,
        int? subdivideFace = null, Vector3? subdivisionPosition = null, int? extrudeEdge = null, Vector3? extrusionPosition = null)
    {
        if ((flipEdge is not null ? 1 : 0) + (subdivideFace is not null ? 1 : 0) + (extrudeEdge is not null ? 1 : 0) > 1)
            throw new ArgumentException("One native navigation topology operation is allowed per request.");
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
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(helperPath)!,
            },
        };
        if (Path.GetFileName(helperPath).Equals("Illusion.exe", StringComparison.OrdinalIgnoreCase)) process.StartInfo.ArgumentList.Add("--native-nav-cook");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token), error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var request = new Request(executable, resource, vertices.Select(v => new Vertex(v.Key, v.Value.X, v.Value.Y, v.Value.Z)).ToArray(),
                extrudeEdge is not null ? "navigation_extrude" : subdivideFace is not null ? "navigation_subdivide" : flipEdge is null ? null : "navigation_flip", extrudeEdge ?? flipEdge,
                subdivideFace, (extrusionPosition ?? subdivisionPosition) is { } position ? new Vertex(0, position.X, position.Y, position.Z) : null);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            string responseText = await output, stderr = await error;
            if (string.IsNullOrWhiteSpace(responseText)) throw new InvalidDataException("Native cook worker failed without a result: " + stderr);
            var response = JsonSerializer.Deserialize<Response>(responseText) ?? throw new InvalidDataException("Invalid native cook response.");
            if (!response.Success || response.Resource is null || process.ExitCode != 0)
                throw new NotSupportedException(response.Error ?? "Native cook worker refused this resource.");
            return response.Resource;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { if (!process.HasExited) process.Kill(true); throw new InvalidOperationException("Native navigation cook timed out."); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
