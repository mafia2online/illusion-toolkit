namespace Illusion.Domain;

/// <summary>The persistence half of an undoable subtree removal.</summary>
public interface IFrameDetachment
{
    void Detach();
    void Reattach();
    string? VolumesRefused => null;
}

/// <summary>A document with its own serialized object-removal rules. Null means the requested
/// removal is unsupported and the host must keep the objects in the scene.</summary>
public interface IFrameDetachmentProvider
{
    IFrameDetachment? CaptureDetachment(IReadOnlyList<IFrameNode> frames);
}
