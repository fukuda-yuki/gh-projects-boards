using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnitLite;

namespace GhProjectsBoards.UiIntegration.Tests;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Any(a => a.StartsWith("--explore"))) return new AutoRun(typeof(Program).Assembly).Execute(args);
        Environment.ExitCode = 2; // Closing the window before NUnit completes is incomplete execution.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            TrackedContext.Start(dispatcher);
            var app = new HostedApplication();
            Ui.Queue = dispatcher;
            app.UnhandledException += (_, e) => {
                Ui.Trace($"[UNHANDLED XAML] {e.Message}{Environment.NewLine}{Environment.StackTrace}");
                Ui.RecordFailure(e.Exception); e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, e) => {
                Ui.Trace("[UNOBSERVED TASK]"); Ui.RecordFailure(e.Exception); e.SetObserved();
            };
        });
        // NUnit can report every case passed while the host still fails: a failure recorded outside a case,
        // or asynchronous work left after the window closed. State which, since results.xml cannot.
        Ui.Trace($"[EXIT] nunitExit={Environment.ExitCode} failures={Ui.FailureCount} operations={TrackedContext.Operations} posts={TrackedContext.Posts} {TrackedContext.DescribePosts()}");
        return Ui.FailureCount == 0 && TrackedContext.Operations == 0 && TrackedContext.Posts == 0 ? Environment.ExitCode : 1;
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
            // Routine cases drive focus only through XAML and UI Automation. Activation changes and
            // physical input from the desktop session would invalidate focus and input observations.
            Ui.Window.Activated += (_, e) => Ui.Trace("[WINDOW] " + e.WindowActivationState);
            Ui.Window.Closed += (_, _) => Ui.Trace("[WINDOW] Closed");
            Ui.Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) => Ui.Trace("[INPUT] key " + e.OriginalKey)), true);
            Ui.Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => Ui.Trace("[INPUT] pointer " + e.Pointer.PointerDeviceType)), true);
            Ui.Window.Activate();
            _ = Task.Run(() =>
            {
                var result = new AutoRun(typeof(Program).Assembly).Execute(Environment.GetCommandLineArgs().Skip(1).ToArray());
                Environment.ExitCode = result == 0 && Ui.FailureCount == 0 ? 0 : 1;
                if (!Ui.Queue.TryEnqueue(() => {
                    // Each mounted workspace TitleBar registers non-client regions on this shared window and leaves
                    // them after it unloads. Closing the window with those stale regions fail-fasts in Microsoft.UI.Input.
                    Ui.Trace("[CLOSE] regions");
                    InputNonClientPointerSource.GetForWindowId(Ui.Window.AppWindow.Id).ClearAllRegionRects();
                    // Closing the host's only window ends the application; do not request exit again during that close.
                    Ui.Trace("[CLOSE] window");
                    Ui.Window.Close();
                    Ui.Trace("[CLOSE] window returned");
                })) Environment.Exit(2);
            });
        }
    }
}
