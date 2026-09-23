using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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
        await Ui.Ready<Button>("GridCell2_1");
        await Ui.Run(() =>
        {
            var row = Work.Open(Workspace.Selected!).Single(r => r.ItemId == id);
            Assert.That(row.Cells[1].Key, Is.EqualTo(new FieldKey("LocalSelect", id, "P1", "P1-status")));
            Assert.That(ChoiceText(), Does.Contain("未指定（送信しない）"));
        });
        await Ui.ChooseCell("GridCell2_1", "done");
        await Ui.Until(() => Work.LocalRows.Single().Selects.Any(s => s.OptionId == "done"));
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().SelectionIdentity?.Field == new FieldKey("LocalSelect", id, "P1", "P1-status"));
        await Ui.Run(() =>
        {
            Assert.That(ChoiceText(), Does.Contain("Done"));
        });
        await Ui.ClickCommand("GridClear");
        await Ui.Until(() => Work.LocalRows.Single().Selects.Single().Intent.ToString() == "ExplicitClear");
        await Ui.Run(() => Assert.That(ChoiceText(), Does.Contain("明示的にクリア")));
        await Ui.ClickCommand("GridRemoveRows");
        await Ui.Until(() => Work.LocalRows.Count == 0);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain("削除・変更")));
        await Ui.Until(() => Workspace.Drafts!.DurableRevision == Work.Revision);
        Assert.That((await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope))!.LocalRows, Is.Empty);
        await Ui.ClickCommand("GridUndo");
        await Ui.Until(() => Work.LocalRows.Count == 1);
        await Ui.Ready<Button>("GridCell2_1");
        await Ui.Run(() =>
        {
            Assert.That(Work.LocalRows.Single().Id, Is.EqualTo(id));
            Assert.That(ChoiceText(), Does.Contain("明示的にクリア"));
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain("ローカル新規 1行"));
        });
        await Ui.Until(() => Workspace.Drafts!.DurableRevision == Work.Revision);
        Assert.That((await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope))!.LocalRows!.Single().Id, Is.EqualTo(id));

        static string[] ChoiceText() => Ui.Tree(Ui.Find<Button>("GridCell2_1")).OfType<TextBlock>().Select(text => text.Text).ToArray();
    }

    [TestCase("GridRemoveRows"), TestCase("GridUndo")]
    public async Task RemovingSelectedLocalTitleSavesAndAllowsTheNextRowInput(string command)
    {
        await Ui.Run(() => Ui.Click("GridAddRow"));
        await Ui.Until(() => Work.LocalRows.Count == 1 && Workspace.Drafts!.DurableRevision == Work.Revision);
        await Ui.Ready<TextBox>("GridCell2_0");
        TextBox removed = null!;
        await Ui.Run(() => removed = Ui.Find<TextBox>("GridCell2_0"));

        await Ui.ClickCommand(command);
        await Ui.Until(() => Work.LocalRows.Count == 0);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain("削除・変更")));
        await Ui.Until(() => !removed.IsLoaded && Workspace.Drafts!.DurableRevision == Work.Revision);
        Assert.That((await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope))!.LocalRows, Is.Empty);

        await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0")).SetFocus());
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "continued after removal");
        await Ui.Until(() => Workspace.Drafts!.DurableRevision == Work.Revision);
        var saved = (await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope))!;
        Assert.That(saved.Fields.Single(field => field.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("continued after removal"));
        Assert.That(saved.Fields.Single(field => field.Key == new FieldKey("Title", "I2")).Buffer, Is.Null);
        Assert.That(h.Writes, Is.Empty);
    }

    [Test]
    public async Task HiddenLocalTitleKeepsItsPendingHostCaretAndDurableIdentity()
    {
        await Ui.Run(() => Ui.Click("GridAddRow"));
        await Ui.Until(() => Work.LocalRows.Count == 1);
        await Ui.Ready<TextBox>("GridCell2_0");
        TextBox pending = null!;
        var id = Work.LocalRows.Single().Id;
        await Ui.Run(() =>
        {
            pending = Ui.Find<TextBox>("GridCell2_0"); pending.Text = "draft pending"; pending.Select(3, 0);
            Ui.Find<TextBox>("GridQuickTitleFilter").Text = "Issue"; Ui.Click("GridQuickFilterApply");
        });
        await Ui.Until(() => !Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds.Contains(id));
        await Ui.Run(() =>
        {
            Assert.That(pending.IsLoaded, Is.True);
            Assert.That(pending.Text, Is.EqualTo("draft pending"));
            Ui.Click("GridQuickFilterClear");
        });
        await Ui.Ready<TextBox>("GridCell2_0");
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBox>("GridCell2_0"), Is.SameAs(pending));
            Assert.That(pending.SelectionStart, Is.EqualTo(3));
            Assert.That(pending.SelectionLength, Is.Zero);
            pending.Focus(FocusState.Keyboard); pending.SelectedText = "X";
        });
        await Ui.Until(() => Workspace.Drafts!.DurableRevision == Work.Revision);
        Assert.That((await new DraftStore(h.Existing.Root).LoadAsync(Work.Scope))!.LocalRows!.Single(row => row.Id == id).TitleBuffer,
            Is.EqualTo("draXft pending"));
        Assert.That(h.Writes, Is.Empty);
    }

    [Test]
    public async Task NativeTextChangedPendingBufferSurvivesPresentationUpdate()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Text = "未確定 pending");
        await Ui.Until(() => Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer == "未確定 pending");
        await Ui.ClickCommand("GridSave");
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
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Programmatic));
        await Ui.ClickCommand("GridRemoveRows");
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
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Any(g => g.IsLoaded));
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
        await Ui.Until(() => Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer == "retained pending");
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
        await Ui.Ready<Expander>("ProjectDiscoverySearchExpander");
        await Ui.Run(() => Ui.Find<Expander>("ProjectDiscoverySearchExpander").IsExpanded = true);
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
            await Ui.Ready<Expander>("ProjectDiscoverySearchExpander");
            await Ui.Run(() => Ui.Find<Expander>("ProjectDiscoverySearchExpander").IsExpanded = true);
            await Ui.Ready<ComboBox>("DiscoveryOwners");
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
        await Ui.DialogReady("ApplyReviewDialog");
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Unmount(panel); await Ui.Mount(panel);
        await Ui.Run(() => Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.False));
        gate.Release.TrySetResult();
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => { Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.Null); Assert.That(h.Writes, Is.Empty); });
    }

    [Test]
    public async Task UnregisterDialogCannotFollowPanelIntoAnotherWorkspace()
    {
        await Ui.Run(() => Ui.Click("ProjectSettingsButton"));
        await Ui.Until(() => SettingsContent() is not null);
        await Ui.Until(() => Ui.Find<Button>("UnregisterProjectButton", SettingsContent()!) is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Find<Button>("UnregisterProjectButton", SettingsContent()!).StartBringIntoView());
        await Ui.Idle();
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("UnregisterProjectButton", SettingsContent()!)));
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
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("3件中0件を選択"));
            var list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog"));
            Assert.That(list.SelectedItems, Is.Empty);
            Assert.That(list.Items[0].ToString(), Does.Contain("#1").And.Not.Contain("pending excluded"));
            Assert.That(list.Items[1].ToString(), Does.Contain("sample-user/first").And.Contain("Create title"));
            Assert.That(list.Items[2].ToString(), Does.Contain("宛先未指定"));
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("タイトルが必要です"));
            list.SelectedItems.Add(list.Items[0]); list.SelectedItems.Add(list.Items[1]);
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            var text = Ui.DialogText("ApplyReviewDialog");
            Assert.That(text, Does.Contain("3件中2件を選択").And.Contain("更新1件・新規作成1件").And.Contain("未確定入力は送信対象外"));
            Assert.That(text, Does.Contain("sample-user/first").And.Contain("Create title").And.Contain("Existing update"));
            Assert.That(text, Does.Contain("送らない未確定入力: pending excluded"));
            Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.False);
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", confirm ? "PrimaryButton" : "CloseButton");
        });
        if (confirm) await Ui.Until(() => !Workspace.IsBusy && Work.Creations.Any(c => c.Completed));
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(h.Writes.Count(x => x.Query.Contains("CreateWorkspaceIssue")), Is.EqualTo(confirm ? 1 : 0));
            Assert.That(h.Writes.Count(x => x.Query.Contains("ApplyTitle")), Is.EqualTo(confirm ? 1 : 0));
            Assert.That(Work.LocalRows.Any(r => r.Id == incomplete && r.Title == ""), Is.True);
            Assert.That(Work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending excluded"));
        });
        await Ui.OpenHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.False);
            Ui.DialogButton("ApplyHistoryDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
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
        await Ui.ClickCommand("GridColumns"); await Ui.DialogReady("ColumnSettingsDialog");
        await Ui.Run(() => { Ui.Find<CheckBox>("ColumnVisible-P1-status", Ui.Dialog("ColumnSettingsDialog")).IsChecked = false; Ui.DialogButton("ColumnSettingsDialog", "PrimaryButton"); });
        await Ui.Until(() => Ui.Dialog("ColumnSettingsDialog") is null);
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            var text = Ui.DialogText("ApplyReviewDialog");
            Assert.That(text, Does.Contain("表では非表示").And.Contain("未作成 → Done").And.Contain("Todo → Done"));
            Assert.That(Workspace.ApplyReview!.Batch.Operations.Single().Key.FieldId, Is.EqualTo("P1-status"));
            Assert.That(Workspace.ApplyReview.Batch.Creations!.Single().Selects.Single().FieldId, Is.EqualTo("P1-status"));
            Assert.That(h.Writes, Is.Empty); Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
    }

    [Test]
    public async Task SelectionCancellationDoesNotDispatch()
    {
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "CloseButton"));
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(Work.Journal, Is.Empty);
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
        // Pause the current scoped pre-dispatch observation, including when its
        // first response already contains all field definitions.
        await ControlExternal("ApplyObservation");
        await Ui.OpenHistory() ;
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ResumeApplyBatch-" + Work.Journal.Single().Id, Ui.Dialog("ApplyHistoryDialog"))));
        await gate!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyHistoryDialog"), Is.Null);
            Assert.That(Ui.ProjectCommand("ApplyHistoryButton").IsEnabled, Is.False);
            Assert.That(Ui.Find<ListView>("ApplyProgressRows").Items.Cast<string>().Single(), Does.Contain("sample-user/first #1").And.Contain("タイトル").And.Contain("未送信"));
            Assert.That(Ui.Find<Button>("CancelProjectButton").Content, Is.EqualTo("未送信の処理を止める"));
            Assert.That(h.Writes, Is.Empty);
        });
        gate.Release.TrySetResult();
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        Assert.That(h.Writes.Count(x => x.Query.Contains("ApplyTitle")), Is.EqualTo(1));
        await Ui.OpenHistory() ;
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
