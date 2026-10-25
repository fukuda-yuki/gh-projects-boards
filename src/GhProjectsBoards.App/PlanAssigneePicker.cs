using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace GhProjectsBoards.App;

// #225: the 担当者 candidate list. It is a non-focusable popup so the cell editor keeps keyboard focus: typing
// filters the list and the cell's own key handling moves the highlight. A popup overlays the sheet instead of
// inserting a line or reflowing rows, and its pointer targets are not focusable, so clicking one never commits
// the typed text by taking focus away from the editor first.
internal sealed class PlanAssigneePicker
{
    private readonly Popup popup = new() { DesiredPlacement = PopupPlacementMode.BottomEdgeAlignedLeft };
    private readonly StackPanel choices = new();
    private readonly TextBlock empty = new() { Text = "一致する候補がありません", FontSize = 13, Margin = new(8, 3, 8, 3),
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Visibility = Visibility.Collapsed };
    private readonly Border container;
    private readonly Action<PlanAssigneeChoice> choose;
    private readonly List<Border> rows = [];
    private ImmutableArray<PlanAssigneeChoice> shown = [];
    private int highlight = -1;

    internal PlanAssigneePicker(Action<PlanAssigneeChoice> choose)
    {
        this.choose = choose;
        var content = new StackPanel();
        content.Children.Add(empty); content.Children.Add(choices);
        container = new Border {
            Child = new ScrollViewer { Content = content, MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            MaxWidth = 420, Padding = new(0, 4, 0, 4), CornerRadius = new(4), BorderThickness = new(1),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["WorkspaceCardStrokeBrush"] };
        AutomationProperties.SetAutomationId(container, "PlanAssigneeCandidates");
        AutomationProperties.SetName(container, "担当者の候補");
        AutomationProperties.SetAutomationId(empty, "PlanAssigneeCandidatesEmpty");
        popup.Child = container;
    }

    internal PlanSheetCell? Cell { get; private set; }
    internal bool IsOpen => popup.IsOpen;

    // Shows (or refreshes) the list under the editing cell. A refused typed login keeps its message and the
    // closest: the nearest people are listed first so correcting a typo needs no extra step.
    internal void Show(PlanSheetCell cell, ImmutableArray<PlanAssigneeChoice> candidates, string text, bool closest)
    {
        if (!cell.IsLoaded || cell.XamlRoot is null) return;
        Cell = cell;
        popup.XamlRoot = cell.XamlRoot;
        popup.PlacementTarget = cell;
        container.MinWidth = Math.Max(200, cell.ActualWidth);
        Populate(candidates, text, closest);
        popup.IsOpen = true;
    }

    internal void Filter(ImmutableArray<PlanAssigneeChoice> candidates, string text)
    {
        if (!popup.IsOpen) return;
        Populate(candidates, text, closest: false);
    }

    internal void Hide()
    {
        popup.IsOpen = false;
        Cell = null; shown = []; highlight = -1;
        rows.Clear(); choices.Children.Clear(); empty.Visibility = Visibility.Collapsed;
    }

    // #225 keyboard contract: ↑/↓ move the highlight and wrap, Enter takes it, Escape closes the list. A key this
    // does not use returns false so the cell keeps its ordinary editing behavior, including Enter with no
    // highlighted person committing the typed login through the verification path.
    internal bool Key(VirtualKey key) => key switch
    {
        VirtualKey.Up => Move(-1),
        VirtualKey.Down => Move(1),
        VirtualKey.Enter => Accept(),
        VirtualKey.Escape => Close(),
        _ => false
    };

    // One choice body for the pointer and for the hosted cases: the pointer handler on a row and the keyboard's
    // Enter both end here, so both commit through the same single cell operation.
    internal bool Choose(int index)
    {
        if (index < 0 || index >= shown.Length) return false;
        choose(shown[index]);
        return true;
    }

    private bool Move(int step)
    {
        if (shown.IsEmpty) return false;
        highlight = highlight < 0 ? step > 0 ? 0 : shown.Length - 1 : (highlight + step + shown.Length) % shown.Length;
        Paint();
        return true;
    }

    private bool Accept()
    {
        if (highlight < 0 || highlight >= shown.Length) return false;
        return Choose(highlight);
    }

    private bool Close()
    {
        Cell?.DismissAssigneeList();
        Hide();
        return true;
    }

    private void Populate(ImmutableArray<PlanAssigneeChoice> candidates, string text, bool closest)
    {
        shown = closest ? PlanAssigneeChoices.Closest(candidates, text) : PlanAssigneeChoices.Matching(candidates, text);
        highlight = -1;
        rows.Clear(); choices.Children.Clear();
        foreach (var candidate in shown)
        {
            var label = new TextBlock { Text = candidate.Label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center };
            var row = new Border { Child = label, Padding = new(8, 3, 8, 3) };
            AutomationProperties.SetAutomationId(row, "PlanAssigneeChoice" + rows.Count);
            AutomationProperties.SetName(row, candidate.Label);
            var index = rows.Count;
            row.Tapped += (_, args) => { args.Handled = true; Choose(index); };
            rows.Add(row); choices.Children.Add(row);
        }
        empty.Visibility = shown.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        Paint();
    }

    private void Paint()
    {
        for (var index = 0; index < rows.Count; index++)
            rows[index].Background = index == highlight ? (Brush)Application.Current.Resources["SheetSelectionBrush"] : null;
    }
}

internal sealed partial class PlanSheetView
{
    private PlanAssigneePicker? assigneePicker;
    private PlanAssigneePicker AssigneePicker => assigneePicker ??= new(ChooseAssigneeChoice);

