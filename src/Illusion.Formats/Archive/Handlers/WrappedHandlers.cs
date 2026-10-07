using System.Xml;
using System.Xml.XPath;
using Illusion.Formats.Hashing;
using Illusion.Formats.IO;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Formats.Archive.Handlers;

/// <summary>Texture: the entry wraps a DDS in a small header (name hash + MIP flag). Extraction strips
/// the wrapper and keeps the DDS; packing rebuilds it and budgets VRAM as the DDS payload minus its
/// 128-byte header — plus, for a texture with a MIP companion, that companion's payload: the stock
/// archives charge the separately streamed top level to the TEXTURE entry and leave the Mipmap entry at
/// zero (381 of 381 textures in italy.sds). Leaving it out under-reports the archive's video memory by the
/// size of every top level it carries.</summary>
internal sealed class TextureHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new TextureResource();
        using (var stream = new MemoryStream(entry.Data!))
        {
            resource.Deserialize(entry.Version, stream, ctx.Endian);
        }

        ctx.Manifest.WriteElementString("File", name);
        ctx.Manifest.WriteElementString("HasMIP", XmlConvert.ToString(resource.HasMIP));
        entry.Data = resource.Data;
        return name;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        byte hasMip = XmlConvert.ToByte(nav.Value);
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);
        descNode.InnerText = file;

        byte[] texData = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        var resource = new TextureResource(Fnv64.Hash(file), hasMip, texData);
        using (var stream = new MemoryStream())
        {
            resource.Serialize(entry.Version, stream, ctx.Endian);
            entry.Data = stream.ToArray();
        }
        entry.SlotVramRequired = (uint)(texData.Length - 128);
        if (hasMip != 0)
        {
            var companion = new FileInfo(ResourcePaths.Join(ctx.Folder, "MIP_" + file));
            if (companion.Exists && companion.Length > 128) entry.SlotVramRequired += (uint)(companion.Length - 128);
        }
        return entry;
    }
}

/// <summary>Mipmap: a texture's separately-streamed MIP chain, stored as "MIP_&lt;texture&gt;.dds" —
/// the name hash is of the texture name without the MIP_ prefix.</summary>
internal sealed class MipmapHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new TextureResource();
        resource.DeserializeMIP(entry.Version, new MemoryStream(entry.Data!), ctx.Endian);
        string fileName = "MIP_" + name;
        ctx.Manifest.WriteElementString("File", fileName);
        entry.Data = resource.Data;
        return fileName;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        byte[] texData = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        var resource = new TextureResource(Fnv64.Hash(file.Remove(0, 4)), 0, texData);
        using (var data = new MemoryStream())
        {
            resource.SerializeMIP(entry.Version, data, Endian.Little);
            entry.Data = data.ToArray();
        }
        descNode.InnerText = file.Remove(0, 4);
        return entry;
    }
}

/// <summary>Sound: an FSB bank wrapped with its name and size; extracts to "&lt;name&gt;.fsb"
/// (creating the nested folders its name encodes).</summary>
internal sealed class SoundHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new SoundResource();
        using (var stream = new MemoryStream(entry.Data!))
        {
            resource.Deserialize(entry.Version, stream, ctx.Endian);
        }
        entry.Data = resource.Data;

        string fileName = name + ".fsb";
        string[] dirs = name.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        string dir = ctx.Folder;
        for (int i = 0; i < dirs.Length - 1; i++)
        {
            dir = Path.Combine(dir, dirs[i]);
            Directory.CreateDirectory(dir);
        }

        ctx.Manifest.WriteElementString("File", fileName);
        return fileName;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value.Remove(nav.Value.Length - 4, 4);
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);
        descNode.InnerText = file;

        byte[] fileData = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file) + ".fsb");
        var resource = new SoundResource
        {
            Name = file,
            Data = fileData,
            FileSize = fileData.Length,
        };
        using (var stream = new MemoryStream())
        {
            resource.Serialize(entry.Version, stream, ctx.Endian);
            entry.Data = stream.ToArray();
        }
        entry.SlotRamRequired = 40;
        entry.SlotVramRequired = (uint)resource.FileSize;
        return entry;
    }
}

/// <summary>MemFile: a named text/blob payload with one extra flag (Unk2_V4) the manifest preserves.</summary>
internal sealed class MemFileHandler : IResourceHandler
{
    public string? Extract(ExtractContext ctx, ResourceEntry entry, string name)
    {
        var resource = new MemFileResource();
        using (var stream = new MemoryStream(entry.Data!))
        {
            resource.Deserialize(entry.Version, stream, ctx.Endian);
            entry.Data = resource.Data;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = resource.Name;
        }

        string[] dirs = name.Split('/');
        string dir = ctx.Folder;
        for (int i = 0; i < dirs.Length - 1; i++)
        {
            dir = Path.Combine(dir, dirs[i]);
            Directory.CreateDirectory(dir);
        }

        ctx.Manifest.WriteElementString("File", name);
        ctx.Manifest.WriteElementString("Unk2_V4", XmlConvert.ToString(resource.Unk2_V4));
        return name;
    }

    public ResourceEntry Pack(PackContext ctx, XPathNavigator nav, XmlNode descNode)
    {
        var entry = new ResourceEntry();
        nav.MoveToNext();
        string file = nav.Value;
        nav.MoveToNext();
        uint unk2 = XmlConvert.ToUInt32(nav.Value);
        nav.MoveToNext();
        entry.Version = XmlConvert.ToUInt16(nav.Value);

        var resource = new MemFileResource
        {
            Name = file,
            Unk1 = 1,
            Unk2_V4 = unk2,
        };
        resource.Data = File.ReadAllBytes(ResourcePaths.Join(ctx.Folder, file));
        entry.SlotRamRequired = (uint)resource.Data.Length;

        using (var stream = new MemoryStream())
        {
            resource.Serialize(entry.Version, stream, Endian.Little);
            entry.Data = stream.ToArray();
        }
        descNode.InnerText = file;
        return entry;
    }
}
