using System.IO;
using System.Windows;
using System.Windows.Input;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Mcp;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>What a <see cref="CarToolWindow"/> is opened to do.</summary>
public enum CarTool
{
    /// <summary>A second car under a new model name, registered in the tables.</summary>
    Clone,
    /// <summary>One car built under another car's name, replacing its archive.</summary>
    Substitute,
    /// <summary>A built car written out as a multiplayer resource folder.</summary>
    Export,
}

/// <summary>
/// Tools → Clone car… / Replace a car… / Export car for multiplayer…. The jobs are the ones behind the
/// <c>car_clone</c>, <c>car_substitute</c> and <c>car_export_m2o</c> tools, run through the same session, so
/// their refusals (a car open with unsaved edits, a name already taken, an archive the game holds) are the
/// same too. The window closes when the job is done and says what was written.
/// </summary>
public sealed partial class CarToolWindow : Window
{
    private readonly CarTool _tool;
    private readonly AppEditorSession _session = new();
    private readonly string _hint;

    /// <param name="tool">Which job.</param>
    /// <param name="car">The car to start from - the one open in the Resource Editor - or null.</param>
    public CarToolWindow(CarTool tool, string? car)
    {
        InitializeComponent();
        _tool = tool;

        List<string> cars = Cars();
        CarCombo.ItemsSource = cars;
        TargetCombo.ItemsSource = cars;
        string? start = car == null ? null : Path.GetFileNameWithoutExtension(car);
        if (start != null && start.EndsWith("_z", StringComparison.OrdinalIgnoreCase)) start = start[..^2];
        CarCombo.Text = cars.FirstOrDefault(c => string.Equals(c, start, StringComparison.OrdinalIgnoreCase)) ?? "";

        CloneRows.Visibility = tool == CarTool.Clone ? Visibility.Visible : Visibility.Collapsed;
        SubstituteRows.Visibility = tool == CarTool.Substitute ? Visibility.Visible : Visibility.Collapsed;
        ExportRows.Visibility = tool == CarTool.Export ? Visibility.Visible : Visibility.Collapsed;
        switch (tool)
        {
            case CarTool.Clone:
                Title = "Clone Car";
                CarLabel.Text = "Car to copy";
                IntroText.Text = "Makes a second car under a name of its own - archive, vehicle table, paint and cover "
                    + "points - and builds it, summer and winter. The original is left as it is.";
                HintText.Text = "The clone is made from what is saved. Backups are kept of every archive it replaces.";
                RunBtn.Content = "Clone";
                break;
            case CarTool.Substitute:
                Title = "Replace a Car";
                CarLabel.Text = "Car to put in";
                IntroText.Text = "Builds one car under another car's name. The replaced car keeps its name, its table rows "
                    + "and its place in traffic - only what it looks and drives like changes. No table is touched.";
                HintText.Text = "The replaced archive is kept as a backup beside it.";
                RunBtn.Content = "Replace";
                break;
            default:
                Title = "Export Car for Multiplayer";
                CarLabel.Text = "Car to export";
                IntroText.Text = "Writes the car out as a multiplayer resource folder: package.json, the archives under "
                    + "sds\\cars and vehicles.json. Nothing of the game is changed.";
                HintText.Text = "The archive is exported as it stands in pc\\sds\\cars - Build the car first.";
                RunBtn.Content = "Export";
                break;
        }
        _hint = HintText.Text;
        Validate();
    }

