using System.Windows;

namespace GhProjectsBoards.App.GridPrototype;

internal partial class StandardGridWindow : Window
{
    public StandardGridWindow()
    {
        InitializeComponent();
        DataContext = Enumerable.Range(1, 100).Select(id => new StandardRow(id)).ToArray();
        GridInputTrace.Attach(this, StandardGrid, "standard");
    }

    // Deliberately writable: this control is the standard TwoWay-binding baseline,
    // independent of the prototype's validation and operation history.
    internal sealed class StandardRow(int id)
    {
        public string Title { get; set; } = $"試験データ {id:000}";
        public override string ToString() => $"行 {id:000}";
    }
}
