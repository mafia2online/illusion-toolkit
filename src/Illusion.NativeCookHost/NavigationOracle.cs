using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Illusion.Formats.Navigation;

namespace Illusion.NativeCookHost;

/// <summary>Process-local copied-PE Havok oracle. The PE entrypoint, TLS callbacks, imports,
/// game initialization and Steam are never executed. Only reviewed pure Havok entrypoints run.</summary>
internal sealed unsafe partial class NavigationOracle : IDisposable
{
    private const string SupportedHash = "1DB8D827C1528C3B80861EEED111A3F11A357FC5683F2B5640DBE119F871ABAF";
    private static readonly List<nint> Blocks = [];
    private static long _allocated;
    private nint _image;
    private readonly nint _profile;
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint type);
    private static nint Allocate(int size)
    {
        if (size < 0 || size > 64 * 1024 * 1024 || (_allocated += size) > 512L * 1024 * 1024)
            Environment.FailFast("Native cook allocation limit exceeded.");
        nuint capacity = (nuint)((Math.Max(size, 16) + 15L) & ~15L);
        void* p = NativeMemory.AlignedAlloc(capacity, 16);
        if (p == null) Environment.FailFast("Native cook allocation failed.");
        NativeMemory.Clear(p, capacity); Blocks.Add((nint)p); return (nint)p;
    }
    private static int I(nint p, int offset = 0) => *(int*)(p + offset);
    private static uint U(nint p, int offset = 0) => *(uint*)(p + offset);
    private static nint P(nint p, int offset = 0) => *(nint*)(p + offset);
    private static void W(nint p, int offset, int value) => *(int*)(p + offset) = value;
    private static void Q(nint p, int offset, nint value) => *(nint*)(p + offset) = value;
    private static Vector3 V(nint p) => *(Vector3*)p;
    private static void Copy(nint dst, nint src, int size) => NativeMemory.Copy((void*)src, (void*)dst, (nuint)size);
    [UnmanagedCallersOnly] private static nint BlockAlloc(nint _, int size) => Allocate(size);
    [UnmanagedCallersOnly] private static nint BufferAlloc(nint _, int* size) => Allocate(*size);
    [UnmanagedCallersOnly] private static nint BufferRealloc(nint _, nint old, int oldSize, int* size)
    { nint result = Allocate(*size); if (old != 0) Copy(result, old, Math.Min(oldSize, *size)); return result; }
    [UnmanagedCallersOnly] private static void Free(nint _, nint __, int ___) { }
    [UnmanagedCallersOnly] private static nint Profile(uint _) => _active!._profile;
    [UnmanagedCallersOnly] private static nint MemCopy(nint dst, nint src, nuint size)
    { new ReadOnlySpan<byte>((void*)src, checked((int)size)).CopyTo(new Span<byte>((void*)dst, checked((int)size))); return dst; }
    [UnmanagedCallersOnly] private static nint MemSet(nint dst, int value, nuint size)
    { new Span<byte>((void*)dst, checked((int)size)).Fill((byte)value); return dst; }
    private static NavigationOracle? _active;
    private static void Jump(nint address, nint target)
    { byte* p = (byte*)address; p[0] = 0x48; p[1] = 0xB8; *(nint*)(p + 2) = target; p[10] = 0xFF; p[11] = 0xE0; }

    public NavigationOracle(string executable)
    {
        string path = Path.GetFullPath(executable);
        if (!Path.GetFileName(path).Equals("mafia3definitiveedition.exe", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Native cooking requires a separate copied executable, outside Steam game directories.");
        byte[] bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != SupportedHash)
            throw new NotSupportedException("This copied executable's SHA-256 does not match the reviewed native cook ABI.");
        int pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60)), optional = pe + 24;
        int Int(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        nint expected = checked((nint)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(optional + 24)));
        if (expected != unchecked((nint)0x140000000L) || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 4)) != 0x8664)
            throw new NotSupportedException("Unsupported native PE architecture or base address.");
        _image = VirtualAlloc(expected, (nuint)Int(optional + 56), 0x3000, 0x40);
        if (_image != expected) throw new NotSupportedException("Native cook preferred image base is unavailable.");
        bytes.AsSpan(0, Int(optional + 60)).CopyTo(new Span<byte>((void*)_image, Int(optional + 60)));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6));
        int table = optional + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
        for (int s = 0; s < sectionCount; s++)
        {
            int h = table + s * 40, size = Int(h + 16), offset = Int(h + 20), rva = Int(h + 12);
            bytes.AsSpan(offset, size).CopyTo(new Span<byte>((void*)(_image + rva), size));
        }
        _active = this; _profile = Allocate(128);
        nint tableMemory = Allocate(128);
        Q(tableMemory, 8, (nint)(delegate* unmanaged<nint, int, nint>)&BlockAlloc);
        Q(tableMemory, 16, (nint)(delegate* unmanaged<nint, nint, int, void>)&Free);
        Q(tableMemory, 24, (nint)(delegate* unmanaged<nint, int*, nint>)&BufferAlloc);
        Q(tableMemory, 32, (nint)(delegate* unmanaged<nint, nint, int, void>)&Free);
        Q(tableMemory, 40, (nint)(delegate* unmanaged<nint, nint, int, int*, nint>)&BufferRealloc);
        *(nint*)0x145FF5B40L = tableMemory; *(nint*)0x145FF5B48L = tableMemory;
        *(nint*)0x1449A7378L = (nint)(delegate* unmanaged<uint, nint>)&Profile;
        Jump(unchecked((nint)0x143C6DBC0L), (nint)(delegate* unmanaged<nint, int, nint>)&BlockAlloc);
        foreach (long address in new[] { 0x143C6DD10L, 0x143C6DE40L, 0x143C6DE90L }) *(byte*)address = 0xC3;
        BindImport(0x1445E0480L, (nint)(delegate* unmanaged<nint, nint, nuint, nint>)&MemCopy);
        BindImport(0x1445E0486L, (nint)(delegate* unmanaged<nint, nint, nuint, nint>)&MemCopy);
        BindImport(0x1445E0498L, (nint)(delegate* unmanaged<nint, int, nuint, nint>)&MemSet);
    }
    private static void BindImport(long address, nint target)
    {
        byte* p = (byte*)address;
        if (p[0] != 0xFF || p[1] != 0x25) throw new InvalidDataException("Unexpected native CRT thunk.");
        *(nint*)(p + 6 + *(int*)(p + 2)) = target;
    }

    private static nint Map(byte[] bytes, int pack = 20)
    {
        nint raw = Allocate(bytes.Length + 16) + ((16 - (pack & 15)) & 15);
        bytes.CopyTo(new Span<byte>((void*)raw, bytes.Length));
        int count = I(raw, pack + 20); var sections = new (int Start, int Local, int Global, int Virtual)[count];
        for (int i = 0; i < count; i++)
        { int h = pack + 80 + i * 64; sections[i] = (pack + I(raw, h + 20), I(raw, h + 24), I(raw, h + 28), I(raw, h + 32)); }
        foreach (var section in sections)
        {
            for (int x = section.Local; x <= section.Global - 8; x += 8)
                if (I(raw, section.Start + x) >= 0) Q(raw, section.Start + I(raw, section.Start + x), raw + section.Start + I(raw, section.Start + x + 4));
            for (int x = section.Global; x <= section.Virtual - 12; x += 12)
                if (I(raw, section.Start + x) >= 0) Q(raw, section.Start + I(raw, section.Start + x), raw + sections[I(raw, section.Start + x + 4)].Start + I(raw, section.Start + x + 8));
        }
        return raw;
    }
    private static nint Array(nint data, int count)
    { nint p = Allocate(16); Q(p, 0, data); W(p, 8, count); W(p, 12, unchecked((int)0x80000000) | count); return p; }

    public byte[] Cook(Mafia3NavigationCookPlan plan)
    {
        int[] representatives;
        var baseline = Generate(plan.Baseline, plan, null, out representatives);
        if (baseline.Costs.Count != plan.GraphEdgeCount) throw new NotSupportedException("Native baseline graph edge count differs from the source.");
        for (int c = 0; c < plan.ClusterCount; c++)
        {
            var position = baseline.Positions[c];
            var storedPosition = new Vector3(BitConverter.ToSingle(plan.Baseline, plan.GraphPositions + c * 16),
                BitConverter.ToSingle(plan.Baseline, plan.GraphPositions + c * 16 + 4), BitConverter.ToSingle(plan.Baseline, plan.GraphPositions + c * 16 + 8));
            if (position != storedPosition) throw new NotSupportedException($"Native baseline does not reproduce source cluster position {c}.");
            int first = BinaryPrimitives.ReadInt32LittleEndian(plan.Baseline.AsSpan(plan.GraphNodes + c * 8));
            int count = BinaryPrimitives.ReadInt32LittleEndian(plan.Baseline.AsSpan(plan.GraphNodes + c * 8 + 4));
            for (int e = first; e < first + count; e++)
            {
                int address = plan.GraphEdges + e * 8;
                int target = BinaryPrimitives.ReadInt32LittleEndian(plan.Baseline.AsSpan(address + 4));
                ushort cost = BinaryPrimitives.ReadUInt16LittleEndian(plan.Baseline.AsSpan(address));
                if (BinaryPrimitives.ReadUInt16LittleEndian(plan.Baseline.AsSpan(address + 2)) != 0)
                    throw new NotSupportedException("Native baseline contains unsupported graph edge flags.");
                if (!baseline.Costs.TryGetValue((c, target), out ushort value) || value != cost)
                    throw new NotSupportedException($"Native baseline does not reproduce source cluster cost {c}->{target}; deformation is refused.");
            }
        }
        if (plan.Modified.AsSpan().SequenceEqual(plan.Baseline)) return plan.Baseline.ToArray();
        var cooked = Generate(plan.Modified, plan.ModifiedLayout ?? plan, representatives, out _);
        if (cooked.Costs.Count != baseline.Costs.Count || !cooked.Costs.Keys.ToHashSet().SetEquals(baseline.Costs.Keys))
            throw new NotSupportedException("Deformation changes native cluster connectivity.");
        byte[] result = plan.Modified.ToArray();
        for (int c = 0; c < plan.ClusterCount; c++)
        {
            var position = cooked.Positions[c];
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(plan.GraphPositions + c * 16), position.X);
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(plan.GraphPositions + c * 16 + 4), position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(plan.GraphPositions + c * 16 + 8), position.Z);
            int first = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(plan.GraphNodes + c * 8));
            int count = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(plan.GraphNodes + c * 8 + 4));
            for (int e = first; e < first + count; e++)
            {
                int address = plan.GraphEdges + e * 8, target = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(address + 4));
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(address), cooked.Costs[(c, target)]);
            }
        }
        // Both accelerator variants are optional to the proven native loader. They must never
        // retain old geometric caches; leave their unreachable objects/fixups intact for exact undo.
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(plan.RootCount), 2);
        foreach (var link in Mafia3NavMeshFile.Read(plan.Baseline).ReadStreaming().Graph)
            if (!plan.Baseline.AsSpan(plan.GraphPositions + link.Node * 16, 12).SequenceEqual(result.AsSpan(plan.GraphPositions + link.Node * 16, 12)))
                throw new NotSupportedException("Moving a streamed cluster representative requires joint peer cost recooking.");
        return result;
    }
    private sealed record Graph(Vector3[] Positions, Dictionary<(int, int), ushort> Costs);
    private static Graph Generate(byte[] bytes, Mafia3NavigationCookPlan plan, int[]? originalRepresentatives, out int[] representatives, byte[][]? streamingPeers = null)
    {
        nint raw = Map(bytes), mesh = raw + plan.MeshObject;
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
        W(instance, 456, 1);
        nint collection = Allocate(128), info = Allocate(48); Q(info, 0, instance); Q(collection, 32, info); W(collection, 40, 1);
        if (streamingPeers is not null) collection = StreamingCollection(bytes, mesh, instance, streamingPeers);
        nint centers = Allocate(plan.FaceCount * 16), normals = Allocate(plan.FaceCount * 16), temporary = Allocate(16);
        for (int f = 0; f < plan.FaceCount; f++)
        {
            ((delegate* unmanaged<nint, int, nint, nint>)0x143BB73C0L)(instance, f, centers + f * 16);
            ((delegate* unmanaged<nint, nint, int, nint, nint>)0x143BB7630L)(temporary, instance, f, normals + f * 16);
        }
        representatives = originalRepresentatives?.ToArray() ?? Enumerable.Repeat(-1, plan.ClusterCount).ToArray();
        if (originalRepresentatives == null)
        {
            var distances = Enumerable.Repeat(float.PositiveInfinity, plan.ClusterCount).ToArray();
            for (int f = 0; f < plan.FaceCount; f++)
            {
                int c = *(ushort*)(raw + plan.Faces + f * 16 + 12);
                float distance = Vector3.DistanceSquared(V(raw + plan.GraphPositions + c * 16), V(centers + f * 16));
                if (distance < distances[c]) { distances[c] = distance; representatives[c] = f; }
            }
            for (int c = 0; c < plan.ClusterCount; c++)
                if (representatives[c] < 0 || distances[c] > .00001f) throw new NotSupportedException("A source cluster position does not identify a representative face.");
        }
        nint context = Allocate(160); Q(context, 0, collection); Q(context, 8, mesh); Q(context, 112, centers); Q(context, 128, normals);
        for (int i = 0; i < 8; i++) *(float*)(context + 80 + i * 4) = -1;
        nint faceClusters = Allocate(plan.FaceCount * 4), repIds = Allocate(plan.ClusterCount * 4), repPositions = Allocate(plan.ClusterCount * 16);
        for (int f = 0; f < plan.FaceCount; f++) W(faceClusters, f * 4, *(ushort*)(raw + plan.Faces + f * 16 + 12));
        for (int c = 0; c < plan.ClusterCount; c++) { W(repIds, c * 4, representatives[c]); Copy(repPositions + c * 16, centers + representatives[c] * 16, 16); }
        nint settings = Allocate(128), output = Allocate(80);
        ((delegate* unmanaged<nint, nint>)0x143C31550L)(settings);
        if (streamingPeers is not null)
            return GenerateStreamingGraph(bytes, plan, representatives, centers, context, settings);
        foreach (int off in new[] { 16, 32, 48 }) W(output, off + 12, unchecked((int)0x80000000));
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint>)0x143C32140L)(context, output,
            Array(faceClusters, plan.FaceCount), Array(repIds, plan.ClusterCount), Array(repPositions, plan.ClusterCount), settings + 16, settings + 96);
        if (I(output, 40) != plan.ClusterCount || I(output, 56) is < 0 or > 100000)
            throw new InvalidDataException("Native graph builder returned invalid counts.");
        var costs = new Dictionary<(int, int), ushort>();
        for (int c = 0; c < plan.ClusterCount; c++)
        {
            nint node = P(output, 32) + c * 8;
            for (int e = I(node); e < I(node) + I(node, 4); e++)
            {
                nint edge = P(output, 48) + e * 8; ushort cost = *(ushort*)edge;
                if ((cost & 0x7F80) == 0x7F80 || !costs.TryAdd((c, I(edge, 4)), cost))
                    throw new NotSupportedException("Native query failed or produced duplicate graph edges.");
            }
        }
        if (plan.RequiredFaceConnections is { } connections)
        {
            if ((connections.Length & 1) != 0 || connections.Any(f => (uint)f >= plan.FaceCount)) throw new InvalidDataException("Invalid native face connectivity proof request.");
            nint work = Allocate(64);
            for (int i = 0; i < connections.Length; i += 2)
                foreach (var (from, to) in new[] { (connections[i], connections[i + 1]), (connections[i + 1], connections[i]) })
                {
                    float distance = ((delegate* unmanaged<nint, int, int, nint, nint, nint, float>)0x143C34F40L)(context, from, to, settings + 16, settings + 96, work);
                    if (!float.IsFinite(distance) || distance < 0 || distance >= 1e30f)
                        throw new NotSupportedException("Native pathfinding cannot traverse the newly authored face connection.");
                }
        }
        return new(Enumerable.Range(0, plan.ClusterCount).Select(c => V(P(output, 16) + c * 16)).ToArray(), costs);
    }
    public void Dispose()
    {
        foreach (nint p in Blocks) NativeMemory.AlignedFree((void*)p);
        Blocks.Clear(); _allocated = 0; _active = null;
        if (_image != 0) { VirtualFree(_image, 0, 0x8000); _image = 0; }
    }
}
