using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
namespace GhProjectsBoards.App;

// #298: the sheet's ガント toggle is the second entry point to this PC's stored Gantt visibility. The
// workspace owns that preference: it saves a change and re-renders every Project's sheet with the stored
// value, so the settings switch and this toggle never hold two states.
internal sealed partial class PlanSheetView
{
    private readonly CheckBox ganttToggle = Id(new CheckBox { Content = "ガント", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center }, "PlanSheetShowGantt");
    // 基準日へ移動 and 表示単位 move the chart, so they are hidden with it instead of pointing at nothing.
    private readonly StackPanel ganttScale = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private Func<bool, Task>? changeGanttVisible;
    private bool ganttVisible = true;
    internal bool GanttVisible => ganttVisible;

    private void InitializeGanttToggle(Grid controls, Func<bool, Task>? change)
    {
        changeGanttVisible = change;
        AutomationProperties.SetName(ganttToggle, "ガントを表示");
        ToolTipService.SetToolTip(ganttToggle, "ガントを表示します。件数と絞り込みは変わりません。");
        ganttToggle.IsChecked = ganttVisible;
        // Checked/Unchecked are the path a pointer, a keyboard Space and an automation toggle all take.
        ganttToggle.Checked += async (_, _) => await SetGanttChecked(true);
        ganttToggle.Unchecked += async (_, _) => await SetGanttChecked(false);
        controls.Children.Add(ganttToggle);
    }

    // Called by the workspace after it loaded or saved the this-PC display preference.
    internal void SetGanttVisible(bool value)
    {
        if (ganttVisible != value)
        {
            ganttVisible = value;
            SyncGanttChrome();
            RefreshOverviewCommands();
            RefreshLayout();
            return;
        }
        SyncGanttChrome();
    }

    private async Task SetGanttChecked(bool on)
    {
        if (rendering || disposed || ganttVisible == on) return;
        if (changeGanttVisible is not { } change) { SetGanttVisible(on); return; }
        // The workspace queues the save and redraws the sheet; a failed save leaves the stored value, which the
        // sync below puts back on the toggle.
        await change(on);
        if (!disposed) SyncGanttChrome();
    }

    private void SyncGanttChrome()
    {
        var shown = ganttVisible ? Visibility.Visible : Visibility.Collapsed;
        chartHead.Visibility = shown;
        chartHorizontal.Visibility = shown;
        chartBackground.Visibility = shown;
        chartStatus.Visibility = shown;
        ganttScale.Visibility = shown;
        if (divider is not null) divider.Visibility = shown;
        if (ganttToggle.IsChecked != ganttVisible)
        {
            rendering = true;
            try { ganttToggle.IsChecked = ganttVisible; }
            finally { rendering = false; }
        }
    }
}
