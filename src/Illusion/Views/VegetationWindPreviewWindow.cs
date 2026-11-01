using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class VegetationWindPreviewWindow : Window
{
    private readonly D3DImageHost _host;
    private readonly TextBox _global = new() { Text = "0, 0, 0, 0" };
    private readonly TextBox _localPosition = new() { Text = "0, 0, 0" };
    private readonly TextBox _localParameters = new() { Text = "0, 0" };
    private readonly TextBox _time = new() { Text = "0" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) };
    internal VegetationWindPreviewWindow(D3DImageHost host)
    {
        _host = host; Title = "Native vegetation wind preview"; Width = 520; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "LARGE/GENERAL instanced vertex preview. Enter explicit native wind context. Toolkit lighting and rest-pose picking remain active; unsupported meshes stay static. Suspended during Blender editing. No resources are written.", TextWrapping = TextWrapping.Wrap });
        Add("Global wind D119 (four floats)",_global); Add("Local position D213 (three floats)",_localPosition);
        Add("Local parameters D214 (two floats)",_localParameters); Add("Time in seconds (−600 to 600)",_time);
        var apply = new Button { Content = "Enable / apply explicit context", Margin = new Thickness(0,12,0,4) };
        apply.Click += (_,_) => Apply(); panel.Children.Add(apply);
        var disable = new Button { Content = "Disable / inspect" };
        disable.Click += (_,_) => ShowResult(_host.SetNativeVegetationWind(false,null,null,null,null,out var result),result);
        panel.Children.Add(disable); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel,VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (_host.SetNativeVegetationWind(null,null,null,null,null,out var initial) is null && initial is not null)
        {
            _global.Text = Format(initial.GlobalWind); _localPosition.Text = Format(initial.LocalPosition);
            _localParameters.Text = Format(initial.LocalParameters); _time.Text = initial.Time.ToString("R",CultureInfo.InvariantCulture);
        }
        void Add(string label, TextBox field) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,8,0,2) }); panel.Children.Add(field); }
    }
    private static string Format(float[] values) => string.Join(", ",values.Select(v => v.ToString("R",CultureInfo.InvariantCulture)));
    private static float[] Parse(TextBox text, int length)
    {
        var values = text.Text.Split([',',';',' ','\t'],StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v,NumberStyles.Float,CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != length) throw new FormatException($"Expected {length} values."); return values;
    }
    private void Apply()
    {
        try
        {
            string? error = _host.SetNativeVegetationWind(true,Parse(_time,1)[0],Parse(_global,4),Parse(_localPosition,3),Parse(_localParameters,2),out var result);
            ShowResult(error,result);
        }
        catch (Exception error) when (error is FormatException or OverflowException) { _status.Text = error.Message; }
    }
    private void ShowResult(string? error, Mcp.VegetationWindInfo? result) => _status.Text = error ?? (result is null ? "Viewport unavailable."
        : $"Enabled: {result.Enabled}; bridge suspended: {result.BridgeSuspended}. Last frame: {result.EligibleMeshes} eligible meshes, {result.DrawnParts} drawn parts, {result.PendingMeshes} pending.\n"
            + string.Join("\n",result.Refusals.Select(pair => $"{pair.Value}: {pair.Key}")));
}
