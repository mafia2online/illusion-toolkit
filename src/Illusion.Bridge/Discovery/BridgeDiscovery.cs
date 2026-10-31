using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Illusion.Bridge.Discovery;

/// <summary>The addon-published endpoint: where its server listens and which Blender owns it.</summary>
public sealed class BridgeEndpoint
{
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("blenderVersion")] public string? BlenderVersion { get; set; }
    [JsonPropertyName("addonVersion")] public string? AddonVersion { get; set; }
    [JsonPropertyName("startedUtc")] public string? StartedUtc { get; set; }
}

/// <summary>
/// Reads the discovery file the Blender addon writes on register (<c>%APPDATA%\Illusion\bridge.json</c>)
/// and owns the exchange-folder layout. A discovery file whose PID is dead is stale — the caller
/// deletes it and spawns a fresh Blender.
/// </summary>
public static class BridgeDiscovery
{
    /// <summary>Roaming app folder shared with the addon (which resolves it as %APPDATA%\Illusion).</summary>
    public static string AppDataDir => Environment.GetEnvironmentVariable("ILLUSION_BRIDGE_DIR") is { Length: > 0 } directory
        ? Path.GetFullPath(directory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Illusion");

    public static string DiscoveryFile => Path.Combine(AppDataDir, "bridge.json");

    public static string ExchangeRoot => Path.Combine(AppDataDir, "bridge", "exchange");

    /// <summary>The per-session exchange folder (created on demand) that .ilx payloads live in.</summary>
    public static string SessionExchangeDir(string sessionId)
    {
        string dir = Path.Combine(ExchangeRoot, sessionId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Parses the discovery file; null when missing or malformed.</summary>
    public static BridgeEndpoint? TryRead()
    {
        try
        {
            if (!File.Exists(DiscoveryFile)) return null;
            var endpoint = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllText(DiscoveryFile));
            return endpoint is { Port: > 0, Pid: > 0 } ? endpoint : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the publishing Blender process is still alive.</summary>
    public static bool IsAlive(BridgeEndpoint endpoint)
    {
        try
        {
            using Process process = Process.GetProcessById(endpoint.Pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // exited between lookup and query
        }
    }

    /// <summary>Deletes a stale discovery file (best-effort).</summary>
    public static void DeleteStale()
    {
        try { File.Delete(DiscoveryFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes exchange folders of long-dead sessions (payload files are debugging
    /// artifacts, not user data). Best-effort; called once per toolkit session.</summary>
    public static void SweepExchange(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(ExchangeRoot)) return;
            DateTime cutoff = DateTime.UtcNow - olderThan;
            foreach (string dir in Directory.GetDirectories(ExchangeRoot))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