    // The cars the game has: the summer archives of pc\sds\cars, by model name. Winter twins (_z) follow
    // their summer car in every one of these jobs and are not offered on their own.
    private static List<string> Cars()
    {
        if (!MafiaEnvironment.IsInitialized) return [];
        string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*.sds")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && !n.EndsWith("_z", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    private string Car => CarCombo.Text.Trim();

    private string Target => TargetCombo.Text.Trim();

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) Validate();
    }

    // The button is live only for an input the job would not refuse on sight. A field still empty needs no
    // comment; something typed that cannot work is said in the footer, where the hint otherwise stands - a
    // disabled button shows no tooltip to say it.
    private void Validate()
    {
        bool complete = Car.Length > 0;
        string? wrong = null;
        if (_tool == CarTool.Clone)
        {
            string name = NameBox.Text.Trim();
            complete &= name.Length > 0;
            if (name.Length > 0 && CarCloner.Refuse(name) is { } refused) wrong = ToolText.ForPeople(refused) + ".";
        }
        if (_tool == CarTool.Substitute)
        {
            complete &= Target.Length > 0;
            if (Car.Length > 0 && string.Equals(Target, Car, StringComparison.OrdinalIgnoreCase)) wrong = "A car cannot take its own place.";
        }
        RunBtn.IsEnabled = complete && wrong == null;
        HintText.Text = wrong ?? _hint;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Folder to write the resource into" };
        if (Directory.Exists(FolderBox.Text.Trim())) dialog.InitialDirectory = FolderBox.Text.Trim();
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!RunBtn.IsEnabled) return;

        if (_tool == CarTool.Substitute)
        {
            DialogOutcome sure = AppDialog.Show(this, new DialogOptions
            {
                Title = Title,
                Icon = DialogIcon.Question,
                Heading = $"Put {Car} in the place of {Target}?",
                Text = $"The archive of {Target} is replaced, summer and winter where there is one. What it replaces is kept "
                    + "as a backup in the 'backups' folder beside it.",
                Buttons = DialogButtons.YesCancel,
                ConfirmText = "Replace",
            });
            if (!sure.Confirmed) return;
        }

        string? refused;
        string heading, text;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            switch (_tool)
            {
                case CarTool.Clone:
                {
                    string title = TitleBox.Text.Trim();
                    refused = _session.CloneCar(Car, NameBox.Text.Trim(), TrafficBox.IsChecked == true,
                        title.Length == 0 ? null : title, out CarCloneInfo? made);
                    heading = made == null ? "" : $"{made.Name} is in the game";
                    text = made == null ? "" : $"Vehicle id {made.VehicleId}"
                        + (made.TrafficRows > 0 ? $", {made.TrafficRows} traffic row(s)" : "")
                        + (made.TextId is { } id ? $", name text {id}" : "") + "."
                        + Packed(made.Packed) + Notes(made.Notes);
                    break;
                }
                case CarTool.Substitute:
                {
                    refused = _session.SubstituteCar(Car, Target, out CarSubstituteInfo? done);
                    heading = done == null ? "" : $"{Target} is now {Car}";
                    text = done == null ? "" : $"Model {done.Model}." + Packed(done.Packed) + Notes(done.Notes);
                    break;
                }
                default:
                {
                    refused = _session.ExportCarForM2o(Car, FolderBox.Text.Trim(), ResourceBox.Text.Trim(), out M2oExportInfo? exported);
                    heading = exported == null ? "" : $"Exported as {exported.Resource}";
                    text = exported == null ? "" : $"{exported.Folder}\n\n{exported.Files.Count} file(s); the folder lists "
                        + $"{exported.Vehicles} car(s) now." + Notes(exported.Notes);
                    break;
                }
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (refused != null)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = Title,
                Icon = DialogIcon.Warning,
                Heading = "Not done",
                Text = ToolText.ForPeople(refused),
            });
            return;
        }
        AppDialog.Show(this, new DialogOptions { Title = Title, Icon = DialogIcon.Success, Heading = heading, Text = text });
        DialogResult = true;
    }

    private static string Packed(IReadOnlyList<PackedArchive> packed) => packed.Count == 0
        ? ""
        : "\n\nBuilt: " + string.Join(", ", packed.Select(p => Path.GetFileName(p.Archive)))
            + (packed.Any(p => p.Backup != null) ? ". What they replaced is in the 'backups' folders beside them." : ".");

    private static string Notes(IReadOnlyList<string> notes) =>
        notes.Count == 0 ? "" : "\n\n" + string.Join("\n", notes.Select(n => "• " + ToolText.ForPeople(n)));

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
