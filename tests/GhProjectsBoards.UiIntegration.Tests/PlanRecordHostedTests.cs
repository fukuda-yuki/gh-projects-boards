using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

// #308 R8/R11: 着手日 and 完了日 are read-only records of the Issue's history, a complete task's Gantt bar uses
// them instead of the plan dates, and the Project settings choose which Status options mean not started.
internal sealed partial class PlanWorkspaceHostedTests
{
    private static DateOnly LocalDay(DateTimeOffset at)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).DateTime);
    // The short form the spec fixes for these date cells: M/d (曜).
    private static string ShortDate(DateOnly day)
        => day.ToString("M/d", CultureInfo.InvariantCulture) + " (" + "日月火水木金土"[(int)day.DayOfWeek] + ")";
    // The record wording carries the actor and the local time, as the spec fixes it (the hosted runner is in UTC).
    private static string WordingStamp(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static PlanFakeIssue WithHistory(PlanFakeIssue issue, params PlanHistoryEvent[] events)
        => issue with { History = events.ToImmutableArray(), ItemUpdatedAt = FakePlanEditor.ObservedAt, IssueUpdatedAt = FakePlanEditor.ObservedAt };
    private static PlanHistoryEvent StatusChanged(DateTimeOffset at, string from, string to, bool automated = false)
        => new(PlanHistoryEventKind.StatusChanged, at, automated ? "github-project-automation" : "fukuda-yuki", automated, from, to);
    private static PlanHistoryEvent Closed(DateTimeOffset at, bool automated = false)
        => new(PlanHistoryEventKind.Closed, at, automated ? "github-project-automation" : "fukuda-yuki", automated, StateReason: "COMPLETED");

    [Test]
    public async Task RecordColumnsShowTheIssueHistoryAndRefuseEdits()
    {
        var startAt = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var finishAt = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        FakePlanEditor.Save(root, new([
            WithHistory(new(new("I1", "設計", "acme/repo") { Closed = true, CloseDate = LocalDay(finishAt) }, "", true),
                StatusChanged(startAt, "Backlog", "In progress"), Closed(finishAt)),
            new(new("I2", "記録なし", "acme/repo") { Estimate = 8, Remaining = 8 }, "", true)
        ], 3));
        await Open();
        await Ui.Ready<PlanSheetCell>("PlanCell1_ActualStart");
        await Ui.Ready<PlanSheetCell>("PlanCell2_ActualFinish");
        var sheet = default(PlanSheetView)!;
        await Ui.Run(() => {
            sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            var labels = sheet.VisibleColumns.Select(column => column.Label).ToArray();
            // R8: two read-only columns follow 終了日, keep the default 92 px width, and 見込終了 follows them.
            var end = Array.IndexOf(labels, "終了日");
            Assert.That(labels.Skip(end + 1).Take(2), Is.EqualTo(new[] { "着手日", "完了日" }));
            Assert.That(Array.IndexOf(labels, "見込終了"), Is.EqualTo(Array.IndexOf(labels, "完了日") + 1));
            Assert.That(sheet.VisibleColumns.Single(c => c.Label == "着手日").Width, Is.EqualTo(92));
            Assert.That(sheet.VisibleColumns.Single(c => c.Label == "完了日").Width, Is.EqualTo(92));

            var started = Ui.Find<PlanSheetCell>("PlanCell1_ActualStart");
            Assert.That(started.Text, Is.EqualTo(ShortDate(LocalDay(startAt))), "The record shows its short local date.");
            Assert.That(started.IsReadOnly, Is.True);
            var startWording = "fukuda-yuki が In progress に変更 · " + WordingStamp(startAt);
            Assert.That(AutomationProperties.GetName(started), Does.Contain("着手日 " + startWording).And.StartWith("#1 "));
            Assert.That(ToolTipService.GetToolTip(started), Is.EqualTo(startWording));
            var finished = Ui.Find<PlanSheetCell>("PlanCell1_ActualFinish");
            Assert.That(finished.Text, Is.EqualTo(ShortDate(LocalDay(finishAt))));
            var finishWording = "fukuda-yuki が完了としてクローズ · " + WordingStamp(finishAt);
            Assert.That(AutomationProperties.GetName(finished), Does.Contain("完了日 " + finishWording));
            Assert.That(ToolTipService.GetToolTip(finished), Is.EqualTo(finishWording));

            // A row without the record is blank and names no actor.
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell2_ActualStart").Text, Is.Empty);
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell2_ActualFinish").Text, Is.Empty);
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell2_ActualStart").IsReadOnly, Is.True);
        });
        // Selecting the cell names the record and why it cannot be edited, before the row's plan dates.
        await Ui.Run(() => sheet.Select("I1", "ActualStart", false));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanStartReason").Text.Contains("fukuda-yuki が In progress に変更"));
        await Ui.Run(() => {
            var reason = Ui.Find<TextBlock>("PlanStartReason").Text;
            Assert.That(reason, Does.Contain("Issue の履歴から読み取ります（編集不可）"));
            Assert.That(reason, Does.Contain("fukuda-yuki が In progress に変更 · " + WordingStamp(startAt)));
            // F2 and a double-click enter editing through BeginEditing; a record refuses before the editor opens.
            var started = Ui.Find<PlanSheetCell>("PlanCell1_ActualStart");
            started.BeginEditing();
            Assert.That(started.Editing, Is.False, "A record cell never becomes an editor.");
            Assert.That(sheet.Pending, Is.Empty);
        });
        // Delete and fill never change a record and create no Undo step.
        var before = PlanJson.Text(workspace.Session!.Document);
        var undo = workspace.Session.UndoCount;
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Delete)); await Ui.Idle();
        await Ui.Run(() => sheet.Select("I2", "ActualStart", false));
        await Ui.Run(() => sheet.Fill(PlanOperationKind.CtrlD)); await Ui.Idle();
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase("actual-start")]
    [TestCase("plan-start")]
    [TestCase("finish-only")]
    [TestCase("capped-at-finish")]
    [TestCase("open-task")]
    public async Task ClosedTaskGanttBarUsesTheRecordDates(string state)
    {
        var startAt = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
        var finishAt = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
        var planStart = new DateOnly(2026, 9, 28);
        var planEnd = new DateOnly(2026, 10, 2);
        var row = new PlanRow("I1", "設計", "acme/repo");
        PlanFakeIssue issue;
        switch (state)
        {
            case "actual-start":
                // The plan says 10/20-10/21, but the record ran 10/1-10/5: the bar is the record.
                issue = WithHistory(new(row with { Closed = true, CloseDate = LocalDay(finishAt), Start = new(2026, 10, 20), End = new(2026, 10, 21) }, "", true),
                    StatusChanged(startAt, "Backlog", "In progress"), Closed(finishAt));
                break;
            case "plan-start":
                // No 着手日: the bar starts from the plan 開始日 and ends at 完了日.
                issue = WithHistory(new(row with { Closed = true, CloseDate = LocalDay(finishAt), Start = planStart, End = planEnd }, "", true), Closed(finishAt));
                break;
            case "capped-at-finish":
                // The plan start is after 完了日, so it is capped to that day.
                issue = WithHistory(new(row with { Closed = true, CloseDate = LocalDay(finishAt), Start = new(2026, 10, 20), End = new(2026, 10, 21) }, "", true), Closed(finishAt));
                break;
            case "finish-only":
                issue = WithHistory(new(row with { Closed = true, CloseDate = LocalDay(finishAt) }, "", true), Closed(finishAt));
                break;
            default:
                issue = new(row with { Start = planStart, End = planEnd }, "", true);
                break;
        }
        FakePlanEditor.Save(root, new([issue], 2));
        await Open();
        await Ui.Ready<Shape>("PlanBar1");
        var finishDay = LocalDay(finishAt);
        DateOnly expectedStart, expectedEnd;
        switch (state)
        {
            case "actual-start": expectedStart = LocalDay(startAt); expectedEnd = finishDay; break;
            case "plan-start": expectedStart = planStart; expectedEnd = finishDay; break;
            case "capped-at-finish": expectedStart = finishDay; expectedEnd = finishDay; break;
            case "finish-only": expectedStart = finishDay; expectedEnd = finishDay; break;
            default: expectedStart = planStart; expectedEnd = planEnd; break;
        }
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            var bar = Ui.Find<Shape>("PlanBar1");
            Assert.That(Canvas.GetLeft(bar), Is.EqualTo(sheet.X(expectedStart) + sheet.ChartOffset).Within(1),
                "The bar starts at the date the record or plan states.");
            Assert.That(bar.Width, Is.EqualTo((expectedEnd.DayNumber - expectedStart.DayNumber + 1) * sheet.DayWidth).Within(1));
            var name = AutomationProperties.GetName(bar);
            Assert.That(ToolTipService.GetToolTip(bar), Is.EqualTo(name), "The bar's tooltip carries the dates it used.");
            Assert.That(name, Does.StartWith("#1 "));
            switch (state)
            {
                case "actual-start":
                    Assert.That(name, Does.Contain("着手日 " + expectedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                        .And.Contain("完了日 " + finishDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                    break;
                case "plan-start":
                    Assert.That(name, Does.Contain("開始日 " + planStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                        .And.Contain("完了日 " + finishDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                        .And.Not.Contain("着手日"));
                    break;
                case "capped-at-finish":
                case "finish-only":
                    Assert.That(name, Does.Contain("完了日 " + finishDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).And.Not.Contain("着手日"));
                    break;
                default:
                    Assert.That(name, Does.Contain(planStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " – "
                        + planEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).And.Not.Contain("着手日").And.Not.Contain("完了日"));
                    break;
            }
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task RecordSettingsChooseNotStartedStatusOptionsAsOneUndoStep()
    {
        var startAt = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        FakePlanEditor.Save(root, new([
            WithHistory(new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8 }, "", true), StatusChanged(startAt, "Backlog", "In progress"))
        ], 2));
        await Open();
        await Ui.Ready<PlanSheetCell>("PlanCell1_ActualStart");
        await Ui.Until(() => Ui.Find<PlanSheetCell>("PlanCell1_ActualStart").Text == ShortDate(LocalDay(startAt)));
        await Settings();
        await Ui.Ready<TextBlock>("PlanRecordNotStartedNote");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSettingsHeading_records").Text, Is.EqualTo("記録"));
            Assert.That(Ui.Find<TextBlock>("PlanRecordNotStartedNote").Text, Is.EqualTo("Status なしは常に着手前に数えます。"));
            var boxes = Ui.Tree(view).OfType<CheckBox>()
                .Where(box => AutomationProperties.GetAutomationId(box).StartsWith("PlanRecordNotStarted", StringComparison.Ordinal)).ToArray();
            Assert.That(boxes.Select(box => box.Content).ToArray(), Is.EqualTo(new object[] { "In progress", "Done", "Backlog" }),
                "One check box per Status option, in the field's order.");
            Assert.That(boxes.Single(box => AutomationProperties.GetAutomationId(box) == "PlanRecordNotStartedBacklog").IsChecked, Is.True);
            Assert.That(boxes.Single(box => AutomationProperties.GetAutomationId(box) == "PlanRecordNotStartedIn progress").IsChecked, Is.False);
            Assert.That(boxes.Single(box => AutomationProperties.GetAutomationId(box) == "PlanRecordNotStartedDone").IsChecked, Is.False);
        });
        var undo = workspace.Session!.UndoCount;
        // Making In progress a not-started option removes the only start from the history: 着手日 becomes blank.
        await Ui.Ready<CheckBox>("PlanRecordNotStartedIn progress");
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanRecordNotStartedIn progress")));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.NotStartedStatuses is { } names && names.Contains("In progress"));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1), "A check box change is one settings Undo step.");
        await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle();
        await Ui.Ready<PlanSheetCell>("PlanCell1_ActualStart");
        await Ui.Until(() => Ui.Find<PlanSheetCell>("PlanCell1_ActualStart").Text.Length == 0);
        await Ui.Run(() => Assert.That(Ui.Find<PlanSheetCell>("PlanCell1_ActualStart").Text, Is.Empty));
        // Undo restores the defaults (null) and the record date.
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        await Ui.Ready<PlanSheetCell>("PlanCell1_ActualStart");
        await Ui.Until(() => Ui.Find<PlanSheetCell>("PlanCell1_ActualStart").Text == ShortDate(LocalDay(startAt)));
        Assert.That(workspace.Session.Document.State.Settings.NotStartedStatuses, Is.Null, "Null keeps the defaults.");
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        // Unchecking Backlog stores an explicit list; checking it again returns to null.
        await Settings();
        await Ui.Ready<CheckBox>("PlanRecordNotStartedBacklog");
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanRecordNotStartedBacklog")));
        await Ui.Until(() => !PlanActualDates.NotStarted(workspace.Session!.Document.State.Settings).Contains("Backlog"));
        Assert.That(workspace.Session.Document.State.Settings.NotStartedStatuses, Is.Not.Null);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Ready<CheckBox>("PlanRecordNotStartedBacklog");
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanRecordNotStartedBacklog")));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.NotStartedStatuses is null);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 2), "Each toggle is its own settings Undo step.");
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}
