using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    private async Task MountStatusGantt()
    {
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<SelectorBar>("ProjectViews");
        await Ui.Run(() => { var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[1]; });
        await Ui.Ready<ListView>("GanttTasks");
    }

    [Test, Category("WorkspaceStatus")]
    public async Task ProjectStatusCountsHiddenWorkAndPendingRoutePreservesExactInputAndSavedView()
    {
        await Ui.Unmount(grid); var work = session.Workspace;
        var other = EditingTests.Registration("P2", count: 2); work.SetRegistrations([project, other]);
        var rows = work.Open(project); var otherRows = work.Open(other);
        work.Commit("P1", rows[0].Cells[1], "done", optionId: true);
        work.Commit("P1", rows[1].Cells[0], "hidden changed title");
        work.Commit("P2", otherRows[0].Cells[1], "done", optionId: true);
        work.SetBuffer(otherRows[0].Cells[1], "other Project pending");
        var actual = rows[1].Cells.Single(cell => cell.Key?.FieldId == "F-Actual"); work.SetPlanningBuffer(actual, "8");
        var columns = work.PrepareColumns(project);
        work.SaveColumns(columns with { Columns = columns.Columns.Select(column => column.Id.FieldId == "F-Actual" ? column with { Visible = false } : column).ToArray() });
        work.SaveRowView(work.PrepareRowView(project) with { Definition = new(Title: "Issue 1") });
        var before = JsonSerializer.Serialize(work.Snapshot());
        await MountStatusGantt();
        await Ui.Run(() => grid.Width = 620);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "P1T1" }));
            Assert.That(Ui.Find<Grid>("ProjectWorkStatus").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("WorkspaceUnpublished")), Is.EqualTo("GitHub未反映 2セル"));
            Assert.That(Ui.Find<Button>("GridPendingInput").Content, Is.EqualTo("入力途中 1セル"));
            foreach (var id in new[] { "WorkspaceSaveStatus", "WorkspaceUnpublished", "GridPendingInput" })
            {
                var control = Ui.Find<FrameworkElement>(id);
                var bounds = control.TransformToVisual(grid).TransformBounds(new(0, 0, control.ActualWidth, control.ActualHeight));
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Right, Is.LessThanOrEqualTo(grid.ActualWidth));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(grid.ActualHeight));
            }
            Ui.Click("GridPendingInput");
        });
        await Ui.Until(() => grid.CurrentProjectView == ProjectView.Boards && grid.SelectionIdentity == (rows[1].ItemId, actual.Key));
        await Ui.Run(() => {
            Assert.That(work.Buffer(actual), Is.EqualTo("8")); Assert.That(work.Value(actual), Is.Null);
            Assert.That(Ui.Find<Grid>("ProjectWorkStatus").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Ui.Find<TextBlock>("PendingInputValue").Text, Is.EqualTo("入力途中: 8"));
            Assert.That(work.Columns(project).Hidden("F-Actual"), Is.True); Assert.That(work.RowView(project).Title, Is.EqualTo("Issue 1"));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before)); Assert.That(work.Journal, Is.Empty);
        });
    }

    [Test, Category("WorkspaceStatus")]
    public async Task DependencyOnlyManualTaskShowsUnpublishedRelationshipAndOffersReviewWithoutWriting()
    {
        await Ui.Unmount(grid); var work = session.Workspace; var plan = work.Planning("P1")!;
        work.CommitPlanning(project, plan with { Tasks = [plan.Tasks[0] with { Mode = PlanningMode.Manual }] }, work.Revision);
        await MountStatusGantt(); await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.ClickCommand("GanttTaskDetailsEdit"); await Ui.DialogReady("PlanningDialog");
        await Expand("先行Issue（終了→開始）", "PlanPredecessors");
        await Ui.Run(() => {
            var choices = Ui.Find<ListView>("PlanPredecessors", Ui.Dialog("PlanningDialog")!);
            choices.SelectedItems.Add(choices.Items[0]); Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Until(() => session.DurableRevision == work.Revision);
        var before = JsonSerializer.Serialize(work.Snapshot()); var reviewRequested = false;
        await Ui.Run(() => {
            Assert.That(work.Fields.Where(field => field.Change is not null).Select(field => field.Key.Kind), Is.EqualTo(new[] { "Dependency" }));
            Assert.That(Ui.Find<Button>("WorkspaceUnpublished").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("WorkspaceUnpublished")), Is.EqualTo("GitHub未反映 先行関係 1件"));
            grid.ApplyReviewRequested += (_, _) => reviewRequested = true;
            Ui.Click("WorkspaceUnpublished");
            Assert.That(reviewRequested, Is.True, "The visible native action opens the existing review entry point.");
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before)); Assert.That(work.Journal, Is.Empty);
        });
    }

    [Test, Category("WorkspaceStatus")]
    public async Task MissingFieldPendingOnlyWorkOffersReviewWithoutPretendingThereIsAnEditableCell()
    {
        await Ui.Unmount(grid); var work = session.Workspace;
        var field = work.Open(project)[0].Cells.Single(cell => cell.Key?.FieldId == "P1-status");
        work.SetBuffer(field, "unfinished retained option");
        var current = project with { RetrievedAt = project.RetrievedAt.AddSeconds(1), Snapshot = project.Snapshot with {
            Fields = project.Snapshot.Fields.Where(definition => definition.Id.NodeId != "P1-status").ToArray(),
            Items = project.Snapshot.Items.Select(item => item with { Values = item.Values.Where(value => value.FieldId?.NodeId != "P1-status").ToArray() }).ToArray()
        } };
        work.Reconcile(project, current); work.SetRegistrations([current]); project = current;
        Assert.That(work.Field(field)!.Change, Is.Null); Assert.That(work.Buffer(field), Is.EqualTo("unfinished retained option"));
        Assert.That(work.ReadRows(project).SelectMany(row => row.Cells).Any(cell => cell.Key == field.Key), Is.False);
        var before = JsonSerializer.Serialize(work.Snapshot()); var reviewRequested = false;
        await MountStatusGantt();
        await Ui.Run(() => {
            var review = Ui.Find<Button>("WorkspaceUnpublished");
            Assert.That(review.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(AutomationProperties.GetName(review), Does.Contain("要確認 1項目").And.Not.Contain("GitHub未反映"));
            Assert.That(Ui.Find<Button>("GridPendingInput").Visibility, Is.EqualTo(Visibility.Collapsed),
                "An unavailable field cannot be offered as an exact editable-cell target.");
            grid.ApplyReviewRequested += (_, _) => reviewRequested = true;
            Ui.Click(review); Assert.That(reviewRequested, Is.True);
            Assert.That(grid.CurrentProjectView, Is.EqualTo(ProjectView.Gantt));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before)); Assert.That(work.Journal, Is.Empty);
            var views = Ui.Find<SelectorBar>("ProjectViews"); views.SelectedItem = views.Items[0];
        });
        await Ui.Run(() => {
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("WorkspaceUnpublished")), Does.Contain("要確認 1項目"));
            Assert.That(Ui.Find<Button>("GridPendingInput").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(work.Buffer(field), Is.EqualTo("unfinished retained option"));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(before));
        });
    }

    [Test, Category("WorkspaceStatus")]
    public async Task FailedLocalSaveRemainsVisibleAndGanttRetryPersistsTheSameDraft()
    {
        await Ui.Unmount(grid); var work = session.Workspace;
        var directory = Path.Combine(Path.GetTempPath(), "ghpb-work-status-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(directory); session = new(store, work, 0);
        Assert.That(await session.FlushAsync(), Is.True);
        await MountStatusGantt(); await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 0);
        await Ui.Until(() => session.DurableRevision == work.Revision);
        var row = work.Open(project)[0]; var remaining = row.Cells.Single(cell => cell.Key?.FieldId == "F-Remaining");
        using (var held = new FileStream(Path.Combine(directory, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.ClickCommand("GanttProgress"); await Ui.DialogReady("DailyProgressDialog");
            await Ui.Run(() => {
                Ui.Find<TextBox>("DailyRemaining", Ui.Dialog("DailyProgressDialog")!).Text = "2";
            });
            await CloseDailyAndWaitForSave("PrimaryButton");
            await Ui.Until(() => session.Status.Contains("失敗"));
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus").Text, Does.Contain("ローカル保存失敗"));
                Assert.That(Ui.Find<Button>("WorkspaceSaveRetry").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(work.Value(remaining), Is.EqualTo("2")); Assert.That(work.Journal, Is.Empty);
            });
            var saved = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
            Assert.That(saved.Value(remaining), Is.Null, "A failed local save must not be presented as durable.");
        }
        var beforeRetry = JsonSerializer.Serialize(work.Snapshot());
        await Ui.Run(() => Ui.Click("WorkspaceSaveRetry"));
        await Ui.Until(() => session.DurableRevision == work.Revision && !session.Status.Contains("失敗"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus").Text, Does.Contain("ローカル保存済み"));
            Assert.That(Ui.Find<Button>("WorkspaceSaveRetry").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(JsonSerializer.Serialize(work.Snapshot()), Is.EqualTo(beforeRetry)); Assert.That(work.Journal, Is.Empty);
        });
        var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        Assert.That(restored.Value(remaining), Is.EqualTo("2"));
    }
}
