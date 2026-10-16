using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView
{
    private readonly ToggleButton detailToggle = Id(new ToggleButton { Height = 28, MinHeight = 0, Padding = new(8, 0, 8, 0), Visibility = Visibility.Collapsed }, "PlanSheetDetail");
    private bool settingDetailToggle;
    internal event Action<bool>? DetailToggleRequested;
    internal void SetDetailChecked(bool value)
    {
        settingDetailToggle = true;
        try { detailToggle.IsChecked = value; }
        finally { settingDetailToggle = false; }
    }
    private void InitializeDetailToggle(Grid line)
    {
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        caption.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 14 }); caption.Children.Add(new TextBlock { Text = "詳細" });
        detailToggle.Content = caption;
        AutomationProperties.SetName(detailToggle, "詳細");
        const string help = "Issue の本文とコメントを右に表示します（Ctrl+I）";
        AutomationProperties.SetHelpText(detailToggle, help); ToolTipService.SetToolTip(detailToggle, help);
        detailToggle.Checked += (_, _) => { if (!settingDetailToggle) DetailToggleRequested?.Invoke(true); };
        detailToggle.Unchecked += (_, _) => { if (!settingDetailToggle) DetailToggleRequested?.Invoke(false); };
        line.Children.Add(detailToggle); SetColumn(detailToggle, 3);
    }
}
