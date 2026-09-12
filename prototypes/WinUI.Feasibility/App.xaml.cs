using Microsoft.UI.Xaml;

namespace WinUI.Feasibility;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var mode = Environment.GetEnvironmentVariable("GHPB_IME_MODE");
        if (mode is "column" or "standard")
            UnhandledException += (_, error) =>
            {
                var trace = Environment.GetEnvironmentVariable("GHPB_IME_TRACE");
                if (!string.IsNullOrEmpty(trace)) System.IO.File.WriteAllText(trace + ".error.txt", error.Exception.ToString());
            };
        window = mode is "column" or "standard" ? new InputProbeWindow(mode) : new MainWindow();
        window.Activate();
    }
}
