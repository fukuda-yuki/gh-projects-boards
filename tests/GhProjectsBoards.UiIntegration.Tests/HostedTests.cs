using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed partial class HostedTests
{
    private CreationHarness h = null!;
    private RegistrationPanel panel = null!;
    private ControlledRunner? gate;
    private RegistrationWorkspace Workspace => h.Workspace;
    private EditingWorkspace Work => Workspace.Drafts!.Workspace;
    [SetUp]
    public async Task SetUp()
    {
        Ui.Check(); gate = null;
        h = await CreationHarness.Create(2);
        TestContext.Out.WriteLine($"Store: {h.Existing.Root}; production: {typeof(RegistrationPanel).Assembly.Location}; MVID: {typeof(EditingGrid).Module.ModuleVersionId}");
        await Ui.Run(() => { panel = new RegistrationPanel(); panel.Initialize(Workspace); });
        await Ui.Mount(panel);
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Any(g => g.IsLoaded));
    }
    [TearDown]
    public async Task TearDown()
    {
        gate?.Release.TrySetResult();
        FrameworkElement[] mounted = [];
        // Cleanup must run even after an async failure. Failure state is never reset.
        await Ui.Run(async () =>
        {
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot))
                foreach (var dialog in Ui.Tree(popup.Child).OfType<ContentDialog>().ToArray()) dialog.Hide();
            mounted = Ui.Root.Children.OfType<FrameworkElement>().ToArray();
            await Task.CompletedTask;
        }, check: false);
        foreach (var view in mounted) await Ui.Unmount(view, check: false);
        await Ui.Run(async () =>
        {
            await Workspace.StopAsync();
            Assert.That(await Workspace.FlushDraftsAsync(), Is.True);
        }, check: false);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(panel.IsLoaded, Is.False));
    }

    [Test]
    public async Task LoadedGridAddChoiceClearRemoveUndoPreservesStableIdentity()
    {
        await Ui.Run(() => Ui.Click("GridAddRow"));
        await Ui.Until(() => Work.LocalRows.Count == 1);
        var id = Work.LocalRows.Single().Id;
        await Ui.Ready<ComboBox>("GridCell2_1");
        await Ui.Run(() =>
        {
            var row = Work.Open(Workspace.Selected!).Single(r => r.ItemId == id);
            Assert.That(row.Cells[1].Key, Is.EqualTo(new FieldKey("LocalSelect", id, "P1", "P1-status")));
            var choice = Ui.Find<ComboBox>("GridCell2_1");
            choice.Focus(FocusState.Programmatic);
            choice.SelectedItem = choice.Items.OfType<SelectOption>().Single(o => o.Id == "done");
        });
        await Ui.Until(() => Work.LocalRows.Single().Selects.Any(s => s.OptionId == "done"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("LocalSelect", id, "P1", "P1-status"));
        await Ui.Run(() =>
        {
            Assert.That(((SelectOption)Ui.Find<ComboBox>("GridCell2_1").SelectedItem).Id, Is.EqualTo("done"));
            Ui.Click("GridClear");
        });
        await Ui.Until(() => Work.LocalRows.Single().Selects.Single().Intent.ToString() == "ExplicitClear");
        await Ui.Run(() => { Assert.That(Ui.Find<ComboBox>("GridCell2_1").SelectedItem, Is.Null); Ui.Click("GridRemoveRows"); });
        await Ui.Until(() => Work.LocalRows.Count == 0);
        await Ui.Run(() => Ui.Click("GridUndo"));
        await Ui.Until(() => Work.LocalRows.Count == 1);
        await Ui.Ready<ComboBox>("GridCell2_1");
        await Ui.Run(() =>
        {
            Assert.That(Work.LocalRows.Single().Id, Is.EqualTo(id));
            Assert.That(Ui.Find<ComboBox>("GridCell2_1").SelectedItem, Is.Null);
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain("ローカル行 1"));
        });
    }

    [Test]
    public async Task NativeTextChangedPendingBufferSurvivesPresentationUpdate()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "未確定 pending");
        await Ui.Until(() => Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer == "未確定 pending");
        await Ui.Run(() => Ui.Click("GridSave"));
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("未確定 pending"));
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Change, Is.Null);
        });
    }

    [Test]
    public async Task RejectedExistingRowRemovalShowsErrorAndPreservesWork()
    {
        await Ui.Run(() => { Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Programmatic); Ui.Click("GridRemoveRows"); });
        await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("削除は選択した新規ローカル行だけ"));
        await Ui.Run(() =>
        {
            Assert.That(Work.Open(Workspace.Selected!).Select(r => r.ItemId), Is.EqualTo(new[] { "P1-T1", "P1-T2" }));
            Assert.That(Work.DifferenceCount, Is.Zero);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    [Test]
    public async Task PanelUnloadReleasesWorkspaceRefreshGuardAndRemounts()
    {
        await Ui.Unmount(panel);
        Assert.That((object?)Workspace.CanRefresh, Is.Null);
        await Ui.Mount(panel);
        Assert.That((object?)Workspace.CanRefresh, Is.Not.Null);
        await Ui.Run(() => Ui.Click("GridAddRow"));
        await Ui.Until(() => Work.LocalRows.Count == 1);
    }

    [Test]
    public async Task CachedAndConnectedPanelReflectReadAvailability()
    {
        await Ui.Run(async () => { await Workspace.BindAsync(null); await Workspace.SelectProfileAsync(new("github.com", 42)); await Workspace.SelectAsync(new(new("github.com", 42), "P1")); });
        await Ui.Ready<Button>("GridAddRow");
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBlock>("WorkspaceIdentity").Text, Does.Contain("キャッシュのみ"));
            Assert.That(Ui.Find<Button>("RefreshProjectButton").IsEnabled, Is.False);
            Ui.Click("GridAddRow");
        });
        await Ui.Until(() => Work.LocalRows.Count == 1);
        await Ui.Run(async () => { await Workspace.BindAsync(h.Existing.Context, h.Existing.Service); await Workspace.SelectAsync(new(new("github.com", 42), "P1")); });
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBlock>("WorkspaceIdentity").Text, Does.Contain("接続確認済み"));
            Assert.That(Ui.Find<Button>("RefreshProjectButton").IsEnabled, Is.True);
            Assert.That(Work.LocalRows.Count, Is.EqualTo(1));
        });
    }

    private async Task ControlExternal(string query)
    {
        gate = new ControlledRunner(h.Existing.Boundary.Runner, query);
        var service = new GhConnectionService("synthetic-only.exe", "github.com", gate);
        var context = (await service.ConnectAsync()).Context!;
        await Ui.Run(async () => { await Workspace.BindAsync(context, service); await Workspace.SelectAsync(new(new("github.com", 42), "P1")); });
        await Ui.Ready<TextBox>("GridCell0_0");
        gate.Armed = true;
    }

    [TestCase("cancel"), TestCase("failure"), TestCase("success")]
    public async Task ControlledRetrievalShowsBusyAndSettledStates(string outcome)
    {
        await ControlExternal("ProjectFields");
        if (outcome == "failure") gate!.Fail = true;
        await Ui.Run(() => Ui.Click("RefreshProjectButton"));
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<Button>("CancelProjectButton").IsEnabled, Is.True);
            Assert.That(Ui.Find<Button>("RefreshProjectButton").IsEnabled, Is.False);
            Assert.That(Ui.Tree(panel).OfType<ProgressBar>().Single().Visibility, Is.EqualTo(Visibility.Visible));
            if (outcome == "cancel") Ui.Click("CancelProjectButton");
        });
        gate.Release.TrySetResult();
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<Button>("CancelProjectButton").IsEnabled, Is.False);
            Assert.That(Ui.Find<Button>("RefreshProjectButton").IsEnabled, Is.True);
            Assert.That(Ui.Tree(panel).OfType<ProgressBar>().Single().Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Workspace.LatestAttempt, Is.EqualTo(outcome == "cancel" ? RegistrationAttempt.Cancelled : outcome == "failure" ? RegistrationAttempt.Failed : RegistrationAttempt.Complete));
            Assert.That(Ui.Find<TextBlock>("RegistrationStatus").Text, Is.EqualTo(Workspace.Status).And.Not.Empty);
        });
    }

    [Test]
    public async Task OffThreadChangeAndLateTransitionResultKeepCurrentWorkspace()
    {
        await ControlExternal("ProjectFields");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "retained pending");
        var oldSession = Workspace.Drafts!;
        await Ui.Run(() => Ui.Click("RefreshProjectButton"));
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var transition = Task.Run(() => Workspace.SelectProfileAsync(new("other.test", 99)));
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult();
        await transition.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Until(() => Ui.Find<TextBlock>("WorkspaceIdentity").Text.Contains("other.test"));
        await Ui.Run(() =>
        {
            Assert.That(Workspace.Selected, Is.Null);
            Assert.That(Ui.Tree(panel).OfType<EditingGrid>(), Is.Empty);
            Assert.That(oldSession.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("retained pending"));
        });
        await Ui.Run(async () => { await Workspace.SelectProfileAsync(new("github.com", 42)); await Workspace.SelectAsync(new(new("github.com", 42), "P1")); });
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("retained pending")));
    }

    [Test]
    public async Task DelayedGridCommandCannotCrossUnloadRemount()
    {
        await Ui.Unmount(panel);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(Workspace.Selected!, Workspace.Drafts!, async () => { started.TrySetResult(); await release.Task; return await Workspace.PrepareLocalRowsAsync(); }));
        try
        {
            await Ui.Mount(grid);
            await Ui.Run(() => Ui.Click("GridAddRow"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Ui.Unmount(grid);
            await Ui.Mount(grid);
            release.TrySetResult(true);
            await Ui.Idle();
            await Ui.Run(() => Assert.That(Work.LocalRows, Is.Empty));
            await Ui.Run(() => Ui.Click("GridAddRow"));
            await Ui.Until(() => Work.LocalRows.Count == 1);
        }
        finally { release.TrySetResult(true); await Ui.Unmount(grid); }
    }

    [Test]
    public async Task LateDiscoveryCannotPopulateReinitializedPanel()
    {
        await ControlExternal("RegistrationOwners");
        await Ui.Run(() => Ui.Click("AddProjectButton"));
        await Ui.Ready<Button>("LoadOwnersButton");
        await Ui.Run(() => Ui.Click("LoadOwnersButton"));
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = await ApplyTests.Harness.Create(2);
        try
        {
            await Ui.Unmount(panel);
            await Ui.Run(() => panel.Initialize(replacement.Workspace));
            await Ui.Mount(panel);
            await Ui.Run(() => Ui.Click("AddProjectButton"));
            gate.Release.TrySetResult();
            await Ui.Until(() => !Workspace.IsBusy);
            await Ui.Idle();
            await Ui.Run(() =>
            {
                Assert.That(Ui.Find<ComboBox>("DiscoveryOwners").Items, Is.Empty);
                Assert.That(panel.Workspace, Is.SameAs(replacement.Workspace));
                Assert.That((object?)Workspace.CanRefresh, Is.Null);
            });
        }
        finally
        {
            gate.Release.TrySetResult();
            await Ui.Unmount(panel);
            await replacement.Workspace.StopAsync();
            Assert.That(await replacement.Workspace.FlushDraftsAsync(), Is.True);
        }
    }

    [Test]
    public async Task DelayedReviewCannotOpenDialogAfterRemount()
    {
        await ControlExternal("ProjectFields");
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplySelectionDialog");
        await Ui.Run(() =>
        {
            Ui.Select(Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplySelectionDialog")), 0);
            Ui.DialogButton("ApplySelectionDialog", "PrimaryButton");
        });
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Unmount(panel); await Ui.Mount(panel);
        await Ui.Run(() => Assert.That(Ui.Find<Button>("ApplyHistoryButton").IsEnabled, Is.False));
        gate.Release.TrySetResult();
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => { Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.Null); Assert.That(h.Writes, Is.Empty); });
    }

    [Test]
    public async Task UnregisterDialogCannotFollowPanelIntoAnotherWorkspace()
    {
        await Ui.Run(() => Ui.Click("UnregisterProjectButton"));
        await Ui.DialogReady("LocalUnregisterConfirmation");
        var replacement = await ApplyTests.Harness.Create(2);
        try
        {
            await Ui.Unmount(panel);
            await Ui.Run(() => panel.Initialize(replacement.Workspace));
            await Ui.Mount(panel);
            await Ui.Idle();
            await Ui.Run(() =>
            {
                Assert.That(Ui.Dialog("LocalUnregisterConfirmation"), Is.Null);
                Assert.That(Workspace.Registrations.Count, Is.EqualTo(1));
                Assert.That(replacement.Workspace.Registrations.Count, Is.EqualTo(1));
                Assert.That(panel.Workspace, Is.SameAs(replacement.Workspace));
            });
        }
        finally { await Ui.Unmount(panel); await replacement.Workspace.StopAsync(); Assert.That(await replacement.Workspace.FlushDraftsAsync(), Is.True); }
    }

    [TestCase(false), TestCase(true)]
    public async Task ApplyReviewShowsSelectedUpdatesAndCreationsAndExcludesIncompleteRows(bool confirm)
    {
        string local = "", incomplete = "";
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Existing update");
            local = h.Add("Create title"); incomplete = h.Add("", "");
            await Workspace.FlushDraftsAsync();
        });
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "pending excluded");
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplySelectionDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplySelectionDialog"), Does.Contain("選択候補 4 件").And.Contain("未選択の未完成行"));
            var list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplySelectionDialog"));
            Assert.That(list.Items[2].ToString(), Does.Contain(local).And.Contain("sample-user/first"));
            Assert.That(list.Items[3].ToString(), Does.Contain(incomplete));
            Ui.Select(list, 0); Ui.Select(list, 2);
            Ui.DialogButton("ApplySelectionDialog", "PrimaryButton");
        });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Run(() =>
        {
            var text = Ui.DialogText("ApplyReviewDialog");
            Assert.That(text, Does.Contain("選択行 2 / 更新 1 / 作成 1").And.Contain("未確定文字 1 件は除外").And.Contain("未選択の未完成行"));
            Assert.That(text, Does.Contain("宛先 sample-user/first / Repository ID R-first").And.Contain("Create title").And.Contain("Existing update"));
            Assert.That(text, Does.Not.Contain("pending excluded"));
            Assert.That(Ui.Find<Button>("ApplyHistoryButton").IsEnabled, Is.False);
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", confirm ? "PrimaryButton" : "CloseButton");
        });
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(h.Writes.Count(x => x.Query.Contains("CreateWorkspaceIssue")), Is.EqualTo(confirm ? 1 : 0));
            Assert.That(h.Writes.Count(x => x.Query.Contains("ApplyTitle")), Is.EqualTo(confirm ? 1 : 0));
            Assert.That(Work.LocalRows.Any(r => r.Id == incomplete && r.Title == ""), Is.True);
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending excluded"));
            Ui.Click("ApplyHistoryButton");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<Button>("ApplyHistoryButton").IsEnabled, Is.False);
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        Assert.That(h.Writes.Count(x => x.Query.Contains("CreateWorkspaceIssue")), Is.EqualTo(confirm ? 1 : 0));
    }

    [Test]
    public async Task ColumnHiddenDifferencesRemainInExistingAndCreationReview()
    {
        await Ui.Run(async () =>
        {
            var p = Workspace.Selected!; Work.Commit("P1", Work.Open(p)[0].Cells[1], "Done");
            var id = h.Add("Hidden creation"); Work.Commit("P1", Work.Open(p).Single(r => r.ItemId == id).Cells[1], "Done");
            await Workspace.FlushDraftsAsync();
        });
        await Ui.Run(() => Ui.Click("GridColumns")); await Ui.DialogReady("ColumnSettingsDialog");
        await Ui.Run(() => { Ui.Find<CheckBox>("ColumnVisible-P1-status", Ui.Dialog("ColumnSettingsDialog")).IsChecked = false; Ui.DialogButton("ColumnSettingsDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("ColumnSettingsDialog") is null);
        await Ui.Run(() => Ui.Click("ReviewApplyButton")); await Ui.DialogReady("ApplySelectionDialog");
        await Ui.Run(() => { var list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplySelectionDialog")); Ui.Select(list, 0); Ui.Select(list, 2); Ui.DialogButton("ApplySelectionDialog", "PrimaryButton"); });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Run(() =>
        {
            var text = Ui.DialogText("ApplyReviewDialog");
            Assert.That(text, Does.Contain("グリッドでは非表示").And.Contain("P1-status").And.Contain("Set Done [done]").And.Contain("適用値: done"));
            Assert.That(Workspace.ApplyReview!.Batch.Operations.Single().Key.FieldId, Is.EqualTo("P1-status"));
            Assert.That(Workspace.ApplyReview.Batch.Creations!.Single().Selects.Single().FieldId, Is.EqualTo("P1-status"));
            Assert.That(h.Writes, Is.Empty); Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton").IsEnabled);
    }

    [Test]
    public async Task SelectionCancellationDoesNotDispatch()
    {
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplySelectionDialog");
        await Ui.Run(() => Ui.DialogButton("ApplySelectionDialog", "CloseButton"));
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton").IsEnabled);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(Workspace.ApplyReview, Is.Null);
    }

    [Test]
    public async Task HistoryRemainsDisabledUntilOutstandingContinuationSettles()
    {
        await Ui.Run(async () =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "History update");
            await Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
            var review = Workspace.ApplyReview!;
            Assert.That(await Workspace.Drafts!.CommitAsync(w => { w.ConfirmApply(review); return w; }, () => true), Is.True);
        });
        // Arrange a durably approved, undispatched batch, then use the history UI to resume.
        await ControlExternal("ProjectFields");
        await Ui.Run(() => Ui.Click("ApplyHistoryButton"));
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "PrimaryButton"));
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyHistoryDialog"), Is.Null);
            Assert.That(Ui.Find<Button>("ApplyHistoryButton").IsEnabled, Is.False);
            Assert.That(h.Writes, Is.Empty);
        });
        gate.Release.TrySetResult();
        await Ui.Until(() => Ui.Find<Button>("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        Assert.That(h.Writes.Count(x => x.Query.Contains("ApplyTitle")), Is.EqualTo(1));
        await Ui.Run(() => Ui.Click("ApplyHistoryButton"));
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.DialogButton("ApplyHistoryDialog", "CloseButton"));
        await Ui.Idle();
        Assert.That(h.Writes.Count(x => x.Query.Contains("ApplyTitle")), Is.EqualTo(1));
    }

    private sealed class ControlledRunner(IGhProcessRunner inner, string query) : IGhProcessRunner
    {
        public bool Armed, Fail;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            if (Armed && command.StandardInput?.Contains(query) == true)
            {
                using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
                Started.TrySetResult(); await Release.Task;
                if (Fail) return ScriptedRunner.Http("{}", 503);
            }
            return await inner.RunAsync(command, CancellationToken.None);
        }
    }
}
