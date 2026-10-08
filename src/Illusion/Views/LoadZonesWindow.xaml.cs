using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Mcp;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Tools → Loading zones…: reads the load zones of city_univers at a point and moves one face of a zone. The
/// reading goes through the session the <c>zones_at</c> tool uses, so the window and an agent see the same
/// thing; a move goes through the viewport's own zone editing (<see cref="ZoneEditController"/>), the way a pull
/// on the gizmo does: written to the working copy at once, queued for a Build, one step of the undo history.
/// The window follows the zones wherever they are changed - here, by the gizmo, by an undo, by an agent.
/// </summary>
public sealed partial class LoadZonesWindow : Window
{
    private const float Near = 100f;

    /// <summary>One zone in the list, with the columns shown beside its name.</summary>
    public sealed record ZoneRow(LoadZoneInfo Info)
    {
        public string Name => Info.Name;
        public string DistrictsText => Info.Districts.Count == 0 ? "no district" : string.Join(" + ", Info.Districts);
        public string StatusText => Info.Inside ? "holds the point" : $"{Info.OutsideBy:0.#} m away";
    }

    private sealed record Face(string Id, string Label, int Axis, bool Upper);

    private static readonly Face[] Faces =
    [
        new("+x", "East face (+X)", 0, true), new("-x", "West face (-X)", 0, false),
        new("+y", "North face (+Y)", 1, true), new("-y", "South face (-Y)", 1, false),
        new("+z", "Top face (+Z)", 2, true), new("-z", "Bottom face (-Z)", 2, false),
    ];

    private readonly D3DImageHost _host;
    private readonly AppEditorSession _session = new();

    public LoadZonesWindow(D3DImageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        InitializeComponent();
        _host = host;
        FaceCombo.ItemsSource = Faces;
        PointBox.ValueCommitted += (_, _) => Refresh(keep: null);
        _host.Catalogs.ZonesChanged += OnZonesChanged;
        Closed += (_, _) =>
        {
            _host.Catalogs.ZonesChanged -= OnZonesChanged;
            _host.Catalogs.SelectZone(null);
        };
        TakeCamera();
    }

    // The zones were read again - one was moved, here or anywhere else: what the window says about the point
    // and about the picked zone is read again with them.
    private void OnZonesChanged() => Refresh(keep: _host.Catalogs.SelectedZone);

    private bool _refreshing;

    private void TakeCamera()
    {
        Vector3 at = _host.CameraPose.Position;
        (PointBox.X, PointBox.Y, PointBox.Z) = (Math.Round(at.X, 1), Math.Round(at.Y, 1), Math.Round(at.Z, 1));
        Refresh(keep: null);
    }

    private void Camera_Click(object sender, RoutedEventArgs e) => TakeCamera();

    /// <summary>Shows a point clicked on the scene and the zone picked there (null: no zone holds it).</summary>
    public void ShowPoint(Vector3 at, string? zone)
    {
        (PointBox.X, PointBox.Y, PointBox.Z) = (Math.Round(at.X, 1), Math.Round(at.Y, 1), Math.Round(at.Z, 1));
        Refresh(keep: zone);
    }

    // Reads the zones afresh - they are read from the working copy on every call, so this is also what shows
    // a move that was just made. `keep` re-selects a zone by name across the reload.
    private void Refresh(string? keep)
    {
        float[] point = [(float)PointBox.X, (float)PointBox.Y, (float)PointBox.Z];
        if (_session.ZonesAt(point, Near, null, out IReadOnlyList<LoadZoneInfo> zones, out IReadOnlyList<string> districts, out _)
            is { } failed)
        {
            Fill([], null);
            SummaryText.Text = ToolText.ForPeople(failed);
            return;
        }

        List<ZoneRow> rows = [.. zones.OrderByDescending(z => z.Inside).ThenBy(z => z.OutsideBy).ThenBy(z => z.Name, StringComparer.Ordinal)
            .Select(z => new ZoneRow(z))];
        Fill(rows, keep);
        // A point of the user's own choosing starts with nothing picked. A zone to keep stays picked in the
        // viewport even when the list has no row for it: the list is the zones near the point by their
        // planes, the viewport picks by the box drawn, and the two need not agree at a sliced corner.
        if (keep == null) _host.Catalogs.SelectZone(null);

        // What the game does at a login here: measured in the game, only a zone named as a seam - two words
        // after its number - makes its districts stream in (LoadZones.LoadsOnArrival). So "greenfield is asked
        // for" by the district's own box is not yet "it loads".
        List<LoadZoneInfo> holding = [.. zones.Where(z => z.Inside)];
        List<string> loaded = [.. holding.Where(z => Assets.World.LoadZones.LoadsOnArrival(z.Name)).SelectMany(z => z.Districts)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (holding.Count == 0)
        {
            SummaryText.Text = "No zone holds this point. A player who appears here gets no district loaded; "
                + "one who walks in keeps what was loaded before.";
        }
        else if (loaded.Count == 0)
        {
            SummaryText.Text = districts.Count == 0
                ? "The zones that hold this point name no district."
                : $"Only zones with one word in their name hold this point ({string.Join(", ", districts)}). They do not load a "
                    + "district by themselves: a player who appears here sees it unloaded until he crosses a zone named with two.";
        }
        else
        {
            SummaryText.Text = $"A player who appears here gets: {string.Join(", ", loaded)}.";
        }
    }

    // Puts rows into the list and re-selects one by name, without the list's "selection changed" being taken
    // for a pick by the user: replacing the rows of a list with a selected row reports the selection as gone,
    // and that unpicked the zone the viewport had just picked.
    private void Fill(List<ZoneRow> rows, string? keep)
    {
        _refreshing = true;
        try
        {
            ZoneList.ItemsSource = rows;
            ZoneList.SelectedItem = keep == null ? null : rows.FirstOrDefault(r => r.Name == keep);
        }
        finally
        {
            _refreshing = false;
        }
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSelected();
    }

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        // the zone picked here is the one drawn bright in the viewport, and the other way round
        _host.Catalogs.SelectZone((ZoneList.SelectedItem as ZoneRow)?.Name);
        ShowSelected();
    }

