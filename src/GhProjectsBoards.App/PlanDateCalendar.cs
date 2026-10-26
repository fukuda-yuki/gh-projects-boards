using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;
namespace GhProjectsBoards.App;

// #298: a plan-sheet date cell offers the same calendar the settings date pickers show. The cell keeps its
// verified ISO text editor (the settings control's own text field would replace that parser and the cell's
// IME/Enter/Tab behaviour); the calendar button and Alt+↓ open this month calendar, and a chosen day becomes
// the cell's ISO edit form committed through the ordinary cell path, so one choice is one Undo step.
internal sealed partial class PlanSheetView
{
    private readonly CalendarView dateCalendar = Id(new CalendarView {
        SelectionMode = CalendarViewSelectionMode.Single, IsGroupLabelVisible = false,
        CalendarIdentifier = Windows.Globalization.CalendarIdentifiers.Gregorian, Language = "ja-JP" }, "PlanDateCalendar");
    private readonly Flyout dateCalendarFlyout = new() {
        AreOpenCloseAnimationsEnabled = false, Placement = FlyoutPlacementMode.Bottom,
        FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter)) { BasedOn = (Style)Application.Current.Resources["PlanFlyoutPresenterStyle"] }
    };
    private PlanSheetCell? dateCalendarCell;
    private bool settingCalendar;

    private void InitializeDateCalendar()
    {
        dateCalendarFlyout.Content = dateCalendar;
        dateCalendarFlyout.Opened += (_, _) => dateCalendar.Focus(FocusState.Programmatic);
        dateCalendar.SelectedDatesChanged += (_, _) => ChooseDate();
        // Escape closes the calendar and leaves the editor and its typed text; the cell's next Escape cancels.
        dateCalendar.PreviewKeyDown += (_, args) => {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true; dateCalendarFlyout.Hide();
        };
        dateCalendarFlyout.Closed += (_, _) => {
            var cell = dateCalendarCell; dateCalendarCell = null; settingCalendar = false;
            // The editor kept its draft while the calendar had focus; it takes the keyboard back, unless the
            // PMO moved to another cell (that cell's focus change already ended this edit).
            if (cell is { } && !disposed && IsLoaded && cell.Editing && cell.IsLoaded) cell.Focus(FocusState.Keyboard);
        };
    }

    internal void OpenDateCalendar(PlanSheetCell cell)
    {
        if (disposed || !cell.IsLoaded || cell.XamlRoot is null) return;
        if (!IsDateField(cell.Field) || ReadOnly(cell.Identity, cell.Field)) return;
        if (!cell.Editing) cell.BeginEditing();
        dateCalendarCell = cell;
        var day = CellDate(cell.Identity, cell.Field);
        // Show the cell's date (or the 基準日's month when it is empty) without treating the setup as a choice.
        settingCalendar = true;
        try
        {
            dateCalendar.SelectedDates.Clear();
            if (day is { } selected) dateCalendar.SelectedDates.Add(new DateTimeOffset(selected.ToDateTime(TimeOnly.MinValue)));
            dateCalendar.SetDisplayDate(new DateTimeOffset((day ?? StatusDate).ToDateTime(TimeOnly.MinValue)));
        }
        finally { settingCalendar = false; }
        dateCalendarFlyout.ShowAt(cell);
    }

    private void ChooseDate()
    {
        if (settingCalendar || disposed || dateCalendarCell is not { } cell) return;
        if (dateCalendar.SelectedDates.Count == 0) return;
        var chosen = dateCalendar.SelectedDates[dateCalendar.SelectedDates.Count - 1];
        dateCalendarCell = null;
        dateCalendarFlyout.Hide();
        // A scrolled-away row's editor is recycled for another task; a choice for it is dropped, never committed
        // to whichever row now owns the editor.
        if (!cell.IsLoaded || !ReferenceEquals(cell, Realized.FirstOrDefault(row => row.Identity == cell.Identity)?.Cells.FirstOrDefault(current => current == cell))) return;
        cell.ApplyDateChoice(DateOnly.FromDateTime(chosen.Date));
    }

    internal void CloseDateCalendar() => dateCalendarFlyout.Hide();

    // The cell's LostFocus asks this before committing: focus inside the calendar keeps the draft with the editor.
    internal bool PreserveForDateCalendar(DependencyObject? target)
    {
        if (target is null || dateCalendarCell is null || !dateCalendarFlyout.IsOpen) return false;
        for (DependencyObject? node = target; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, dateCalendar)) return true;
        return false;
    }
}
