using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Textures;

/// <summary>
/// Minimal DDS loader → ID3D11ShaderResourceView. Supports BC1/2/3 (DXT1/3/5), BC4/5 (ATI1/2),
/// the extended DX10 header and 32-bit uncompressed. Loads only mip0 (enough for the viewport).
/// </summary>
public static unsafe class DdsTexture
{
    private const uint DXT1 = 0x31545844, DXT3 = 0x33545844, DXT5 = 0x35545844, DX10 = 0x30315844;
    private const uint ATI1 = 0x31495441, ATI2 = 0x32495441;
    private const uint DDPF_FOURCC = 0x4;

    public static ComPtr<ID3D11ShaderResourceView> Load(GpuContext gpu, byte[] dds)
    {
        if (dds == null || dds.Length < 128 || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S')
        {
            return default;
        }

        int height = BitConverter.ToInt32(dds, 12);
        int width = BitConverter.ToInt32(dds, 16);
        uint pfFlags = BitConverter.ToUInt32(dds, 80);
        uint fourCC = BitConverter.ToUInt32(dds, 84);

        int dataOffset = 128;
        Format fmt;
        int blockBytes;
        bool compressed = true;

        if (fourCC == DXT1) { fmt = Format.FormatBC1Unorm; blockBytes = 8; }
        else if (fourCC == DXT3) { fmt = Format.FormatBC2Unorm; blockBytes = 16; }
        else if (fourCC == DXT5) { fmt = Format.FormatBC3Unorm; blockBytes = 16; }
        else if (fourCC == ATI1) { fmt = Format.FormatBC4Unorm; blockBytes = 8; }
        else if (fourCC == ATI2) { fmt = Format.FormatBC5Unorm; blockBytes = 16; }
        else if (fourCC == DX10)
        {
            if (dds.Length < 148) return default; // 128-byte DDS header + 20-byte DX10 extension must be present
            uint dxgi = BitConverter.ToUInt32(dds, 128);
            fmt = (Format)dxgi;
            dataOffset = 148;
            blockBytes = BlockBytes(fmt);
            compressed = blockBytes > 0;
        }
        else if ((pfFlags & DDPF_FOURCC) == 0)
        {
            fmt = Format.FormatB8G8R8A8Unorm; // uncompressed — assume 32-bit BGRA
            blockBytes = 0;
            compressed = false;
        }
        else
        {
            return default; // unknown fourCC
        }

        // D3D11 caps texture dimensions at 16384; anything outside also guards the pitch math below.
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return default;

        int bytesPerPixel = 4;
        if (fourCC == DX10 && !compressed)
        {
            // The generic uncompressed path assumes 32-bit texels; a DX10 header can declare any DXGI format,
            // and a wrong stride would read out of bounds (sub-4-byte) or shear every row (over-4-byte).
            bytesPerPixel = BytesPerPixel(fmt);
            if (bytesPerPixel == 0) return default; // unsupported uncompressed DXGI format
        }

        int rowPitch;
        long requiredBytes;
        if (compressed)
        {
            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);
            rowPitch = blocksWide * blockBytes;
            requiredBytes = (long)rowPitch * blocksHigh;
        }
        else
        {
            rowPitch = width * bytesPerPixel;
            requiredBytes = (long)rowPitch * height;
        }

        // The header's declared size must actually be present — CreateTexture2D reads rowPitch × rows from the
        // pinned array, and a truncated/hand-edited .dds would make it walk past the end into the process heap.
        if (dataOffset >= dds.Length || dds.Length - dataOffset < requiredBytes) return default;

        ComPtr<ID3D11ShaderResourceView> srv;
        fixed (byte* pData = &dds[dataOffset])
        {
            srv = CreateImmutableSrv(gpu, pData, (uint)rowPitch, (uint)width, (uint)height, fmt);
        }
        return srv;
    }

    /// <summary>
    /// Builds an immutable 2D texture from raw pixel data (pinned by the caller) and returns its SRV,
    /// or <c>default</c> on failure. The intermediate texture is released — the SRV owns its reference.
    /// </summary>
    public static ComPtr<ID3D11ShaderResourceView> CreateImmutableSrv(GpuContext gpu, void* data,
        uint rowPitch, uint width, uint height, Format fmt)
    {
        var desc = new Texture2DDesc
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = fmt,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Immutable,
            BindFlags = (uint)BindFlag.ShaderResource,
        };
        var srd = new SubresourceData { PSysMem = data, SysMemPitch = rowPitch };
        ID3D11Texture2D* tex = null;
        if (gpu.Device11.CreateTexture2D(in desc, in srd, ref tex) < 0 || tex == null) return default;

        ID3D11ShaderResourceView* view = null;
        int hr = gpu.Device11.CreateShaderResourceView((ID3D11Resource*)tex, (ShaderResourceViewDesc*)null, ref view);
        ((ComPtr<ID3D11Texture2D>)tex).Dispose(); // SRV holds its own reference to the resource
        return hr < 0 ? default : view;
    }

    private static int BytesPerPixel(Format f)
    {
        switch (f)
        {
            case Format.FormatR8Unorm:
            case Format.FormatA8Unorm:
                return 1;
            case Format.FormatR8G8Unorm:
            case Format.FormatR16Unorm:
            case Format.FormatR16Float:
                return 2;
            case Format.FormatR8G8B8A8Unorm:
            case Format.FormatR8G8B8A8UnormSrgb:
            case Format.FormatB8G8R8A8Unorm:
            case Format.FormatB8G8R8A8UnormSrgb:
            case Format.FormatB8G8R8X8Unorm:
            case Format.FormatR10G10B10A2Unorm:
            case Format.FormatR11G11B10Float:
            case Format.FormatR16G16Unorm:
            case Format.FormatR16G16Float:
            case Format.FormatR32Float:
                return 4;
            case Format.FormatR16G16B16A16Unorm:
            case Format.FormatR16G16B16A16Float:
            case Format.FormatR32G32Float:
                return 8;
            case Format.FormatR32G32B32A32Float:
                return 16;
            default:
                return 0; // unknown — refuse rather than guess a stride
        }
    }

    private static int BlockBytes(Format f)
    {
        switch (f)
        {
            case Format.FormatBC1Unorm:
            case Format.FormatBC1UnormSrgb:
            case Format.FormatBC4Unorm:
            case Format.FormatBC4SNorm:
                return 8;
            case Format.FormatBC2Unorm:
            case Format.FormatBC2UnormSrgb:
            case Format.FormatBC3Unorm:
            case Format.FormatBC3UnormSrgb:
            case Format.FormatBC5Unorm:
            case Format.FormatBC5SNorm:
            case Format.FormatBC7Unorm:
            case Format.FormatBC7UnormSrgb:
                return 16;
            default:
                return 0; // uncompressed
        }
    }
}
