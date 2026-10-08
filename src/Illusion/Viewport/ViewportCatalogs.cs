using System.Diagnostics;
using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;

namespace Illusion.Viewport;

/// <summary>
/// Environment and map catalogs of the viewport: the map-area list (districts + interiors from
/// cityareas.bin), the streaming zones and the sky texture. Built once in the background after the
/// renderer initializes; nothing can load before <see cref="D3DImageHost.CatalogReady"/> fires.
/// </summary>
internal sealed class ViewportCatalogs
{
    private readonly D3DImageHost _host;

    public ViewportCatalogs(D3DImageHost host) => _host = host;

    /// <summary>Main catalog: map areas (districts + interiors from cityareas) for the selector.</summary>
    public IReadOnlyList<MapArea> Areas { get; private set; } = Array.Empty<MapArea>();

    public MapCatalog? Map { get; private set; }

    /// <summary>District base names — for detecting neighbor proxy meshes in LoadHierarchy.</summary>
    public List<string> DistrictNames { get; private set; } = new();

    /// <summary>Streaming zones (AREA boxes city_univers ⋈ cityareas) for Whole map mode.</summary>
    public List<AreaZone>? Zones { get; private set; }

    /// <summary>Extracted path of the game's sky panorama (FreeRide.dds), or null when unavailable —
    /// secondary viewports (the material preview) load the same sky as the map.</summary>
    public string? SkyTexturePath { get; private set; }

    // Prepare the environment (sky) and build the map areas catalog. We load nothing into the viewport:
    // content arrives via location selection through LoadArea. The heavy part (city_univers unpack,
    // zone parse, sky extraction, first .mtl load) runs in the background so the first launch doesn't
    // freeze the window; results marshal back to the UI thread. Nothing can load before the catalogs
    // land: LoadArea/EnqueueCrashLayer bail while Map is null, and CatalogReady re-populates the UI.
    /// <param name="withMap">Whether this viewport needs the CITY catalogs — the district list and the
    /// streaming zones. The resource editor's stage does not: it is handed one archive by path and never
    /// streams, so reading <c>cityareas.bin</c> for it would be work with no reader. The sky and the shared
    /// materials table are loaded either way; both are about looking at geometry, not about the city.</param>
    public void InitAsync(bool withMap = true)
    {
        Task.Run(() =>
        {
            try
            {
                // The launcher initializes the environment before opening the viewport; without it
                // there is nothing to load here.
                if (!MafiaEnvironment.IsInitialized)
                {
                    Debug.WriteLine("Mafia II environment is not initialized — launcher must set the game path first.");
                    return;
                }

                // Mafia sky (equirect panorama FreeRide.dds from skies\freeride.sds).
                string? skyTex = null;
                string skySds = Path.Combine(MafiaEnvironment.PcFolder, "sds", "skies", "freeride.sds");
                if (File.Exists(skySds))
                {
                    string tex = Path.Combine(SdsMeshLoader.EnsureExtracted(new FileInfo(skySds)), "FreeRide.dds");
                    if (File.Exists(tex)) skyTex = tex;
                }

                // First .mtl load happens on THIS thread, and no SDS load can start until CatalogReady —
                // the "first load is single-threaded" invariant of MafiaMaterials holds.
                MafiaMaterials.EnsureLoaded();

                // Global .dds index for the material editor (its own background task — a full-mirror scan
                // must not delay the catalogs). Ready long before the first material click, typically.
                Assets.Textures.TextureSearchIndex.WarmUp();

                // Main catalog: map areas from cityareas.bin (city_univers), resolve names to files.
                MapCatalog? map = withMap
                    ? MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f))
                    : null;

                // Streaming zones (AREA boxes city_univers ⋈ cityareas) for Whole map mode.
                var zones = new List<AreaZone>();
                if (map != null)
                {
                    try
                    {
                        zones = AreaZones.Load(f => SdsMeshLoader.EnsureExtracted(f),
                            map.Areas.Select(a => a.BaseName).ToList());
                    }
                    catch { zones = new List<AreaZone>(); }
                }

                _host.Dispatcher.Invoke(() =>
                {
                    if (_host.Rnd == null) return; // disposed while initializing
                    if (map != null)
                    {
                        Map = map;
                        Areas = map.Areas;
                        DistrictNames = Areas.Select(a => a.BaseName).ToList();
                        Zones = zones;
                        BuildZoneBoxes();
                    }
                    SkyTexturePath = skyTex;
                    if (skyTex != null) _host.LoadSky(skyTex);
                    _host.RaiseCatalogReady();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Catalog init error: " + ex);
            }
        });
    }

