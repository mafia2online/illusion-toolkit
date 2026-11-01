using System.Windows;
using System.Windows.Controls;
using Illusion.Mcp;
using Illusion.Rendering.Passes;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class WaterReflectionPreviewWindow : Window
{
    internal WaterReflectionPreviewWindow(D3DImageHost host)
    {
        Title = "Temporal water reflection preview"; Width = 510; Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = SceneRenderer.NativeWaterTemporalReflectionScope + " Configure water optics first. History advances per rendered frame. Reset replays from empty history; disabling restores ordinary SSR. No game resources are written.", TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        void Show(string? error, WaterReflectionInfo? info) => status.Text = error ?? (info == null ? "Viewport unavailable."
            : $"Enabled: {info.Enabled}; active: {info.Active}; rendered history frames: {info.Frames}. Optics configured: {info.OpticsConfigured}.");
        void Button(string label, bool? enabled, bool reset)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 10, 0, 0) };
            button.Click += (_, _) => Show(host.SetNativeWaterTemporalReflection(enabled, reset, out var info), info);
            panel.Children.Add(button);
        }
        Button("Enable native temporal filter", true, true); Button("Reset / inspect", null, true); Button("Disable / restore ordinary SSR", false, false);
        panel.Children.Add(status); Content = panel;
        Show(host.SetNativeWaterTemporalReflection(null, false, out var initial), initial);
    }
}
