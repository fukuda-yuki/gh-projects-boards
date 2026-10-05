using GhProjectsBoards.App.Prototypes.WinUi;
using GhProjectsBoards.App.Prototypes.WebView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace GhProjectsBoards.App.Prototypes;
public sealed class PrototypeWindow : Window
{
    public PrototypeWindow(string kind)
    {
        Title = $"計画表の試作 — {kind}";
        AppWindow.Resize(new(1280, 760));
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var close = new Button { Content = "閉じる", Margin = new(4) }; close.Click += (_, _) => Close();
        root.Children.Add(close);
        FrameworkElement view = kind == "winui" ? new NativePrototype() : new WebPrototype();
        Grid.SetRow(view, 1); root.Children.Add(view); Content = root;
        AppWindow.Closing += (_, _) => close.Focus(FocusState.Programmatic);
    }
}
