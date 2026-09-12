using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;

namespace WinUI.Feasibility;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<ProbeRow> Rows { get; } =
        new(Enumerable.Range(1, 3).Select(id => new ProbeRow { Title = $"試験データ {id:000}", State = "Open" }));

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 650));
    }
}

// Synthetic input probe only; these properties are not the product's field model.
public sealed class ProbeRow
{
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
}
