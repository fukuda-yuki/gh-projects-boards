using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(960, 600), TestCase(1267, 794), Category("ApplyPublicationPresentation")]
    public async Task ReviewInformationPopupKeepsItsCompleteExplanationWithinVisibleBounds(int width, int height)
    {
        Windows.Graphics.SizeInt32? previous = null;
        Flyout information = null!;
        TextBlock text = null!;
        try
        {
            await Ui.Run(() => {
                previous = Ui.Window.AppWindow.Size;
                Ui.Window.AppWindow.Resize(new(width, height));
                Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Reviewed title");
                Ui.Click("ReviewApplyButton");
            });
            await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
            string before = "";
            await Ui.Run(() => {
                before = System.Text.Json.JsonSerializer.Serialize(Work.Snapshot());
                var button = Ui.Find<Button>("ApplyReviewIdentity", Ui.Dialog("ApplyReviewDialog"));
                information = (Flyout)button.Flyout; text = (TextBlock)information.Content;
                Ui.Click(button);
            });
            await Ui.Until(() => information.IsOpen && text.IsLoaded && text.ActualWidth > 0 && text.ActualHeight > 0);
            await SheetNativeInput.Rendered();
            await Ui.Run(async () => {
                Assert.That(text.Text, Does.Contain("最終反映ボタンを押すまで送信しません").And.Contain("送信直前にも対象・値・権限を再確認します。"));
                var bounds = text.TransformToVisual(null).TransformBounds(new(0, 0, text.ActualWidth, text.ActualHeight));
                double left = 0, right = text.XamlRoot.Size.Width, top = 0, bottom = text.XamlRoot.Size.Height;
                for (var parent = VisualTreeHelper.GetParent(text); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is not FrameworkElement { ActualWidth: > 0 } ancestor) continue;
                    var visible = ancestor.TransformToVisual(null).TransformBounds(new(0, 0, ancestor.ActualWidth, ancestor.ActualHeight));
                    left = Math.Max(left, visible.Left); right = Math.Min(right, visible.Right);
                    top = Math.Max(top, visible.Top); bottom = Math.Min(bottom, visible.Bottom);
                }
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(left - 1));
                Assert.That(bounds.Right, Is.LessThanOrEqualTo(right + 1), "The information text must fit every containing visible surface, not merely the dialog width.");
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(top - 1));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(bottom + 1));
                var last = text.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                Assert.That(last.Height, Is.GreaterThan(0));
                Assert.That(last.Right, Is.LessThanOrEqualTo(text.ActualWidth + 1));
                Assert.That(last.Bottom, Is.LessThanOrEqualTo(text.ActualHeight + 1));
                Assert.That(text.IsTextTrimmed, Is.False);
                // Render the opened flyout's content; native popup roots can return an empty bitmap.
                await ApplyInformationEvidence.Capture(text, $"review-information-popup-content-{width}x{height}");
                Assert.That(System.Text.Json.JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
                Assert.That(h.Writes, Is.Empty);
                information.Hide(); Ui.DialogButton("ApplyReviewDialog", "CloseButton");
            });
        }
        finally
        {
            await Ui.Run(() => {
                information?.Hide(); Ui.Dialog("ApplyReviewDialog")?.Hide();
                if (previous is { } size) Ui.Window.AppWindow.Resize(size);
                return Task.CompletedTask;
            }, check: false);
        }
    }

    [TestCase(401, true), TestCase(503, false)]
    public async Task FailedLatestCheckShowsRecoveryForAuthenticationOnly(int httpStatus, bool needsConnection)
    {
        var boundary = h.Existing.Boundary.Override!;
        h.Existing.Boundary.Override = (query, variables) => query.Contains("ProjectFields") ? ScriptedRunner.Http("{}", httpStatus) : boundary(query, variables);
        await Ui.Run(() => { Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Preserved outgoing"); Ui.Click("ReviewApplyButton"); });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(async () =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            Assert.That(dialog.PrimaryButtonText, Does.Not.Contain("0件").And.Not.Contain("確認中"));
            Assert.That(Ui.Find<TextBlock>("ApplyCheckStatus", dialog).Text, Does.Contain("最新状態を確認できません").And.Contain("保存済み・最新未確認"));
            Assert.That(Ui.Find<Button>("ApplyConnectionSettings", dialog).Visibility, Is.EqualTo(needsConnection ? Visibility.Visible : Visibility.Collapsed));
            Assert.That(Ui.Find<Button>("ApplyCheckAgain", dialog).IsEnabled, Is.True);
            Assert.That(Ui.Find<TextBlock>("ApplyTargetCounts", dialog).Text, Is.EqualTo("1件中1件を選択"));
            Assert.That(h.Writes, Is.Empty);
            await ApplyInformationEvidence.Capture(dialog, "check-failed-" + httpStatus);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }

    [Test]
    public async Task LatestCheckCompletesWithOpenDetailsWithoutResettingPartialSelectionOrViewport()
    {
        await UseReviewRows(23); await ControlExternal("ProjectFields"); gate!.Armed = false;
        await Ui.Run(() => { foreach (var row in Work.Open(Workspace.Selected!)) Work.Commit("P1", row.Cells[1], "done", true); Ui.Click("ReviewApplyButton"); });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        ListView list = null!; ScrollViewer scroll = null!; Button details = null!; double position = 0;
        await Ui.Run(() =>
        {
            list = Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog"));
            list.SelectedItems.Add(list.Items[10]);
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() => list.ScrollIntoView(list.Items[10], ScrollIntoViewAlignment.Leading));
        await Ui.Until(() => list.ContainerFromIndex(10) is FrameworkElement { IsLoaded: true } row
            && Ui.Tree(list).OfType<ScrollViewer>().First().VerticalOffset > 0
            && row.TransformToVisual(list).TransformPoint(new()).Y >= 0 && row.TransformToVisual(list).TransformPoint(new()).Y < list.ActualHeight);
        await Ui.Run(() =>
        {
            scroll = Ui.Tree(list).OfType<ScrollViewer>().First(); position = scroll.VerticalOffset;
            gate.Armed = true; Ui.Click(Ui.Find<Button>("ApplyCheckAgain", Ui.Dialog("ApplyReviewDialog")));
        });
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Run(() => { details = Ui.Find<Button>("ApplyDetails-P1-T11", list); details.Focus(FocusState.Keyboard); Ui.Click(details); });
        await Ui.Until(() => details.Flyout.IsOpen);
        gate.Release.TrySetResult();
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            Assert.That(details.Flyout.IsOpen, Is.True);
            Assert.That(list.SelectedItems.Cast<ApplyConfirmationRow>().Select(r => r.Data.Id), Is.EqualTo(new[] { "P1-T11" }));
            Assert.That(scroll.VerticalOffset, Is.EqualTo(position).Within(1));
            Assert.That(Workspace.ApplyReview!.Batch.Operations.Single().ItemId, Is.EqualTo("P1-T11"));
            details.Flyout.Hide();
        });
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), details));
        await Ui.Run(async () =>
        {
            Assert.That(scroll.VerticalOffset, Is.EqualTo(position).Within(1)); Assert.That(h.Writes, Is.Empty);
            await ApplyInformationEvidence.Capture(Ui.Dialog("ApplyReviewDialog")!, "latest-check-retains-partial-selection");
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }

    private async Task UseReviewRows(int count)
    {
        await Ui.Unmount(panel);
        await Workspace.StopAsync(); Assert.That(await Workspace.FlushDraftsAsync(), Is.True);
        h = await CreationHarness.Create(count);
        h.Existing.ChangeResponse = (query, json) =>
        {
            if (!query.Contains("ProjectItems")) return;
            foreach (var item in json["data"]!["node"]!["items"]!["nodes"]!.AsArray())
            {
                item!["content"]!["repository"]!["nameWithOwner"] = "sample-user/first";
                item["content"]!["repository"]!["id"] = "R-first";
            }
        };
        await Workspace.PrepareApplyAsync(new HashSet<string>());
        await Ui.Run(() => { panel = new RegistrationPanel(); panel.Initialize(Workspace); });
        await Ui.Mount(panel);
        await Ui.Ready<TextBox>("GridCell0_0");
    }

    [Test]
    public async Task LastRowConflictIsDiscoverableFromTheTopAndJumpRetainsSelection()
    {
        await UseReviewRows(23);
        await Ui.Run(() =>
        {
            foreach (var row in Work.Open(Workspace.Selected!)) Work.Commit("P1", row.Cells[0], "Local " + row.ItemId);
            h.Existing.Titles["I23"] = "Remote last row";
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => !Workspace.IsBusy && Workspace.ApplyReview?.Problems.Length > 0);
        ListView list = null!;
        await Ui.Run(async () =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!; list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(Ui.Find<TextBlock>("ApplyBlockReason", dialog).Text, Does.Contain("1件に要対応"));
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            var scroll = Ui.Tree(list).OfType<ScrollViewer>().First(); Assert.That(scroll.VerticalOffset, Is.Zero);
            await ApplyInformationEvidence.Capture(dialog, "conflict-from-top");
            Ui.Click(Ui.Find<Button>("ApplyGoToProblem", dialog));
        });
        await Ui.Until(() => list.ContainerFromIndex(22) is FrameworkElement { IsLoaded: true } row
            && row.TransformToVisual(list).TransformPoint(new()).Y >= 0 && row.TransformToVisual(list).TransformPoint(new()).Y < list.ActualHeight);
        await Ui.Run(async () =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(FocusManager.GetFocusedElement(list.XamlRoot), Is.SameAs(list.ContainerFromIndex(22)));
            Assert.That(list.SelectedItems.Count, Is.EqualTo(23));
            Assert.That(Ui.Find<TextBlock>("ApplyValue-P1-T23-Title-", dialog).Text, Does.Contain("Remote last row").And.Contain("Local P1-T23"));
            await ApplyInformationEvidence.Capture(dialog, "conflict-jumped-to-last");
            Ui.Click(Ui.Find<Button>("ApplyResolve-P1-T23-Title-Local", dialog));
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            Assert.That(Workspace.ApplyReview!.IssueCount, Is.EqualTo(23)); Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }

    [Test]
    public async Task HiddenZeroHasNoSupplementAndAddingThreeHiddenCandidatesDoesNotSelectThem()
    {
        await UseReviewRows(4);
        await Ui.Run(() => { foreach (var row in Work.Open(Workspace.Selected!)) Work.Commit("P1", row.Cells[1], "done", true); Ui.Click("ReviewApplyButton"); });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(((FrameworkElement)Ui.Find<CheckBox>("ApplyIncludeHidden", dialog).Parent).Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBlock>("ApplyHiddenSummary", dialog).Text, Is.Empty);
            Assert.That(Ui.Find<Button>("ApplyConnectionSettings", dialog).Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null); await Ui.Idle();
        await Ui.Run(async () =>
        {
            var p = Workspace.Selected!;
            await Workspace.Drafts!.CommitAsync(w => { w.SaveRowView(w.PrepareRowView(p) with { Definition = new(Title: "Issue 1") }); return w; }, () => true);
            Ui.Click("GridReapply"); Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(async () =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(Ui.Find<TextBlock>("ApplyHiddenSummary", dialog).Text, Does.Contain("3件は候補から除外中"));
            Assert.That(Ui.Find<ListView>("ApplyTargetRows", dialog).Items.Count, Is.EqualTo(1));
            await ApplyInformationEvidence.Capture(dialog, "three-hidden-excluded");
            Ui.Toggle(Ui.Find<CheckBox>("ApplyIncludeHidden", dialog));
        });
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Until(() => Ui.Find<ListView>("ApplyTargetRows", Ui.Dialog("ApplyReviewDialog")).ContainerFromIndex(3)
            is FrameworkElement { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(async () =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!; var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(list.Items.Count, Is.EqualTo(4)); Assert.That(list.SelectedItems, Is.Empty);
            var last = (FrameworkElement)list.ContainerFromIndex(3);
            Assert.That(last.TransformToVisual(list).TransformPoint(new()).Y + last.ActualHeight, Is.LessThanOrEqualTo(list.ActualHeight));
            Assert.That(Ui.Find<TextBlock>("ApplyHiddenSummary", dialog).Text, Does.Contain("追加済み（0件を選択）"));
            await ApplyInformationEvidence.Capture(dialog, "three-hidden-added-unselected");
            list.SelectedItems.Add(list.Items[3]);
        });
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(Ui.Find<TextBlock>("ApplyHiddenSummary", dialog).Text, Does.Contain("追加済み（1件を選択）"));
            Assert.That(Workspace.ApplyReview!.Batch.Operations.Single().ItemId, Is.EqualTo("P1-T4"));
            Assert.That(h.Writes, Is.Empty); Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }

    [Test]
    public async Task UnselectedIssueShowsItsShortDifferenceWithoutAnotherList()
    {
        await Ui.Run(() =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[1], "done", true);
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() =>
        {
            var dialog = Ui.Dialog("ApplyReviewDialog")!;
            var list = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(list.SelectedItems, Is.Empty);
            var values = Ui.Tree(list).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.That(values, Has.Some.Contains("Todo → Done"), "An unselected Issue must expose both values in its selection row.");
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }
}
