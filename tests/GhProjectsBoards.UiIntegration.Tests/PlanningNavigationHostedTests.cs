using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("WorkflowPlanning")]
    public async Task SettingsCandidateAllowsReadingHistoryButPreventsRecoveryMutations()
    {
        await Ui.Run(async () => {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "History update");
            await Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
            var review = Workspace.ApplyReview!;
            Assert.That(await Workspace.Drafts!.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
        });
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<TextBox>("PlanProjectStart");
        await Ui.Run(() => Ui.Find<TextBox>("PlanProjectStart").Text = "2026-10-");
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyHistoryDialog"); var batch = Work.Journal.Single();
            Assert.That(Ui.Find<Button>("ResumeApplyBatch-" + batch.Id, dialog).IsEnabled, Is.False);
            Assert.That(Ui.Find<Button>("WithdrawApplyBatch-" + batch.Id, dialog).IsEnabled, Is.False);
            Assert.That(Ui.Tree(dialog!).OfType<TextBlock>().Any(t => t.Text.Contains("閲覧のみ")), Is.True);
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyHistoryDialog") is null);
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanProjectStart").Text, Is.EqualTo("2026-10-"));
            Ui.Click("PlanSettingsCancel");
        });
        await Ui.Until(() => !Ui.Tree(panel).OfType<EditingGrid>().Single().PlanningSettingsOpen && Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("WithdrawApplyBatch-" + Work.Journal.Single().Id, Ui.Dialog("ApplyHistoryDialog")).IsEnabled, Is.True);
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
    }

    [TestCase(false), TestCase(true), Category("WorkflowPlanning")]
    public async Task ConnectionRoundtripRetainsSettingsAndAnotherAccountNeedsAnExplicitDiscard(bool changeAccount)
    {
        var original = Workspace.Selected!;
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        await model.CheckAsync(false);
        ConnectionPanel connection = null!;
        await Ui.Run(() => {
            connection = new ConnectionPanel { ConfirmWorkspaceChangeAsync = scope => panel.ConfirmPlanningNavigationAsync(scope) };
            connection.Initialize(Workspace, model);
            panel.ConnectionRequested += (_, _) => { panel.Visibility = Visibility.Collapsed; connection.Visibility = Visibility.Visible; };
            connection.ReturnRequested += (_, _) => { connection.Visibility = Visibility.Collapsed; panel.Visibility = Visibility.Visible; panel.Update(); panel.ReturnFromConnection(); };
        });
        await Ui.Mount(connection); await Ui.Run(() => connection.Visibility = Visibility.Collapsed);
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<TextBox>("PlanProjectStart");
        await Ui.Run(() => { Ui.Find<TextBox>("PlanProjectStart").Text = "2026-10-"; Ui.Click("ConnectionPageButton"); });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        if (changeAccount) h.Existing.Boundary.ViewerId = 99;
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection)));
        if (changeAccount)
        {
            await Ui.Until(() => model.StatusText.Contains("変更を検出") && Ui.Find<Button>("NewConnectionButton", connection).IsEnabled);
            await Ui.Run(() => Ui.Click(Ui.Find<Button>("NewConnectionButton", connection)));
            await Ui.DialogReady("LeavePlanningSettings");
            await Ui.Run(() => Ui.DialogButton("LeavePlanningSettings", "CloseButton"));
        }
        await Ui.Until(() => Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled && !Workspace.IsBusy);
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection)));
        await Ui.Until(() => panel.Visibility == Visibility.Visible);
        await Ui.Ready<Button>("PlanSettingsCancel");
        await Ui.Run(() => {
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(original.Snapshot.Id));
            Assert.That(Ui.Find<TextBox>("PlanProjectStart").Text, Is.EqualTo("2026-10-"));
            Assert.That(Work.Planning("P1"), Is.Null); Assert.That(h.Writes, Is.Empty);
            Ui.Click("PlanSettingsCancel");
        });
    }

    [Test, Category("WorkflowPlanning")]
    public async Task ProjectSwitchRequiresDiscardAndCancelRetainsTheSameSettingsAndTaskInput()
    {
        var first = Workspace.Selected!;
        await Ui.Run(async () => {
            var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
                "https://github.com/users/sample-user/projects/2", default);
            await Workspace.RegisterAsync(choice, null); await Workspace.SelectAsync(first.Snapshot.Id);
        });
        await OpenNavigation(SplitViewDisplayMode.Inline);
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<TextBox>("PlanProjectStart");
        await Ui.Run(() => Ui.Find<TextBox>("PlanProjectStart").Text = "2026-10-05");
        await InvokeProjectNode("Project 2"); await Ui.DialogReady("LeavePlanningSettings");
        await Ui.Run(() => Ui.DialogButton("LeavePlanningSettings", "CloseButton"));
        await Ui.Until(() => Ui.Dialog("LeavePlanningSettings") is null);
        await Ui.Run(() => {
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(first.Snapshot.Id));
            Assert.That(Ui.Find<TextBox>("PlanProjectStart").Text, Is.EqualTo("2026-10-05"));
            Assert.That(Work.Planning("P1"), Is.Null);
        });
        await InvokeProjectNode("Project 2"); await Ui.DialogReady("LeavePlanningSettings");
        await Ui.Run(() => Ui.DialogButton("LeavePlanningSettings", "PrimaryButton"));
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P2");
        await Ui.Run(() => {
            Assert.That(Work.Planning("P1"), Is.Null); Assert.That(Work.Planning("P2"), Is.Null);
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>().Single().PlanningSettingsOpen, Is.False);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test, Category("WorkflowPlanning")]
    public async Task FailedSettingsSaveKeepsTheCandidateAndRetryReturnsOnlyAfterDurableSuccess()
    {
        await Ui.Run(async () => Assert.That(await Workspace.FlushDraftsAsync(), Is.True));
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<TextBox>("PlanProjectStart"); await Ui.Ready<Button>("PlanSettingsSave");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("ReviewApplyButton").IsEnabled, Is.False);
            Assert.That(Ui.Find<Button>("ApplyHistoryButton").IsEnabled, Is.True, "History remains available for reading.");
            Ui.Find<TextBox>("PlanProjectStart").Text = "2026-10-05 09:00";
        });
        using (var competing = new FileStream(Path.Combine(h.Existing.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.Click("PlanSettingsSave"));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanningStatus").Text.Contains("保存できません"));
            await Ui.Run(() => {
                Assert.That(Work.Planning("P1"), Is.Null);
                Assert.That(Ui.Find<TextBox>("PlanProjectStart").Text, Is.EqualTo("2026-10-05 09:00"));
                Assert.That(Ui.Find<Button>("PlanSettingsSave").IsEnabled, Is.True);
                Ui.Find<TextBox>("PlanProjectStart").Text = "2026-10-05 09:01";
            });
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanningStatus").Text, Does.Contain("保存できません"), "Editing input must not clear a storage failure."));
        }
        await Ui.Run(() => Ui.Click("PlanSettingsSave"));
        await Ui.Until(() => !Ui.Tree(panel).OfType<EditingGrid>().Single().PlanningSettingsOpen);
        var saved = await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope);
        Assert.That(EditingWorkspace.Restore(saved!).Planning("P1")!.Start, Is.EqualTo(new DateTime(2026, 10, 5, 9, 1, 0)));
        Assert.That(h.Writes, Is.Empty);
    }
}
