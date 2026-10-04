using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private bool guideOpen, guideRegistration, guideReturnToDiscovery, guideReturnRegistration;
    // Returning focus must not keep an editor alive after its Project is replaced.
    private WeakReference<Control>? guideReturnFocus;

    internal void OfferGettingStarted()
    {
        // Restore must settle first: an unreadable cache is not a new workspace.
        if (Workspace.Registrations.Count == 0 && Workspace.Profile is null && Workspace.Status.Length == 0)
            OpenGettingStarted();
    }

    private void ShowGettingStarted(object sender, RoutedEventArgs e)
    {
        if (Workspace.IsBusy || !CanLeaveForConnection()) return;
        if (guideOpen) { FocusGettingStarted(); return; }
        guideReturnToDiscovery = DiscoveryForm.Visibility == Visibility.Visible;
        guideReturnRegistration = guideRegistration;
        guideReturnFocus = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Control focused ? new(focused) : null;
        OpenGettingStarted();
    }

    private void OpenGettingStarted()
    {
        guideOpen = true; guideRegistration = false;
        DiscoveryForm.Visibility = Preview.Visibility = Visibility.Collapsed;
        GettingStarted.Visibility = Visibility.Visible;
        Update();
        FocusGettingStarted();
    }

    private void UpdateGettingStarted(bool settingsOpen)
    {
        var available = !Workspace.IsBusy && !applyDialog && !settingsOpen;
        StartGuide.IsEnabled = available;
        GuideConnection.IsEnabled = available;
        GuideRegister.IsEnabled = available && Workspace.CanRead;
        GuideOpenBoard.IsEnabled = available && Workspace.Selected is not null;
        GuideConnectionStatus.Text = Workspace.CanRead
            ? $"確認済み: {Workspace.ProfileLogin} / {Workspace.Profile!.Host}"
            : "接続は未確認です。保存済みProjectはオフラインでも開けます。";
        GuideProjectStatus.Text = Workspace.Selected is { } selected
            ? $"登録済み: {selected.Snapshot.Title} / {selected.OwnerLogin} / Project #{selected.Snapshot.Number}"
            : Workspace.Registrations.Any(r => r.Snapshot.Id.Scope == Workspace.Profile)
                ? "登録済みProjectを左の一覧から選んでください。"
                : "Projectは未登録です。接続を確認すると登録できます。";
        if (guideOpen)
        {
            DiscoveryForm.Visibility = Preview.Visibility = Visibility.Collapsed;
            GettingStarted.Visibility = Visibility.Visible;
        }
    }

    private void FocusGettingStarted()
    {
        if (!guideOpen || !IsLoaded || Visibility != Visibility.Visible) return;
        var next = Workspace.Selected is not null ? GuideOpenBoard : Workspace.CanRead ? GuideRegister : GuideConnection;
        next.Focus(FocusState.Programmatic);
    }

    private void RegisterFromGuide(object sender, RoutedEventArgs e)
    {
        if (!Workspace.CanRead || Workspace.IsBusy || !CanLeaveForConnection()) return;
        ShowAdd(sender, e);
        guideRegistration = true;
        DiscoveryBack.Content = "ガイドへ戻る";
        Url.Focus(FocusState.Programmatic);
    }

    private void DismissGettingStarted(object sender, RoutedEventArgs e)
    {
        var returnToDiscovery = guideReturnToDiscovery;
        var returnToRegistration = guideReturnRegistration;
        guideReturnToDiscovery = guideReturnRegistration = false;
        ShowPreview();
        if (returnToDiscovery)
        {
            DiscoveryForm.Visibility = Visibility.Visible;
            Preview.Visibility = Visibility.Collapsed;
            guideRegistration = returnToRegistration;
            DiscoveryBack.Content = guideRegistration ? "ガイドへ戻る" : "ワークスペースへ戻る";
        }
        if (guideReturnFocus is { } focus && focus.TryGetTarget(out var previous) && previous.IsLoaded && previous.IsEnabled)
            previous.Focus(FocusState.Programmatic);
        else StartGuide.Focus(FocusState.Programmatic);
        guideReturnFocus = null;
    }

    private void OpenBoardFromGuide(object sender, RoutedEventArgs e)
    {
        if (Workspace.Selected is null || Workspace.IsBusy) return;
        guideReturnToDiscovery = guideReturnRegistration = false; guideReturnFocus = null;
        ShowPreview();
        EditorHost.Children.OfType<EditingGrid>().FirstOrDefault()?.ShowProjectView(ProjectView.Boards);
        FocusHeader();
    }
}
