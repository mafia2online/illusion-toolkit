using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// The picked loading zone as something the tool shelf can move and stretch (<c>BoxGizmo</c>): the box the
/// gizmo stands on, the box a drag has reached, and - when the drag is let go - the zone written to
/// city_univers, as one step of the editor's undo history. The Loading zones window moves a face through the
/// same door, so its moves are in the history too.
/// <para>
/// A zone is not edited as a scene object: it is changed in the working copy of city_univers at once, the
/// archive is queued for a Build, and undo writes it back (<see cref="ZoneWrites"/> keeps an editor that has
/// that archive loaded - the map editor in Whole map mode - in step). A step records WHAT was changed - which
/// faces, from where to where, or how far the zone was carried - and undoing it takes back that and nothing
/// else: a face moved since by another path stays where that path put it.
/// </para>
/// </summary>
internal sealed class ZoneEditController
{
    private const float Same = 0.002f;         // faces closer than this stand in the same place
    private const float Stale = 0.05f;         // the box on disk against the box the layer shows

    private readonly D3DImageHost _host;
    private string? _zone;
    private (Vector3 Min, Vector3 Max) _start;
    private (Vector3 Min, Vector3 Max)? _reached;

    public ZoneEditController(D3DImageHost host) => _host = host;

    /// <summary>The picked zone's box while the Loading zones layer is shown; null otherwise.</summary>
    public (Vector3 Min, Vector3 Max)? Target => _host.ShowZones ? _host.Catalogs.SelectedZoneBox : null;

    /// <summary>What a zone edit changed: the faces that moved (each by its own plane, from where to where) or
    /// the offset the whole zone was carried by.</summary>
    private sealed record Change(string Zone, IReadOnlyList<(string Face, float From, float To)> Faces, Vector3 By);

    public void Begin()
    {
        _zone = _host.Catalogs.SelectedZone;
        _reached = null;
        if (_host.Catalogs.SelectedZoneBox is { } box) _start = box;
    }

    public void Preview(Vector3 min, Vector3 max)
    {
        if (_zone == null) return;
        _reached = (min, max);
        _host.Catalogs.PreviewZone(_reached);
    }

    public void End(bool commit)
    {
        string? zone = _zone;
        (Vector3 Min, Vector3 Max)? reached = _reached;
        _zone = null;
        _reached = null;
        if (zone == null) return;
        // the layer was switched off under the drag (a tool can do that): what cannot be seen is not written
        if (!_host.ShowZones) commit = false;
        if (!commit || reached is not { } to || (Vector3.Distance(to.Min, _start.Min) < 0.005f && Vector3.Distance(to.Max, _start.Max) < 0.005f))
        {
            _host.Catalogs.PreviewZone(null);
            return;
        }

        // What the drag did, said as what it changed: a box of the same size somewhere else was carried, and
        // anything else is the faces that left their places. Said against the box the drag STARTED from - the
        // write below checks that the disk still has that box.
        Vector3 grew = (to.Max - to.Min) - (_start.Max - _start.Min);
        var faces = new List<(string Face, float From, float To)>();
        Vector3 by = Vector3.Zero;
        if (MathF.Abs(grew.X) < Same && MathF.Abs(grew.Y) < Same && MathF.Abs(grew.Z) < Same)
        {
            by = to.Min - _start.Min;
        }
        else
        {
            foreach ((string face, float value, float was) in new[]
                     {
                         ("+x", to.Max.X, _start.Max.X), ("-x", to.Min.X, _start.Min.X), ("+y", to.Max.Y, _start.Max.Y),
                         ("-y", to.Min.Y, _start.Min.Y), ("+z", to.Max.Z, _start.Max.Z), ("-z", to.Min.Z, _start.Min.Z),
                     })
            {
                if (MathF.Abs(value - was) >= Same) faces.Add((face, was, value));
            }
        }

        string? refused = Write(new Change(zone, faces, by), forward: true, expect: _start, out Change? done);
        _host.Catalogs.PreviewZone(null);
        if (refused != null || done == null)
        {
            _host.RaiseNotice($"{zone} was not changed: {refused}", isError: true);
            return;
        }
        _host.History.Push(new ZoneEdit(this, done));
        _host.RaiseNotice($"{zone} changed - Build packs city_univers; every player of a server needs that archive.");
    }

