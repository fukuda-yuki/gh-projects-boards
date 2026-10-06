using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Windows.System;
namespace GhProjectsBoards.App;

internal sealed class PlanSheetDivider : UserControl
{
    private readonly Func<double> value, maximum;
    private readonly Action<double> change;
    internal PlanSheetDivider(Func<double> value, Func<double> maximum, Action<double> change)
    {
        this.value = value; this.maximum = maximum; this.change = change;
        IsTabStop = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        var thumb = new Thumb { IsTabStop = false, Background = PlanSheetView.Brush("DividerStrokeColorDefaultBrush") };
        Content = thumb;
        ActualThemeChanged += (_, _) => thumb.Background = PlanSheetView.Brush("DividerStrokeColorDefaultBrush");
        thumb.Template = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
                <Border Background="{TemplateBinding Background}" />
            </ControlTemplate>
            """);
        thumb.DragDelta += (_, args) => Set(value() + args.HorizontalChange);
        KeyDown += (_, args) => {
            if (args.Key is VirtualKey.Left or VirtualKey.Right)
            { Set(value() + (args.Key == VirtualKey.Left ? -16 : 16)); args.Handled = true; }
        };
    }
    private void Set(double next) => change(Math.Clamp(next, 160, maximum()));
    protected override AutomationPeer OnCreateAutomationPeer() => new DividerPeer(this);
    private sealed class DividerPeer(PlanSheetDivider owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(PlanSheetDivider);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => false;
        public double LargeChange => 64;
        public double SmallChange => 16;
        public double Maximum => owner.maximum();
        public double Minimum => 160;
        public double Value => owner.value();
        public void SetValue(double value) => owner.Set(value);
    }
}
