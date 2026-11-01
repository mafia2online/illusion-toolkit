using System.Buffers.Binary;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Textures;

public static unsafe partial class DdsTexture
{
    /// <summary>Loads the complete authored mip chain of a single packed 2D DDS. Unlike Load, this
    /// opt-in path validates all mip payloads and refuses arrays, cubemaps, volumes and unknown layouts.</summary>
    public static ComPtr<ID3D11ShaderResourceView> LoadMipmapped(GpuContext gpu, byte[] dds)
    {
        if (dds is null || dds.Length < 128) return default;
        uint U(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(offset));
        if (U(0) != 0x20534444 || U(4) != 124 || U(76) != 32) return default;
        uint width = U(16), height = U(12), mips = Math.Max(1, U(28));
        if (width is 0 or > 16384 || height is 0 or > 16384 || U(24) > 1 || (U(112) & 0x20FE00) != 0) return default;
        uint maxMips = 1;
        for (uint extent = Math.Max(width, height); extent > 1; extent >>= 1) maxMips++;
        if (mips > maxMips) return default;
        uint flags = U(80), fourCC = U(84); int offset = 128; Format format;
        if ((flags & DDPF_FOURCC) != 0)
        {
            if (fourCC == DXT1) format = Format.FormatBC1Unorm;
            else if (fourCC == DXT3) format = Format.FormatBC2Unorm;
            else if (fourCC == DXT5) format = Format.FormatBC3Unorm;
            else if (fourCC == ATI1 || fourCC == 0x55344342) format = Format.FormatBC4Unorm; // BC4U
            else if (fourCC == 0x53344342) format = Format.FormatBC4SNorm; // BC4S
            else if (fourCC == ATI2 || fourCC == 0x55354342) format = Format.FormatBC5Unorm; // BC5U
            else if (fourCC == 0x53354342) format = Format.FormatBC5SNorm; // BC5S
            else if (fourCC == DX10)
            {
                if (dds.Length < 148 || U(132) != 3 || U(140) != 1 || (U(136) & 4) != 0) return default;
                format = (Format)U(128); offset = 148;
            }
            else return default;
        }
        else
        {
            // The legacy RGB path must describe its masks rather than silently assume BGRA.
            if ((flags & 0x40) == 0 || U(88) != 32 || U(96) != 0x0000FF00) return default;
            if (U(92) == 0x00FF0000 && U(100) == 0x000000FF)
                format = U(104) == 0xFF000000 ? Format.FormatB8G8R8A8Unorm
                    : U(104) == 0 ? Format.FormatB8G8R8X8Unorm : Format.FormatUnknown;
            else if (U(92) == 0x000000FF && U(100) == 0x00FF0000 && U(104) == 0xFF000000)
                format = Format.FormatR8G8B8A8Unorm;
            else return default;
        }
        int block = BlockBytes(format);
        if ((uint)format is 95 or 96) block = 16; // BC6H UF16/SF16
        int pixel = block == 0 ? BytesPerPixel(format) : 0;
        if (block == 0 && pixel == 0) return default;
        // Ambiguous padded legacy scanlines are not guessed: native water assets are packed DDS.
        if (block == 0 && (U(8) & 8) != 0 && U(20) != width * pixel) return default;
        var initial = new SubresourceData[mips];
        fixed (byte* bytes = dds)
        {
            long cursor = offset;
            uint w = width, h = height;
            for (int mip = 0; mip < mips; mip++)
            {
                uint pitch = block > 0 ? Math.Max(1, (w + 3) / 4) * (uint)block : w * (uint)pixel;
                uint rows = block > 0 ? Math.Max(1, (h + 3) / 4) : h;
                long length = (long)pitch * rows;
                if (cursor > dds.Length || length > dds.Length - cursor) return default;
                initial[mip] = new SubresourceData { PSysMem = bytes + cursor, SysMemPitch = pitch, SysMemSlicePitch = checked((uint)length) };
                cursor += length; w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1);
            }
            var desc = new Texture2DDesc
            {
                Width = width, Height = height, MipLevels = mips, ArraySize = 1, Format = format,
                SampleDesc = new SampleDesc(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource
            };
            ID3D11Texture2D* texture = null;
            fixed (SubresourceData* data = initial)
            {
                int hr = gpu.Device11.CreateTexture2D(in desc, data, ref texture);
                if (hr < 0 || texture is null)
                {
                    if (texture is not null) ((ComPtr<ID3D11Texture2D>)texture).Dispose();
                    return default;
                }
            }
            ID3D11ShaderResourceView* view = null;
            int result = gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, ref view);
            ((ComPtr<ID3D11Texture2D>)texture).Dispose();
            if (result >= 0) return view;
            if (view is not null) ((ComPtr<ID3D11ShaderResourceView>)view).Dispose();
            return default;
        }
    }
}
