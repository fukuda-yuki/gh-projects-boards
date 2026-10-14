using Microsoft.UI.Xaml;
namespace GhProjectsBoards.App;
public partial class App : Application
{
    internal static bool UseTableView { get; } = Environment.GetEnvironmentVariable("GHPB_PLAN_RENDERER") == "tableview";
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
