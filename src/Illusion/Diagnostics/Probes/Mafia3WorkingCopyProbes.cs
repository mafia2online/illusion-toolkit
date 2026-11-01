using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    // Archive hashes alone miss a failed scenario that leaves edited companion GENRs in its
    // extraction cache. Audit the complete native working set after the authoring scenarios.
    private static void TestCopiedResourceRestoration(Action<string, bool> check)
    {
        foreach (var file in ResourceUnpacker.EnumerateGameSds())
        {
            var original = SdsArchive.Open(file.FullName);
            var current = SdsArchive.Pack(SdsMeshLoader.EnsureExtracted(file), MafiaEnvironment.Profile);
            bool exact = original.Version == current.Version && original.Flags == current.Flags
                && original.ResourceInfoXml == current.ResourceInfoXml && original.Unknown20.SequenceEqual(current.Unknown20)
                && original.SlotRamRequired == current.SlotRamRequired && original.SlotVramRequired == current.SlotVramRequired
                && original.OtherRamRequired == current.OtherRamRequired && original.OtherVramRequired == current.OtherVramRequired
                && original.ResourceTypes.SequenceEqual(current.ResourceTypes) && original.Entries.Count == current.Entries.Count
                && original.Entries.Zip(current.Entries).All(pair => pair.First.TypeId == pair.Second.TypeId
                    && pair.First.Version == pair.Second.Version && pair.First.FileHash == pair.Second.FileHash
                    && pair.First.SlotRamRequired == pair.Second.SlotRamRequired && pair.First.SlotVramRequired == pair.Second.SlotVramRequired
                    && pair.First.OtherRamRequired == pair.Second.OtherRamRequired && pair.First.OtherVramRequired == pair.Second.OtherVramRequired
                    && pair.First.Data!.SequenceEqual(pair.Second.Data!));
            check(file.Name + " final working resources and manifest match copied archive", exact);
        }
    }
}
