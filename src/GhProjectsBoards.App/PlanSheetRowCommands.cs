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
    // The rows every row-scoped command acts on: the same set the outline commands move, without the
    // trailing input row, which is not a plan row.
    private ImmutableArray<string> CommandTargets => [.. MoveTargets.Where(Rows.ContainsKey)];

    // The rows the row commands act on: the rows of the current selection, without the trailing input row.
    private ImmutableArray<string> MenuTargets() => CommandTargets;

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

    private void AddRowItem(string label, string id, string shortcut, string? refusal, Func<Task> run)
        => Add(label, id, shortcut, refusal is null, refusal, run);
    private void AddRowItem(string label, string id, string shortcut, ImmutableArray<string> targets, PlanRowAction action, Func<Task> run)
    {
        var (enabled, hint) = RowCommandState(targets, action);
        Add(label, id, shortcut, enabled, hint, run);
    }
    // One place decides whether a row command applies and what it will leave alone, so the row context menu
    // and the overflow never disagree.
    internal (bool Enabled, string? Hint) RowCommandState(ImmutableArray<string> targets, PlanRowAction action)
    {
        var (allowed, denied) = Permitted(targets);
        var eligibility = allowed.IsEmpty ? new PlanRowEligibility([], []) : Eligibility(allowed, action);
        var combined = eligibility with { Skipped = eligibility.Skipped.AddRange(denied) };
        // A command that still applies to some selected rows stays enabled and says which rows it will
        // skip and why; only a command that applies to none is disabled with its refusal (#231).
        return (!combined.Applies.IsEmpty,
            !combined.Applies.IsEmpty ? SkipReason(combined) : NoEligibleReason(combined));
    }
    internal const string NoPermissionReason = "この Issue を更新する権限がありません";
    internal bool CannotUpdate(string identity) => Session.Document.Sync.CannotUpdate.Contains(identity);
    // The read's permission observation is not plan state, so it filters the command's targets here rather
    // than in eligibility: a row the viewer cannot update is skipped with its own reason (#231).
    private (ImmutableArray<string> Allowed, ImmutableArray<(string Identity, string Reason)> Denied) Permitted(ImmutableArray<string> targets)
    {
        var denied = targets.Where(CannotUpdate).ToArray();
        return ([.. targets.Where(id => !CannotUpdate(id))], [.. denied.Select(id => (id, NoPermissionReason))]);
    }
    // One item shape for every command: a disabled item keeps focus and explains itself, so its refusal is
    // never silent, and an enabled item explains what it will leave alone.
    private void Add(string label, string id, string shortcut, bool enabled, string? hint, Func<Task> run)
    {
        var item = Id(new MenuFlyoutItem { Text = label, IsEnabled = enabled, AllowFocusWhenDisabled = true,
            KeyboardAcceleratorTextOverride = shortcut }, id);
        AutomationProperties.SetName(item, label);
        if (hint is not null) { AutomationProperties.SetHelpText(item, hint); ToolTipService.SetToolTip(item, label + ": " + hint); }
        else ToolTipService.SetToolTip(item, label);
        item.Click += async (_, _) => { if (enabled) await Run(run, label); };
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
        var (allowed, denied) = Permitted(targets);
        var eligibility = allowed.IsEmpty ? new PlanRowEligibility([], []) : Eligibility(allowed, action);
        var skipped = eligibility with { Skipped = eligibility.Skipped.AddRange(denied) };
        if (eligibility.Applies.IsEmpty) { error.Text = NoEligibleReason(skipped); return; }
        var command = action switch
        {
            PlanRowAction.Complete => (PlanCommand)new ClosePlanRows(eligibility.Applies),
            PlanRowAction.Cancel => new ClosePlanRows(eligibility.Applies, Cancelled: true),
            PlanRowAction.Remove => new RemovePlanRows(eligibility.Applies),
            _ => new ReopenPlanRows(eligibility.Applies)
        };
        Check(await Session.Execute(command, Today));
        Refresh();
        // The command's own feedback is the row marks and the selection line, so they are repainted here
        // rather than waiting for an unrelated layout pass (#231: a hosted run saw the tag stay absent).
        RefreshRealized();
        // Repaint only: the row's own cell pass owns building the mark, so a command never edits the live
        // tree while a container is being realised (#231).
        foreach (var row in Realized) row.RepaintStateMark();
        UpdateReason();
        // Say what happened and what was left alone; choosing another row or cell dismisses it.
        error.Text = skipped.Skipped.IsEmpty ? done : done + "。" + SkipReason(skipped);
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
