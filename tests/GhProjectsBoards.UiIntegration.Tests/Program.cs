using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnitLite;

namespace GhProjectsBoards.UiIntegration.Tests;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.StartsWith("--explore"))) return new AutoRun(typeof(Program).Assembly).Execute(args);
        Environment.ExitCode = 2; // Closing the window before NUnit completes is incomplete execution.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new TrackedContext(dispatcher));
            var app = new HostedApplication();
            Ui.Queue = dispatcher;
            app.UnhandledException += (_, e) => { Ui.RecordFailure(e.Exception); e.Handled = true; };
            TaskScheduler.UnobservedTaskException += (_, e) => { Ui.RecordFailure(e.Exception); e.SetObserved(); };
        });
        return Ui.Fatal is null && TrackedContext.Operations == 0 && TrackedContext.Posts == 0 ? Environment.ExitCode : 1;
    }

    // Inherit the actual compiled Application resources and XAML metadata provider.
    // Only test startup differs; the ordinary product never references this assembly.
    private sealed class HostedApplication : App.App
    {
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Ui.Root = new Grid();
            Ui.Window = new Window { Content = Ui.Root, Title = "GHPB hosted UI integration" };
            Ui.Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            Ui.Window.Activate();
            _ = Task.Run(() =>
            {
                var result = new AutoRun(typeof(Program).Assembly).Execute(Environment.GetCommandLineArgs().Skip(1).ToArray());
                Environment.ExitCode = result == 0 && Ui.Fatal is null ? 0 : 1;
                if (!Ui.Queue.TryEnqueue(() => { Ui.Window.Close(); Exit(); })) Environment.Exit(2);
            });
        }
    }
}