    /// <summary>
    /// Reads the loading zones again from the working copy of city_univers and redraws their boxes — after one
    /// was moved (Tools → Loading zones), so the layer does not go on showing where it stood. False when the
    /// catalog is not up yet or the zones could not be read; the boxes then stay as they were.
    /// </summary>
    public bool ReloadZones()
    {
        if (Map == null || _host.Rnd == null) return false;
        try
        {
            Zones = AreaZones.Load(f => SdsMeshLoader.EnsureExtracted(f), Map.Areas.Select(a => a.BaseName).ToList());
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            Debug.WriteLine("Zone reload error: " + ex);
            return false;
        }
        _preview = null;
        SelectedZoneFaces = FacesOf(SelectedZone);
        BuildZoneBoxes();
        _host.RaiseBoxGizmoChanged();
        ZonesChanged?.Invoke();
        return true;
    }

    /// <summary>Raised when the zones were read again from the working copy - after any write to one, by the
    /// gizmo, the Loading zones window, an undo or the <c>zone_move_face</c> tool. For whoever shows them.</summary>
    public event Action? ZonesChanged;

    /// <summary>The zone picked - by a click on the scene with the layer up, or in the Loading zones window. It is
    /// drawn bright and the others are dimmed, so that among boxes lying two and three deep it is plain which one
    /// is being looked at.</summary>
    public string? SelectedZone { get; private set; }

    /// <summary>Picks a zone by name, or none (null); the boxes are redrawn.</summary>
    public void SelectZone(string? name)
    {
        if (string.Equals(SelectedZone, name, StringComparison.Ordinal)) return;
        SelectedZone = name;
        _preview = null;
        SelectedZoneFaces = FacesOf(name);
        if (_host.Rnd != null) BuildZoneBoxes();
        _host.RaiseBoxGizmoChanged();
    }

    /// <summary>The picked zone in a line: its name, the districts it keeps loaded, and - for one whose name is
    /// not a seam's (<see cref="LoadZones.LoadsOnArrival"/>) - that it does not load them by itself.</summary>
    public string? SelectedZoneLabel
    {
        get
        {
            if (SelectedZone == null || Zones?.FirstOrDefault(z => z.Name == SelectedZone) is not { } zone) return null;
            return LoadZones.LoadsOnArrival(zone.Name)
                ? $"{zone.Name}  ·  {string.Join(" + ", zone.Districts)}"
                : $"{zone.Name}  ·  {string.Join(" + ", zone.Districts)}  ·  does not load it by itself (one word in its name)";
        }
    }

    /// <summary>The faces of the picked zone that can be pulled ("+x", "-z", ...): a zone with a corner sliced off
    /// has a side with no face square to its axis, and that side gets no arrow.</summary>
    public IReadOnlySet<string> SelectedZoneFaces { get; private set; } = new HashSet<string>();

    private IReadOnlySet<string> FacesOf(string? zone)
    {
        if (zone == null) return new HashSet<string>();
        try
        {
            return LoadZones.Open(f => SdsMeshLoader.EnsureExtracted(f), DistrictNames).SquareFaces(zone);
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            Debug.WriteLine("Zone faces error: " + ex);
            return new HashSet<string>();
        }
    }

