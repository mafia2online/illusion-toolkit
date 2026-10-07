using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Rendering.Shaders;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Gpu;

[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;
    public Vector3 Tangent;   // local frame; transformed to world in the VS for normal mapping
    public Vector3 Binormal;  // decoder-supplied, handedness-signed
}

/// <summary>
/// The skin of one vertex, in its own stream. Kept off <see cref="MeshVertex"/> on purpose: a district is
/// millions of vertices and not one of them is skinned, so paying twenty bytes each for a channel only a car
/// or a character uses would be a third of its vertex memory spent on nothing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinVertex
{
    public byte Bone0;
    public byte Bone1;
    public byte Bone2;
    public byte Bone3;
    public Vector4 Weights;
}

/// <summary>Range of indices with its material's texture maps (SRVs belong to TextureLibrary).</summary>
public struct GpuPart
{
    public uint StartIndex;
    public uint IndexCount;
    public ulong MaterialHash;                          // source game material (0 = none) — for live re-resolve
    public Vector4 Tint;                                // multiplies the albedo; white unless the material paints itself
    public ComPtr<ID3D11ShaderResourceView> Srv;        // diffuse t0
    public ComPtr<ID3D11ShaderResourceView> NormalSrv;  // normal t1 (flat-normal when absent)
    public ComPtr<ID3D11ShaderResourceView> SpecSrv;    // specular-level t2 (white when absent → full level)
}

/// <summary>GPU buffers of a single mesh + its parts by material + world matrix.</summary>
public sealed unsafe class GpuMesh : IDisposable
{
    public ComPtr<ID3D11Buffer> VertexBuffer;
    public ComPtr<ID3D11Buffer> IndexBuffer;
    public Matrix4x4 World;
    public bool Visible = true;

    /// <summary>Bridge edit mode: true renders the mesh desaturated/translucent in a separate pass
    /// (it is NOT part of the set currently open in Blender). Managed by SceneRenderer.SetGhostFocus.</summary>
    public bool Ghost;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public int TriangleCount;
    public readonly List<GpuPart> Parts = new();

    // Hardware instancing (city_crash): geometry is shared, copies live in a separate matrix buffer.
    // The buffer is cell-major sorted; InstanceCells lets the renderer frustum-cull whole cells and
    // draw only visible ranges (see InstanceChunks). Null only for a mesh built before chunking.
    public ComPtr<ID3D11Buffer> InstanceBuffer;
    public int InstanceCount = 1;
    public bool Instanced;
    public InstanceCell[]? InstanceCells;

    /// <summary>The scene-tree leaf that owns this mesh (set on upload) — lets a viewport pick resolve to a node.</summary>
    public object? Owner;

    /// <summary>Stream 1: four bone influences per vertex. Null for everything that is not a skinned model.</summary>
    public ComPtr<ID3D11Buffer> SkinBuffer;

    /// <summary>
    /// What each bone does to the mesh right now: the matrix from the pose the geometry was authored in to
    /// the bone's current pose. Identity everywhere until a bone is moved, so a freshly loaded model draws
    /// exactly as it did before skinning existed.
    /// </summary>
    public BonePalette Palette = BonePalette.Identity;

    /// <summary>Inverse of each bone's rest transform, captured at upload — the bind pose. Recomputing a
    /// palette entry is <c>BindInverse[i] * currentRest[i]</c> and nothing else.</summary>
    public Matrix4x4[]? BindInverse;

    /// <summary>The rig's rest transforms as the document holds them (see <see cref="MeshData.LiveRest"/>) —
    /// read at draw time so a moved bone needs no notification to show up.</summary>
    public IReadOnlyList<Matrix4x4>? LiveRest;

    /// <summary>Recomputes the palette from the live rest transforms. Called once per draw; a no-op for a
    /// mesh with no skin or no live source.</summary>
    public void RefreshPose()
    {
        if (LiveRest is { } rest) SetPose(rest);
    }

    /// <summary>True when the mesh has everything a skinned draw needs.</summary>
    public bool IsSkinned => SkinBuffer.Handle != null;

    // CPU geometry (local space) retained for ray-picking and world-AABB recompute after a transform edit.
    // These reference the source MeshData arrays (no extra copy). An instanced cloud keeps them too — not for the
    // ordinary picker (which skips instanced meshes, since one silhouette cannot stand for thousands of copies)
    // but so the crash-placement picker can test a ray against this prototype at one copy's matrix.
    public Vector3[]? PickPositions;
    public uint[]? PickIndices;

    /// <summary>Local-space AABB of the prototype geometry — the crash-placement picker's broad phase transforms
    /// its corners by a single copy's matrix.</summary>
    public Vector3 LocalMin => _localMin;

