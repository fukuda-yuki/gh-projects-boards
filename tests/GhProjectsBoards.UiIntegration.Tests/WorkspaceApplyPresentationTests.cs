using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test]
    public async Task UncertainCreationHistoryShowsDestinationStageAndContextualResolutionWithoutRetrying()
    {
        string local = "", attempt = "";
        await Ui.Run(async () =>
        {
            local = h.Add("Investigate deployment", "sample-user/first");
            h.LoseCreate = true;
            await h.Apply(local);
            attempt = Work.Creations.Single().Id;
        });
        await Ui.OpenHistory();
        await Ui.DialogReady("ApplyHistoryDialog");
        await Ui.Run(() =>
        {
            var dialog = Ui.Dialog("ApplyHistoryDialog")!;
            Assert.That(Ui.DialogText("ApplyHistoryDialog"), Does.Contain("Investigate deployment")
                .And.Contain("sample-user/first").And.Contain("作成結果が不確定")
                .And.Contain("Issue確認").And.Contain("Project所属").And.Contain(local));
            Ui.Click(Ui.Find<Button>("ResolveCreation-" + attempt, dialog));
        });
        await Ui.DialogReady("CreationResolutionDialog");
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("CreationResolutionDialog"), Does.Contain("作成済みの可能性があります"));
            Ui.DialogButton("CreationResolutionDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.ProjectCommand("ApplyHistoryButton").IsEnabled);
        await Ui.Idle();
        Assert.That(Work.Creations.Single().Verified, Is.Null);
        Assert.That(Work.Creations.Single().Dispatched, Is.True);
        Assert.That(h.Issues.Count, Is.EqualTo(1), "Inspecting and keeping the uncertain attempt must not create another Issue.");
    }
}

[TestFixture, NonParallelizable]
public sealed class ComparisonPresentationTests
{
    [TestCase("present")]
    [TestCase("empty")]
    [TestCase("unavailable")]
    public async Task ComparisonDistinguishesLocalClearKnownEmptyAndUnconfirmedRemoteWithoutChangingDraft(string remoteState)
    {
        var previous = EditingTests.Registration(count: 1);
        var work = new EditingWorkspace(previous.Snapshot.Id.Scope);
        work.SetRegistrations([previous]);
        var cell = work.Open(previous)[0].Cells[1];
        if (remoteState == "empty") work.Commit("P1", cell, "Done");
        else work.Clear("P1", [cell]);
        var current = ReconciliationTests.Remote(previous, "Issue 1", remoteState == "empty" ? null : "done");
        if (remoteState == "unavailable") current = current with { Snapshot = current.Snapshot with {
            Items = current.Snapshot.Items.Select(i => i with {
                Values = i.Values.Select(v => v with { Availability = ValueAvailability.Unavailable, OptionId = null }).ToArray()
            }).ToArray()
        } };
        work.Reconcile(previous, current); work.SetRegistrations([current]);
        var expected = work.Fields.Single(f => f.Key == cell.Key).Change;
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-comparison-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(current, session, () => Task.FromResult(true)));
        await Ui.Mount(grid);
        try
        {
            TextBox editor = null!;
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                editor = Ui.Find<TextBox>("GridCell0_0");
                Assert.That(editor.Focus(FocusState.Keyboard), Is.True);
                editor.Text = "比較後も保持する未確定文字";
                editor.Select(2, 0);
            });
            await Ui.ClickCommand("GridConflicts", focus: true);
            await Ui.DialogReady("ConflictDialog");
            await Ui.Run(() =>
            {
                var dialog = Ui.Dialog("ConflictDialog")!;
                Assert.That(Ui.Find<TextBlock>("ConflictBaselineValue", dialog).Text, Is.EqualTo("Todo [ID: todo]"));
                Assert.That(Ui.Find<TextBlock>("ConflictLocalValue", dialog).Text,
                    Is.EqualTo(remoteState == "empty" ? "Done [ID: done]" : "明示的にクリア"));
                Assert.That(Ui.Find<TextBlock>("ConflictRemoteValue", dialog).Text, Is.EqualTo(remoteState switch {
                    "empty" => "（明示的な空値）", "unavailable" => "未確認（閲覧不可）", _ => "Done [ID: done]"
                }));
                var comparison = Ui.Find<ContentControl>("ConflictComparison", dialog);
                Assert.That(comparison.IsLoaded && comparison.ActualHeight > 0, Is.True);
                Assert.That(AutomationProperties.GetName(comparison), Does.Contain("B 基準:").And.Contain("L ローカル:").And.Contain("R GitHub:"));
                var canResolve = remoteState != "unavailable";
                Assert.That(dialog.IsPrimaryButtonEnabled, Is.EqualTo(canResolve));
                Assert.That(dialog.IsSecondaryButtonEnabled, Is.EqualTo(canResolve));
                Assert.That(Ui.Find<Button>("ConflictUseAlternative", dialog).IsEnabled, Is.EqualTo(canResolve));
                Assert.That(Ui.Find<ComboBox>("ConflictAlternativeOption", dialog).IsEnabled, Is.EqualTo(canResolve));
                Ui.DialogButton("ConflictDialog", "CloseButton");
            });
            await Ui.Until(() => Ui.Dialog("ConflictDialog") is null);
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), editor));
            await Ui.Run(() =>
            {
                Assert.That(editor.Text, Is.EqualTo("比較後も保持する未確定文字"));
                Assert.That(editor.SelectionStart, Is.EqualTo(2));
                Assert.That(editor.SelectionLength, Is.Zero);
                Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo(editor.Text));
            });
            await Ui.Idle();
            Assert.That(session.Workspace.Fields.Single(f => f.Key == cell.Key).Change, Is.EqualTo(expected));
        }
        finally
        {
            await Ui.Run(() => Ui.Dialog("ConflictDialog")?.Hide());
            await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            await Ui.Idle();
        }
    }
}
