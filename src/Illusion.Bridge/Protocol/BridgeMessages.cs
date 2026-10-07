using System.Text.Json.Serialization;

namespace Illusion.Bridge.Protocol;

/// <summary>
/// Control-channel messages (NDJSON over localhost TCP, one JSON object per line, discriminated by
/// <c>type</c>). Payload geometry never travels inline — messages carry .ilx file paths. Every
/// message has a per-sender monotonic <see cref="Seq"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(HelloAckMessage), "hello_ack")]
[JsonDerivedType(typeof(HelloDeniedMessage), "hello_denied")]
[JsonDerivedType(typeof(LoadSceneMessage), "load_scene")]
[JsonDerivedType(typeof(SceneReadyMessage), "scene_ready")]
[JsonDerivedType(typeof(PushMessage), "push")]
[JsonDerivedType(typeof(PushAckMessage), "push_ack")]
[JsonDerivedType(typeof(SetOptionsMessage), "set_options")]
[JsonDerivedType(typeof(RequestPushMessage), "request_push")]
[JsonDerivedType(typeof(ClearSceneMessage), "clear_scene")]
[JsonDerivedType(typeof(SceneLostMessage), "scene_lost")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
public abstract class BridgeMessage
{
    [JsonPropertyName("seq")] public long Seq { get; set; }
}

/// <summary>The current wire protocol version; both peers must agree in the handshake.</summary>
public static class BridgeProtocol
{
    public const int Version = 1;
}

public sealed class HelloMessage : BridgeMessage
{
    [JsonPropertyName("session")] public string Session { get; set; } = "";
    [JsonPropertyName("toolkitVersion")] public string ToolkitVersion { get; set; } = "";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = BridgeProtocol.Version;
}

public sealed class HelloAckMessage : BridgeMessage
{
    [JsonPropertyName("blenderVersion")] public string BlenderVersion { get; set; } = "";
    [JsonPropertyName("addonVersion")] public string AddonVersion { get; set; } = "";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
}

public sealed class HelloDeniedMessage : BridgeMessage
{
    /// <summary>Session id of the toolkit instance that owns this Blender.</summary>
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class LoadSceneMessage : BridgeMessage
{
    /// <summary>Absolute path of the .ilx container to load.</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sceneName")] public string SceneName { get; set; } = "";
    [JsonPropertyName("autoPush")] public bool AutoPush { get; set; }
}

public sealed class SceneReadyMessage : BridgeMessage
{
    /// <summary>Ids of the objects the addon actually built.</summary>
    [JsonPropertyName("objects")] public List<string> Objects { get; set; } = new();
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
}

public sealed class PushMessage : BridgeMessage
{
    [JsonPropertyName("file")] public string File { get; set; } = "";

    /// <summary>"manual" (panel button) or "auto" (left Edit Mode).</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "manual";
    [JsonPropertyName("objects")] public List<string> Objects { get; set; } = new();
    [JsonPropertyName("deleted")] public List<string> Deleted { get; set; } = new();
    [JsonPropertyName("newObjects")] public int NewObjects { get; set; }
}

public sealed class PushSkip
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

/// <summary>A Blender-made material the push turned into a game material (or matched to one by name) —
/// the addon stamps the hash on its datablock, so later pushes send the identity instead of the pixels.
/// An EMPTY hash says the opposite: the material the datablock remembers is gone, and it should forget.</summary>
public sealed class PushMaterial
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";

    /// <summary>True when the bridge created the material (now or earlier in this toolkit session), false
    /// when it merely matched a material the game already had — only the former may have its texture
    /// replaced by a later push.</summary>
    [JsonPropertyName("authored")] public bool Authored { get; set; }
}

public sealed class PushAckMessage : BridgeMessage
{
    [JsonPropertyName("applied")] public List<string> Applied { get; set; } = new();
    [JsonPropertyName("skipped")] public List<PushSkip> Skipped { get; set; } = new();
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
    [JsonPropertyName("materials")] public List<PushMaterial> Materials { get; set; } = new();
}

public sealed class SetOptionsMessage : BridgeMessage
{
    [JsonPropertyName("autoPush")] public bool AutoPush { get; set; }
}

/// <summary>Toolkit → addon: export the bridge scene and push it back now — the programmatic
/// equivalent of the N-panel button (used by the e2e probes; harmless in interactive sessions).</summary>
public sealed class RequestPushMessage : BridgeMessage;

/// <summary>Toolkit → addon: the edit session ended — remove the bridge objects from the Blender
/// scene (Blender itself stays up, ready for the next load).</summary>
public sealed class ClearSceneMessage : BridgeMessage;

public sealed class SceneLostMessage : BridgeMessage
{
    /// <summary>"file_opened" | "addon_disabled" | "scene_cleared".</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public sealed class PingMessage : BridgeMessage;

public sealed class PongMessage : BridgeMessage;

public sealed class ErrorMessage : BridgeMessage
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("fatal")] public bool Fatal { get; set; }
}

public sealed class ByeMessage : BridgeMessage;
