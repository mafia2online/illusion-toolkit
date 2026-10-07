using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
// System.Windows.Interop also defines a RenderMode enum — ours must win unqualified.
using RenderMode = Illusion.Rendering.Passes.RenderMode;

namespace Illusion.Viewport;

/// <summary>
/// The material editor's live preview: a <see cref="ViewportControl"/> whose whole scene is one shaded
/// sphere carrying the selected material's textures, lit with the material's own specular/fresnel
/// parameters (D013/D027 — the scene viewport uses one global block instead). Left-drag orbits around
/// the sphere, the wheel zooms; the base's fly-camera stays available but is slowed to sphere scale.
/// This is the app's second live GPU stack beside the map viewport — each instance owns its devices.
/// </summary>
public sealed class MaterialPreviewViewport : ViewportControl
{
    private MeshPart _part = new(0, 0, null);
    private LightingConstants _materialLighting = LightingConstants.Default;
    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private bool _orbiting;
    private Point _lastOrbit;

    // Mesh-shape preview: raw pick geometry (positions + indices) from a scene node.
    // When set and UseMesh is true, the preview shows this geometry instead of the sphere.
    private Vector3[]? _meshPositions;
    private uint[]? _meshIndices;
    private bool _useMesh;

    public bool UseMesh
    {
        get => _useMesh;
        set { _useMesh = value; Rebuild(); }
    }

