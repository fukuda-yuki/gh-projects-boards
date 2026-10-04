using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.Foundation;
using Windows.Graphics;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class WeeklyApplyHostedTests
{
    private ApplyTests.Harness h = null!;
    private RegistrationPanel panel = null!;

    [SetUp]
    public async Task Setup()
    {
        Ui.Check(); h = await ApplyTests.Harness.Create(2, planning: true);
        Assert.That(await h.Workspace.PrepareLocalRowsAsync(), Is.True);
        var work = h.Workspace.Drafts!.Workspace; var project = h.Workspace.Selected!;
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = Enumerable.Range(1, 2).Select(i =>
            new PlanningTask("I" + i, PlanningMode.Auto, "U1", Actuals: [new("U1", i + 6, new(2026, 10, 13))])).ToArray() }, work.Revision,
            Enumerable.Range(1, 2).SelectMany(i => new[] {
                new PlanningValueEdit("P1-T" + i, "Estimate", "16"), new PlanningValueEdit("P1-T" + i, "Remaining", "4") }).ToArray());
        foreach (var row in work.Open(project)) work.Commit("P1", row.Cells[0], work.Value(row.Cells[0]) + " local title");
        Assert.That(await h.Workspace.Drafts.FlushAsync(), Is.True);
        await Ui.Run(() => { panel = new RegistrationPanel(); panel.Initialize(h.Workspace); });
        await Ui.Mount(panel); await Ui.Ready<FrameworkElement>("GridCell0_0");
    }

    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => {
            Ui.Dialog("ApplyReviewDialog")?.Hide(); Ui.Dialog("ApplyHistoryDialog")?.Hide();
            return Task.CompletedTask;
        }, check: false);
        await Ui.Unmount(panel, check: false);
        await Ui.Run(async () => { await h.Workspace.StopAsync(); Assert.That(await h.Workspace.FlushDraftsAsync(), Is.True); }, check: false);
        await Ui.Idle();
    }

    [Test, Category("WeeklyApply")]
    public async Task WeeklyCommandPreselectsChangedRowsAndDisplaysOnlyActualAndRemainingWithoutSending()
    {
        await OpenWeeklyReview();
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!; var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(dialog.Title, Is.EqualTo("週次反映 — 実績・残工数"));
            Assert.That(list.Items, Has.Count.EqualTo(2)); Assert.That(list.SelectedItems, Has.Count.EqualTo(2));
            Assert.That(DisplayedValues(dialog), Is.EquivalentTo(new[] {
                "ApplyValue-P1-T1-Field-F-Actual", "ApplyValue-P1-T1-Field-F-Remaining",
                "ApplyValue-P1-T2-Field-F-Actual", "ApplyValue-P1-T2-Field-F-Remaining" }));
            Assert.That(Ui.Find<TextBlock>("ApplyValue-P1-T1-Field-F-Actual", dialog).Text, Does.EndWith("→ 7"));
            Assert.That(Ui.Find<TextBlock>("ApplyValue-P1-T2-Field-F-Remaining", dialog).Text, Does.EndWith("→ 4"));
            Assert.That(h.Workspace.ApplyReview!.Batch.Operations.Select(operation => operation.Key.FieldId).Distinct(), Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
            Assert.That(h.Writes, Is.Empty); Assert.That(h.Workspace.Drafts!.Workspace.Journal, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null);
        await Ui.Run(() => {
            Assert.That(h.Writes, Is.Empty); Assert.That(h.Workspace.Drafts!.Workspace.Journal, Is.Empty);
            Assert.That(h.Workspace.Drafts.Workspace.Fields.Any(field => field.Key.Kind == "Title" && field.Change is not null), Is.True);
        });
    }

    [Test, Category("WeeklyApply")]
    public async Task WeeklyHiddenRowsRequireExplicitInclusionAndSelectionBeforeTheyJoinTheReview()
    {
        await Ui.Run(async () => {
            var project = h.Workspace.Selected!;
            Assert.That(await h.Workspace.Drafts!.CommitAsync(work => {
                work.SaveRowView(work.PrepareRowView(project) with { Definition = new(Title: "Issue 1") }); return work;
            }, () => true), Is.True);
            Ui.Click("GridReapply");
        });
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Single().DisplayedRowIds.SequenceEqual(new[] { "P1-T1" }));
        await OpenWeeklyReview();
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!; var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(list.Items, Has.Count.EqualTo(1)); Assert.That(list.SelectedItems, Has.Count.EqualTo(1));
            Assert.That(Ui.Find<CheckBox>("ApplyIncludeHidden", dialog).IsChecked, Is.False);
            Assert.That(h.Workspace.ApplyReview!.Batch.Operations.Select(operation => operation.ItemId).Distinct(), Is.EqualTo(new[] { "P1-T1" }));
            Ui.Toggle(Ui.Find<CheckBox>("ApplyIncludeHidden", dialog));
        });
        await Ui.Until(() => !h.Workspace.IsBusy && Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled
            && Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog")).Items.Count == 2);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!; var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(list.SelectedItems, Has.Count.EqualTo(1)); Assert.That(h.Writes, Is.Empty);
            Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", dialog));
        });
        await Ui.Until(() => !h.Workspace.IsBusy && h.Workspace.ApplyReview?.SelectedRows == 2 && Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(h.Workspace.ApplyReview!.Batch.Operations.Select(operation => operation.ItemId).Distinct(), Is.EquivalentTo(new[] { "P1-T1", "P1-T2" }));
            Assert.That(h.Workspace.ApplyReview.Batch.Operations.Select(operation => operation.Key.FieldId).Distinct(), Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
            Assert.That(h.Writes, Is.Empty); Assert.That(h.Workspace.Drafts!.Workspace.Journal, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null);
    }

    [TestCase(false), TestCase(true), Category("WeeklyApply")]
    public async Task HistoryRecoveryRetainsWeeklyPresetAndDoesNotSendOtherDrafts(bool historical)
    {
        string batchId = "", operationId = "";
        await Ui.Run(async () => {
            h.MutationResult = (_, _) => historical
                ? new GhProcessResult(ProcessCompletion.TimedOut, true, null)
                : ScriptedRunner.Http("{}", 403);
            await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }, weeklyEffort: true);
            await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
            var batch = h.Workspace.Drafts!.Workspace.Journal.Single();
            batchId = batch.Id;
            Assert.That(batch.WeeklyEffort, Is.True);
            Assert.That(batch.Operations.All(operation => operation.State != ApplyState.Succeeded), Is.True);
            h.MutationResult = null;
            if (historical)
            {
                operationId = batch.Operations.First(ApplyJournal.HasUnresolvedDispatch).Id;
                await h.Workspace.SupersedeApplyAsync(batchId);
                await h.Workspace.PrepareHistoricalFieldAsync(new(batchId, null, operationId));
                Assert.That(h.Workspace.HistoricalFieldReview, Is.Not.Null, h.Workspace.Status);
                var decision = await h.Workspace.ConfirmHistoricalFieldAsync(h.Workspace.HistoricalFieldReview!, HistoricalFieldDecisionKind.ContinueChange);
                Assert.That(decision.AllLocalWorkSaved, Is.True, decision.Problem);
            }
        });
        var priorWrites = h.Writes.Select(write => write.GetRawText()).ToArray();
        await Ui.OpenHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        var command = historical ? "HistoricalContinue-" + operationId : "ResumeApplyBatch-" + batchId;
        await Ui.Until(() => Ui.Popup<Button>(command) is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Click(Ui.Find<Button>(command, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !h.Workspace.IsBusy && h.Workspace.ApplyReview is not null);
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(dialog.Title, Is.EqualTo("週次反映 — 実績・残工数"));
            Assert.That(h.Workspace.ApplyReview!.Batch.WeeklyEffort, Is.True);
            Assert.That(DisplayedValues(dialog), Is.EquivalentTo(new[] {
                "ApplyValue-P1-T1-Field-F-Actual", "ApplyValue-P1-T1-Field-F-Remaining",
                "ApplyValue-P1-T2-Field-F-Actual", "ApplyValue-P1-T2-Field-F-Remaining" }));
            Assert.That(h.Workspace.ApplyReview.Batch.Operations.Select(operation => operation.Key.FieldId).Distinct(),
                Is.EquivalentTo(new[] { "F-Actual", "F-Remaining" }));
            if (historical)
                Assert.That(h.Workspace.ApplyReview.Batch.Operations.Select(operation => operation.ItemId).Distinct(), Is.EqualTo(new[] { "P1-T1" }),
                    "Continuing one historical field must not preselect other changed weekly rows.");
            Assert.That(h.Writes.Select(write => write.GetRawText()), Is.EqualTo(priorWrites));
            Assert.That(h.Workspace.Drafts!.Workspace.Fields.Count(field => field.Key.Kind == "Title" && field.Change is not null), Is.EqualTo(2));
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null);
    }

    [Test, Category("WeeklyApply")]
    public async Task ReviewCommandsRemainFullyVisibleWhenTheWindowHeightChanges()
    {
        SizeInt32 originalSize = default;
        await Ui.Run(() => {
            originalSize = Ui.Window.AppWindow.Size;
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            Ui.Window.AppWindow.Resize(new((int)(1400 * scale), (int)(1000 * scale)));
        });
        try
        {
            await Ui.Until(() => Ui.Root.ActualHeight > 900);
            await OpenWeeklyReview();
            await AssertCommandsVisible();
            await Ui.Run(() => {
                var scale = Ui.Root.XamlRoot.RasterizationScale;
                Ui.Window.AppWindow.Resize(new((int)(1400 * scale), (int)(720 * scale)));
            });
            await Ui.Until(() => Ui.Root.ActualHeight < 720);
            await AssertCommandsVisible();
        }
        finally
        {
            await Ui.Run(() => {
                Ui.Dialog("ApplyReviewDialog")?.Hide();
                Ui.Window.AppWindow.Resize(originalSize);
            });
            await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null);
        }

        Task AssertCommandsVisible() => Ui.Run(async () => {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            dialog.UpdateLayout();
            await ApplyInformationEvidence.Capture(dialog, "weekly-review-commands-" + (int)Ui.Root.ActualHeight);
            foreach (var id in new[] { "ApplyCheckAgain", "ApplyReviewIdentity", "ApplyReviewHistory" })
            {
                var button = Ui.Find<Button>(id, dialog);
                Assert.That(button.IsLoaded && button.IsEnabled && button.ActualHeight > 0, Is.True, id);
                // A clipped child still reports its full ActualHeight. Compare
                // its rendered bounds against every containing visible surface.
                for (var parent = VisualTreeHelper.GetParent(button); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is not FrameworkElement { ActualWidth: > 0 } frame) continue;
                    var bounds = button.TransformToVisual(frame).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    var width = frame is ScrollViewer horizontal ? horizontal.ViewportWidth : frame.ActualWidth;
                    var height = frame is ScrollViewer vertical ? vertical.ViewportHeight : frame.ActualHeight;
                    if (frame is ContentPresenter or ScrollViewer)
                        Console.WriteLine($"{id}: {bounds}; visible={width}x{height}");
                    Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-1), id);
                    Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(height + 1),
                        $"{id} must be completely visible above the native dialog footer.");
                    Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1), id);
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(width + 1), id);
                }
                Assert.That(button.Focus(FocusState.Keyboard), Is.True, id);
            }
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.True);
            Assert.That(h.Writes, Is.Empty);
        });
    }

    private async Task OpenWeeklyReview()
    {
        await Ui.Ready<Button>("ReviewWeeklyApplyButton");
        await Ui.Run(() => Ui.Click("ReviewWeeklyApplyButton"));
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !h.Workspace.IsBusy && Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
    }

    private static string[] DisplayedValues(ContentDialog dialog) => Ui.Tree(dialog).OfType<TextBlock>()
        .Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("ApplyValue-", StringComparison.Ordinal)).ToArray();
}
