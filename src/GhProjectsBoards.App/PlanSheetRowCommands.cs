using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace GhProjectsBoards.App;

// #231 slice B: the row context menu and the row-state commands' presentation. The commands themselves
// are Core (slice A); this file owns reachability, the per-item reason when a command cannot apply, and
// the mixed-selection report.
internal sealed partial class PlanSheetView
{
    private MenuFlyout? rowMenu;
    private string? rowMenuIdentity;
    internal string FirstField => Fields.Length > 0 ? Fields[0] : PlanField.Title;

    // The rows the row commands act on: the rows of the current selection, without the trailing input row.
    private ImmutableArray<string> MenuTargets()
        => [.. RowIds.Skip(Range.Row).Take(Range.RowCount).Where(Rows.ContainsKey)];

    internal void ShowRowMenu(string identity, string field, FrameworkElement target, Point? position = null)
    {
        if (!Rows.ContainsKey(identity)) return;
        // Right-clicking outside the selection selects that cell, exactly like a left click, so the
        // commands never act on a row the PMO is not looking at. The whole-row gesture stays #240's.
        if (!IsSelected(identity, field)) Select(identity, field, false);
        var targets = MenuTargets();
        if (targets.IsEmpty) return;
        rowMenuIdentity = identity;
        rowMenu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        AddRowItem("行を削除", "PlanRowDelete", "Ctrl+-", DeleteReason(targets), () => DeleteRows(targets));
        AddRowItem("完了にする", "PlanRowComplete", "", targets, PlanRowAction.Complete, () => RowStateReport(targets, PlanRowAction.Complete, "完了にしました"));
        AddRowItem("中止にする", "PlanRowCancel", "", targets, PlanRowAction.Cancel, () => RowStateReport(targets, PlanRowAction.Cancel, "中止にしました"));
        AddRowItem("この版から外す", "PlanRowRemove", "", targets, PlanRowAction.Remove, () => RowStateReport(targets, PlanRowAction.Remove, "この版から外しました"));
        AddRowItem("未完了に戻す", "PlanRowReopen", "", targets, PlanRowAction.Reopen, () => RowStateReport(targets, PlanRowAction.Reopen, "未完了に戻しました"));
        rowMenu.Closed += (_, _) => { rowMenuIdentity = null; };
        if (position is { } point) rowMenu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else rowMenu.ShowAt(target, new FlyoutShowOptions { Position = new Point(target.ActualWidth / 2, target.ActualHeight / 2) });
    }

    private void AddRowItem(string label, string id, string shortcut, string? reason, Func<Task> run)
        => Add(label, id, shortcut, reason, run);
    private void AddRowItem(string label, string id, string shortcut, ImmutableArray<string> targets, PlanRowAction action, Func<Task> run)
    {
        var eligibility = Eligibility(targets, action);
        Add(label, id, shortcut, !eligibility.Applies.IsEmpty ? SkipReason(eligibility) : NoEligibleReason(eligibility), run);
    }
    // One item shape for every command: a disabled item keeps focus and explains itself, so a reason is
    // never silent (the sheet's command rows follow the same rule).
    private void Add(string label, string id, string shortcut, string? reason, Func<Task> run)
    {
        var item = Id(new MenuFlyoutItem { Text = label, IsEnabled = reason is null, AllowFocusWhenDisabled = true,
            KeyboardAcceleratorTextOverride = shortcut }, id);
        AutomationProperties.SetName(item, label);
        if (reason is not null) { AutomationProperties.SetHelpText(item, reason); ToolTipService.SetToolTip(item, label + ": " + reason); }
        else ToolTipService.SetToolTip(item, label);
        item.Click += async (_, _) => { if (reason is null) await Run(run, label); };
        rowMenu!.Items.Add(item);
    }

    private PlanRowEligibility Eligibility(ImmutableArray<string> targets, PlanRowAction action)
    {
        try { return PlanOperations.Eligible(Session.Document, targets, action); }
        catch (ArgumentException) { return new([], [.. targets.Select(id => (id, "選べない行です"))]); }
    }
    // How many rows the command will skip and why, shown as the item's reason while it still applies to
    // at least one row (#231 mixed selections).
    private static string? SkipReason(PlanRowEligibility eligibility)
        => eligibility.Skipped.IsEmpty ? null
            : $"{eligibility.Skipped.Length}行を除外します（{string.Join("、", eligibility.Skipped.Select(s => s.Reason).Distinct(StringComparer.Ordinal))}）";
    private static string NoEligibleReason(PlanRowEligibility eligibility)
        => eligibility.Skipped.IsEmpty ? PlanOperations.NothingEligible
            : string.Join("、", eligibility.Skipped.Select(s => s.Reason).Distinct(StringComparer.Ordinal));
    // A published Issue cannot be deleted; the reason is the same sentence Core would refuse with.
    private string? DeleteReason(ImmutableArray<string> targets)
    {
        var baseline = Session.Document.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        var outline = new PlanOutline(Session.Document.State.Rows);
        var published = targets.SelectMany(id => outline.Subtree(id)).Any(r => baseline.Contains(r.Identity));
        return published ? "GitHub の Issue は削除できません。完了・中止・この版から外す を使ってください" : null;
    }

    private async Task RowStateReport(ImmutableArray<string> targets, PlanRowAction action, string done)
    {
        var eligibility = Eligibility(targets, action);
        if (eligibility.Applies.IsEmpty) { error.Text = NoEligibleReason(eligibility); return; }
        var command = action switch
        {
            PlanRowAction.Complete => (PlanCommand)new ClosePlanRows(eligibility.Applies),
            PlanRowAction.Cancel => new ClosePlanRows(eligibility.Applies, Cancelled: true),
            PlanRowAction.Remove => new RemovePlanRows(eligibility.Applies),
            _ => new ReopenPlanRows(eligibility.Applies)
        };
        Check(await Session.Execute(command, Today));
        Refresh();
        // Say what happened and what was left alone; choosing another row or cell dismisses it.
        error.Text = eligibility.Skipped.IsEmpty ? done : done + "。" + SkipReason(eligibility);
    }

    // The tag a row carries while a state command is pending: the command's own name, so the tag and the
    // outlined lateness marks (never filled) cannot be read as the same thing.
    internal string? RowStateTag(string identity)
    {
        if (!Rows.TryGetValue(identity, out var row)) return null;
        var baseline = Session.Document.Baseline.Rows.FirstOrDefault(r => r.Identity == identity);
        return TagFor(row, baseline is not null) ?? ReopenTagFor(row, baseline);
    }
    internal bool IsDimmed(string identity) => RowStateTag(identity) is "中止にする" or "この版から外す";
    internal static string? TagFor(PlanRow row, bool published) => !published ? null : row.Removed ? "この版から外す"
        : row.Cancelled ? "中止にする" : row.Closed ? "完了にする" : null;
    internal static string? ReopenTagFor(PlanRow row, PlanRow? baseline) => baseline is { Closed: true } && !row.Closed ? "未完了に戻す" : null;
    // A locally completed row is not closed on GitHub yet, so the GitHub-owned reason would mislead.
    internal bool IsPendingClose(string identity)
    {
        if (!Rows.TryGetValue(identity, out var row)) return false;
        var baseline = Session.Document.Baseline.Rows.FirstOrDefault(r => r.Identity == identity);
        return baseline is not null && row.Closed != baseline.Closed;
    }
}