    /// <inheritdoc cref="LocalMin"/>
    public Vector3 LocalMax => _localMax;

    // Local-space AABB, cached once so RecomputeBounds transforms 8 corners (O(1)) instead of every vertex.
    private Vector3 _localMin;
    private Vector3 _localMax;

    // Texture acquisitions recorded at creation; returned to the library on dispose so an SRV whose last
    // user is gone can actually be released (otherwise VRAM grows for the whole session while streaming).
    private TextureLibrary? _textures;
    private readonly List<TextureLease> _textureLeases = new();

    // …and per part, so a part that is rebound can return what it held. Without this every repaint of a
    // texture left one more GPU copy alive for as long as the mesh stayed loaded: the library retires a
    // rewritten texture until its last lease comes back, and a lease only ever came back on dispose.
    private readonly List<List<TextureLease>> _partLeases = new();
    private bool _disposed;

    public static GpuMesh Create(GpuContext gpu, MeshData mesh, TextureLibrary textures)
    {
        int n = mesh.VertexCount;
        var verts = new MeshVertex[n];
        Vector3[]? tangents = mesh.Tangents;
        Vector3[]? binormals = mesh.Binormals;
        for (int i = 0; i < n; i++)
        {
            verts[i].Position = mesh.Positions[i];
            verts[i].Normal = mesh.Normals[i];
            verts[i].UV = mesh.UVs != null ? mesh.UVs[i] : default;
            if (tangents != null && binormals != null)
            {
                verts[i].Tangent = tangents[i];
                verts[i].Binormal = binormals[i];
            }
            // No tangent channel: leave Tangent/Binormal zero (from new MeshVertex[n]). The shader's
            // zero-tangent guard then reconstructs the geometric normal and skips the normal-map sample.
        }

        bool instanced = mesh.Instances != null && mesh.Instances.Length > 0;

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        var lmin = new Vector3(float.MaxValue);
        var lmax = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            lmin = Vector3.Min(lmin, mesh.Positions[i]);
            lmax = Vector3.Max(lmax, mesh.Positions[i]);
        }

        Matrix4x4[]? sortedInstances = null;
        InstanceCell[]? cells = null;
        if (instanced)
        {
            // Partition the cloud into XY cells (cell-major sorted copy + per-cell AABB) so the renderer
            // can cull whole cells; the mesh AABB is the union of cell AABBs, which covers the prototype
            // geometry extents — not just the copy translations.
            (sortedInstances, cells) = InstanceChunks.Build(mesh.Instances!, lmin, lmax, mesh.InstanceDrawDistances);
            foreach (InstanceCell cell in cells)
            {
                bmin = Vector3.Min(bmin, cell.Min);
                bmax = Vector3.Max(bmax, cell.Max);
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = Vector3.Transform(mesh.Positions[i], mesh.World);
                bmin = Vector3.Min(bmin, wp);
                bmax = Vector3.Max(bmax, wp);
            }
        }

        var result = new GpuMesh
        {
            World = mesh.World,
            BoundsMin = bmin,
            BoundsMax = bmax,
            _localMin = lmin,
            _localMax = lmax,
            TriangleCount = mesh.Indices.Length / 3,
            Instanced = instanced,
            InstanceCount = instanced ? mesh.Instances!.Length : 1,
            InstanceCells = cells,
            PickPositions = mesh.Positions,
            PickIndices = mesh.Indices,
        };

        fixed (MeshVertex* pVerts = verts)
            result.VertexBuffer = GpuBuffers.CreateImmutable(gpu, pVerts, (uint)(n * sizeof(MeshVertex)), BindFlag.VertexBuffer);

        fixed (uint* pIdx = mesh.Indices)
            result.IndexBuffer = GpuBuffers.CreateImmutable(gpu, pIdx, (uint)(mesh.Indices.Length * sizeof(uint)), BindFlag.IndexBuffer);

        result._textures = textures;
        foreach (MeshPart part in mesh.Parts)
        {
            var leases = new List<TextureLease>(3);
            result.Parts.Add(new GpuPart
            {
                StartIndex = (uint)part.StartIndex,
                IndexCount = (uint)part.IndexCount,
                MaterialHash = part.MaterialHash,
                Tint = part.Tint,
                Srv = textures.Acquire(part.DiffuseTexture, leases),
                NormalSrv = textures.AcquireNormalOrFlat(part.NormalTexture, leases), // flat-normal when absent
                SpecSrv = textures.Acquire(part.SpecularTexture, leases),             // white when absent → spec level ×1
            });
            result._partLeases.Add(leases);
        }

