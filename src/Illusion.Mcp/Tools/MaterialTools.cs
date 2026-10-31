using System.ComponentModel;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Reading material libraries (<c>.mtl</c>) — the shader assignments and texture bindings every mesh
/// in the game resolves through.
/// <para>
/// A frame's mesh does not name its material; it stores the FNV64 of the material's name and the
/// engine looks that up in a library. So "which material is 0x…?" and "what textures does this
/// material use?" are the two questions these tools answer, and the hash is reported beside every
/// material for exactly that reason.
/// </para>
/// Mafia II ships v57 libraries and the Definitive Edition v58. The two hold their samplers in
/// differently typed lists with no shared accessor, so <see cref="Samplers"/> matches the concrete
/// material type and enumerates what it actually stores.
/// </summary>
[McpServerToolType]
public sealed class MaterialTools
{
    [McpServerTool(Name = "open_mtl_file")]
    [Description("Open a material library (.mtl) and list its materials: name, name hash, shader id and hash, and the textures each one binds. Paginated.")]
    public static string OpenMtlFile(
        [Description("Full path to the .mtl file.")] string filePath,
        [Description("Index of the first material to return. Default 0.")] int offset = 0,
        [Description("How many materials to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Invalid($"no such file: {filePath}");
            }

            var library = new MaterialLibrary(MaterialVersion.V_57);
            library.ReadMatFile(filePath);

            List<IMaterial> materials = library.Materials.Values.ToList();
            (int start, int count) = Page.Clamp(offset, limit);
            List<IMaterial> window = Page.Slice(materials, start, count);

            return ToolResult.Json(new
            {
                success = true,
                path = filePath,
                // Read from the file itself, not from the value the library was constructed with —
                // ReadMatFile overwrites it, and reporting the constructor's guess would be a lie
                // on every DE library.
                version = library.Version.ToString(),
                versionNumber = (int)library.Version,
                total = materials.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                materials = window.Select(Summarize),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "list_mtl_files")]
    [Description("List the material libraries in a directory, each with its version and material count. Opens every file it finds, so point it at a materials folder rather than a whole install.")]
    public static string ListMtlFiles(
        [Description("Directory to search.")] string directoryPath,
        [Description("Search subdirectories too. Default true.")] bool recursive = true,
        [Description("Index of the first library to return. Default 0.")] int offset = 0,
        [Description("How many libraries to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return ToolResult.Invalid($"no such directory: {directoryPath}");
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
            };
            List<string> found = Directory
                .EnumerateFiles(directoryPath, "*.mtl", options)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            (int start, int count) = Page.Clamp(offset, limit);
            List<string> window = Page.Slice(found, start, count);

            var libraries = new List<object>(window.Count);
            foreach (string path in window)
            {
                // One unreadable library must not fail the listing — a folder of mods routinely has
                // a half-written or foreign-version file in it, and the caller wants the rest.
                try
                {
                    var library = new MaterialLibrary(MaterialVersion.V_57);
                    library.ReadMatFile(path);
                    libraries.Add(new
                    {
                        path,
                        name = Path.GetFileName(path),
                        version = library.Version.ToString(),
                        materialCount = library.Materials.Count,
                    });
                }
                catch (Exception ex)
                {
                    libraries.Add(new { path, name = Path.GetFileName(path), error = ex.Message });
                }
            }

            return ToolResult.Json(new
            {
                success = true,
                directoryPath,
                total = found.Count,
                offset = start,
                limit = count,
                returned = libraries.Count,
                libraries,
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "get_material_info")]
    [Description("Get one material in full: shader id and hash, flags, every sampler with the texture it binds, and every shader parameter with its float values. Select it by materialIndex, by name, or by the FNV64 hash a mesh references it with.")]
    public static string GetMaterialInfo(
        [Description("Full path to the .mtl file.")] string filePath,
        [Description("Zero-based index into the library's material list. Omit to select by name or hash.")] int materialIndex = -1,
        [Description("Material name, matched exactly. Omit to select by index or hash.")] string? name = null,
        [Description("Material name FNV64, as a mesh stores it. Omit to select by index or name.")] ulong hash = 0)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Invalid($"no such file: {filePath}");
            }

            var library = new MaterialLibrary(MaterialVersion.V_57);
            library.ReadMatFile(filePath);
            List<IMaterial> materials = library.Materials.Values.ToList();

            IMaterial? material;
            if (materialIndex >= 0)
            {
                if (materialIndex >= materials.Count)
                {
                    return ToolResult.Invalid(
                        $"materialIndex {materialIndex} is out of range — the library holds {materials.Count} materials");
                }
                material = materials[materialIndex];
            }
            else if (name is not null)
            {
                material = library.LookupMaterialByName(name);
            }
            else if (hash != 0)
            {
                material = library.LookupMaterialByHash(hash);
            }
            else
            {
                return ToolResult.Invalid("pass one of materialIndex, name or hash");
            }

            if (material is null)
            {
                return ToolResult.Invalid("no such material in this library");
            }

            return ToolResult.Json(new
            {
                success = true,
                path = filePath,
                version = library.Version.ToString(),
                material = Detail(material),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "search_materials")]
    [Description("Search a material library by name, case-insensitive substring. Returns the same summary as open_mtl_file for each match.")]
    public static string SearchMaterials(
        [Description("Full path to the .mtl file.")] string filePath,
        [Description("Substring to look for in the material name.")] string pattern,
        [Description("Index of the first match to return. Default 0.")] int offset = 0,
        [Description("How many matches to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Invalid($"no such file: {filePath}");
            }

            var library = new MaterialLibrary(MaterialVersion.V_57);
            library.ReadMatFile(filePath);

            List<IMaterial> matches = library.Materials.Values
                .Where(m => m.GetMaterialName().Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();

            (int start, int count) = Page.Clamp(offset, limit);
            List<IMaterial> window = Page.Slice(matches, start, count);

            return ToolResult.Json(new
            {
                success = true,
                path = filePath,
                pattern,
                total = matches.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                materials = window.Select(Summarize),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── shaping ──

    private static object Summarize(IMaterial material) => new
    {
        name = material.GetMaterialName(),
        // The FNV64 a mesh stores to reference this material — the key the engine looks it up by,
        // and what a caller chasing a mesh's material reference has in hand.
        hash = material.GetMaterialHash(),
        shaderId = material.ShaderID,
        shaderHash = material.ShaderHash,
        flags = (uint)material.Flags,
        textures = material.CollectTextures() ?? [],
        parameterCount = material.Parameters.Count,
    };

    private static object Detail(IMaterial material) => new
    {
        name = material.GetMaterialName(),
        hash = material.GetMaterialHash(),
        guid = material.MaterialGUID,
        shaderId = material.ShaderID,
        shaderHash = material.ShaderHash,
        flags = (uint)material.Flags,
        flagNames = material.Flags.ToString(),
        samplers = Samplers(material),
        parameters = material.Parameters.Select(p => new
        {
            id = p.ID,
            // The four-character ids are opaque; the format layer keeps the table that maps the
            // known ones to readable names, and an unknown id maps to itself.
            name = MaterialParameterNames.GetName(p.ID),
            values = p.Paramaters,
        }),
    };

    /// <summary>
    /// The material's texture samplers — all of them, read out of the list the material actually
    /// holds.
    /// <para>
    /// This used to probe <c>GetSamplerByKey</c> for S000 through S007, on the assumption that
    /// sampler ids are a small dense range. They are not: the retail <c>default.mtl</c> uses
    /// fourteen distinct ids running up to S072, so that loop returned four of them and dropped
    /// 3,678 bindings without a word — while the tool's own description promised every sampler.
    /// </para>
    /// v57 and v58 keep their samplers in differently typed lists with no common accessor, so the
    /// concrete type is matched here. An unrecognized material contributes nothing rather than
    /// silently reporting an empty sampler set as fact.
    /// </summary>
    private static IEnumerable<object> Samplers(IMaterial material)
    {
        IEnumerable<IMaterialSampler> stored = material switch
        {
            Material_v57 v57 => v57.Samplers,
            Material_v58 v58 => v58.Samplers,
            Material_v63 v63 => v63.Bindings,
            _ => [],
        };

        return stored.Select(sampler => new
        {
            id = sampler.ID,
            textureName = sampler.GetFileName(),
            textureHash = sampler.GetFileHash(),
            samplerStates = sampler.SamplerStates,
        }).ToList();
    }
}
