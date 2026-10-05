using Microsoft.UI.Xaml;
namespace GhProjectsBoards.App;
public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        window = arguments.Length == 2 && arguments[0] == "--prototype" && arguments[1] is "winui" or "webview"
            ? new Prototypes.PrototypeWindow(arguments[1])
            : arguments.SequenceEqual(["--input-check"]) ? new InputCheckWindow() : new MainWindow();
        window.Activate();
    }
}
