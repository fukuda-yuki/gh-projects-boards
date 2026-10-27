using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace GhProjectsBoards.App;

internal static class PlanStartupView
{
    internal static FrameworkElement Create(PlanStartupNotice notice)
    {
        var text = new TextBox
        {
            // Enable multiline input before assigning Text; otherwise WinUI drops everything after the first newline.
            AcceptsReturn = true, Text = notice.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        AutomationProperties.SetAutomationId(text, notice.Kind == PlanStartupKind.DataRoot ? "PlanStartupDataRoot" : "PlanStartupFailure");
        var caption = new TextBlock { Text = "コピー", VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new SymbolIcon(Symbol.Copy));
        content.Children.Add(caption);
        var copy = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(copy, "PlanStartupCopy");
        AutomationProperties.SetName(copy, "コピー");
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(notice.Text);
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }
            catch (Exception)
            {
                caption.Text = "コピーできませんでした";
                AutomationProperties.SetName(copy, "コピーできませんでした");
            }
        };
        var panel = new StackPanel { Margin = new(24), Spacing = 12, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(text);
        panel.Children.Add(copy);
        return panel;
    }
}
