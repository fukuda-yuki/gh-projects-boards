using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task LateApplyOutcomeCannotNavigateAfterInvokingAnotherProject()
    {
        var first = Workspace.Selected!;
        await Ui.Run(async () =>
        {
            var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
                "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, null); await Workspace.SelectAsync(first.Snapshot.Id);
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "approved before navigation");
        });
        await ControlExternal("ApplyTitle"); gate!.Fail = true;
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await OpenNavigation(SplitViewDisplayMode.Inline); await InvokeProjectNode("Project 2");
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); gate.Release.TrySetResult();
        await Ui.Until(() => !Workspace.IsBusy && Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        await Task.Delay(400);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyOutcomeWarning"), Is.Null);
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.All.StartsWith("P2-"));
            Assert.That(Ui.Find<TextBlock>("ProjectSummary").Text, Is.EqualTo("Project 2"));
            Assert.That(Work.Journal.Single().Project.NodeId, Is.EqualTo("P1"));
        });
    }

    [Test]
    public async Task DeletedProblemTargetExplainsAbsenceWithoutSelectingAnotherRow()
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[1].Cells[0], "failed title");
            h.Existing.MutationResult = (_, _) => ScriptedRunner.Http("{}", 403);
            await h.Apply("P1-T2");
            h.Existing.ChangeResponse = (query, data) => {
                if (!query.Contains("ProjectItems")) return;
                var items = data["data"]!["node"]!["items"]!;
                items["nodes"]!.AsArray().RemoveAt(1); items["totalCount"] = 1;
            };
            Ui.Click("RefreshProjectButton");
        });
        await Ui.Until(() => !Workspace.IsBusy && Workspace.Selected!.Snapshot.Items.Count == 1);
        await Ui.Until(() => Ui.Find<Button>("NextApplyProblem", panel).IsLoaded);
        await Ui.Run(() =>
        {
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            var before = grid.SelectionIdentity;
            Ui.Click("NextApplyProblem");
            Assert.That(grid.SelectionIdentity, Is.EqualTo(before));
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus", panel).Text, Does.Contain("対象を現在のProjectで確認できません").And.Contain("反映結果"));
            Assert.That(Work.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Failed));
            Ui.Click("GridApplyHistory");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => { Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("#2")); Ui.DialogButton("ApplyHistoryDialog", "CloseButton"); });
        Assert.That(h.Writes.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CompletedApplyHistoryDefaultsToAttentionWithoutDeletingVerifiedRecord()
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Verified outgoing title");
            await h.Apply("P1-T1");
        });
        await Ui.OpenHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Not.Contain("反映済み・読み戻し確認済み"));
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("対応が必要な項目はありません"));
            Assert.That(Work.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Succeeded));
            Ui.Toggle(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains("反映済み・読み戻し確認済み"));
        await Ui.Run(() =>
        {
            var help = Ui.Find<Button>("ApplyHistoryHelp", Ui.Dialog("ApplyHistoryDialog"));
            Assert.That(help.Focus(FocusState.Keyboard), Is.True);
            Ui.Click(help);
            Assert.That(ToolTipService.GetToolTip(help), Is.Not.Null);
        });
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryHelp", Ui.Dialog("ApplyHistoryDialog")).Flyout.IsOpen);
        await Ui.Run(() =>
        {
            Ui.Find<Button>("ApplyHistoryHelp", Ui.Dialog("ApplyHistoryDialog")).Flyout.Hide();
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        Assert.That(h.Writes.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task SuccessfulConfirmationReturnsToTableWithoutOpeningResults()
    {
        await Ui.Run(() =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Approved outgoing title");
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "PrimaryButton"));
        await Ui.Until(() => !Workspace.IsBusy && Work.Journal.Any(b => b.Operations.All(o => o.State == ApplyState.Succeeded)));
        // Completion feedback is deferred to avoid taking native composition focus.
        await Task.Delay(400);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyHistoryDialog"), Is.Null);
            Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.True);
            Assert.That(Work.DifferenceCount, Is.Zero);
        });
    }

    [TestCase(false, false), TestCase(true, false), TestCase(true, true)]
    public async Task MixedResultsReturnToExactProblemAndPreservePendingInputAndView(bool hidden, bool uncertain)
    {
        string batch = "";
        await Ui.Run(async () =>
        {
            var p = Workspace.Selected!; var rows = Work.Open(p);
            Work.Commit("P1", rows[0].Cells[0], "keep approved");
            Work.SetBuffer(rows[0].Cells[0], "pending text is not sent");
            Work.Commit("P1", rows[1].Cells[0], "failed title");
            Work.Commit("P1", rows[1].Cells[1], "done", true);
            await Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1", "P1-T2" });
            var review = Workspace.ApplyReview!; batch = review.Batch.Id;
            Assert.That(await Workspace.Drafts!.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
            if (hidden)
            {
                p = Workspace.Selected!;
                Assert.That(await Workspace.Drafts.CommitAsync(w => {
                    w.SaveRowView(w.PrepareRowView(p) with { Definition = new(Title: "keep") });
                    var columns = w.PrepareColumns(p);
                    w.SaveColumns(columns with { Columns = columns.Columns.Select(c => c.Id.FieldId == "P1-status" ? c with { Visible = false } : c).ToArray() });
                    return w;
                }, () => true), Is.True);
            }
            h.Existing.MutationResult = (_, input) => input.TryGetProperty("id", out var id) && id.GetString() == "I1" ? null
                : uncertain ? new GhProcessResult(ProcessCompletion.TimedOut, true, null) : ScriptedRunner.Http("{}", 403);
        });
        if (hidden)
        {
            await Ui.Until(() => Ui.Find<Button>("GridReapply", panel) is { IsLoaded: true, IsEnabled: true });
            await Ui.Run(() => Ui.Click("GridReapply"));
        }
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResumeApplyBatch-" + batch, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.DialogReady("ApplyOutcomeWarning");
        await Ui.Run(async () =>
        {
            Assert.That(Ui.DialogText("ApplyOutcomeWarning"), Does.Contain(uncertain ? "要確認 2フィールド" : "失敗 2フィールド"));
            Assert.That(Ui.DialogText("ApplyOutcomeWarning"), Does.Not.Contain("反映済み"));
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyOutcomeWarning")!, $"apply-warning-{hidden}-{uncertain}");
            Ui.DialogButton("ApplyOutcomeWarning", "CloseButton");
        });
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("Title", "I2"));
        await Ui.Run(() => Ui.Click("NextApplyProblem"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("Select", "P1-T2", "P1", "P1-status"));
        await Ui.Run(async () =>
        {
            Assert.That(Ui.Find<TextBlock>("ApplyProblemStatus").Text, Does.Contain(uncertain ? "結果の確認が必要" : "失敗"));
            // The native recycling pool also retains offscreen containers. Inspect the current item.
            var grid = Ui.Tree(panel).OfType<EditingGrid>().Single();
            var list = Ui.Find<ListView>("ProjectItems", grid);
            var marker = Ui.Find<TextBlock>("GridMarker1_1", (ListViewItem)list.Items[1]);
            Assert.That(marker.Text, Is.EqualTo(uncertain ? "?" : "!"));
            Assert.That(marker.TransformToVisual(grid).TransformPoint(new(0, 0)).Y, Is.InRange(0, grid.ActualHeight));
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending text is not sent"));
            Assert.That(Work.Journal.Single().Operations.Count(o => o.State == ApplyState.Succeeded), Is.EqualTo(1));
            Assert.That(Work.Columns(Workspace.Selected!).Hidden("P1-status"), Is.EqualTo(hidden));
            Assert.That(Work.RowView(Workspace.Selected!).Title, Is.EqualTo(hidden ? "keep" : ""));
            await ApplyInformationEvidence.Capture(panel, $"apply-problem-{hidden}-{uncertain}");
            if (hidden)
            {
                Ui.Click("GridReapply");
                Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds, Is.EqualTo(new[] { "P1-T1" }));
                Assert.That(Work.Columns(Workspace.Selected!).Hidden("P1-status"), Is.True);
            }
        });
        Assert.That(h.Writes.Count, Is.EqualTo(3), "Showing results and navigating must not dispatch another update.");
    }
}
