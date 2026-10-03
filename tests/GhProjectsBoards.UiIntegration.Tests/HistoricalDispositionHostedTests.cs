using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase), Category("HistoricalDisposition")]
public sealed class HistoricalDispositionHostedTests
{
    private CreationHarness h = null!;
    private RegistrationPanel panel = null!;
    private HistoricalFieldTarget target = null!;
    private Windows.Graphics.SizeInt32? originalWindowSize;
    private const string LongExpectedTitle = "Before review / compare the complete saved title with the intended title / the final words must remain readable BEFORE-END";
    private const string LongIntendedTitle = "After review / retain the complete intended title in the selected history record / the final words must remain readable AFTER-END";
    private EditingWorkspace Work => h.Session.Workspace;

    private async Task Mount(string origin = "superseded-existing", bool longDetails = false)
    {
        (h, target) = await HistoricalDispositionTests.UncertainHistory(origin);
        if (origin == "superseded-existing") h.Existing.Titles["I1"] = "Current external title";
        if (longDetails)
        {
            var snapshot = Work.Snapshot();
            ApplyOperation WithDiagnostic(ApplyOperation operation) => operation.Id != target.OperationId ? operation : operation with {
                Expected = operation.Key.Kind == "Title" ? LongExpectedTitle : operation.Expected,
                Intended = operation.Key.Kind == "Title" ? operation.Intended with { Value = LongIntendedTitle } : operation.Intended,
                Attempts = operation.Attempts.Select(attempt => attempt with {
                    Reason = attempt.Reason + " / " + string.Join(" / ", Enumerable.Range(1, 24).Select(line => $"保存された診断 {line}: 読み戻しで元の要求結果を確認できませんでした。")) + " / ATTEMPT-END"
                }).ToImmutableArray() };
            snapshot = snapshot with { Journal = snapshot.Journal!.Select(batch => batch with {
                Operations = batch.Operations.Select(WithDiagnostic).ToImmutableArray(),
                Creations = batch.Creations?.Select(creation => creation with { EarlierFields = creation.EarlierFields?.Select(WithDiagnostic).ToArray() }).ToArray()
            }).ToArray() };
            await new DraftStore(h.Existing.Root).SaveAsync(snapshot, snapshot.Revision);
            await h.Restart();
        }
        await Ui.Run(() => { panel = new RegistrationPanel(); panel.Initialize(h.Workspace); });
        await Ui.Mount(panel);
        await Ui.Until(() => Ui.Tree(panel).OfType<EditingGrid>().Any(grid => grid.IsLoaded));
    }

    [TearDown]
    public async Task Cleanup()
    {
        if (panel is not null)
        {
            await Ui.Run(() => {
                if (panel.XamlRoot is { } root)
                    foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
                        foreach (var dialog in Ui.Tree(popup.Child).OfType<ContentDialog>().ToArray()) dialog.Hide();
                return Task.CompletedTask;
            }, check: false);
            await Ui.Unmount(panel, check: false);
        }
        if (h is not null) await Ui.Run(async () => { await h.Workspace.StopAsync(); await h.Workspace.FlushDraftsAsync(); }, check: false);
        if (originalWindowSize is { } size) await Ui.Run(() => { Ui.Window.AppWindow.Resize(size); return Task.CompletedTask; }, check: false);
    }

