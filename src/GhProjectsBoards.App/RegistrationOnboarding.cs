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
        var savedProfile = Workspace.Profile is null && Workspace.Registrations.Count > 0;
        var savedProject = Workspace.Registrations.Any(r => r.Snapshot.Id.Scope == Workspace.Profile);
        var label = Workspace.Selected is not null ? "Boardsで開く"
            : savedProfile ? "保存済みProjectを開く"
            : savedProject ? "Projectを選ぶ"
            : Workspace.CanRead ? "Projectを追加" : "GitHubに接続";
        GuideNextAction.Content = EmptyNextAction.Content = label;
        GuideNextAction.IsEnabled = EmptyNextAction.IsEnabled = available;
        GuideTitle.Text = EmptyTitle.Text = savedProfile || savedProject ? "作業を続ける" : "Projectを開く";
        GuideContext.Text = Workspace.Selected?.Snapshot.Title ?? "";
        GuideContext.Visibility = GuideContext.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = savedProject && !Workspace.CanRead ? "保存済みデータ" : "";
        EmptyHint.Visibility = EmptyHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (guideOpen)
        {
            DiscoveryForm.Visibility = Preview.Visibility = Visibility.Collapsed;
            GettingStarted.Visibility = Visibility.Visible;
        }
    }

    private void FocusGettingStarted()
    {
        if (!guideOpen || !IsLoaded || Visibility != Visibility.Visible) return;
        GuideNextAction.Focus(FocusState.Programmatic);
    }

    private void ContinueGettingStarted(object sender, RoutedEventArgs e)
    {
        if (Workspace.IsBusy || !CanLeaveForConnection()) return;
        if (Workspace.Selected is not null) { OpenBoardFromGuide(sender, e); return; }
        if (Workspace.Profile is null && Workspace.Registrations.Count > 0)
        {
            ShowPreview(); OpenProjectNavigation(); Profiles.Focus(FocusState.Programmatic); Profiles.IsDropDownOpen = true; return;
        }
        if (Workspace.Registrations.Any(r => r.Snapshot.Id.Scope == Workspace.Profile))
        {
            ShowPreview(); OpenProjectNavigation(); Navigation.Focus(FocusState.Programmatic); return;
        }
        if (Workspace.CanRead)
        {
            if (guideOpen) RegisterFromGuide(sender, e); else ShowAdd(sender, e);
            return;
        }
        RequestConnection(sender, e);
    }

    private void OpenProjectNavigation()
    {
        WorkspaceSplitView.IsPaneOpen = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NavigationToggle, "Project一覧を折りたたむ");
    }

    private void RegisterFromGuide(object sender, RoutedEventArgs e)
    {
        if (!Workspace.CanRead || Workspace.IsBusy || !CanLeaveForConnection()) return;
        ShowAdd(sender, e);
        guideRegistration = true;
        DiscoveryBack.Content = "戻る";
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
            DiscoveryBack.Content = guideRegistration ? "戻る" : "ワークスペースへ戻る";
        }
        if (guideReturnFocus is { } focus && focus.TryGetTarget(out var previous) && previous.IsLoaded && previous.IsEnabled)
            previous.Focus(FocusState.Programmatic);
        else if (StartGuide.Visibility == Visibility.Visible) StartGuide.Focus(FocusState.Programmatic);
        else EmptyNextAction.Focus(FocusState.Programmatic);
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
