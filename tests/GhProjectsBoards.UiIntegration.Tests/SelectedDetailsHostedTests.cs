using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SelectedDetailsHostedTests
{
    [TestCase(false), TestCase(true), Category("RangeContext")]
    public async Task PlannedRangeLeadsWithItsScopeAndNamesTheCurrentCellWithoutChangingWork(bool reverse)
    {
        const string rangeSummary = "Renamed workflow：4行・4セル";
        var (project, work) = PlannedRange();
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-range-context-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)) { Width = 640, Height = 680 });
        await Ui.Mount(grid);
        try
        {
            var anchor = reverse ? 3 : 0; var current = reverse ? 0 : 3;
            await Ui.Ready<FrameworkElement>($"GridCell{anchor}_1");
            await SheetNativeInput.Click($"GridCell{anchor}_1");
            await Ui.Until(() => grid.SelectionIdentity?.Item == $"P1T{anchor + 1}");
            string before = "";
            await Ui.Run(() => before = JsonSerializer.Serialize(work.Snapshot()));
            await SheetNativeInput.Click("GridDetails");
            await Ui.Ready<Expander>("SelectedCellDisclosure");
            await Ui.Run(() => ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
                Ui.Find<Expander>("SelectedCellDisclosure")).GetPattern(PatternInterface.ExpandCollapse)).Expand());
            await SheetNativeInput.Press(VirtualKey.F6);
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<FrameworkElement>($"GridCell{anchor}_1")));
            for (var count = 0; count < 3; count++)
            {
                await SheetNativeInput.Press(reverse ? VirtualKey.Up : VirtualKey.Down, VirtualKey.Shift);
                var next = anchor + (reverse ? -1 : 1) * (count + 1);
                await Ui.Until(() => grid.SelectionIdentity?.Item == $"P1T{next + 1}");
            }
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text == rangeSummary
                && grid.SelectionIdentity?.Item == $"P1T{current + 1}");
            await SheetNativeInput.Rendered();
            await Ui.Run(async () => {
                await ApplyInformationEvidence.Capture(grid, "range-context-640-" + (reverse ? "reverse" : "forward"));
                var selection = Ui.Find<TextBlock>("GridSelection");
                var primary = Ui.Find<TextBlock>("SelectedCellDetails");
                var scopeAt = primary.Text.IndexOf(rangeSummary, StringComparison.Ordinal);
                Assert.Multiple(() => {
                    Assert.That(selection.IsTextTrimmed, Is.False, "The complete range count and column must remain readable at narrow width.");
                    Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(selection).IsOffscreen(), Is.False);
                    Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBlock>("DateInputState")).IsOffscreen(), Is.True,
                        "Routine dates for one current task must not lead a multiple-cell selection.");
                    Assert.That(scopeAt, Is.GreaterThanOrEqualTo(0));
                    Assert.That(primary.Text.IndexOf("現在のセル", StringComparison.Ordinal), Is.GreaterThan(scopeAt));
                    Assert.That(primary.Text, Does.Contain($"#{current + 1}  owner/repo · Issue {current + 1}").And.Contain("Renamed workflow"));
                    Assert.That(primary.Text, Does.Not.Contain("計画担当:").And.Not.Contain("日程（自動計算）"));
                    Assert.That(Ui.Find<Expander>("SelectedCellDisclosure").IsExpanded, Is.True, "Selection must keep an already-open disclosure open.");
                    Assert.That(Ui.Find<TextBlock>("SelectedCellDiagnostics").Text, Does.Contain("計画担当: Owner").And.Contain("日程（自動計算）"));
                    Assert.That(AutomationProperties.GetHelpText(selection), Does.Contain($"先頭 P1T{anchor + 1} / アクティブ P1T{current + 1}"));
                    Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before), "Presentation and range selection must preserve every value, buffer, Undo operation and journal entry.");
                    Assert.That(work.Journal, Is.Empty);
                });
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    [TestCase("Start", 5, "DateInputCommit"), TestCase("Actual", 4, "ActualUpdate"), Category("RangeContext")]
    public async Task RangePendingPlanningInputKeepsItsCurrentCellContextAndCancelsOnlyThatInput(string role, int column, string confirmAction)
    {
        var (project, work) = PlannedRange(actualReportProblem: role == "Actual");
        var rows = work.Open(project); var current = rows[1].Cells.Single(cell => cell.Key?.FieldId == "F-" + role);
        work.SetBuffer(rows[5].Cells[0], "unrelated unfinished title");
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-range-pending-context-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!; TextBox editor = null!;
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)) { Width = 740, Height = 680 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<FrameworkElement>($"GridCell0_{column}");
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>($"GridCell0_{column}")).SetFocus());
            await Ui.Ready<TextBox>($"GridCell0_{column}");
            await SheetNativeInput.ActivateWindow();
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<TextBox>($"GridCell0_{column}")));
            await SheetNativeInput.Press(VirtualKey.Down, VirtualKey.Shift);
            await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2" && Ui.Find<TextBlock>("GridSelection").Text.Contains("2行・2セル"));
            string beforeEdit = "";
            await Ui.Run(() => { editor = Ui.Find<TextBox>($"GridCell1_{column}"); beforeEdit = JsonSerializer.Serialize(work.Snapshot() with { Revision = 0 }); });
            await SheetNativeInput.Press(VirtualKey.F2); await SheetNativeInput.Press(VirtualKey.Number8);
            await Ui.Until(() => work.Buffer(current) == "8");
            string beforeDetails = "";
            await Ui.Run(() => beforeDetails = JsonSerializer.Serialize(work.Snapshot()));
            await SheetNativeInput.Click("GridDetails");
            await Ui.Ready<TextBlock>("SelectedCellDetails");
            await SheetNativeInput.Rendered();
            await Ui.Run(async () => {
                await ApplyInformationEvidence.Capture(grid, "range-pending-context-" + role);
                Assert.Multiple(() => {
                    Assert.That(Ui.Find<TextBox>($"GridCell1_{column}"), Is.SameAs(editor));
                    Assert.That(editor.Text, Is.EqualTo("8"));
                    Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain(role).And.Contain("2行・2セル"));
                    Assert.That(Ui.Find<TextBlock>("SelectedCellDetails").Text, Does.Contain("現在のセル").And.Contain("#2  owner/repo · Issue 2")
                        .And.Contain(role).And.Contain("入力途中: 8"));
                    Assert.That(Ui.Find<TextBlock>("PendingInputValue").Text, Is.EqualTo("入力途中: 8"));
                    Assert.That(Ui.Find<TextBlock>("ConfirmedInputValue").Text, Is.EqualTo("確定値: " + work.Value(current)));
                    Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<Button>(confirmAction)).IsOffscreen(), Is.False,
                        "The current cell's required Date/Actual confirmation action must remain available during a range edit.");
                    Assert.That(Ui.Find<Button>(confirmAction).IsEnabled, Is.True);
                    Assert.That(Ui.Find<Button>("GridRangeCommands").Visibility, Is.EqualTo(Visibility.Collapsed), "A range remains visible even while unfinished input blocks its commands.");
                    Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(beforeDetails));
                    Assert.That(work.Buffer(rows[5].Cells[0]), Is.EqualTo("unrelated unfinished title"));
                    Assert.That(work.Journal, Is.Empty);
                    if (role == "Actual") Assert.That(Ui.Find<TextBlock>("SelectedCellDetails").Text,
                        Does.Contain("実績の反映前に記録を確認: 実績合計の内訳・報告対象日が未入力です。"), "Required current-task problems must not move into optional routine-plan disclosure.");
                });
            });
            await SheetNativeInput.Press(VirtualKey.F6);
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), editor));
            await SheetNativeInput.Press(VirtualKey.Escape);
            await Ui.Until(() => work.Buffer(current) is null && Ui.Find<Button>("GridRangeCommands").Visibility == Visibility.Visible);
            await Ui.Run(() => {
                Assert.That(JsonSerializer.Serialize(work.Snapshot() with { Revision = 0 }), Is.EqualTo(beforeEdit));
                Assert.That(Ui.Find<TextBox>($"GridCell1_{column}"), Is.SameAs(editor));
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(editor));
                Assert.That(grid.SelectionIdentity, Is.EqualTo((rows[1].ItemId, current.Key)));
                Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("2行・2セル"));
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    private static (ProjectRegistration Project, EditingWorkspace Work) PlannedRange(bool actualReportProblem = false)
    {
        var project = PlanningPathTests.Registration(6);
        project = project with { Snapshot = project.Snapshot with {
            Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key, pair => pair.Value with {
                Native = pair.Value.Native! with { Assignees = [new(new(project.Snapshot.Id.Scope, "U1"), "Owner")] } }) } };
        if (actualReportProblem) project = project with { Snapshot = project.Snapshot with {
            Items = project.Snapshot.Items.Select(item => item.Id.NodeId == "P1T2" ? item with {
                Values = item.Values.Select(value => value.FieldId?.NodeId == "F-Actual"
                    ? value with { Availability = ValueAvailability.Present, Scalar = "3" } : value).ToArray() } : item).ToArray() } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Version = 3,
            Tasks = Enumerable.Range(1, 6).Select(i => new PlanningTask("I" + i, PlanningMode.Auto, "U1", Assignment: new(["U1"], true))).ToArray() }, work.Revision,
            Enumerable.Range(1, 6).Select(i => new PlanningValueEdit("P1T" + i, "Estimate", "4")).ToArray());
        work.Open(project);
        return (project, work);
    }

    [TestCase("unchanged"), TestCase("pending"), TestCase("conflict"), TestCase("unavailable"), TestCase("actual-report")]
    public async Task SelectedTaskAndDecisionValuesPrecedeOptionalDiagnosticsWithoutChangingWork(string condition)
    {
        var project = PlanningAssignmentTests.Assigned("U1");
        if (condition == "actual-report") project = project with { Snapshot = project.Snapshot with {
            Items = project.Snapshot.Items.Select(item => item.Id.NodeId == "P1T1" ? item with {
                Values = item.Values.Select(value => value.FieldId?.NodeId == "F-Actual"
                    ? value with { Availability = ValueAvailability.Present, Scalar = "3" } : value).ToArray() } : item).ToArray() } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var task = new PlanningTask("I1", PlanningMode.Auto, "U1", Assignment: new(["U1"], true));
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Version = 3, Tasks = [task] }, work.Revision,
            [new("P1T1", "Estimate", "4")]);
        var title = work.Open(project)[0].Cells[0];
        var compare = condition is "conflict" or "unavailable";
        if (compare)
        {
            work.Commit("P1", title, "Local title");
            var previous = project;
            project = previous with { RetrievedAt = previous.RetrievedAt.AddMinutes(1), Snapshot = previous.Snapshot with {
                Issues = previous.Snapshot.Issues.ToDictionary(pair => pair.Key, pair => pair.Key.NodeId == "I1"
                    ? pair.Value with { Title = condition == "conflict" ? new(ValueAvailability.Present, "Remote title") : new(ValueAvailability.Unavailable) } : pair.Value) } };
            work.Reconcile(previous, project); work.SetRegistrations([project]);
            Assert.That(work.Field(title)!.Observation, Is.Not.Null);
            if (condition == "conflict") Assert.That(work.Field(title)!.Conflict, Is.True);
        }
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-selected-details-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!; TextBox editor = null!;
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)) { Width = 740, Height = 600 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<FrameworkElement>("GridCell0_0");
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0")).SetFocus());
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() => {
                editor = Ui.Find<TextBox>("GridCell0_0");
                if (condition == "pending") editor.Text = "Unfinished title";
            });
            await Ui.Until(() => grid.SelectionIdentity?.Field == new FieldKey("Title", "I1")
                && (condition != "pending" || work.Buffer(title) == "Unfinished title"));
            var before = JsonSerializer.Serialize(work.Snapshot());
            await Ui.Run(() => { Ui.Find<Button>("GridDetails").Focus(FocusState.Keyboard); Ui.Click("GridDetails"); });
            await Ui.Ready<TextBlock>("SelectedCellDetails");
            await SheetNativeInput.Rendered();
            await Ui.Run(async () => {
                var primary = Ui.Find<TextBlock>("SelectedCellDetails");
                Assert.That(primary.Text, Does.StartWith("#1  owner/repo · " + (compare ? "Local title" : "Issue 1")));
                Assert.That(primary.Text, Does.Contain("タイトル（Issue共通）").And.Contain("確定値: " + (compare ? "Local title" : "Issue 1")));
                Assert.That(primary.Text, Does.Contain("計画担当: Owner").And.Contain("計画上の進捗: 未着手").And.Contain("2026-10-05 09:00").And.Contain("2026-10-05 13:00"));
                Assert.That(primary.Text, Does.Not.Contain("Project: P1").And.Not.Contain("ID: I1").And.Not.Contain("観測:")
                    .And.Not.Contain("未割当").And.Not.Contain("保存済み").And.Not.Contain("GitHub未反映"));
                if (condition == "actual-report") Assert.That(primary.Text, Does.Contain("実績の反映前に記録を確認: 実績合計の内訳・報告対象日が未入力です。"));
                else Assert.That(primary.Text, Does.Not.Contain("実績合計の内訳・報告対象日が未入力です。"));
                Assert.That(Ui.Find<Expander>("SelectedCellDisclosure").IsExpanded, Is.False);
                if (condition == "pending") Assert.That(primary.Text, Does.Contain("入力途中: Unfinished title").And.Contain("IMEの確定とセル確定は別"));
                if (condition == "conflict") Assert.That(primary.Text, Does.Contain("競合").And.Contain("変更前: Issue 1")
                    .And.Contain("ローカル確定値: Local title").And.Contain("GitHub取得値: Remote title"));
                else if (condition == "unavailable") Assert.That(primary.Text, Does.Contain("変更前: Issue 1")
                    .And.Contain("ローカル確定値: Local title").And.Contain("GitHub取得値: 閲覧不可"));
                else Assert.That(primary.Text, Does.Not.Contain("変更前:").And.Not.Contain("GitHub取得値:"));
                var scroll = Ui.Tree(grid).OfType<ScrollViewer>().Single(view => view.MaxHeight == 156);
                Assert.That(primary.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y, Is.InRange(0, 1));
                Assert.That(primary.ActualHeight, Is.LessThanOrEqualTo(scroll.ViewportHeight),
                    "Task/scope, confirmed and unfinished values, any active comparison/report problem, and adopted dates must fit before technical disclosure.");
                Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
                await ApplyInformationEvidence.Capture(grid, "selected-task-first-" + condition);
                var disclosure = Ui.Find<Expander>("SelectedCellDisclosure");
                FrameworkElementAutomationPeer.CreatePeerForElement(disclosure).SetFocus();
                ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(disclosure).GetPattern(PatternInterface.ExpandCollapse)).Expand();
            });
            await Ui.Ready<TextBlock>("SelectedCellDiagnostics");
            await Ui.Run(() => Ui.Find<TextBlock>("SelectedCellDiagnostics").StartBringIntoView());
            await SheetNativeInput.Rendered();
            await Ui.Run(async () => {
                Assert.That(Ui.Find<TextBlock>("SelectedCellDiagnostics").Text, Does.Contain("Project: P1").And.Contain("ID: I1").And.Contain("観測:"));
                Assert.That(Ui.Find<TextBlock>("SelectedCellDiagnostics").Text, Does.Contain("担当者別集計（任意）").And.Contain("見積の未割当: 4人時"));
                Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(editor));
                Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
                Assert.That(work.Journal, Is.Empty);
                await ApplyInformationEvidence.Capture(grid, "selected-task-diagnostics-" + condition);
                ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<Expander>("SelectedCellDisclosure")).GetPattern(PatternInterface.ExpandCollapse)).Collapse();
                editor.Focus(FocusState.Keyboard);
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(editor));
                Assert.That(work.Buffer(title), Is.EqualTo(condition == "pending" ? "Unfinished title" : null));
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }
}