    // #225: the cell reports every edit and text change; the first one opens the list, later ones filter it.
    // Escape closes the list without ending the edit, and the same cell typing after that keeps it closed.
    internal void AssigneeEdited(PlanSheetCell cell)
    {
        var candidates = PlanAssigneeChoices.All(Session.Document);
        if (candidates.IsEmpty) { assigneePicker?.Hide(); return; }
        if (assigneePicker is { IsOpen: true } picker && ReferenceEquals(picker.Cell, cell)) picker.Filter(candidates, cell.Text);
        else if (!cell.AssigneeListDismissed) AssigneePicker.Show(cell, candidates, cell.Text, closest: false);
    }

    // #225: a refused typed login keeps its message and the list stays open with the nearest people, so the
    // correction is one edit rather than a second step to see the candidates.
    private void ShowClosestAssigneeCandidates(string identity, string field, string text)
    {
        var cell = Realized.Where(row => row.Identity == identity).SelectMany(row => row.Cells)
            .FirstOrDefault(candidate => candidate.Field == field && candidate.Editing);
        if (cell is null) return;
        var candidates = PlanAssigneeChoices.All(Session.Document);
        if (candidates.IsEmpty) return;
        cell.AllowAssigneeList();
        AssigneePicker.Show(cell, candidates, text, closest: true);
    }

    internal void CloseAssigneeCandidates(PlanSheetCell? cell = null)
    {
        if (assigneePicker is null) return;
        if (cell is null || ReferenceEquals(assigneePicker.Cell, cell)) assigneePicker.Hide();
    }

    internal bool AssigneePickerKey(PlanSheetCell cell, VirtualKey key)
        => assigneePicker is { IsOpen: true } picker && ReferenceEquals(picker.Cell, cell) && picker.Key(key);

    // The pointer route: a candidate row's tap and the hosted cases both enter the one choice body here.
    internal bool ChooseAssigneeCandidate(int index)
        => assigneePicker is { IsOpen: true } picker && picker.Choose(index);

    // #225: Alt+↓ starts editing the selected 担当者 cell and opens its list; F2, double-click and typing reach
    // the same state from the cell.
    internal void OpenAssigneeListFromSelection()
    {
        if (selected.Length == 0 || selectedField != PlanField.Assignees || ReadOnly(selected, selectedField)) return;
        var cell = Realized.FirstOrDefault(row => row.Identity == selected)?.Cells.FirstOrDefault(c => c.Field == selectedField);
        cell?.BeginEditing();
    }

    private void ChooseAssigneeChoice(PlanAssigneeChoice choice)
    {
        if (assigneePicker?.Cell is not { Editing: true } cell) { assigneePicker?.Hide(); return; }
        cell.ApplyAssigneeChoice(choice.Login);
    }
}
