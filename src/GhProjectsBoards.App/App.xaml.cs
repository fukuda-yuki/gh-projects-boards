using Microsoft.UI.Xaml;
namespace GhProjectsBoards.App;
public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        window = arguments.SequenceEqual(["--prototype", "winui"])
            ? new Prototypes.PrototypeWindow()
            : arguments.SequenceEqual(["--input-check"]) ? new InputCheckWindow() : new MainWindow();
        window.Activate();
    }
}
