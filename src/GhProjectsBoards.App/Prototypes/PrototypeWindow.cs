using GhProjectsBoards.App.Prototypes.WinUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace GhProjectsBoards.App.Prototypes;
public sealed class PrototypeWindow : Window
{
    public PrototypeWindow()
    {
        Title = "計画表の試作 — winui";
        AppWindow.Resize(new(1280, 760));
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var close = new Button { Content = "閉じる", Margin = new(4) }; close.Click += (_, _) => Close();
        root.Children.Add(close);
        var view = new NativePrototype();
        Grid.SetRow(view, 1); root.Children.Add(view); Content = root;
        AppWindow.Closing += (_, _) => close.Focus(FocusState.Programmatic);
    }
}
