using System.ComponentModel;
using System.Diagnostics;
using GhProjectsBoards.Core.PlanEditor;
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
        // #172: F1 again brings the open manual forward instead of opening another copy.
        if (HelpWindow is { IsClosed: false } open) { open.Activate(); return; }
        if (helpOpening) return;
        var path = Path.Combine(AppContext.BaseDirectory, "マニュアル.md");
        helpOpening = true;
        try {
            var rendered = await Task.Run(() => {
                if (!File.Exists(path)) return null;
                string document;
                try {
                    var html = PlanManualHtml.Render(File.ReadAllText(path), AppContext.BaseDirectory);
                    // Separate invocations cannot overwrite a document that is still being opened.
                    var folder = Path.Combine(Path.GetTempPath(), "GhProjectsBoards", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    document = Path.Combine(folder, "マニュアル.html");
                    File.WriteAllText(document, html);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { document = path; }
                OpenDocument?.Invoke(document);
                return document;
            });
            if (rendered is null) { if (!closing) error.Text = "マニュアルが見つかりません: " + path; return; }
            if (OpenDocument is not null || closing) return;
            if (Path.GetExtension(rendered) == ".html"
                && await PlanHelpWindow.OpenAsync(rendered, Path.Combine(assistDataRoot, "PlanningEditor", "v1", "webview2"), XamlRoot.RasterizationScale) is { } window) {
                if (closing) window.Close(); else HelpWindow = window;
                return;
            }
            await Task.Run(() => LaunchDocument(rendered));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            if (!closing) error.Text = ex.Message;
        }
        finally { helpOpening = false; }
    }
    private bool helpOpening;
    internal PlanHelpWindow? HelpWindow { get; private set; }
    private void CloseHelp() { HelpWindow?.Close(); HelpWindow = null; }

    private static void LaunchDocument(string path)
    {
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) {
            if (Path.GetExtension(path) == ".html") { LaunchDocument(Path.Combine(AppContext.BaseDirectory, "マニュアル.md")); return; }
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
    }
}
