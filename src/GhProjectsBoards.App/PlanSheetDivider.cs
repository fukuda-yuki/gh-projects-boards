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
    private readonly double minimum;
    private readonly Func<bool> canChange;
    internal PlanSheetDivider(Func<double> value, Func<double> maximum, Action<double> change, double minimum = 160,
        Action? dragStarted = null, Action<double>? dragPreview = null, Func<bool, Task>? dragCompleted = null,
        Func<Task>? fit = null, Func<bool>? canChange = null)
    {
        this.value = value; this.maximum = maximum; this.change = change; this.minimum = minimum;
        this.canChange = canChange ?? (() => IsEnabled);
        IsTabStop = dragPreview is null;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        var thumb = new Thumb { IsTabStop = false, Background = PlanSheetView.Brush(dragPreview is null ? "DividerStrokeColorDefaultBrush" : "ControlFillColorTransparentBrush") };
        Content = thumb;
        ActualThemeChanged += (_, _) => thumb.Background = PlanSheetView.Brush(dragPreview is null ? "DividerStrokeColorDefaultBrush" : "ControlFillColorTransparentBrush");
        thumb.Template = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Thumb">
                <Border Background="{TemplateBinding Background}" />
            </ControlTemplate>
            """);
        var dragging = false; var next = 0d;
        thumb.DragStarted += (_, _) => { dragging = this.canChange(); next = value(); if (dragging) dragStarted?.Invoke(); };
        thumb.DragDelta += (_, args) => {
            if (!dragging || !this.canChange()) return;
            next = Math.Clamp(next + args.HorizontalChange, minimum, maximum());
            if (dragPreview is null) Set(next); else dragPreview(next);
        };
        thumb.DragCompleted += async (_, args) => {
            if (!dragging) return;
            dragging = false;
            if (dragCompleted is not null) await dragCompleted(args.Canceled);
        };
        if (fit is not null) thumb.DoubleTapped += async (_, args) => { args.Handled = true; if (this.canChange()) await fit(); };
        KeyDown += (_, args) => {
            if (args.Key is VirtualKey.Left or VirtualKey.Right)
            { Set(value() + (args.Key == VirtualKey.Left ? -16 : 16)); args.Handled = true; }
        };
    }
    private void Set(double next)
    {
        if (!canChange()) return;
        if (!double.IsFinite(next)) throw new ArgumentException("幅が不正です。");
        change(Math.Clamp(next, minimum, maximum()));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new DividerPeer(this);
    private sealed class DividerPeer(PlanSheetDivider owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(PlanSheetDivider);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => !owner.canChange();
        public double LargeChange => 64;
        public double SmallChange => 16;
        public double Maximum => owner.maximum();
        public double Minimum => owner.minimum;
        public double Value => owner.value();
        public void SetValue(double value) => owner.Set(value);
    }
}
