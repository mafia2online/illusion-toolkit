namespace Illusion.Formats.Archive;

/// <summary>What one resource asks the engine to budget for it — the four RAM/VRAM figures its archive entry
/// carries — and the payload size those figures were stated for.</summary>
public sealed record SdsMemoryRequirement(uint SlotRam, uint SlotVram, uint OtherRam, uint OtherVram, int DataSize);
