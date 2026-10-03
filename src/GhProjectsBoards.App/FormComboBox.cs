using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GhProjectsBoards.App;

public sealed class FormComboBox : ComboBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // The native closed control changes its focused selection before routed
        // event handlers run. Leave the wheel to the containing scroll surface;
        // open lists and keyboard selection keep their native behavior.
        if (IsDropDownOpen) base.OnPointerWheelChanged(e);
    }
}