    public MaterialPreviewViewport()
    {
        ShowSky = true; // gradient sky, or the game panorama once the owner calls LoadSky
        RenderMode = RenderMode.Render;

        // Left-drag orbit (the base's left click is a pick hook, middle-drag is free-look — neither
        // suits inspecting a single sphere).
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _orbiting = true;
            _lastOrbit = e.GetPosition(this);
            CaptureMouse();
        };
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _orbiting = false;
            ReleaseMouseCapture();
        };
        MouseMove += (_, e) =>
        {
            if (!_orbiting) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _orbiting = false; return; }
            Point p = e.GetPosition(this);
            const float sens = 0.008f;
            OrbitCamera(-(float)(p.X - _lastOrbit.X) * sens, -(float)(p.Y - _lastOrbit.Y) * sens);
            _lastOrbit = p;
        };
    }

    /// <summary>Texture folders the sphere's maps resolve against (mirror of the map viewport's scope).
    /// Safe before load; applied on the next rebuild.</summary>
    public void SetFolders(IReadOnlyList<string> folders)
    {
        _folders = folders;
        Rebuild();
    }

    /// <summary>Shows a material: its three maps on the sphere (or the current mesh geometry) + its own specular/fresnel response.</summary>
    public void SetMaterial(ulong hash, string? diffuse, string? normal, string? specular, LightingConstants lighting)
    {
        // The tint is part of the material's look, not a decoration: a car body has no albedo at all and
        // shows nothing but its colour, so a preview without it is a white ball whatever the paint says.
        // …and so is how its alpha is read. A translucent material previewed as a cut-out is tested against
        // one half and a pane of glass at 0.3 simply vanishes from the ball, while the scene draws it blended.
        Assets.MafiaMaterials.MaterialTextures look = Assets.MafiaMaterials.GetMaterialTextures(hash);
        _part = new MeshPart(0, 0, diffuse, normal, specular, hash, look.Tint, look.Blended);
        _materialLighting = lighting;
        Lighting = lighting;
        Rebuild();
    }

    /// <summary>
    /// Provides the raw pick geometry (subset of a mesh's PickPositions/PickIndices for one material slot)
    /// that will be shown when <see cref="UseMesh"/> is true. Pass null to clear and fall back to the sphere.
    /// </summary>
    public void SetMeshGeometry(Vector3[]? positions, uint[]? indices)
    {
        _meshPositions = positions;
        _meshIndices = indices;
        if (_useMesh) Rebuild();
    }

    protected override void OnSceneInitialized()
    {
        Renderer!.Camera.LookAt(new Vector3(0f, -2.6f, 1.0f), Vector3.Zero);
        Renderer.Camera.MoveSpeed = 2.5f; // the base fly-camera at map speed would leave the sphere instantly
        _orbitDistance = new Vector3(0f, -2.6f, 1.0f).Length();
        Lighting = _materialLighting;
        Rebuild();
    }

    // The sphere sits at the origin and is zoomed between hard limits, so this replaces the base viewport's
    // free dolly outright — calling base here would apply both moves to a single wheel notch.
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Renderer == null) return;
        Camera cam = Renderer.Camera;
        float dist = cam.Position.Length(); // the orbit pivot is the sphere at the origin
        float target = Math.Clamp(dist - e.Delta / 120f * 0.3f, 1.3f, 8f);
        cam.Position *= target / MathF.Max(dist, 0.001f);
        _orbitDistance = target;
        e.Handled = true;
    }

    private void Rebuild()
    {
        if (Renderer == null) return; // pre-load: OnSceneInitialized rebuilds with the cached state
        foreach (string folder in _folders) Renderer.Textures.AddFolder(folder);
        // Whole-mirror fallback: the preview shows real textures even for unloaded districts.
        Renderer.Textures.SetFallbackResolver(Assets.Textures.TextureSearchIndex.FindPath);
        Renderer.Clear();
        MeshData mesh = _useMesh && _meshPositions != null && _meshIndices is { Length: >= 3 }
            ? BuildFlatMesh(_meshPositions, _meshIndices, _part)
            : SphereMesh.Create(_part);
        Renderer.AddMesh(mesh);
    }

    /// <summary>
    /// Builds a flat-shaded <see cref="MeshData"/> from raw pick geometry for the mesh-shape preview.
    /// Positions are centred and normalised to fit a unit sphere; face normals are computed analytically
    /// (per-triangle flat shading). UVs and tangents are zeroed — the shader skips normal-map sampling
    /// for a zero tangent, giving a clean diffuse-only silhouette at unit-UV (0,0).
    /// </summary>
    private static MeshData BuildFlatMesh(Vector3[] allPositions, uint[] indices, MeshPart part)
    {
        // Compute AABB over the referenced positions to centre + normalise.
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int k = 0; k < indices.Length; k++)
        {
            Vector3 p = allPositions[indices[k]];
            bmin = Vector3.Min(bmin, p);
            bmax = Vector3.Max(bmax, p);
        }
        Vector3 center = (bmin + bmax) * 0.5f;
        float extent = (bmax - bmin).Length() * 0.5f;
        float scale = extent > 0.0001f ? 1f / extent : 1f;

        // Fan out indexed geometry: each triangle becomes 3 unique vertices with a shared face normal.
        int triCount = indices.Length / 3;
        var positions = new Vector3[triCount * 3];
        var normals = new Vector3[triCount * 3];
        var uvs = new Vector2[triCount * 3];   // zeroed — UV(0,0) samples the texture centre
        var tangents = new Vector3[triCount * 3];   // zeroed → shader skips normal-map sampling
        var outIdx = new uint[triCount * 3];

        for (int t = 0; t < triCount; t++)
        {
            Vector3 p0 = (allPositions[indices[t * 3]] - center) * scale;
            Vector3 p1 = (allPositions[indices[t * 3 + 1]] - center) * scale;
            Vector3 p2 = (allPositions[indices[t * 3 + 2]] - center) * scale;

            Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
            Vector3 n = cross.LengthSquared() > 1e-10f ? Vector3.Normalize(cross) : Vector3.UnitZ;

            int vi = t * 3;
            positions[vi] = p0; positions[vi + 1] = p1; positions[vi + 2] = p2;
            normals[vi] = normals[vi + 1] = normals[vi + 2] = n;
            outIdx[vi] = (uint)vi; outIdx[vi + 1] = (uint)(vi + 1); outIdx[vi + 2] = (uint)(vi + 2);
        }

        return new MeshData
        {
            Name = "mesh-shape-preview",
            World = Matrix4x4.Identity,
            Positions = positions,
            Normals = normals,
            UVs = uvs,
            Tangents = tangents,
            Binormals = tangents, // also zero — same fallback in the shader
            Indices = outIdx,
            Parts = new[]
            {
                // The tint with the rest: a material that paints itself has no albedo, and without its
                // colour the mesh preview showed it white while the sphere showed it painted.
                new MeshPart(0, outIdx.Length,
                    part.DiffuseTexture, part.NormalTexture, part.SpecularTexture, part.MaterialHash, part.Tint,
                    part.Blended),
            },
        };
    }

    /// <summary>
    /// The preview lighting block for a material: the scene defaults with the specular response replaced by
    /// the material's own D013 (SpecularPowerAndLevel) and D027 (FresnelBiasAndPower → the shader's rim
    /// term, its closest analogue). Clamped — game data carries outliers.
    /// </summary>
    public static LightingConstants LightingFor(MaterialInfo? info)
    {
        LightingConstants lighting = LightingConstants.Default;
        if (info == null) return lighting;
        Vector4 spec = lighting.SpecParams;
        foreach (MaterialParamInfo p in info.Parameters)
        {
            if (p.ParamId == "D013" && p.Values.Count >= 2)
            {
                spec.X = Math.Clamp(p.Values[0], 1f, 256f);   // Phong exponent
                spec.Y = Math.Clamp(p.Values[1], 0f, 2f);     // specular level
            }
            else if (p.ParamId == "D027" && p.Values.Count >= 2)
            {
                spec.Z = Math.Clamp(p.Values[0] * 0.5f, 0f, 0.4f); // fresnel bias → rim strength
                spec.W = Math.Clamp(p.Values[1], 1f, 16f);         // fresnel power → rim power
            }
        }
        return lighting with { SpecParams = spec };
    }
}
