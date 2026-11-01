using System.Numerics;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

internal sealed unsafe partial class NavigationOracle
{
    /// <summary>Read-only proof of the original offline streaming builder, using only bounded
    /// preallocated scratch arrays. The builder's allocation branches are never needed.</summary>
    public int ValidateStreamingCrossCosts(byte[][] resources)
    {
        if (resources.Length is < 2 or > 32 || resources.Sum(b => (long)b.Length) > 32 * 1024 * 1024)
            throw new NotSupportedException("Streaming cost validation requires 2..32 bounded sections.");
        var files = resources.Select(bytes => Mafia3NavMeshFile.Read(bytes)).ToArray();
        if (files.Select(f => f.ResourceId).Distinct().Count() != files.Length) throw new NotSupportedException("Duplicate streaming UID.");
        var plans = files.Select(f => f.PrepareInteriorDeformation(new Dictionary<int, Vector3>())).ToArray();
        int checkedCosts = 0;
        for (int i = 0; i < files.Length; i++)
            foreach (var link in files[i].ReadStreaming().Graph)
            {
                int j = System.Array.FindIndex(files, f => f.ResourceId == link.OppositeUid); if (j < 0) continue;
                _ = files[i].ValidateStreamingPair(files[j]);
                Vector3 Read(int section, int node)
                {
                    int at = plans[section].GraphPositions + node * 16;
                    return new(BitConverter.ToSingle(resources[section], at), BitConverter.ToSingle(resources[section], at + 4), BitConverter.ToSingle(resources[section], at + 8));
                }
                ushort cost = NativeStreamingCost(Read(i, link.Node), Read(j, link.OppositeNode));
                if (cost != link.Cost) throw new NotSupportedException("Native offline streaming cost does not reproduce the source metadata.");
                checkedCosts++;
            }
        return checkedCosts;
    }

    private static ushort NativeStreamingCost(Vector3 a, Vector3 b)
    {
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(a.Z) || !float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.Z) || !float.IsFinite(Vector3.DistanceSquared(a, b)))
            throw new NotSupportedException("Streaming representatives exceed the finite native math range.");
        nint meshA = Allocate(176), meshB = Allocate(176), graphA = Allocate(128), graphB = Allocate(128);
        Q(meshA, 16, Allocate(16)); Q(meshB, 16, Allocate(16));
        nint posA = Allocate(16), posB = Allocate(16); *(Vector3*)posA = a; *(Vector3*)posB = b;
        Q(graphA, 16, posA); Q(graphB, 16, posB);
        nint setA = Allocate(56), setB = Allocate(56);
        foreach (nint set in new[] { setA, setB })
            foreach (int off in new[] { 8, 24 })
            { Q(set, off, Allocate(16)); W(set, off + 12, unchecked((int)0x80000001)); }
        ((delegate* unmanaged<nint, int, int, nint, nint, nint, nint, int, uint, nint, nint, nint, nint>)0x143C37A90L)
            (meshA, 0, 0, graphA, setA, setA, meshB, 0, 0, graphB, setB, setB);
        if (I(setA, 16) != 1 || I(setA, 32) != 1 || I(setB, 16) != 1 || I(setB, 32) != 1)
            throw new InvalidDataException("Native offline streaming builder returned invalid scratch counts.");
        ushort cost = *(ushort*)(P(setA, 24) + 12);
        if (cost != *(ushort*)(P(setB, 24) + 12) || (cost & 0x7F80) == 0x7F80)
            throw new InvalidDataException("Native offline streaming builder returned invalid reciprocal costs.");
        return cost;
    }
}