    private (Vector3 Min, Vector3 Max)? _preview;

    /// <summary>The picked zone's box - the one a gizmo drag has reached while one is in progress.</summary>
    public (Vector3 Min, Vector3 Max)? SelectedZoneBox =>
        SelectedZone != null && Zones?.FirstOrDefault(z => z.Name == SelectedZone) is { } zone ? _preview ?? (zone.Min, zone.Max) : null;

    /// <summary>Draws the picked zone as the box given instead of its own, until a null puts it back: what a
    /// drag shows before it is let go. Nothing is written.</summary>
    public void PreviewZone((Vector3 Min, Vector3 Max)? box)
    {
        _preview = box;
        if (_host.Rnd != null) BuildZoneBoxes();
        _host.RaiseBoxGizmoChanged();
    }

    /// <summary>The zones whose drawn box holds a world point, the smallest first: a click picks the most
    /// particular zone there, and the next click at the same spot the next one out.</summary>
    public IReadOnlyList<AreaZone> ZonesHolding(Vector3 point)
    {
        if (Zones == null) return [];
        const float Slack = 0.3f;      // a point ON the ground of a zone whose floor is that ground
        static float Volume(AreaZone z) => (z.Max.X - z.Min.X) * (z.Max.Y - z.Min.Y) * (z.Max.Z - z.Min.Z);
        return [.. Zones.Where(z => point.X >= z.Min.X - Slack && point.X <= z.Max.X + Slack
                                    && point.Y >= z.Min.Y - Slack && point.Y <= z.Max.Y + Slack
                                    && point.Z >= z.Min.Z - Slack && point.Z <= z.Max.Z + Slack)
            .OrderBy(Volume).ThenBy(z => z.Name, StringComparer.Ordinal)];
    }

    // Zone boxes for the debug overlay: world AABB of the zone + color by its (first) district.
    public void BuildZoneBoxes()
    {
        if (Zones == null || Zones.Count == 0) return;

        var hue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MapArea a in Areas) if (!hue.ContainsKey(a.BaseName)) hue[a.BaseName] = hue.Count;
        int count = Math.Max(1, hue.Count);

        var boxes = new List<Rendering.Passes.ZoneBox>(Zones.Count);
        AreaZone? picked = SelectedZone == null ? null : Zones.FirstOrDefault(z => z.Name == SelectedZone);
        foreach (AreaZone z in Zones)
        {
            if (ReferenceEquals(z, picked)) continue;
            string? d = z.Districts.Count > 0 ? z.Districts[0] : null;
            float h = d != null && hue.TryGetValue(d, out int i) ? (float)i / count : 0.5f;
            Vector4 colour = HueToColor(h, 1f);
            // a zone named as a seam is the kind that loads its districts for a player who appears in it
            boxes.Add(new Rendering.Passes.ZoneBox(z.Min, z.Max, new Vector3(colour.X, colour.Y, colour.Z), LoadZones.LoadsOnArrival(z.Name), Picked: false));
        }
        // last, so it is laid over the rest: the accent the editor selects with
        if (picked != null)
        {
            (Vector3 min, Vector3 max) = _preview ?? (picked.Min, picked.Max);
            boxes.Add(new Rendering.Passes.ZoneBox(min, max, new Vector3(0.91f, 0.53f, 0.24f), LoadZones.LoadsOnArrival(picked.Name), Picked: true));
        }
        _host.Rnd!.SetZoneBoxes(boxes);
    }

    // HSV(h,0.7,0.95) → RGBA. Even hue by district index → neighboring districts are distinguishable.
    private static Vector4 HueToColor(float h, float alpha)
    {
        const float s = 0.7f, v = 0.95f;
        float hh = (h - MathF.Floor(h)) * 6f;
        int sec = (int)hh % 6;
        float f = hh - MathF.Floor(hh);
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (float r, float g, float b) = sec switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return new Vector4(r, g, b, alpha);
    }
}
