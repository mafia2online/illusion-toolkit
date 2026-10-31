namespace Illusion.Formats.Archive;

/// <summary>Platform tag in the SDS header. PC is little-endian; the console tags are big-endian.</summary>
public enum Platform : uint
{
    PC = 0x50430000,      // 'PC'
    Xbox360 = 0x58424F58, // 'XBOX'
    PS3 = 0x50533300,     // 'PS3'
}

/// <summary>An entry of the archive's resource-type table: numeric id ↔ type name (+ an opaque parent
/// index a few types carry).</summary>
public struct SdsResourceTypeEntry : IEquatable<SdsResourceTypeEntry>
{
    public uint Id;
    public string Name;
    public uint Parent;

    public bool Equals(SdsResourceTypeEntry other) =>
        Id == other.Id && string.Equals(Name, other.Name, StringComparison.Ordinal) && Parent == other.Parent;

    public override bool Equals(object? obj) => obj is SdsResourceTypeEntry other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Parent);

    public static bool operator ==(SdsResourceTypeEntry left, SdsResourceTypeEntry right) => left.Equals(right);
    public static bool operator !=(SdsResourceTypeEntry left, SdsResourceTypeEntry right) => !left.Equals(right);

    public override string ToString() => $"ID: {Id}, Name: {Name}, Parent: {Parent}";
}

/// <summary>One resource of the archive: its type, format version, payload bytes and the RAM/VRAM
/// accounting the engine budgets by.</summary>
public sealed class ResourceEntry
{
    public int TypeId = -1;
    public ushort Version;
    public ulong FileHash;
    public byte[]? Data;
    public uint SlotRamRequired;
    public uint SlotVramRequired;
    public uint OtherRamRequired;
    public uint OtherVramRequired;

    public override string ToString() => $"TypeID: {TypeId}, Version: {Version}, DataSize: {Data?.Length ?? 0}";
}

