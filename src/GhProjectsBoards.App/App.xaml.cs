using Microsoft.UI.Xaml;

namespace GhProjectsBoards.App;

public partial class App : Application
{
    private Window? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = Environment.GetCommandLineArgs().Skip(1).SequenceEqual(["--input-check"])
            ? new InputCheckWindow()
            : new MainWindow();
        window.Activate();
    }
}
