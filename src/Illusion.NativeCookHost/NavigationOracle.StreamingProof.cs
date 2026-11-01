using System.Numerics;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

internal sealed unsafe partial class NavigationOracle
{
    private static nint ProofInstance(nint mesh, Mafia3NavigationCookPlan plan)
    {
        nint instance = Allocate(512);
        foreach (var (dst, src) in new[] { (16, 16), (32, 32), (48, 48), (64, 80), (80, 96) }) Q(instance, dst, P(mesh, src));
        foreach (var (dst, src) in new[] { (24, 24), (40, 40), (56, 56), (72, 112), (88, 116) }) W(instance, dst, I(mesh, src));
        Q(instance, 96, mesh);
        for (int i = 0; i < 3; i++) *(float*)(instance + 112 + i * 16 + i * 4) = 1;
        for (int off = 208; off < 448; off += 16) W(instance, off + 12, unchecked((int)0x80000000));
        foreach (var (off, count) in new[] { (208, plan.EdgeCount), (240, plan.FaceCount) })
        {
            nint data = Allocate(count * 4); new Span<byte>((void*)data, count * 4).Fill(255);
            Q(instance, off, data); W(instance, off + 8, count); W(instance, off + 12, unchecked((int)0x80000000) | count);
        }
        W(instance, 456, 1); return instance;
    }

    private static void ProveStreamingHalf(byte[][] resources, int faceA, int edgeA, int faceB, int edgeB)
    {
        var a = Mafia3NavMeshFile.Read(resources[0]); var b = Mafia3NavMeshFile.Read(resources[1]);
        var pa = a.PrepareInteriorDeformation(new Dictionary<int, Vector3>()); var pb = b.PrepareInteriorDeformation(new Dictionary<int, Vector3>());
        nint rawA = Map(resources[0]), rawB = Map(resources[1]), meshA = rawA + pa.MeshObject, meshB = rawB + pb.MeshObject;
        nint instanceA = ProofInstance(meshA, pa);
        nint collection = StreamingCollection(resources[0], meshA, instanceA, [resources[1]]);
        nint info = P(collection, 32), instanceB = P(info, 48);
        // Only the half under test remains open between the two scratch instances. A finite
        // cross-section query must actually traverse that half rather than the existing seam.
        foreach (var (file, instance, selected, peerId) in new[] { (a, instanceA, edgeA, b.ResourceId), (b, instanceB, edgeB, a.ResourceId) })
            foreach (var link in file.ReadStreaming().Mesh.Where(c => c.OppositeUid == peerId && c.Edge != selected))
                *((byte*)(P(instance, 32) + link.Edge * 20) + 16) |= 0x20;
        NativeCrossPath(collection, instanceA, faceA, 0, instanceB, faceB, 1);
        NativeCrossPath(collection, instanceB, faceB, 1, instanceA, faceA, 0);

        // Verify the exact offline connection builder's four IDs in both directions, not merely
        // editor parsing. Fake owning sets use preallocated one-record arrays, avoiding allocation.
        nint setA = Allocate(56), setB = Allocate(56);
        foreach (nint set in new[] { setA, setB }) foreach (int off in new[] { 8, 24 })
        { Q(set, off, Allocate(16)); W(set, off + 12, unchecked((int)0x80000001)); }
        ((delegate* unmanaged<nint, int, int, nint, nint, nint, nint, int, uint, nint, nint, nint, nint>)0x143C37A90L)
            (meshA, faceA, edgeA, rawA + pa.GraphObject, setA, setA, meshB, faceB, checked((uint)edgeB), rawB + pb.GraphObject, setB, setB);
        if (I(setA, 16) != 1 || I(setB, 16) != 1 || I(setA, 32) != 1 || I(setB, 32) != 1) throw new InvalidDataException("Native stream builder did not emit one reciprocal mesh/graph connection.");
        nint connA = P(setA, 8), connB = P(setB, 8);
        foreach (var (at, expected) in new[] { (0, faceA), (4, edgeA), (8, faceB), (12, edgeB) })
            if (I(connA, at) != expected) throw new InvalidDataException("Native stream builder source face/edge IDs disagree.");
        foreach (var (at, expected) in new[] { (0, faceB), (4, edgeB), (8, faceA), (12, edgeA) })
            if (I(connB, at) != expected) throw new InvalidDataException("Native stream builder peer face/edge IDs disagree.");
        int clusterA = *(ushort*)(P(meshA, 16) + faceA * 16 + 12), clusterB = *(ushort*)(P(meshB, 16) + faceB * 16 + 12);
        if (I(P(setA, 24)) != clusterA || I(P(setA, 24), 4) != clusterB || I(P(setB, 24)) != clusterB || I(P(setB, 24), 4) != clusterA)
            throw new InvalidDataException("Native stream builder reciprocal cluster IDs disagree.");
    }

    private static void NativeCrossPath(nint collection, nint fromInstance, int fromFace, int fromSection, nint toInstance, int toFace, int toSection)
    {
        nint from = Allocate(16), to = Allocate(16), normalA = Allocate(16), normalB = Allocate(16), temp = Allocate(16);
        ((delegate* unmanaged<nint, int, nint, nint>)0x143BB73C0L)(fromInstance, fromFace, from);
        ((delegate* unmanaged<nint, int, nint, nint>)0x143BB73C0L)(toInstance, toFace, to);
        ((delegate* unmanaged<nint, nint, int, nint, nint>)0x143BB7630L)(temp, fromInstance, fromFace, normalA);
        ((delegate* unmanaged<nint, nint, int, nint, nint>)0x143BB7630L)(temp, toInstance, toFace, normalB);
        nint settings = Allocate(128), input = Allocate(256), output = Allocate(80), work = Allocate(64);
        ((delegate* unmanaged<nint, nint>)0x143C31550L)(settings);
        ((delegate* unmanaged<nint, int, nint>)0x143BC84B0L)(input, 1);
        Copy(input + 16, from, 16); Copy(P(input, 32), to, 16);
        W(input, 48, (fromSection << 22) | fromFace); W(P(input, 56), 0, (toSection << 22) | toFace);
        Copy(input + 96, settings + 16, 80); Copy(input + 76, settings + 96, 8);
        Vector3 up = Vector3.Normalize(V(normalA) + V(normalB));
        if (!float.IsFinite(up.X) || !float.IsFinite(up.Y) || !float.IsFinite(up.Z)) throw new NotSupportedException("Opposing streaming patch normals cannot define native query up.");
        *(Vector3*)(input + 96) = up;
        ((delegate* unmanaged<nint, nint, void>)0x143BC86D0L)(input, work); *((byte*)input + 129) = 0;
        W(output, 8, unchecked((int)0xFFFF0001)); W(output, 28, unchecked((int)0x80000000)); W(output, 44, unchecked((int)0x80000000)); W(output, 52, -1); W(output, 56, 0x7F7FFFEE);
        ((delegate* unmanaged<nint, nint, nint, nint>)0x143BC8CD0L)(collection, input, output);
        float distance = *(float*)(output + 56);
        if (!float.IsFinite(distance) || distance < 0 || distance >= 1e30f)
            throw new NotSupportedException("Native cross-section pathfinding cannot traverse the isolated streaming half.");
    }
}
