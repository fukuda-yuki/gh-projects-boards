using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
namespace GhProjectsBoards.App;
public partial class App : Application
{
    private const string InstanceKey = "GhProjectsBoards.PlanEditor";
    private Window? window;
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One editor per Project: a second launch hands its activation to the running window, which is
        // brought to the front, instead of opening a second editor on the same plan (#248).
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            return;
        }
        instance.Activated += (_, _) =>
        {
            var target = window;
            if (target is null) return;
            target.DispatcherQueue.TryEnqueue(() =>
            {
                target.Activate();
                if (target.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                    presenter.Restore();
            });
        };
        window = new MainWindow();
        window.Activate();
    }
}
