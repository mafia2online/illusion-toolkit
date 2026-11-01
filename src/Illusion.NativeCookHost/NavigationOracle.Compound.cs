using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.NativeCookHost;

internal sealed unsafe partial class NavigationOracle
{
    // Pure PC hkAabbUtil::calcAabb: no allocation, virtual dispatch or engine initialization.
    public int ValidateCompoundTransforms(byte[] packet, int? selectedVariant)
    {
        var file = new HavokMeshPackfile(packet);
        var types = file.Variants.ToDictionary(v => v.Address, v => v.Type);
        var affected = new HashSet<int>();
        if (selectedVariant is { } selected)
        {
            if ((uint)selected >= file.Variants.Count) throw new ArgumentOutOfRangeException(nameof(selectedVariant));
            affected.Add(file.Variants[selected].Address);
            bool changed;
            do
            {
                changed = false;
                foreach (var variant in file.Variants.Where(v => v.Type == "hknpDynamicCompoundShape"))
                {
                    var instances = file.Array(variant.Address + 96, 128);
                    for (int i = 0; i < instances.Count; i++)
                        if (affected.Contains(file.Pointer(instances.Address + i * 128 + 80))) changed |= affected.Add(variant.Address);
                }
            } while (changed);
        }
        nint transform = Allocate(64), box = Allocate(32), result = Allocate(32);
        int count = 0;
        foreach (var variant in file.Variants.Where(v => v.Type == "hknpDynamicCompoundShape"))
        {
            var children = file.Array(variant.Address + 96, 128);
            for (int i = 0; i < children.Count; i++)
            {
                int instance = children.Address + i * 128, child = file.Pointer(instance + 80);
                if (!types.TryGetValue(child, out var type) || type is not ("hknpCompressedMeshShape" or "hknpDynamicCompoundShape")) continue;
                int source = type == "hknpCompressedMeshShape" ? file.Pointer(child + 96) + 32 : child + 128;
                Vector3 Read(int p) => new(file.F(p), file.F(p + 4), file.F(p + 8));
                var bounds = new HavokCompoundBoundsWriter.Bounds(Read(source), Read(source + 16));
                *(Vector3*)box = bounds.Min; *(Vector3*)(box + 16) = bounds.Max;
                for (int column = 0; column < 3; column++)
                {
                    *(Vector3*)(transform + column * 16) = Read(instance + column * 16) * file.F(instance + 64 + column * 4);
                    *(float*)(transform + column * 16 + 12) = 0;
                }
                *(Vector3*)(transform + 48) = Read(instance + 48); *(float*)(transform + 60) = 1;
                ((delegate* unmanaged<nint, nint, nint, void>)0x143C74EA0L)(transform, box, result);
                var managed = HavokCompoundBoundsWriter.Transform(bounds, packet.AsSpan(instance, 128));
                if (V(result) != managed.Min || V(result + 16) != managed.Max)
                    throw new InvalidDataException($"Native compound AABB transform differs at instance {i} in {variant.Address:X}.");
                if (affected.Contains(child))
                {
                    var nodes = file.Array(file.Pointer(variant.Address + 192) + 16, 32);
                    int leaf = file.U16(instance + 60);
                    var leafBox = new HavokCompoundBoundsWriter.Bounds(Read(nodes.Address + leaf * 32), Read(nodes.Address + leaf * 32 + 16));
                    if (!leafBox.Contains(managed)) throw new InvalidDataException("Refitted compound leaf misses native child bounds.");
                    if ((file.U(instance + 12) & 0x40) != 0 && !new HavokCompoundBoundsWriter.Bounds(Read(variant.Address + 128), Read(variant.Address + 144)).Contains(managed))
                        throw new InvalidDataException("Refitted compound cached bounds miss an enabled native child.");
                    var visited = new HashSet<int>();
                    while (file.U16(nodes.Address + leaf * 32 + 12) is > 0 and var parent)
                    {
                        if (!visited.Add(parent) || parent >= nodes.Count) throw new InvalidDataException("Invalid compound ancestor chain.");
                        var parentBox = new HavokCompoundBoundsWriter.Bounds(Read(nodes.Address + parent * 32), Read(nodes.Address + parent * 32 + 16));
                        if (!parentBox.Contains(leafBox)) throw new InvalidDataException("Compound ancestor misses a refitted child.");
                        leafBox = parentBox; leaf = parent;
                    }
                }
                count++;
            }
        }
        if (count == 0) throw new NotSupportedException("No supported compound transforms to validate.");
        return count;
    }
}
