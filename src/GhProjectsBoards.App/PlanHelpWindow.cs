using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace GhProjectsBoards.App;

// #172: the manual opens in one window of its own. A browser cannot be told to reuse a tab, so every F1 there added
// another one; this window is brought forward instead.
internal sealed class PlanHelpWindow : Window
{
    private readonly WebView2 web = new();
    internal string Document { get; }
    internal bool IsClosed { get; private set; }

    private PlanHelpWindow(string document)
    {
        Document = document;
        Title = "マニュアル – 計画エディタ";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(web, "マニュアル");
        Content = web;
        Closed += (_, _) => IsClosed = true;
    }

    // Returns null when WebView2 cannot start; the caller then opens the document in its default app.
    internal static async Task<PlanHelpWindow?> OpenAsync(string document, string dataFolder, double scale)
    {
        var window = new PlanHelpWindow(document);
        try {
            window.AppWindow.Resize(new((int)(960 * scale), (int)(1000 * scale)));
            window.Activate();
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, dataFolder, null);
            await window.web.EnsureCoreWebView2Async(environment);
            var core = window.web.CoreWebView2;
            core.Settings.IsScriptEnabled = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = false; core.Settings.AreHostObjectsAllowed = false;
            var home = new Uri(document);
            core.NavigationStarting += (_, args) => {
                // The manual and its own headings stay here; GitHub pages and other shipped files open in their app.
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)) { args.Cancel = true; return; }
                if (target.GetLeftPart(UriPartial.Query) == home.GetLeftPart(UriPartial.Query)) return;
                args.Cancel = true; OpenOutside(target);
            };
            core.NewWindowRequested += (_, args) => {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)) OpenOutside(target);
            };
            core.DownloadStarting += (_, args) => args.Cancel = true;
            window.web.Source = home;
            return window;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            window.Close();
            return null;
        }
    }

    private static void OpenOutside(Uri target)
    {
        if (!target.IsFile) { _ = Launcher.LaunchUriAsync(target); return; }
        try { using var process = Process.Start(new ProcessStartInfo(target.LocalPath) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
}
