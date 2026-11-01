using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestMipmappedWaterTextures(Action<string, bool> check)
    {
        using var gpu = new GpuContext();
        static byte[] Dds(int width, int height, int mips, int block, int pixel, uint fourCC, uint dxgi, bool bgra = false)
        {
            int length = dxgi == 0 ? 128 : 148;
            for (int mip = 0, w = width, h = height; mip < mips; mip++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
                length += block > 0 ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block : w * h * pixel;
            var bytes = new byte[length];
            void U(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
            U(0, 0x20534444); U(4, 124); U(8, 0x21007); U(12, (uint)height); U(16, (uint)width); U(28, (uint)mips); U(76, 32); U(108, 0x401008);
            if (dxgi != 0) { U(80, 4); U(84, 0x30315844); U(128, dxgi); U(132, 3); U(140, 1); }
            else if (fourCC != 0) { U(80, 4); U(84, fourCC); }
            else { U(80, 0x41); U(88, 32); U(92, bgra ? 0xFF0000u : 0xFFu); U(96, 0xFF00); U(100, bgra ? 0xFFu : 0xFF0000u); U(104, 0xFF000000); }
            int cursor = dxgi == 0 ? 128 : 148;
            for (int mip = 0, w = width, h = height; mip < mips; mip++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                int size = block > 0 ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block : w * h * pixel;
                for (int i = 0; i < size; i++) bytes[cursor + i] = (byte)((mip * 53 + i * 17 + 3) & 255);
                cursor += size;
            }
            return bytes;
        }
        (Texture2DDesc Desc, byte[][] Data) Read(ComPtr<ID3D11ShaderResourceView> view, int block, int pixel)
        {
            ID3D11Resource* ptr = null; view.GetResource(ref ptr);
            using ComPtr<ID3D11Resource> resource = ptr;
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            Texture2DDesc desc; texture.GetDesc(&desc);
            var stagingDesc = desc; stagingDesc.Usage = Usage.Staging; stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = (uint)CpuAccessFlag.Read; stagingDesc.MiscFlags = 0;
            ID3D11Texture2D* stagingPtr = null; SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in stagingDesc, (SubresourceData*)null, ref stagingPtr));
            using ComPtr<ID3D11Texture2D> staging = stagingPtr;
            gpu.Context11.CopyResource((ID3D11Resource*)staging.Handle, ptr);
            var result = new byte[desc.MipLevels][];
            for (uint mip = 0, w = desc.Width, h = desc.Height; mip < desc.MipLevels; mip++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                int pitch = block > 0 ? (int)Math.Max(1, (w + 3) / 4) * block : (int)w * pixel;
                int rows = block > 0 ? (int)Math.Max(1, (h + 3) / 4) : (int)h;
                var mapped = new MappedSubresource(); SilkMarshal.ThrowHResult(gpu.Context11.Map((ID3D11Resource*)staging.Handle, mip, Map.Read, 0, ref mapped));
                try
                {
                    var bytes = new byte[pitch * rows];
                    for (int row = 0; row < rows; row++) Marshal.Copy((nint)((byte*)mapped.PData + row * mapped.RowPitch), bytes, row * pitch, pitch);
                    result[mip] = bytes;
                }
                finally { gpu.Context11.Unmap((ID3D11Resource*)staging.Handle, mip); }
            }
            return (desc, result);
        }
        var cases = new (string Name, int Block, int Pixel, uint FourCC, uint Dxgi, bool Bgra)[]
        {
            ("DXT1",8,0,0x31545844,0,false), ("DXT3",16,0,0x33545844,0,false), ("DXT5",16,0,0x35545844,0,false),
            ("ATI1",8,0,0x31495441,0,false), ("ATI2",16,0,0x32495441,0,false),
            ("BC6H DX10",16,0,0,95,false), ("BC7 DX10",16,0,0,98,false),
            ("R8 DX10",0,1,0,61,false), ("RG8 DX10",0,2,0,49,false), ("RGBA8 DX10",0,4,0,28,false),
            ("RGBA16F DX10",0,8,0,10,false), ("RGBA32F DX10",0,16,0,2,false),
            ("legacy RGBA",0,4,0,0,false), ("legacy BGRA",0,4,0,0,true)
        };
        foreach (var item in cases)
        {
            int width = item.Block > 0 ? 8 : 7, height = item.Block > 0 ? 8 : 5;
            int mips = item.Block > 0 ? 4 : 3;
            byte[] dds = Dds(width, height, mips, item.Block, item.Pixel, item.FourCC, item.Dxgi, item.Bgra);
            using var view = DdsTexture.LoadMipmapped(gpu, dds);
            if (view.Handle is null) { check("full native mip GPU upload " + item.Name, false); continue; }
            var read = Read(view, item.Block, item.Pixel); int cursor = item.Dxgi == 0 ? 128 : 148;
            bool equal = read.Desc.MipLevels == mips && read.Desc.Width == width && read.Desc.Height == height;
            foreach (var bytes in read.Data) { equal &= bytes.AsSpan().SequenceEqual(dds.AsSpan(cursor, bytes.Length)); cursor += bytes.Length; }
            check("every authored mip survives GPU upload/readback " + item.Name, equal && cursor == dds.Length);
            using var truncated = DdsTexture.LoadMipmapped(gpu, dds[..^1]);
            check("truncated final native mip refused " + item.Name, truncated.Handle is null);
        }
        byte[] packed = Dds(8, 8, 4, 16, 0, 0x35545844, 0);
        foreach (var invalid in new[] { (3, (uint)0), (4, (uint)123), (28, (uint)5), (112, (uint)0x200), (24, (uint)8) })
        {
            var bytes = packed.ToArray();
            if (invalid.Item1 == 3) bytes[3] = 0; else BitConverter.GetBytes(invalid.Item2).CopyTo(bytes, invalid.Item1);
            using var rejected = DdsTexture.LoadMipmapped(gpu, bytes);
            check("invalid native DDS header refused at" + invalid.Item1, rejected.Handle is null);
        }
        byte[] array = Dds(8, 8, 4, 16, 0, 0, 77); BitConverter.GetBytes(2).CopyTo(array, 140);
        using (var rejected = DdsTexture.LoadMipmapped(gpu, array)) check("native mip DDS arrays remain refused", rejected.Handle is null);
        string folder = Path.GetFullPath(Path.Combine(".scratch", "mip-lease-probe-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(folder);
        var a = new List<TextureLease>(); var b = new List<TextureLease>(); var ordinary = new List<TextureLease>(); var fresh = new List<TextureLease>();
        using var library = new TextureLibrary(gpu);
        try
        {
            library.AddFolder(folder); File.WriteAllBytes(Path.Combine(folder, "mask.dds"), packed);
            var mipA = library.AcquireMipmapped("mask.dds", a); var mipB = library.AcquireMipmapped("mask.dds", b); var top = library.Acquire("mask.dds", ordinary);
            check("water native mips reuse their own lease cache independently of ordinary mip0", mipA.Handle == mipB.Handle && mipA.Handle != top.Handle
                && Read(mipA, 16, 0).Desc.MipLevels == 4 && Read(top, 16, 0).Desc.MipLevels == 1 && a.Single().Name != ordinary.Single().Name);
            library.Release(a);
            check("releasing one native-mip lease retains the other GPU resource", a.Count == 0 && Read(mipB, 16, 0).Desc.MipLevels == 4);
            library.Invalidate("mask.dds"); var replacement = library.AcquireMipmapped("mask.dds", fresh);
            check("water invalidation retires both texture policies without replacing live resources", library.RetiredCount == 2 && replacement.Handle != mipB.Handle && Read(mipB, 16, 0).Desc.MipLevels == 4);
            library.Release(b); library.Release(ordinary);
            check("returning retired ordinary and full-mip leases releases both entries", library.RetiredCount == 0);
            library.Release(fresh);
            var cache = (IDictionary)typeof(TextureLibrary).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(library)!;
            check("returning final native mip lease empties live cache", cache.Count == 0);
            var concurrent = Enumerable.Range(0, 32).Select(_ => new List<TextureLease>()).ToArray();
            var handles = new nint[concurrent.Length];
            try
            {
                Parallel.For(0, concurrent.Length, i => handles[i] = (nint)library.AcquireMipmapped("mask.dds", concurrent[i]).Handle);
                object entry = cache.Values.Cast<object>().Single();
                int refs = (int)entry.GetType().GetField("Refs")!.GetValue(entry)!;
                check("concurrent native mip acquisitions share exactly one32-reference cache entry", handles.All(handle => handle != 0 && handle == handles[0]) && refs == 32 && cache.Count == 1);
            }
            finally { foreach (var leases in concurrent) library.Release(leases); }
            check("all concurrent native mip leases return without live or retired entries", cache.Count == 0 && library.RetiredCount == 0);
            var miss = library.AcquireMipmapped("missing.dds", a); File.WriteAllBytes(Path.Combine(folder, "bad.dds"), packed[..^1]);
            var bad = library.AcquireMipmapped("bad.dds", a); var empty = library.AcquireMipmapped(null, a);
            var white = library.Acquire("missing.dds", ordinary);
            check("native water missing malformed and empty DDS refuse null without leases while ordinary remains white", miss.Handle is null && bad.Handle is null && empty.Handle is null && a.Count == 0 && white.Handle is not null);
        }
        finally
        {
            library.Release(a); library.Release(b); library.Release(ordinary); library.Release(fresh);
            foreach (string path in Directory.GetFiles(folder)) File.Delete(path); Directory.Delete(folder);
        }
    }
}
