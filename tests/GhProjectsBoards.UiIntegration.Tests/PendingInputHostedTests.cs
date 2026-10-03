using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.Foundation;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("PendingInput")]
    public async Task UnfinishedActualRemainsMarkedAfterSelectionMovesAndExplainsTheConfirmedCalculation()
    {
        await Ui.Unmount(grid);
        var work = session.Workspace; var row = work.Open(project)[0];
        var actual = row.Cells.Single(c => c.Key?.FieldId == "F-Actual");
        work.Commit("P1", row.Cells.Single(c => c.Key?.FieldId == "F-Estimate"), "8");
        work.CommitActualInput(project, row.ItemId, "4", new(2026, 10, 6), "U1", work.Revision);
        var adopted = work.PlanFor(project).Tasks.Single(t => t.Id == "I1");
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await ShowDateColumns();
        await FocusEstimateInput("GridCell0_4");
        await SheetNativeInput.Press(VirtualKey.F2); await SheetNativeInput.Press(VirtualKey.Number8);
        await Ui.Until(() => work.Buffer(actual) == "8");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PendingInputValue").Text, Is.EqualTo("入力途中: 8"));
            Assert.That(Ui.Find<TextBlock>("ConfirmedInputValue").Text, Is.EqualTo("確定値: 4"));
            Assert.That(Ui.Find<TextBlock>("PendingCalculationMeaning").Text, Does.Contain("日程計算は確定値"));
            Assert.That(Ui.Find<Grid>("PendingInputContext").Visibility, Is.EqualTo(Visibility.Visible));
            FocusCell("GridCell1_4");
        });
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        await Ui.Run(() => {
            var marker = Ui.Find<TextBlock>("GridMarker0_4");
            Assert.That(marker.Visibility, Is.EqualTo(Visibility.Visible)); Assert.That(marker.Text, Is.EqualTo("…"));
            Assert.That(AutomationProperties.GetName(marker), Does.Contain("未確定"));
            Assert.That(work.Value(actual), Is.EqualTo("4")); Assert.That(work.Buffer(actual), Is.EqualTo("8"));
            Assert.That(work.PlanFor(project).Tasks.Single(t => t.Id == "I1").Finish, Is.EqualTo(adopted.Finish));
            Assert.That(Ui.Find<Button>("GridPendingInput").Content, Is.EqualTo("入力途中 1セル"));
            Assert.That(Ui.Find<Grid>("PendingInputContext").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("GridPendingInput"); Ui.Click("GridDetails");
        });
        await Ui.Ready<TextBlock>("SelectedCellDetails");
        await Ui.Run(async () => {
            Assert.That(Ui.Find<TextBlock>("SelectedCellDetails").Text, Does.Contain("入力途中: 8").And.Contain("確定値: 4"));
            Assert.That(work.Journal, Is.Empty);
            await ApplyInformationEvidence.Capture(grid, "pending-actual-and-confirmed-context");
        });
        var remaining = row.Cells.Single(cell => cell.Key?.FieldId == "F-Remaining");
        await Ui.Run(() => {
            work.Commit("P1", remaining, "2");
            work.SetBuffer(remaining, "2");
        });
        var beforeDialogPlan = JsonSerializer.Serialize(work.Planning("P1"));
        var beforeDialogHistory = work.Snapshot().History.Length;
        await Ui.ClickCommand("GridDailyProgress"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("DailyProgressDialog")!;
            Assert.That(Ui.Find<TextBox>("DailyActual", dialog).Text, Is.EqualTo("8"));
            Assert.That(Ui.Find<TextBlock>("DailyActualPending", dialog).Text, Is.EqualTo("入力途中 · 確定済み 4"));
            Assert.That(Ui.Find<TextBlock>("DailyActualPending", dialog).Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Ui.Find<TextBlock>("DailyRemainingPending", dialog).Text, Is.EqualTo("入力途中 · 確定済み 2"));
            Assert.That(Ui.Find<TextBlock>("DailyRemainingPending", dialog).Visibility, Is.EqualTo(Visibility.Visible),
                "A same-value buffer is still unfinished input.");
            var cancelHint = Ui.Find<TextBlock>("DailyCancelHint", dialog);
            Assert.That(cancelHint.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(cancelHint.Text, Does.Contain("この画面での変更を取り消します").And.Contain("開く前の入力途中は残ります"));
            Assert.That(cancelHint.TransformToVisual(dialog).TransformPoint(new(0, cancelHint.ActualHeight)).Y,
                Is.LessThanOrEqualTo(dialog.ActualHeight), "The explanation remains in the fixed area near dismissal.");
            Ui.Find<TextBox>("DailyRemaining", dialog).Text = "9";
            Ui.Find<TextBox>("DailyActual", dialog).Text = "4";
            Assert.That(Ui.Find<TextBlock>("DailyRemainingPending", dialog).Text, Is.EqualTo("入力途中 · 確定済み 2"));
            Assert.That(Ui.Find<TextBlock>("DailyActualPending", dialog).Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(work.Buffer(remaining), Is.EqualTo("9")); Assert.That(work.Buffer(actual), Is.EqualTo("4"));
            Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null && work.Buffer(actual) == "8" && work.Buffer(remaining) == "2"
            && session.DurableRevision == work.Revision);
        await Ui.Run(() => {
            Assert.That(work.Value(actual), Is.EqualTo("4")); Assert.That(work.Value(remaining), Is.EqualTo("2"));
            Assert.That(JsonSerializer.Serialize(work.Planning("P1")), Is.EqualTo(beforeDialogPlan));
            Assert.That(work.Snapshot().History, Has.Length.EqualTo(beforeDialogHistory)); Assert.That(work.Journal, Is.Empty);
        });
    }

    [Test, Category("PendingInput")]
    public async Task PendingCountCyclesExactHiddenTargetsWithoutChangingSavedViewOrInput()
    {
        await Ui.Unmount(grid);
        var work = session.Workspace; var rows = work.Open(project);
        var actual = rows[0].Cells.Single(c => c.Key?.FieldId == "F-Actual");
        var remaining = rows[1].Cells.Single(c => c.Key?.FieldId == "F-Remaining");
        work.SetBuffer(rows[0].Cells[0], "unfinished title"); work.SetPlanningBuffer(actual, "8"); work.SetBuffer(remaining, "2");
        var columns = work.PrepareColumns(project);
        work.SaveColumns(columns with { Columns = columns.Columns.Select(c => c.Id.FieldId == "F-Actual" ? c with { Visible = false } : c).ToArray() });
        work.SaveRowView(work.PrepareRowView(project) with { Definition = new(Title: "Issue 1") });
        var before = JsonSerializer.Serialize(work.Snapshot());
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_2");
        await Ui.Run(() => {
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { rows[0].ItemId }));
            Assert.That(Ui.Find<Button>("GridPendingInput").Content, Is.EqualTo("入力途中 3セル"));
            FocusCell("GridCell0_2");
        });
        foreach (var target in new[] { (rows[0].ItemId, rows[0].Cells[0].Key), (rows[0].ItemId, actual.Key), (rows[1].ItemId, remaining.Key), (rows[0].ItemId, rows[0].Cells[0].Key) })
        {
            await SheetNativeInput.ActivateWindow();
            await Ui.Run(() => Assert.That(Ui.Find<Button>("GridPendingInput").Focus(FocusState.Keyboard), Is.True));
            await SheetNativeInput.Press(VirtualKey.Enter);
            await Ui.Until(() => grid.SelectionIdentity == target);
            await Ui.Run(() => {
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.InstanceOf<TextBox>());
                Assert.That(work.Columns(project).Hidden("F-Actual"), Is.True);
                Assert.That(work.RowView(project).Title, Is.EqualTo("Issue 1"));
                Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
            });
        }
        await Ui.Run(() => {
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { rows[0].ItemId, rows[1].ItemId }));
            Ui.Click("GridReapply");
        });
        await Ui.Until(() => grid.DisplayedRowIds.Length == 1);
        await Ui.Run(() => {
            Assert.That(work.Columns(project).Hidden("F-Actual"), Is.True);
            Assert.That(Ui.Find<Button>("GridPendingInput").Content, Is.EqualTo("入力途中 3セル"));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        });
    }

    [Test, Category("PendingInput")]
    public async Task CancelAndConfirmRetireThePendingMarkWhileConfirmedChangeAndUndoStayDistinct()
    {
        await FocusEstimateInput("GridCell0_2");
        await SheetNativeInput.Press(VirtualKey.F2); await SheetNativeInput.Press(VirtualKey.Number8);
        await Ui.Until(() => session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[2]) == "8");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GridMarker0_2").Text, Is.EqualTo("…")));
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => Ui.Find<Button>("GridPendingInput").Visibility == Visibility.Collapsed);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("GridMarker0_2").Visibility, Is.EqualTo(Visibility.Collapsed)));
        await EnterEstimate("8", VirtualKey.Number8);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GridMarker0_2").Text, Is.EqualTo("◆"));
            Assert.That(Ui.Find<Button>("GridPendingInput").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<Grid>("PendingInputContext").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => {
            var cell = session.Workspace.Open(project)[0].Cells[2];
            Assert.That(session.Workspace.Value(cell), Is.Null); Assert.That(session.Workspace.Buffer(cell), Is.Null);
            Assert.That(Ui.Find<TextBlock>("GridMarker0_2").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<Button>("GridPendingInput").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }
}

[TestFixture, NonParallelizable, Category("ContextRevealViewport")]
public sealed class ContextRevealHostedTests
{
    [TestCase("jump"), TestCase("pointer"), TestCase("shift"), TestCase("uia"), Category("ContextFocus")]
    public async Task FilteredPendingRecoveryAndFollowingTitleSelectionKeepNativeInputOnTheVisibleCell(string route)
    {
        var project = PlanningPathTests.Registration(40);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]); work.SetPlanning(PlanningPathTests.Plan(), 0);
        var rows = work.Open(project); var oldTitle = rows[29].Cells[0]; var estimate = rows[39].Cells.Single(c => c.Key?.FieldId == "F-Estimate");
        work.SetBuffer(oldTitle, "Unfinished earlier title"); work.Commit("P1", estimate, "16"); work.SetBuffer(estimate, "24 unfinished");
        work.SaveRowView(work.PrepareRowView(project) with { Definition = new(Title: "Issue 1") });
        var before = JsonSerializer.Serialize(work.Snapshot());
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-context-focus-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid view = null!;
        await Ui.Run(() => view = new(project, session, () => Task.FromResult(true)) { Width = 740, Height = 420 });
        await Ui.Mount(view);
        try
        {
            await SheetNativeInput.ActivateWindow();
            await Ui.Run(() => Ui.Click("GridPendingInput"));
            await Ui.Until(() => view.SelectionIdentity == (rows[29].ItemId, oldTitle.Key));
            await Ui.Run(ContextRevealFrames);
            await Ui.Run(() => {
                Console.WriteLine("First pending focus: " + AutomationProperties.GetAutomationId((DependencyObject)FocusManager.GetFocusedElement(view.XamlRoot)));
                Ui.Click("GridPendingInput");
            });
            await Ui.Until(() => view.SelectionIdentity == (rows[39].ItemId, estimate.Key));
            await Ui.Run(ContextRevealFrames);
            var targetRow = Array.IndexOf(view.DisplayedRowIds, rows[39].ItemId);
            var estimateColumn = Array.FindIndex(work.Open(project)[39].Cells, c => c.Key == estimate.Key);
            var targetId = $"GridCell{targetRow}_0";
            await Ui.Run(() => Console.WriteLine("Second pending focus: " + AutomationProperties.GetAutomationId((DependencyObject)FocusManager.GetFocusedElement(view.XamlRoot))));
            if (route == "pointer" || route == "shift") await SheetNativeInput.Click(targetId, route == "shift" ? [VirtualKey.Shift] : []);
            else if (route == "uia") await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>(targetId)).SetFocus());
            await Ui.Run(ContextRevealFrames);
            var expectedId = route == "jump" ? $"GridCell{targetRow}_{estimateColumn}" : targetId;
            await Ui.Run(async () => {
                await ApplyInformationEvidence.Capture(view, "context-focus-" + route);
                Console.WriteLine("Before native key focus: " + AutomationProperties.GetAutomationId((DependencyObject)FocusManager.GetFocusedElement(view.XamlRoot)) + "; expected: " + expectedId);
            });
            // A following real key exposes a stale input owner even when a selection frame looks correct.
            await SheetNativeInput.Press(route == "jump" ? VirtualKey.End : VirtualKey.Tab);
            await Ui.Run(ContextRevealFrames);
            await Ui.Run(async () => {
                await ApplyInformationEvidence.Capture(view, "context-focus-after-key-" + route);
                Assert.Multiple(() => {
                    Assert.That(work.Buffer(oldTitle), Is.EqualTo("Unfinished earlier title"), "Choosing another visible cell must not let its next key commit an older pending title.");
                    Assert.That(work.Value(oldTitle), Is.EqualTo("Issue 30"));
                    Assert.That(work.Buffer(estimate), Is.EqualTo("24 unfinished"));
                    Assert.That(work.Value(estimate), Is.EqualTo("16"));
                    var expected = route == "jump" ? $"GridCell{targetRow}_{estimateColumn}" : $"GridCell{targetRow}_1";
                    Assert.That(AutomationProperties.GetAutomationId((DependencyObject)FocusManager.GetFocusedElement(view.XamlRoot)), Is.EqualTo(expected));
                    Assert.That(view.SelectionIdentity, Is.EqualTo((rows[39].ItemId, route == "jump" ? estimate.Key : rows[39].Cells[1].Key)));
                    Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before), "Navigation must preserve every saved input, view preference, confirmed edit and Undo unit.");
                    Assert.That(work.Journal, Is.Empty);
                });
            });
        }
        finally { await Ui.Unmount(view); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    [Test, Category("PendingNavigationViewport")]
    public async Task ArrowIntoPendingTitleAtViewportBottomKeepsTheWholeInputVisibleWithoutChangingWork()
    {
        var project = EditingTests.Registration(count: 40);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var rows = work.Open(project); var target = rows[29].Cells[0];
        work.Commit("P1", target, "Confirmed target"); work.SetBuffer(target, "Unfinished target");
        work.SetBuffer(rows[39].Cells[0], "Independent unfinished");
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-pending-navigation-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid view = null!; ScrollViewer scroll = null!;
        await Ui.Run(() => view = new(project, session, () => Task.FromResult(true)) { Width = 740, Height = 420 });
        await Ui.Mount(view);
        try
        {
            await Ui.Run(() => {
                var list = Ui.Find<ListView>("ProjectItems", view);
                scroll = Ui.Tree(list).OfType<ScrollViewer>().Single();
                list.ScrollIntoView(list.Items[28], ScrollIntoViewAlignment.Leading); list.UpdateLayout();
            });
            await Ui.Ready<FrameworkElement>("GridCell28_0");
            await SheetNativeInput.ActivateWindow();
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell28_0", view)).SetFocus());
            await Ui.Ready<TextBox>("GridCell28_0");
            await Ui.Run(() => {
                var source = Ui.Find<TextBox>("GridCell28_0", view);
                var bounds = source.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, source.ActualWidth, source.ActualHeight));
                scroll.ChangeView(null, scroll.VerticalOffset + bounds.Bottom - scroll.ViewportHeight, null, true);
            });
            await Ui.Until(() => {
                var source = Ui.Find<TextBox>("GridCell28_0", view);
                return Math.Abs(source.TransformToVisual(scroll).TransformPoint(new(0, source.ActualHeight)).Y - scroll.ViewportHeight) < 1;
            });
            await Ui.Run(async () => {
                Assert.That(view.SelectionIdentity, Is.EqualTo((rows[28].ItemId, rows[28].Cells[0].Key)));
                Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<TextBox>("GridCell28_0", view)));
                Assert.That(Ui.Find<Grid>("PendingInputContext", view).Visibility, Is.EqualTo(Visibility.Collapsed));
                Assert.That(await session.FlushAsync(), Is.True);
            });
            var before = JsonSerializer.Serialize(work.Snapshot());

            await SheetNativeInput.Press(VirtualKey.Down);
            await Ui.Until(() => view.SelectionIdentity == (rows[29].ItemId, target.Key));
            await Ui.Ready<TextBox>("GridCell29_0");
            await Ui.Run(ContextRevealFrames);
            await Ui.Run(async () => {
                var editor = Ui.Find<TextBox>("GridCell29_0", view);
                var bounds = editor.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, editor.ActualWidth, editor.ActualHeight));
                await ApplyInformationEvidence.Capture(view, "arrow-into-pending-at-viewport-bottom");
                Assert.Multiple(() => {
                    Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(editor));
                    Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1));
                    Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(scroll.ViewportHeight + 1), "Arrow navigation must use the viewport after the destination's pending context appears.");
                    Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1));
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(scroll.ViewportWidth + 1));
                    Assert.That(editor.Text, Is.EqualTo("Unfinished target"));
                    Assert.That(work.Value(target), Is.EqualTo("Confirmed target"));
                    Assert.That(work.Buffer(target), Is.EqualTo("Unfinished target"));
                    Assert.That(work.Buffer(rows[39].Cells[0]), Is.EqualTo("Independent unfinished"));
                    Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
                    Assert.That(work.Journal, Is.Empty);
                });
            });
        }
        finally { await Ui.Unmount(view); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    public async Task ContextJumpRevealsTheWholePendingTargetWithoutChangingWork(bool filtered, bool useApplyProblem)
    {
        var harness = await CreationHarness.Create(40);
        var project = harness.Workspace.Selected!;
        var work = harness.Session.Workspace;
        var targetId = work.Open(project)[29].ItemId;
        work.Commit("P1", work.Open(project)[29].Cells[0], "Review target confirmed");
        harness.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
        await harness.Apply(targetId);

        // Prepare the existing problem and pending input before mounting the view.
        // The action under test only follows one of the two public recovery routes.
        project = harness.Workspace.Selected!; work = harness.Session.Workspace;
        var rows = work.Open(project); var target = rows.Single(row => row.ItemId == targetId).Cells[0];
        var independent = rows[39].Cells[0];
        work.SetBuffer(target, "U follow-up");
        work.Commit("P1", independent, "Independent confirmed"); work.SetBuffer(independent, "Independent unfinished");
        if (filtered) work.SaveRowView(work.PrepareRowView(project) with { Definition = new(Title: "Issue 1") });
        Assert.That(work.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Failed));
        Assert.That(ApplyResultsPresentation.Attention(work).Single().RowId, Is.EqualTo(targetId));

        var session = harness.Session;
        EditingGrid view = null!; ScrollViewer scroll = null!;
        await Ui.Run(() => view = new(project, session, () => Task.FromResult(true)) { Width = 740, Height = 420 });
        await Ui.Mount(view);
        try
        {
            await Ui.Ready<FrameworkElement>("GridCell0_0");
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0", view)).SetFocus());
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(async () => {
                scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems", view)).OfType<ScrollViewer>().Single();
                Assert.That(scroll.ScrollableHeight, Is.GreaterThan(0));
                Assert.That(scroll.VerticalOffset, Is.EqualTo(0).Within(1));
                Assert.That(view.DisplayedRowIds.Contains(targetId), Is.EqualTo(!filtered));
                Assert.That(Ui.Find<Grid>("PendingInputContext", view).Visibility, Is.EqualTo(Visibility.Collapsed));
                Assert.That(await session.FlushAsync(), Is.True);
            });
            var before = JsonSerializer.Serialize(work.Snapshot());
            var writes = harness.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())).ToArray();
            var command = useApplyProblem ? "NextApplyProblem" : "GridPendingInput";

            await Ui.Run(() => {
                var button = Ui.Find<Button>(command, view);
                Assert.That(button.Focus(FocusState.Keyboard), Is.True);
                Ui.Click(button);
            });
            await Ui.Until(() => view.SelectionIdentity == (targetId, target.Key));
            var targetIndex = Array.IndexOf(view.DisplayedRowIds, targetId);
            await Ui.Ready<TextBox>($"GridCell{targetIndex}_0");
            await Ui.Run(ContextRevealFrames);
            await Ui.Run(async () => {
                var editor = Ui.Find<TextBox>($"GridCell{targetIndex}_0", view);
                var bounds = editor.TransformToVisual(scroll).TransformBounds(new Rect(0, 0, editor.ActualWidth, editor.ActualHeight));
                TestContext.Out.WriteLine($"Context jump command={command}, filtered={filtered}, target={targetId}/{target.Key}, editor={bounds}, viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}, offset={scroll.VerticalOffset}/{scroll.ScrollableHeight}");
                await ApplyInformationEvidence.Capture(view, $"context-jump-{command}-filtered-{filtered}");
                Assert.Multiple(() => {
                    Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(editor));
                    Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1));
                    Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(scroll.ViewportHeight + 1), "Recovery must expose the whole input location after its context changes the viewport, without a test scroll.");
                    Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1));
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(scroll.ViewportWidth + 1));
                    Assert.That(view.SelectionIdentity, Is.EqualTo((targetId, target.Key)));
                    Assert.That(editor.Text, Is.EqualTo("U follow-up"));
                    Assert.That(Ui.Find<Grid>("PendingInputContext", view).Visibility, Is.EqualTo(Visibility.Visible));
                    Assert.That(Ui.Find<TextBlock>("PendingInputValue", view).Text, Is.EqualTo("入力途中: U follow-up"));
                    Assert.That(Ui.Find<TextBlock>("ConfirmedInputValue", view).Text, Is.EqualTo("確定値: Review target confirmed"));
                    Assert.That(work.Value(target), Is.EqualTo("Review target confirmed"));
                    Assert.That(work.Buffer(target), Is.EqualTo("U follow-up"));
                    Assert.That(work.Value(independent), Is.EqualTo("Independent confirmed"));
                    Assert.That(work.Buffer(independent), Is.EqualTo("Independent unfinished"));
                    Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before), "A jump preserves confirmed values, buffers, view preferences, Undo and Apply journals.");
                    Assert.That(harness.Writes.Select(write => (write.Query, Input: write.Input.GetRawText())), Is.EqualTo(writes));
                });
            });
        }
        finally
        {
            await Ui.Unmount(view);
            await Ui.Run(async () => { await harness.Workspace.StopAsync(); Assert.That(await harness.Workspace.FlushDraftsAsync(), Is.True); });
            await Ui.Idle();
        }
    }

    private static async Task ContextRevealFrames()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0; EventHandler<object>? handler = null;
        handler = (_, _) => { if (++frames == 2) completed.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}

