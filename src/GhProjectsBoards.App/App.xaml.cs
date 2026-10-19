using System.Security.Cryptography;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;
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
        // The key is per data root: that is what two editors would share, while an evaluation root and the
        // ordinary root are separate plans that may stay open side by side.
        var root = Path.GetFullPath(PlanStore.UserDataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16]);
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            // No window was created, so nothing else would end this process.
            Exit();
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