    private async Task OpenReview()
    {
        bool missing = false;
        await Ui.Run(() => missing = Ui.Dialog("ApplyHistoryDialog") is null);
        if (missing) await OpenAvailableHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("HistoricalInspect-" + target.OperationId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.DialogReady("HistoricalFieldReviewDialog");
    }

    private static async Task OpenAvailableHistory()
    {
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton") is { IsLoaded: true, IsEnabled: true });
        await Ui.OpenHistory();
    }

    private async Task SizeHistoryWindow(string origin)
    {
        await Ui.Run(() => {
            originalWindowSize = Ui.Window.AppWindow.Size;
            Ui.Window.AppWindow.Resize(origin == "superseded-existing" ? new(1267, 794) : new(960, 600));
        });
    }

    private static void AssertFitsHorizontalViewport(FrameworkElement element)
    {
        double left = 0, right = element.XamlRoot.Size.Width;
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not FrameworkElement { ActualWidth: > 0 } ancestor) continue;
            var ancestorBounds = ancestor.TransformToVisual(null).TransformBounds(new(0, 0, ancestor.ActualWidth, ancestor.ActualHeight));
            left = Math.Max(left, ancestorBounds.Left); right = Math.Min(right, ancestorBounds.Right);
        }
        var bounds = element.TransformToVisual(null).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(left - 1), "History content begins outside its visible viewport.");
        Assert.That(bounds.Right, Is.LessThanOrEqualTo(right + 1),
            $"History content extends past its actual visible viewport: {element.GetType().Name} bounds={bounds}; visible=({left}, {right}).");
    }

    private static void AssertHistoryBodyFitsVisibleWidth(ScrollViewer evidence)
    {
        AssertFitsHorizontalViewport(evidence);
        var body = (FrameworkElement)evidence.Content;
        AssertFitsHorizontalViewport(body);
        Assert.That(evidence.ScrollableWidth, Is.LessThanOrEqualTo(1), "Reading full history must not require unavailable horizontal scrolling.");
        var texts = Ui.Tree(body).OfType<TextBlock>().Where(text => text.IsLoaded && text.ActualHeight > 0 && text.Text.Length > 0).ToArray();
        foreach (var text in texts)
        {
            AssertFitsHorizontalViewport(text);
            Assert.That(text.IsTextTrimmed, Is.False, "The evidence body must retain complete values and explanations.");
            // Native text layout, including the last character, must fit its allocated box.
            // Merely finding the complete Text property cannot detect clipped rendering.
            var first = text.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var last = text.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            Assert.That(first.Height, Is.GreaterThan(0)); Assert.That(last.Height, Is.GreaterThan(0));
            Assert.That(last.Left, Is.GreaterThanOrEqualTo(-1));
            Assert.That(last.Right, Is.LessThanOrEqualTo(text.ActualWidth + 1));
            Assert.That(last.Bottom, Is.LessThanOrEqualTo(text.ActualHeight + 1));
            if (text.Text.Contains("BEFORE-END") || text.Text.Contains("AFTER-END") || text.Text.Contains("ATTEMPT-END"))
            {
                Assert.That(text.Text, Does.Not.Contain("\n"), "This fixture must establish automatic wrapping, not explicit line breaks.");
                Assert.That(last.Top, Is.GreaterThan(first.Top), "The complete long value or diagnostic must wrap onto subsequent rendered lines.");
            }
        }
        Assert.That(texts.Any(text => text.Text.StartsWith("比較に使用したGitHubの値:")), Is.True);
        Assert.That(texts.Any(text => text.Text.StartsWith("当時の反映予定値:")), Is.True);
        Assert.That(texts.Any(text => text.Text.EndsWith("ATTEMPT-END")), Is.True);
    }

    [TestCase("superseded-existing"), TestCase("completed-retired"), Category("HistoryLayout")]
    public async Task ReadingLongHistoryKeepsTheSelectedTargetAndActionWhileEvidenceScrolls(string origin)
    {
        await SizeHistoryWindow(origin);
        await Mount(origin, longDetails: true);
        var before = Work.Snapshot(); var writes = h.Writes.Select(write => write.Input.GetRawText()).ToArray();
        var source = HistoricalFieldHandling.Resolve(Work.Journal, target)!;
        await OpenAvailableHistory(); await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ApplyOperationDetails-" + target.OperationId, Ui.Dialog("ApplyHistoryDialog"))));
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { IsLoaded: true });
        await Ui.Run(() => Ui.Find<Expander>("ApplyOperationEvidence-" + target.OperationId, Ui.Dialog("ApplyHistoryDialog")).IsExpanded = true);
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { ScrollableHeight: > 0 });
        await SheetNativeInput.ActivateWindow();
        ScrollViewer evidence = null!;
        void AssertContext()
        {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            var identity = Ui.Find<TextBlock>("ApplyHistoryTarget", dialog);
            var context = Ui.Find<TextBlock>("ApplyHistoryContext", dialog);
            var action = Ui.Find<Button>("HistoricalInspect-" + target.OperationId, dialog);
            Assert.That(identity.Text, Does.Contain(ApplyResultsPresentation.Identity(source.Operation)));
            Assert.That(context.Text, Does.Contain(source.Batch.ProjectName));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(action),
                Does.Contain(ApplyResultsPresentation.Identity(source.Operation)).And.Contain(source.Operation.FieldName));
            foreach (var element in new FrameworkElement[] { identity, context, action,
                Ui.Tree(dialog).OfType<Button>().Single(b => b.Name == "SecondaryButton"), Ui.Tree(dialog).OfType<Button>().Single(b => b.Name == "CloseButton") })
            {
                var position = element.TransformToVisual(null).TransformPoint(new());
                Assert.That(position.Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(position.Y + element.ActualHeight, Is.LessThanOrEqualTo(dialog.XamlRoot.Size.Height),
                    "Reading evidence must keep the selected target, recovery and navigation inside the window.");
            }
        }
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            evidence = Ui.Find<ScrollViewer>("ApplyHistoryEvidence", dialog);
            Assert.That(Ui.Tree(evidence.Content as DependencyObject ?? evidence).OfType<ScrollViewer>(), Is.Empty,
                "The selected record has one evidence scrolling region.");
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("比較に使用したGitHubの値").And.Contain("当時の反映予定値").And.Contain("確認結果"));
            AssertContext(); await ApplyInformationEvidence.Capture(dialog, "history-detail-top-" + origin);
            AssertHistoryBodyFitsVisibleWidth(evidence);
            evidence.ChangeView(null, evidence.ScrollableHeight, null, true);
        });
        await Ui.Until(() => Math.Abs(evidence.VerticalOffset - evidence.ScrollableHeight) < 0.5);
        await Ui.Run(async () => {
            AssertContext();
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "history-detail-end-" + origin);
            AssertHistoryBodyFitsVisibleWidth(evidence);
            evidence.ChangeView(null, 0, null, true);
        });
        await Ui.Until(() => evidence.VerticalOffset < 0.5);
        await Ui.Run(() => {
            AssertContext();
            var action = Ui.Find<Button>("HistoricalInspect-" + target.OperationId, Ui.Dialog("ApplyHistoryDialog"));
            Assert.That(action.Focus(FocusState.Keyboard), Is.True);
            Assert.That(FocusManager.GetFocusedElement(action.XamlRoot), Is.SameAs(action));
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(JsonSerializer.Serialize(before)));
        });
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.DialogReady("HistoricalFieldReviewDialog");
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("HistoricalFieldReviewDialog"), Does.Contain(source.Operation.Key.Kind == "Title" ? "タイトル" : source.Operation.FieldName));
            Ui.DialogButton("HistoricalFieldReviewDialog", "CloseButton");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => { AssertContext(); Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton"); });
        await Ui.Until(() => Ui.Popup<Button>("ApplyOperationDetails-" + target.OperationId) is { IsLoaded: true });
        await Ui.Run(() => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            var detail = Ui.Find<Button>("ApplyOperationDetails-" + target.OperationId, dialog);
            AssertFitsHorizontalViewport(Ui.Find<Button>("ApplyHistoryHelp", dialog));
            Assert.That(Ui.Find<CheckBox>("ApplyShowAllHistory", dialog).IsChecked, Is.False);
            Assert.That(FocusManager.GetFocusedElement(dialog.XamlRoot), Is.SameAs(detail));
        });
        var after = Work.Snapshot();
        Assert.Multiple(() => {
            Assert.That(JsonSerializer.Serialize(after.Journal), Is.EqualTo(JsonSerializer.Serialize(before.Journal)));
            Assert.That(JsonSerializer.Serialize(after.Fields), Is.EqualTo(JsonSerializer.Serialize(before.Fields)));
            Assert.That(JsonSerializer.Serialize(after.History), Is.EqualTo(JsonSerializer.Serialize(before.History)));
            Assert.That(Work.HistoricalDispositions, Is.Empty);
            Assert.That(h.Writes.Select(write => write.Input.GetRawText()), Is.EqualTo(writes));
        });
    }

    [TestCase("superseded-existing"), TestCase("completed-retired"), Category("HistoryLayout")]
    public async Task HistoryCanDeferThenFinishOnlyHistoricalHandlingWhileOriginalEvidenceAndInputRemain(string origin)
    {
        await SizeHistoryWindow(origin);
        await Mount(origin, longDetails: true);
        await Ui.Run(async () => {
            var field = Work.Open(h.Workspace.Selected!).Single(row => row.ItemId == "P1-T2").Cells[0];
            Work.Commit("P1", field, "Independent accepted title");
            Work.SetBuffer(field, "Independent unfinished title");
            Assert.That(await h.Workspace.FlushDraftsAsync(), Is.True);
        });
        var before = Work.Snapshot();
        var writes = h.Writes.Select(w => w.Input.GetRawText()).ToArray();
        await OpenReview();
        await Ui.Run(async () => {
            Assert.That(Ui.DialogText("HistoricalFieldReviewDialog"), Does.Contain("当時の送信結果：不明").And.Contain("今回確認したGitHubの値"));
            await ApplyInformationEvidence.Capture(Ui.Dialog("HistoricalFieldReviewDialog")!, "historical-review-" + origin);
            Ui.DialogButton("HistoricalFieldReviewDialog", "CloseButton");
        });
        await Ui.DialogReady("ApplyHistoryDialog");
        Assert.That(Work.HistoricalDispositions, Is.Empty);
        Assert.That(Work.HasUnresolvedApply, Is.True);
        await OpenReview();
        await Ui.Run(() => Ui.DialogButton("HistoricalFieldReviewDialog", "PrimaryButton"));
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(async () => {
            Assert.That(Ui.Find<TextBlock>("ApplyHistoryTarget", Ui.Dialog("ApplyHistoryDialog")).Text,
                Does.Contain(ApplyResultsPresentation.Identity(HistoricalFieldHandling.Resolve(Work.Journal, target)!.Operation)));
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("対応は完了").And.Contain("元の送信結果は不明のまま"));
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            var evidence = Ui.Find<ScrollViewer>("ApplyHistoryEvidence", dialog);
            var decisions = Ui.Find<Expander>("HistoricalDecisionDetails-" + target.OperationId, dialog);
            decisions.IsExpanded = true;
            Ui.Find<Expander>("ApplyOperationEvidence-" + target.OperationId, dialog).IsExpanded = true;
            await ApplyInformationEvidence.Capture(dialog, "history-handled-expanded-" + origin);
            AssertHistoryBodyFitsVisibleWidth(evidence);
            evidence.ChangeView(null, evidence.ScrollableHeight, null, true);
        });
        await Ui.Until(() => Ui.Popup<ScrollViewer>("ApplyHistoryEvidence") is { } evidence && Math.Abs(evidence.VerticalOffset - evidence.ScrollableHeight) < 0.5);
        await Ui.Run(async () => {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            await ApplyInformationEvidence.Capture(dialog, "history-handled-expanded-end-" + origin);
            AssertHistoryBodyFitsVisibleWidth(Ui.Find<ScrollViewer>("ApplyHistoryEvidence", dialog));
            Ui.DialogButton("ApplyHistoryDialog", "SecondaryButton");
        });
        await Ui.Until(() => Ui.Popup<CheckBox>("ApplyShowAllHistory") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("対応が必要な項目はありません"));
            Ui.Toggle(Ui.Find<CheckBox>("ApplyShowAllHistory", Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.Until(() => Ui.DialogText("ApplyHistoryDialog").Contains("元の送信結果は不明のまま"));
        await Ui.Run(async () => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("元の送信結果は不明のまま").And.Contain("対応は完了"));
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyHistoryDialog")!, "historical-completed-" + origin);
        });
        var after = Work.Snapshot();
        Assert.Multiple(() => {
            Assert.That(JsonSerializer.Serialize(after.Journal), Is.EqualTo(JsonSerializer.Serialize(before.Journal)));
            Assert.That(JsonSerializer.Serialize(after.Fields), Is.EqualTo(JsonSerializer.Serialize(before.Fields)));
            Assert.That(JsonSerializer.Serialize(after.History), Is.EqualTo(JsonSerializer.Serialize(before.History)));
            Assert.That(h.Writes.Select(w => w.Input.GetRawText()), Is.EqualTo(writes));
            Assert.That(Work.HasUnresolvedApply, Is.False);
        });
    }

    [Test]
    public async Task ChangedObservationReturnsToTheVisibleChoiceAndFailedLocalSaveCanBeRetried()
    {
        await Mount();
        await OpenReview();
        h.Existing.Titles["I1"] = "Changed while reviewing";
        await Ui.Run(() => Ui.DialogButton("HistoricalFieldReviewDialog", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("HistoricalFieldReviewDialog") is { IsLoaded: true }
            && Ui.DialogText("HistoricalFieldReviewDialog").Contains("Changed while reviewing"));
        Assert.That(Work.HistoricalDispositions, Is.Empty);
        await Ui.Run(() => Assert.That(Ui.DialogText("HistoricalFieldReviewDialog"), Does.Contain("もう一度判断")));
        using (var writer = new FileStream(Path.Combine(h.Existing.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.DialogButton("HistoricalFieldReviewDialog", "PrimaryButton"));
            await Ui.Until(() => Ui.Dialog("HistoricalFieldReviewDialog") is { IsLoaded: true }
                && Ui.Tree(Ui.Dialog("HistoricalFieldReviewDialog")!).OfType<TextBlock>().Any(text =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(text) == "HistoricalReviewFeedback"
                    && text.Text.Contains("保存")));
            Assert.That(Work.HistoricalDispositions, Is.Empty);
            Assert.That(Work.HasUnresolvedApply, Is.True);
        }
        await Ui.Run(() => Ui.DialogButton("HistoricalFieldReviewDialog", "PrimaryButton"));
        await Ui.DialogReady("ApplyHistoryDialog");
        Assert.That(Work.HistoricalDispositions.Single().Observation.Current!.Value, Is.EqualTo("Changed while reviewing"));
        Assert.That(Work.HasUnresolvedApply, Is.False);
    }

    [Test]
    public async Task ContinueOpensANewCurrentReviewAndCancellationKeepsItsHistoryEntryReachable()
    {
        await Mount();
        await Ui.Run(async () => {
            var row = Work.Open(h.Workspace.Selected!).Single(row => row.ItemId == "P1-T1");
            Work.Commit("P1", row.Cells[0], "New confirmed title");
            Work.SetBuffer(row.Cells[0], "Unfinished title must not be sent");
            Assert.That(await h.Workspace.FlushDraftsAsync(), Is.True);
        });
        var writes = h.Writes.Select(w => w.Input.GetRawText()).ToArray();
        await OpenReview();
        await Ui.Run(() => Ui.DialogButton("HistoricalFieldReviewDialog", "SecondaryButton"));
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !h.Workspace.IsBusy);
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("現在の確定済み入力").And.Contain("未確定入力は送りません"));
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null);
        await OpenAvailableHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("新しい反映はまだ完了していません"));
            Ui.Click(Ui.Find<Button>("HistoricalContinue-" + target.OperationId, Ui.Dialog("ApplyHistoryDialog")));
        });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !h.Workspace.IsBusy);
        Assert.Multiple(() => {
            Assert.That(Work.HistoricalDispositions.Single().Kind, Is.EqualTo(HistoricalFieldDecisionKind.ContinueChange));
            Assert.That(Work.HistoricalDispositions.Single().FollowUp, Is.Null);
            Assert.That(Work.HasUnresolvedApply, Is.True);
            Assert.That(Work.Fields.Single(field => field.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("Unfinished title must not be sent"));
            Assert.That(h.Writes.Select(w => w.Input.GetRawText()), Is.EqualTo(writes));
        });
    }
}