    private void ShowSelected()
    {
        bool picked = ZoneList.SelectedItem is ZoneRow;
        FaceCombo.IsEnabled = ToBox.IsEnabled = MoveBtn.IsEnabled = picked;
        if (ZoneList.SelectedItem is not ZoneRow row)
        {
            ZoneText.Text = "Pick a zone to see its box and move a face.";
            ToBox.Text = "";
            return;
        }
        float[] lo = row.Info.BoxMin, hi = row.Info.BoxMax;
        ZoneText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{row.Name}: X {lo[0]:0.0} … {hi[0]:0.0},  Y {lo[1]:0.0} … {hi[1]:0.0},  Z {lo[2]:0.0} … {hi[2]:0.0}");
        if (FaceCombo.SelectedItem == null) FaceCombo.SelectedIndex = 2;
        else ShowFaceCoordinate();
    }

    private void Face_Changed(object sender, SelectionChangedEventArgs e) => ShowFaceCoordinate();

    // The field starts from where the face stands now, so a move is an edit of that number. Where it stands is
    // the zone's plane, which is what the game tests against and what a move reports as "from"; the box is
    // only a hull drawn around the planes and can sit a hair off. A zone that cannot be moved at all (it is
    // turned) has no such answer, and shows its box.
    private void ShowFaceCoordinate()
    {
        if (ZoneList.SelectedItem is not ZoneRow row || FaceCombo.SelectedItem is not Face face) return;
        float now = (face.Upper ? row.Info.BoxMax : row.Info.BoxMin)[face.Axis];
        if (_session.ZoneMoveFace(row.Name, face.Id, now, apply: false, null, out IReadOnlyList<LoadZoneMoveInfo> plan) == null
            && plan.Count > 0)
        {
            now = plan[0].From;
        }
        ToBox.Text = now.ToString("0.0#", CultureInfo.InvariantCulture);
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ZoneList.SelectedItem is not ZoneRow row || FaceCombo.SelectedItem is not Face face) return;
        if (!float.TryParse(ToBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float to)
            || !float.IsFinite(to))
        {
            Refuse("Not a coordinate", $"'{ToBox.Text}' is not a number.");
            return;
        }

        // Asked first without writing: it says where the face stands and refuses what cannot be done (a zone
        // that is turned, a face that would pass the opposite one).
        if (_session.ZoneMoveFace(row.Name, face.Id, to, apply: false, null, out IReadOnlyList<LoadZoneMoveInfo> plan) is { } refused)
        {
            Refuse("The face cannot be moved", ToolText.ForPeople(refused));
            return;
        }
        LoadZoneMoveInfo move = plan[0];
        if (Math.Abs(move.From - move.To) < 0.005f) return;

        string districts = move.Districts.Count == 0 ? "no district" : string.Join(" + ", move.Districts);
        DialogOutcome answer = AppDialog.Show(this, new DialogOptions
        {
            Title = "Loading Zones",
            Icon = DialogIcon.Question,
            Heading = $"Move the {face.Label.ToLowerInvariant()} of {move.Zone}?",
            Text = string.Create(CultureInfo.InvariantCulture, $"From {move.From:0.0#} to {move.To:0.0#}. The zone names {districts}.\n\n")
                + "This is written to the working copy of city_univers at once; Undo (Ctrl+Z) in the editor takes it "
                + "back. Build packs the archive (a backup is kept).",
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Move",
        });
        if (!answer.Confirmed) return;

        string? failed;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            // the write reads the layer again, and the window with it (OnZonesChanged)
            failed = _host.ZoneEditing.MoveFace(row.Name, face.Id, to);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        if (failed != null) Refuse("The face was not moved", ToolText.ForPeople(failed));
    }

    private void Refuse(string heading, string text) => AppDialog.Show(this, new DialogOptions
    {
        Title = "Loading Zones",
        Icon = DialogIcon.Warning,
        Heading = heading,
        Text = text,
    });

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
