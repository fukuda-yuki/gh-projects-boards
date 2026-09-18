using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyInformationTests
{
    internal static ProjectRegistration ThreeFields(int count = 23)
    {
        var p = EditingTests.Registration(count: count);
        var labels = new[] { new[] { "Status", "Backlog", "Ready", "Blocked" }, new[] { "Priority", "P2", "P1", "P0" }, new[] { "Size", "S", "M", "L" } };
        var fields = labels.Select((label, i) => p.Snapshot.Fields[0] with {
            Id = new(p.Snapshot.Id.Scope, "F" + i), Name = label[0],
            Options = [new("todo", label[1]), new("done", label[2]), new("exception", label[3]), new("zero", "0")]
        }).ToArray();
        return p with { Snapshot = p.Snapshot with { Fields = fields, Items = p.Snapshot.Items.Select(item => item with {
            Values = fields.Select(f => item.Values[0] with { FieldId = f.Id }).ToArray()
        }).ToArray() } };
    }

    [TestCase(0), TestCase(1), TestCase(23)]
    public void SelectionDoesNotHideDifferencesOrAbsorbExceptionalBeforeAndAfterValues(int selectedCount)
    {
        var p = ThreeFields();
        p = p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Select((item, i) => i == 1 ? item with {
            Values = item.Values.Select(v => v.FieldId?.NodeId == "F1" ? v with { OptionId = "exception" } : v).ToArray()
        } : item).ToArray() } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var row in rows) foreach (var cell in row.Cells.Where(c => c.Key?.Kind == "Select")) w.Commit("P1", cell, "done", true);
        w.Commit("P1", rows[22].Cells[2], "exception", true);
        var selected = rows.Take(selectedCount).Select(r => r.ItemId).ToHashSet();

        var view = ApplyConfirmationPresentation.Create(w, p, w.ApplyCandidates(p), rows.Select(r => r.ItemId).ToArray(), false,
            selected, w.ReviewApply(p, selected), true);

        Assert.That(view.Columns.Select(c => c.Id.FieldId), Is.EqualTo(new[] { "F0", "F1", "F2" }));
        Assert.That(view.Rows.Length, Is.EqualTo(23));
        Assert.That(view.Rows[0].Cells.Select(c => c.Difference), Is.EqualTo(new[] { "Backlog → Ready", "P2 → P1", "S → M" }));
        Assert.That(view.Rows[1].Cells[1].Difference, Is.EqualTo("P0 → P1"));
        Assert.That(view.Rows[22].Cells[1].Difference, Is.EqualTo("P2 → P0"));
        Assert.That(view.Summary, Is.EqualTo($"23件中{selectedCount}件を選択"));
        Assert.That(view.EffectiveCount, Is.EqualTo(selectedCount));
        Assert.That(view.HiddenSummary, Is.Empty);
        Assert.That(w.Journal, Is.Empty);
    }

    [Test]
    public void HiddenCandidatesDistinguishExclusionInclusionAndSelectionWithoutChangingDisplayOrder()
    {
        var p = ThreeFields(5); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var row in rows) w.Commit("P1", row.Cells[1], "done", true);
        var visible = new[] { rows[1].ItemId, rows[0].ItemId };
        ApplyConfirmationPresentation View(bool include, params string[] selected) => ApplyConfirmationPresentation.Create(w, p, w.ApplyCandidates(p), visible, include, selected.ToHashSet(), null, false);

        var excluded = View(false); var added = View(true); var chosen = View(true, rows[4].ItemId);

        Assert.That(excluded.Rows.Select(r => r.Id), Is.EqualTo(visible));
        Assert.That(excluded.HiddenCount, Is.EqualTo(3)); Assert.That(excluded.HiddenSummary, Does.Contain("除外中"));
        Assert.That(added.Rows.Select(r => r.Id), Is.EqualTo(visible.Concat(rows.Skip(2).Select(r => r.ItemId))));
        Assert.That(added.SelectedCount, Is.Zero); Assert.That(added.HiddenSummary, Does.Contain("追加済み"));
        Assert.That(chosen.HiddenSelected, Is.EqualTo(1)); Assert.That(chosen.SelectedCount, Is.EqualTo(1));
        Assert.That(chosen.EffectiveCount, Is.Null, "An incomplete check is not a confirmed zero.");
    }

    [Test]
    public void SameNamedFieldsAndIssueNumbersRetainIdentityAndProblemsUseExactRowAndFieldKeys()
    {
        var p = ThreeFields(10);
        p = p with { Snapshot = p.Snapshot with {
            Fields = p.Snapshot.Fields.Select(f => f with { Name = "Same name" }).ToArray(),
            Issues = p.Snapshot.Issues.ToDictionary(k => k.Key, k => k.Value with { Number = 1,
                Repository = k.Value.Repository with { NameWithOwner = k.Key.NodeId == "I1" ? "owner/one" : "owner/two" },
                Title = new(ValueAvailability.Present, new string('あ', 180) + k.Key.NodeId) })
        } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var row in new[] { rows[0], rows[9] }) foreach (var cell in row.Cells.Where(c => c.Editable))
            w.Commit("P1", cell, cell.Key!.Kind == "Title" ? new string('あ', 180) + "changed" : "done", cell.Key.Kind == "Select");
        var remote = p with { Snapshot = p.Snapshot with { Items = p.Snapshot.Items.Select((item, i) => i == 0 ? item with {
            Values = item.Values.Select(v => v.FieldId?.NodeId == "F1" ? v with { OptionId = "exception" } : v).ToArray()
        } : item).ToArray() } };
        w.Reconcile(p, remote); w.SetRegistrations([remote]); var selected = new[] { rows[0].ItemId, rows[9].ItemId }.ToHashSet();
        var review = w.ReviewApply(remote, selected);

        var view = ApplyConfirmationPresentation.Create(w, remote, w.ApplyCandidates(remote), rows.Select(r => r.ItemId).ToArray(), false, selected, review, true);

        Assert.That(view.Columns.Select(c => c.Name), Is.EqualTo(new[] { "タイトル（Issue共通）", "Same name [F0]", "Same name [F1]", "Same name [F2]" }));
        Assert.That(view.Rows.Select(r => r.Repository), Is.EqualTo(new[] { "owner/one", "owner/two" }));
        Assert.That(view.Rows.Select(r => r.Number), Is.All.EqualTo("#1"));
        Assert.That(view.Rows[0].Cells[0].After.Text, Does.EndWith("changed"));
        Assert.That(view.Rows[0].IdentityDetails, Does.Contain("I1"));
        Assert.That(review.Problems.Single().RowId, Is.EqualTo(rows[0].ItemId));
        Assert.That(review.Problems.Single().Field, Is.EqualTo(rows[0].Cells[2].Key));
        Assert.That(view.Rows[0].Problems, Has.Length.EqualTo(1)); Assert.That(view.Rows[1].Problems, Is.Empty);
    }

    [Test]
    public void PendingTextClearUnchangedAndZeroRemainDistinctFromUnavailableBaselines()
    {
        var p = ThreeFields(2); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        w.Clear("P1", [rows[0].Cells[1]]); w.Commit("P1", rows[0].Cells[2], "zero", true);
        w.SetBuffer(rows[0].Cells[0], "pending only"); w.Commit("P1", rows[1].Cells[3], "done", true);
        var view = ApplyConfirmationPresentation.Create(w, p, w.ApplyCandidates(p), rows.Select(r => r.ItemId).ToArray(), false, new HashSet<string>(), null, false);

        Assert.That(view.Rows[0].Cells.Select(c => c.After.Kind), Is.EqualTo(new[] { ConfirmationValueKind.Unchanged, ConfirmationValueKind.Clear, ConfirmationValueKind.Value, ConfirmationValueKind.Unchanged }));
        Assert.That(view.Rows[0].Cells[0].Pending, Is.EqualTo("pending only"));
        Assert.That(view.Rows[0].Cells[2].After.Text, Is.EqualTo("0"));
        var states = new[] { ValueAvailability.Empty, ValueAvailability.NotLoaded, ValueAvailability.Unavailable, ValueAvailability.Unsupported };
        Assert.That(states.Select(s => ApplyConfirmationPresentation.Format(null, s, null).Text), Is.EqualTo(new[] { "未設定", "未取得", "取得不可", "非対応" }));
        Assert.That(view.EffectiveCount, Is.Null);
        var sameOptions = p.Snapshot.Fields[0] with { Options = [new("a", "Same"), new("b", "Same")] };
        Assert.That(ApplyConfirmationPresentation.Format("a", ValueAvailability.Present, sameOptions).Text, Is.EqualTo("Same [a]"));
        Assert.That(ApplyConfirmationPresentation.Format("b", ValueAvailability.Present, sameOptions).Text, Is.EqualTo("Same [b]"));
    }

    [Test]
    public async Task MixedCreationSummaryUsesConfirmedOperationsAndRetainsPendingOnlySelection()
    {
        var h = await CreationHarness.Create(2); var w = h.Session.Workspace; var rows = w.Open(h.Workspace.Selected!);
        w.Commit("P1", rows[0].Cells[0], "Updated"); w.SetBuffer(rows[1].Cells[0], "Not an outgoing value");
        var local = h.Add("Created"); var selected = new[] { rows[0].ItemId, rows[1].ItemId, local }.ToHashSet();
        await h.Workspace.PrepareApplyAsync(selected); var p = h.Workspace.Selected!;

        var view = ApplyConfirmationPresentation.Create(w, p, w.ApplyCandidates(p), selected.ToArray(), false, selected, h.Workspace.ApplyReview, true);

        Assert.That(view.SelectedCount, Is.EqualTo(3)); Assert.That(view.EffectiveCount, Is.EqualTo(2));
        Assert.That(view.Summary, Does.Contain("3件中3件").And.Contain("反映対象2件（変更なし1件）").And.Contain("更新1件・新規作成1件"));
        Assert.That(view.Rows.Single(r => r.Id == local).Cells[0].Before.Kind, Is.EqualTo(ConfirmationValueKind.NotCreated));
        Assert.That(h.Writes, Is.Empty);
    }
}