    /// <summary>
    /// Moves one face of a zone to a world coordinate, as the Loading zones window asks for it: written at once,
    /// one step of the undo history. Null on success - on a refusal nothing is written.
    /// </summary>
    public string? MoveFace(string zone, string face, float to)
    {
        string? refused = Write(new Change(zone, [(face.ToLowerInvariant(), float.NaN, to)], Vector3.Zero), forward: true, expect: null, out Change? done);
        if (refused != null) return refused;
        // the face stands there already: nothing was written, and there is no step to take back
        if (done != null) _host.History.Push(new ZoneEdit(this, done));
        return null;
    }

    // Carries a change out (forward) or takes it back, in a copy of the scene read from disk, and saves it. A
    // face goes to the change's To (forward) or back to its From; the From of a change made forward is read off
    // the plane as it stands, and comes back in `done` - that is what an undo returns the face to, not the box
    // drawn around the planes. `expect` is the box the caller believes the zone has: when the disk says
    // otherwise the zone was changed behind the layer's back, and nothing is written.
    private string? Write(Change change, bool forward, (Vector3 Min, Vector3 Max)? expect, out Change? done)
    {
        done = null;
        LoadZones written;
        try
        {
            LoadZones zones = LoadZones.Open(f => SdsMeshLoader.EnsureExtracted(f), _host.Catalogs.DistrictNames);
            if (!zones.Volumes.TryGetValue(change.Zone, out Formats.Frames.ObjectTypes.FrameObjectArea? volume))
            {
                return "there is no such zone in city_univers any more";
            }
            if (expect is { } box)
            {
                (Vector3 Min, Vector3 Max) now = LoadZones.WorldBox(volume);
                if (Vector3.Distance(now.Min, box.Min) > Stale || Vector3.Distance(now.Max, box.Max) > Stale)
                {
                    _host.Catalogs.ReloadZones();
                    return "it was changed elsewhere since the layer was drawn - the layer has been read again, pull it once more";
                }
            }
            if (ZoneWrites.Blocked(zones, change.Zone) is { } blocked) return blocked;

            var moved = new List<(string Face, float From, float To)>();
            if (change.Faces.Count == 0 && change.By.Length() < Same)
            {
                return null;       // carried nowhere
            }
            if (change.Faces.Count == 0)
            {
                // the same size somewhere else: the zone is moved by its own place, whatever its shape
                if (zones.Move(change.Zone, forward ? change.By : -change.By) is { } refused) return refused;
            }
            else
            {
                // only the faces that were pulled: a zone with a slanted side has no face to rewrite there
                foreach ((string face, float from, float to) in change.Faces)
                {
                    if (zones.MoveFace(change.Zone, face, forward ? to : from, out LoadZoneFaceMove? move) is { } refused) return refused;
                    moved.Add((face, forward ? move!.From : from, to));
                }
                // every face stands where it was asked to stand: the scene is not rewritten, the archive is
                // not queued for a Build and no step goes into the history
                if (moved.All(m => MathF.Abs(m.From - m.To) < Same)) return null;
            }
            zones.Save();
            written = zones;
            done = new Change(change.Zone, moved, change.By);
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            return "city_univers could not be written: " + ex.Message;
        }

        // The zone is written. What follows only tells the editors about it; a failure there is not a failure
        // of the write, and reporting it as one would leave a change on disk with no step to take it back.
        try
        {
            ZoneWrites.Landed(written, change.Zone);
        }
        catch (Exception ex) when (ZoneWrites.IsFileTrouble(ex))
        {
            _host.RaiseNotice($"{change.Zone} is written, but the editor could not be brought up to date: {ex.Message}", isError: true);
        }
        return null;
    }

    private sealed class ZoneEdit(ZoneEditController owner, Change change) : IEditAction
    {
        public void Undo() => Apply(forward: false);

        public void Redo() => Apply(forward: true);

        // A step that cannot be taken says so and stays where it was in the history (EditHistory leaves its
        // stacks alone when a step throws): reporting it done would leave the zone moved with no way back.
        private void Apply(bool forward)
        {
            if (owner.Write(change, forward, expect: null, out _) is { } refused)
            {
                throw new EditRefusedException($"{change.Zone} was not {(forward ? "changed again" : "put back")}: {refused}");
            }
        }
    }
}
