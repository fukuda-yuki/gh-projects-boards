using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private Button CreateHelpButton()
    {
        var button = Id(new Button {
            Content = new SymbolIcon(Symbol.Help), Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            MinHeight = 0, Height = 32, Padding = new(8, 0, 8, 0), AllowFocusOnInteraction = false
        }, "PlanHelp");
        AutomationProperties.SetName(button, "ヘルプ（F1）");
        ToolTipService.SetToolTip(button, "ヘルプ（F1）");
        // Help must not enter the plan command queue or commit pending input.
        button.Click += async (_, _) => await OpenManual();
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.F1 };
        accelerator.Invoked += async (_, args) => {
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(p => p.Child is ContentDialog)) return;
            args.Handled = true;
            await OpenManual();
        };
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        KeyboardAccelerators.Add(accelerator);
        return button;
    }

    private async Task OpenManual()
    {
        if (closing || !IsLoaded) return;
        var path = Path.Combine(AppContext.BaseDirectory, "マニュアル.md");
        var launch = OpenDocument ?? LaunchDocument;
        try {
            var found = await Task.Run(() => {
                if (!File.Exists(path)) return false;
                launch(path); return true;
            });
            if (!found && !closing) error.Text = "マニュアルが見つかりません: " + path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (!closing) error.Text = ex.Message;
        }
    }

    private static void LaunchDocument(string path)
    {
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
    }
}