        if (instanced)
        {
            // Upload the CELL-MAJOR sorted copy — InstanceCells' Start/Count index into this order.
            fixed (Matrix4x4* pInst = sortedInstances)
                result.InstanceBuffer = GpuBuffers.CreateImmutable(gpu, pInst, (uint)(sortedInstances!.Length * sizeof(Matrix4x4)), BindFlag.VertexBuffer);
        }

        // The skin, when the mesh has one and its rig fits the palette. A rig too big to address is simply not
        // skinned: drawing it with wrapped-around bone indices would tear the model apart.
        if (!instanced && mesh.IsSkinned && mesh.Skeleton!.Bones.Count <= BonePalette.MaxBones)
        {
            var skin = new SkinVertex[n];
            byte[] ids = mesh.BoneIndices!;
            float[] weights = mesh.BoneWeights!;
            for (int i = 0; i < n && ((i * 4) + 3) < ids.Length; i++)
            {
                skin[i].Bone0 = ids[i * 4];
                skin[i].Bone1 = ids[(i * 4) + 1];
                skin[i].Bone2 = ids[(i * 4) + 2];
                skin[i].Bone3 = ids[(i * 4) + 3];
                skin[i].Weights = new Vector4(
                    weights[i * 4], weights[(i * 4) + 1], weights[(i * 4) + 2], weights[(i * 4) + 3]);
            }
            fixed (SkinVertex* pSkin = skin)
                result.SkinBuffer = GpuBuffers.CreateImmutable(
                    gpu, pSkin, (uint)(n * sizeof(SkinVertex)), BindFlag.VertexBuffer);

            // The bind pose comes from the rig's OWN table, not from inverting the current rest transforms.
            // The rest table is the live pose and the editor rewrites it: derive the bind from it and, after
            // any reload, the two agree again, the skinning matrix comes out as the identity and the model
            // snaps back to the shape it was authored in — a bone that moved with geometry that did not.
            var bindInverse = new Matrix4x4[mesh.Skeleton.Bones.Count];
            IReadOnlyList<Matrix4x4> table = mesh.Skeleton.InverseBind;
            for (int i = 0; i < bindInverse.Length; i++)
            {
                if (i < table.Count)
                {
                    bindInverse[i] = table[i];
                    continue;
                }
                Matrix4x4 rest = Affine(mesh.Skeleton.Bones[i].Rest);
                bindInverse[i] = Matrix4x4.Invert(rest, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
            }
            result.BindInverse = bindInverse;
            result.LiveRest = mesh.LiveRest;
            // Start in the pose the rest transforms describe rather than at the identity. For a file nobody
            // has edited the two are the same thing; for one saved with a bone moved they are not, and the
            // identity would draw the model in its authored shape while the game draws it moved.
            result.SetPose(mesh.LiveRest ?? [.. mesh.Skeleton.Bones.Select(b => b.Rest)]);
        }

        return result;
    }

    /// <summary>
    /// Puts the mesh into a pose: entry i of <paramref name="currentRest"/> is where bone i stands now, in the
    /// same space its rest transform was in. Passing the rest transforms back gives the identity palette,
    /// which is the mesh as authored.
    /// </summary>
    public void SetPose(IReadOnlyList<Matrix4x4> currentRest)
    {
        if (BindInverse is not { } bind) return;
        for (int i = 0; i < bind.Length; i++)
        {
            Palette.Set(i, i < currentRest.Count ? bind[i] * Affine(currentRest[i]) : Matrix4x4.Identity);
        }
    }

    // A frame matrix rides as 4x3, so its fourth column is not (0,0,0,1) and neither inversion nor
    // multiplication behaves until the 1 is put back.
    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    /// <summary>
    /// Re-resolves the textures of every part bound to <paramref name="materialHash"/> (a material edit changed
    /// its slots). New SRVs come from the same library the mesh acquired from, and the ones the part held are
    /// handed back once it holds the new ones — other parts and other meshes keep their own leases on the same
    /// texture. Returns the number of parts rebound.
    /// </summary>
    public int RebindPartTextures(ulong materialHash, string? diffuse, string? normal, string? specular,
        Vector4 tint)
    {
        if (_disposed || _textures == null || materialHash == 0) return 0;
        int rebound = 0;
        for (int i = 0; i < Parts.Count; i++)
        {
            if (Parts[i].MaterialHash != materialHash) continue;
            GpuPart p = Parts[i];
            var fresh = new List<TextureLease>(3);
            p.Srv = _textures.Acquire(diffuse, fresh);
            p.NormalSrv = _textures.AcquireNormalOrFlat(normal, fresh);
            p.SpecSrv = _textures.Acquire(specular, fresh);
            p.Tint = tint;   // a material's colour is as editable as its textures — see MafiaMaterials
            Parts[i] = p;
            Supersede(i, fresh);
            rebound++;
        }
        return rebound;
    }

