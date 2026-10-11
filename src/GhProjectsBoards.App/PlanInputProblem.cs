using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace GhProjectsBoards.App;

// Validation must stay next to its editor without taking focus or expiring on a timer.
internal sealed class PlanInputProblem
{
    private readonly Popup popup = new() { DesiredPlacement = PopupPlacementMode.BottomEdgeAlignedLeft };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
    internal PlanInputProblem(string id)
    {
        var border = new Border { Child = message, Padding = new(8), BorderThickness = new(1), CornerRadius = new(4) };
        border.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        border.BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        AutomationProperties.SetAutomationId(border, id);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);
        message.Loaded += (_, _) => Announce();
        popup.Child = border;
    }
    internal void Close() => popup.IsOpen = false;
    internal void Show(FrameworkElement target, string text)
    {
        var announce = !popup.IsOpen || message.Text != text || popup.PlacementTarget != target;
        var loaded = message.IsLoaded;
        popup.XamlRoot = target.XamlRoot; popup.PlacementTarget = target; message.Text = text; popup.IsOpen = true;
        if (announce && loaded) Announce();
    }
    private void Announce() => FrameworkElementAutomationPeer.CreatePeerForElement(message)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
}
