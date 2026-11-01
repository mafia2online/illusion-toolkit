using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Native paint matrix/baked routing from actual template flags, with a bounded, identified stock fallback.</summary>
internal static class CompiledPaintMaterialRouting
{
    private static readonly object Sync=new();
    private static (string Path,long Length,long Stamp)? _key;
    private static IReadOnlyDictionary<ulong,CompiledShaderTemplateInfo>? _catalog;
    private static string? _error;
    // Explicitly measured native templates; this is not a material-name heuristic or a default for unknown shaders.
    private static readonly IReadOnlyDictionary<ulong,CompiledShaderTemplateInfo> Stock=new Dictionary<ulong,CompiledShaderTemplateInfo>
    {
        [0x2D622F8779451F8E]=new(0x2D622F8779451F8E,0x20000,0x00020001),[0x23B239A673BD3AE7]=new(0x23B239A673BD3AE7,0,0x00020001),
        [0x7269390167ABF56F]=new(0x7269390167ABF56F,0x20000,0x00020001),[0x132969A05BE80124]=new(0x132969A05BE80124,0,0x00000001),
        [0x7EF60F4E351B76C2]=new(0x7EF60F4E351B76C2,0x20000,0x00020001),[0x3CE1345F637A3E3B]=new(0x3CE1345F637A3E3B,0,0x00020001),
        [0x46543D8110365C3A]=new(0x46543D8110365C3A,0x20000,0x00020001),[0x139D4B3E3EB34EBA]=new(0x139D4B3E3EB34EBA,0,0x00020001),
        [0x55214EEF120B3600]=new(0x55214EEF120B3600,0,0x00000001),[0x129B7EEC20280CE1]=new(0x129B7EEC20280CE1,0,0x00020001),
        [0x1724679F30D136D4]=new(0x1724679F30D136D4,0,0x00020001),[0x3B113EE2235E5183]=new(0x3B113EE2235E5183,0x20000,0x00020001),
        [0x60912B19336956E4]=new(0x60912B19336956E4,0x20000,0x00020001),
        [0x22412C7B3A05089F]=new(0x22412C7B3A05089F,0x20000,0x00020001)
    };
    public static bool AllInstanced(IEnumerable<ulong> materials,out string source)
    {
        var cache=new FileInfo(Path.Combine(MafiaEnvironment.GameRoot,"edit","shaders","shaders_binaries_[dx11].sc"));
        IReadOnlyDictionary<ulong,CompiledShaderTemplateInfo> templates;
        if(cache.Exists)
        {
            lock(Sync)
            {
                var key=(cache.FullName,cache.Length,cache.LastWriteTimeUtc.Ticks);
                if(_key!=key)
                {
                    _key=key;_catalog=null;_error=null;
                    try{_catalog=CompiledShaderTemplateCatalog.Read(cache);}catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException){_error=ex.Message;}
                }
                templates=_catalog??throw new NotSupportedException("Native paint template flags unavailable: "+_error);
            }
            source="Configured native shader cache";
        }
        else{templates=Stock;source="Verified stock template flags (configured cache absent)";}
        bool all=true;int count=0;
        foreach(ulong material in materials.Distinct())
        {
            ulong template=MafiaMaterials.GetMaterialInfo(material,0,0).ShaderId;
            if(!templates.TryGetValue(template,out var info))throw new NotSupportedException($"Unknown native paint template flags for shader {template:X16}; no orientation route is guessed.");
            all&=info.IsInstanced;count++;
        }
        if(count==0)throw new NotSupportedException("Native paint has no resolved material templates.");
        return all;
    }
}
