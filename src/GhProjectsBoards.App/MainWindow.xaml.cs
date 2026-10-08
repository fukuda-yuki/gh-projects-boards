using System.Runtime.InteropServices;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
namespace GhProjectsBoards.App;
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    private PlanWorkspaceView? workspace;
    private bool closing, ready;
    public MainWindow()
    {
        InitializeComponent();
        try
        {
            workspace = new(PlanWorkspace.ForUser()) { WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this) };
            WorkspaceRoot.Children.Add(workspace);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(workspace.WorkspaceTitleBar);
        }
        catch (Exception)
        {
            WorkspaceRoot.Children.Add(new TextBlock { Text = "保存先を利用できません。GHPB_DATA_ROOT は絶対パスを指定してください。", TextWrapping = TextWrapping.Wrap, Margin = new(24) });
        }
        SizeWorkspaceWindow();
        AppWindow.Closing += Closing;
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
        if (ready) return;
        args.Cancel = true;
        if (closing) return;
        closing = true;
        if (workspace is null || await workspace.StopAsync())
        {
            // StopAsync may complete synchronously. Finish the native Closing
            // callback before allowing the final XAML/input-context teardown.
            if (!DispatcherQueue.TryEnqueue(() => { ready = true; Close(); })) closing = false;
        }
        else closing = false;
    }
}
