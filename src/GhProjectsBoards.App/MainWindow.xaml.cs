using System.Runtime.InteropServices;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace GhProjectsBoards.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private RegistrationWorkspace? workspace;
    private Task? operation;
    private bool closingRequested, closeReady, closed;
    private Control? returnFocus;
    public MainWindow()
    {
        InitializeComponent();
        ProjectsPage.ConnectionRequested += (_, _) => ShowConnection();
        ConnectionPage.ReturnRequested += (_, _) => ShowProjects();
        ConnectionPage.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try
        {
            workspace = new RegistrationWorkspace(RegistrationStore.ForUser());
            ProjectsPage.Initialize(workspace);
            ConnectionPage.Initialize(workspace);
            operation = RestoreAsync();
        }
        catch (Exception)
        {
            ConnectionPage.ShowProblem("保存先を利用できません。GHPB_DATA_ROOTは絶対パスを指定してください。実データへの代替保存はしません。");
            ShowConnection();
        }
        SizeWorkspaceWindow();
        AppWindow.Closing += Closing;
        Closed += (_, _) => { closed = true; closingRequested = true; };
    }
    private async Task RestoreAsync()
    {
        try { await workspace!.RestoreAsync(); }
        catch (Exception)
        {
            ConnectionPage.ShowProblem("保存データを読み込めません。保存先を確認してください。自動削除はしていません。");
            ShowConnection();
        }
    }
    private void ShowConnection()
    {
        if (!ProjectsPage.CanLeaveForConnection()) return;
        returnFocus = ProjectsPage.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        ProjectsPage.Visibility = Visibility.Collapsed;
        ConnectionPage.Visibility = Visibility.Visible;
    }
    private void ShowProjects()
    {
        ConnectionPage.Visibility = Visibility.Collapsed;
        ProjectsPage.Visibility = Visibility.Visible;
        ProjectsPage.Update();
        ProjectsPage.ReturnFromConnection();
        if (returnFocus is { IsLoaded: true, IsEnabled: true }) returnFocus.Focus(FocusState.Programmatic);
    }
    private void SizeWorkspaceWindow()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        // AppWindow uses physical pixels; XamlRoot is not available during construction.
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var width = Math.Min((int)Math.Round(1280 * scale), workArea.Width);
        var height = Math.Min((int)Math.Round(800 * scale), workArea.Height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(900, workArea.Width);
            presenter.PreferredMinimumHeight = Math.Min(620, workArea.Height);
        }
        AppWindow.MoveAndResize(new RectInt32(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2, width, height));
    }

    private async void Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeReady)
        {
            closingRequested = true;
            return;
        }
        args.Cancel = true;
        if (closingRequested) return;
        closingRequested = true;
        workspace?.CancelPendingEdits();
        if (ProjectsPage.Visibility == Visibility.Visible) ProjectsPage.FocusHeader();

        ProjectsPage.IsEnabled = false;

        await ConnectionPage.StopAsync();
        if (workspace is not null) await workspace.StopAsync();
        // Keep the UI dispatcher alive until the owned gh operation has stopped.
        if (operation is not null) await operation;
        if (workspace is not null && !await workspace.FlushDraftsAsync())
        {
            closingRequested = false; ProjectsPage.IsEnabled = true; ConnectionPage.ResumeAfterFailedClose();
            return;
        }
        await SheetDiagnostics.CompleteAsync();
        closeReady = true;
        DispatcherQueue.TryEnqueue(() => { if (!closed) Close(); });
    }
}
