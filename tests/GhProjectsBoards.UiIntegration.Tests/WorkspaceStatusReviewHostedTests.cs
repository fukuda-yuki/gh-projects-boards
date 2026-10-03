using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("WorkspaceStatusReview")]
    public async Task OfflineMissingFieldPendingOnlyStatusOpensLocalReviewWithoutRetargetingOrSending()
    {
        await Ui.Unmount(panel);
        var previous = Workspace.Selected!;
        var originalRow = Work.Open(previous)[0];
        var originalCell = originalRow.Cells.Single(cell => cell.Key?.FieldId == "P1-status");
        var originalField = previous.Snapshot.Fields.Single(field => field.Id.NodeId == originalCell.Key!.FieldId);
        var item = previous.Snapshot.Items.Single(value => value.Id.NodeId == originalRow.ItemId);
        var issue = previous.Snapshot.Issues[item.ContentId!];
        var replacement = originalField with { Id = new(previous.Snapshot.Id.Scope, "P1-same-name-replacement") };
        var current = previous with {
            RetrievedAt = previous.RetrievedAt.AddSeconds(1),
            Snapshot = previous.Snapshot with {
                Fields = previous.Snapshot.Fields.Select(field => field.Id == originalField.Id ? replacement : field).ToArray(),
                Items = previous.Snapshot.Items.Select(value => value with {
                    Values = value.Values.Select(field => field.FieldId == originalField.Id
                        ? field with { FieldId = replacement.Id, ValueId = "replacement-" + field.ValueId } : field).ToArray()
                }).ToArray()
            }
        };
        Assert.That(replacement.Name, Is.EqualTo(originalField.Name));
        Assert.That(await Workspace.Drafts!.CommitAsync(work => {
            work.SetBuffer(originalCell, "Q");
            work.Reconcile(previous, current);
            work.SetRegistrations(Workspace.Registrations.Select(project => project.Snapshot.Id == current.Snapshot.Id ? current : project));
            work.Open(current);
            return work;
        }, () => true), Is.True);
        await Workspace.StopAsync();

        // Reopen the real checkpoint without connecting. Fixture reconciliation
        // creates the unavailable observation; the UI must not fabricate one.
        h.Existing.Workspace = new(new RegistrationStore(h.Existing.Root));
        await Workspace.RestoreAsync();
        await Workspace.SelectProfileAsync(current.Snapshot.Id.Scope);
        Assert.That(await Workspace.SelectAsync(current.Snapshot.Id), Is.True);
        await Ui.Run(() => { panel = new RegistrationPanel(); panel.Initialize(Workspace); });
        await Ui.Mount(panel);
        await Ui.Ready<Button>("WorkspaceUnpublished");
        await Ui.Until(() => Workspace.Drafts!.DurableRevision == Work.Revision);
        var before = JsonSerializer.Serialize(Work.Snapshot());
        var store = new DraftStore(h.Existing.Root);
        var checkpoint = await File.ReadAllBytesAsync(store.FileFor(Work.Scope));
        await Ui.Run(() => {
            Assert.That(Workspace.CanRead, Is.False);
            Assert.That(Work.Fields.Where(field => field.Change is not null), Is.Empty);
            Assert.That(Work.Field(originalCell)!.Buffer, Is.EqualTo("Q"));
            Assert.That(Work.Field(originalCell)!.Observation?.Reason, Does.Contain("フィールドを確認できません"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("WorkspaceUnpublished")),
                Does.Contain("要確認 1項目").And.Not.Contain("GitHub未反映"));
            Assert.That(Ui.Find<Button>("GridPendingInput").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("WorkspaceUnpublished");
        });
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => !Workspace.IsBusy && Ui.Find<TextBlock>("ApplyCheckStatus", Ui.Dialog("ApplyReviewDialog")).Text.Contains("最新未確認"));
        await Ui.Until(() => Ui.Popup<Button>("ApplyDetails-" + originalRow.ItemId)?.IsLoaded == true);
        ContentDialog dialog = null!;
        Button details = null!;
        await Ui.Run(() => {
            dialog = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            Assert.That(Ui.Find<Button>("ApplyConnectionSettings", dialog).Visibility, Is.EqualTo(Visibility.Visible));
            var rows = Ui.Find<ListView>("ApplyTargetRows", dialog);
            Assert.That(rows.Items, Has.Count.EqualTo(1));
            Assert.That(rows.SelectedItems, Is.Empty);
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("#" + issue.Number).And.Contain(issue.Title.Value)
                .And.Contain(originalField.Id.NodeId).And.Contain("送らない未確定入力: Q")
                .And.Contain("取得不可").And.Contain("フィールドを確認できません").And.Not.Contain(replacement.Id.NodeId));
            details = Ui.Find<Button>("ApplyDetails-" + originalRow.ItemId, dialog);
            Ui.Click(details);
        });
        await Ui.Until(() => Ui.Popup<TextBlock>("ApplyFullDetails-" + originalRow.ItemId)?.IsLoaded == true);
        await Ui.Run(() => {
            var full = string.Join("\n", Ui.Tree(((Flyout)details.Flyout).Content).OfType<TextBlock>().Select(text => text.Text));
            Assert.That(full, Does.Contain(issue.Repository.NameWithOwner).And.Contain(issue.Url)
                .And.Contain("Issue ID " + issue.Id.NodeId).And.Contain("項目 ID " + originalRow.ItemId)
                .And.Contain("Projectフィールド " + originalField.Id.NodeId).And.Contain("送らない未確定入力: Q")
                .And.Contain("反映する値: 変更なし").And.Not.Contain(replacement.Id.NodeId));
            Assert.That(Workspace.ApplyReview, Is.Null);
            Assert.That(Work.Journal, Is.Empty); Assert.That(h.Writes, Is.Empty);
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
            details.Flyout.Hide();
        });
        await Ui.Until(() => !details.Flyout.IsOpen);
        await Ui.Run(() => Ui.DialogButton("ApplyReviewDialog", "CloseButton"));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog") is null && Ui.Find<Button>("WorkspaceUnpublished").IsEnabled);
        await Ui.Run(() => {
            Assert.That(Workspace.Selected!.Snapshot.Id, Is.EqualTo(current.Snapshot.Id));
            Assert.That(Workspace.CanRead, Is.False);
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
            var newCell = Work.ReadRows(Workspace.Selected!).Single(row => row.ItemId == originalRow.ItemId).Cells
                .Single(cell => cell.Key?.FieldId == replacement.Id.NodeId);
            Assert.That(Work.Buffer(newCell), Is.Null); Assert.That(Work.Field(newCell)!.Change, Is.Null);
            Assert.That(Work.Field(originalCell)!.Buffer, Is.EqualTo("Q"));
            Assert.That(Work.Journal, Is.Empty); Assert.That(h.Writes, Is.Empty);
        });
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(Work.Scope)), Is.EqualTo(checkpoint));
    }
}