    /// <summary>Repoints one part at a different material (a mesh-slot reassignment): swaps the recorded hash
    /// and re-resolves the three texture SRVs. Same lease policy as <see cref="RebindPartTextures"/>.</summary>
    public bool SetPartMaterial(int index, ulong materialHash, string? diffuse, string? normal, string? specular,
        Vector4 tint)
    {
        if (_disposed || _textures == null || index < 0 || index >= Parts.Count) return false;
        GpuPart p = Parts[index];
        p.MaterialHash = materialHash;
        var fresh = new List<TextureLease>(3);
        p.Srv = _textures.Acquire(diffuse, fresh);
        p.NormalSrv = _textures.AcquireNormalOrFlat(normal, fresh);
        p.SpecSrv = _textures.Acquire(specular, fresh);
        p.Tint = tint;
        Parts[index] = p;
        Supersede(index, fresh);
        return true;
    }

    // The new leases are taken BEFORE the old ones go back: a part rebound to the texture it already has must
    // not drop that texture's last reference on the way.
    private void Supersede(int part, List<TextureLease> fresh)
    {
        if (part >= _partLeases.Count)
        {
            _textureLeases.AddRange(fresh); // a part added outside Create: returned on dispose, as before
            return;
        }
        _textures!.Release(_partLeases[part]);
        _partLeases[part] = fresh;
    }

    /// <summary>Sets a new world matrix and refreshes the world-space AABB (used by frustum culling + picking).</summary>
    public void SetWorld(Matrix4x4 world)
    {
        World = world;
        RecomputeBounds();
    }

    /// <summary>
    /// Recomputes the world-space AABB by transforming the 8 cached local-AABB corners (O(1), conservative under
    /// rotation — always a superset, which is safe for culling and picking broad-phase). No-op for instanced meshes.
    /// </summary>
    public void RecomputeBounds()
    {
        // An instanced cloud's bounds are the union of its cell AABBs, not this mesh's World — see SetInstances.
        if (Instanced || PickPositions == null || _localMin.X > _localMax.X) return;
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? _localMin.X : _localMax.X,
                (i & 2) == 0 ? _localMin.Y : _localMax.Y,
                (i & 4) == 0 ? _localMin.Z : _localMax.Z);
            Vector3 wp = Vector3.Transform(corner, World);
            bmin = Vector3.Min(bmin, wp);
            bmax = Vector3.Max(bmax, wp);
        }
        BoundsMin = bmin;
        BoundsMax = bmax;
    }

    /// <summary>
    /// Replaces the copies of an instanced cloud (a crash placement was moved, added or removed). Only the matrix
    /// buffer is rebuilt — the geometry, its parts and every texture lease stay exactly as they are, which is what
    /// makes this cheap enough to run on every frame of a gizmo drag. The buffer is immutable by design (it is
    /// written once and read every frame), so "update" means release and re-upload; the cell partition and the
    /// world AABB are rebuilt with it. An empty cloud leaves the mesh with nothing to draw.
    /// </summary>
    public void SetInstances(GpuContext gpu, Matrix4x4[] instances, float[]? drawDistances = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (_disposed) return;

        if (Instanced) InstanceBuffer.Dispose();
        Instanced = instances.Length > 0;
        InstanceCount = instances.Length;

        if (!Instanced)
        {
            InstanceBuffer = default;
            InstanceCells = null;
            BoundsMin = new Vector3(float.MaxValue);
            BoundsMax = new Vector3(float.MinValue);
            return;
        }

        (Matrix4x4[] sorted, InstanceCell[] cells) =
            InstanceChunks.Build(instances, _localMin, _localMax, drawDistances);
        InstanceCells = cells;

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        foreach (InstanceCell cell in cells)
        {
            bmin = Vector3.Min(bmin, cell.Min);
            bmax = Vector3.Max(bmax, cell.Max);
        }
        BoundsMin = bmin;
        BoundsMax = bmax;

        fixed (Matrix4x4* pInst = sorted)
            InstanceBuffer = GpuBuffers.CreateImmutable(
                gpu, pInst, (uint)(sorted.Length * sizeof(Matrix4x4)), BindFlag.VertexBuffer);
    }

    public void Dispose()
    {
        if (_disposed) return; // several owners may dispose (render list, delete edit, stale load) — once only
        _disposed = true;
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        if (Instanced) InstanceBuffer.Dispose();
        if (IsSkinned) SkinBuffer.Dispose();
        // SRVs belong to TextureLibrary — returning the leases lets it evict entries with no users left.
        foreach (List<TextureLease> leases in _partLeases) _textures?.Release(leases);
        _textures?.Release(_textureLeases);
    }
}
