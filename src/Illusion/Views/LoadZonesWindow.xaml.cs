using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Assets;
using Illusion.Mcp;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Tools → Loading zones…: reads the load zones of city_univers at a point and moves one face of a zone. Both go
/// through the session the <c>zones_at</c> and <c>zone_move_face</c> tools use, so the window and an agent see
/// and change the same thing. A move is written to the working copy at once; the window then queues
/// city_univers for a Build and has the viewport's zone boxes redrawn.
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
        TakeCamera();
    }

    private void TakeCamera()
    {
        Vector3 at = _host.CameraPose.Position;
        (PointBox.X, PointBox.Y, PointBox.Z) = (Math.Round(at.X, 1), Math.Round(at.Y, 1), Math.Round(at.Z, 1));
        Refresh(keep: null);
    }

    private void Camera_Click(object sender, RoutedEventArgs e) => TakeCamera();

    // Reads the zones afresh - they are read from the working copy on every call, so this is also what shows
    // a move that was just made. `keep` re-selects a zone by name across the reload.
    private void Refresh(string? keep)
    {
        float[] point = [(float)PointBox.X, (float)PointBox.Y, (float)PointBox.Z];
        if (_session.ZonesAt(point, Near, null, out IReadOnlyList<LoadZoneInfo> zones, out IReadOnlyList<string> districts, out _)
            is { } failed)
        {
            ZoneList.ItemsSource = null;
            EmptyLabel.Visibility = Visibility.Visible;
            SummaryText.Text = ToolText.ForPeople(failed);
            return;
        }

        List<ZoneRow> rows = [.. zones.OrderByDescending(z => z.Inside).ThenBy(z => z.OutsideBy).ThenBy(z => z.Name, StringComparer.Ordinal)
            .Select(z => new ZoneRow(z))];
        ZoneList.ItemsSource = rows;
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ZoneList.SelectedItem = keep == null ? null : rows.FirstOrDefault(r => r.Name == keep);

        // What the game does at a login here: measured in the game, only a zone that names two districts
        // makes them stream in. So "greenfield is asked for" by a one-district zone is not yet "it loads".
        List<LoadZoneInfo> holding = [.. zones.Where(z => z.Inside)];
        List<string> loaded = [.. holding.Where(z => z.Districts.Count >= 2).SelectMany(z => z.Districts)
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
                : $"Only one-district zones hold this point ({string.Join(", ", districts)}). They do not load a district "
                    + "by themselves: a player who appears here sees it unloaded until he crosses a two-district zone.";
        }
        else
        {
            SummaryText.Text = $"A player who appears here gets: {string.Join(", ", loaded)}.";
        }
    }

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
                + "This is written to the working copy of city_univers at once and cannot be undone here - to take it "
                + "back, move the face to where it stood. Build packs the archive (a backup is kept).",
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Move",
        });
        if (!answer.Confirmed) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            if (_session.ZoneMoveFace(row.Name, face.Id, to, apply: true, null, out _) is { } failed)
            {
                Mouse.OverrideCursor = null;
                Refuse("The face was not moved", ToolText.ForPeople(failed));
                return;
            }
            _host.MarkArchiveModified(new FileInfo(MafiaEnvironment.CityUniversSds));
            _host.Catalogs.ReloadZones();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        Refresh(keep: row.Name);
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
