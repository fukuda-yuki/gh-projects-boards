using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanColumnRegressionTests
{
    [TestCase("Assignees", "[]"), TestCase("Predecessors", "[]"), TestCase("Labels", "[]")]
    [TestCase("StartNoEarlierThan", "null"), TestCase("Status", "null"), TestCase("MilestoneTitle", "null"), TestCase("Fixed", "false")]
    public void EmptySummaryValuesAgreeWithTypedCopy(string field, string expected)
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo");
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1" };
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([parent, child], []), new([parent, child], new()));
        var effective = PlanOperations.EffectiveRows(document)[0];
        Assert.That(PlanJson.Text(PlanOperations.Value(parent, field)), Is.EqualTo(expected), "plan " + field);
        Assert.That(PlanJson.Text(PlanOperations.Value(effective, field)), Is.EqualTo(expected), "summary " + field);
        Assert.That(PlanJson.Text(PlanSheetEditing.CopyValue(effective, field, "")), Is.EqualTo(expected), "copy " + field);
    }
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static PlanDocument Document(string type, string json)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") {
            ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty.Add("Project:X", new(ValueAvailability.Present, json)) };
        return new(new(new("github.com", 42), "P1"), new([row], [new("X", "Notes", type)]), new([row], new()));
    }
    private static PlanRemoteSnapshot Remote(PlanDocument d, PlanBaseline baseline)
        => new(baseline, ImmutableDictionary<string, string>.Empty.Add("I1", "T1"),
            baseline.Columns.Select(c => new ProjectFieldDefinition(new(d.Project.Scope, c.Id), d.Project, c.Name,
                "ProjectV2Field", c.DataType, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray(), 0, 0);

    [TestCase("1.50", "1.5"), TestCase("0.00", "0"), TestCase("-2.500", "-2.5")]
    public void ExtraNumbersWithEqualValuesHaveNoDifferenceOrWrite(string local, string remote)
    {
        var d = Document("NUMBER", remote);
        d = d with { State = d.State with { Rows = [d.State.Rows[0] with {
            ExtraValues = d.State.Rows[0].ExtraValues.SetItem("Project:X", new(ValueAvailability.Present, local)) }] } };
        Assert.That(PlanOperations.Changes(d, Day).TaskCount, Is.Zero);
        Assert.That(PlanPublishPlan.Build(d, Remote(d, d.Baseline), Day, Guid.NewGuid().ToString("N")).Writes, Is.Empty);
        var changed = d with { State = d.State with { Rows = [PlanValues.Set(d.State.Rows[0], "Project:X", "1.51")] } };
        Assert.That(PlanOperations.Changes(changed, Day).TaskCount, Is.EqualTo(1));
    }

    [Test]
    public void NumericLookingTextStillComparesAsText()
    {
        var d = Document("TEXT", "\"1.5\"");
        d = d with { State = d.State with { Rows = [PlanValues.Set(d.State.Rows[0], "Project:X", "\"1.50\"")] } };
        Assert.That(PlanOperations.Changes(d, Day).TaskCount, Is.EqualTo(1));
        Assert.That(PlanValues.Get(d.State.Rows[0], "Project:X"), Is.EqualTo("\"1.50\""));
    }

    [Test]
    public void EqualNumericLocalAndRemoteEditsConvergeWithoutConflict()
    {
        var d = Document("NUMBER", "0");
        d = d with { State = d.State with { Rows = [PlanValues.Set(d.State.Rows[0], "Project:X", "1.50")] } };
        var remote = d.Baseline with { Rows = [PlanValues.Set(d.Baseline.Rows[0], "Project:X", "1.5")] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.Sync.Conflicts, Is.Empty);
        Assert.That(PlanOperations.Changes(merged, Day).TaskCount, Is.Zero);
    }

    [Test]
    public async Task NumericReadbackRetiresThePublishedExtraEdit()
    {
        var d = Document("NUMBER", "0");
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", "Project:X", 1.50m)]), Day).State };
        var writes = PlanPublishPlan.Build(d, Remote(d, d.Baseline), Day, Guid.NewGuid().ToString("N")).Writes;
        var progress = new PlanPublishProgress(Guid.NewGuid().ToString("N"), writes.Select(w => w with { State = PlanWriteState.Succeeded }).ToImmutableArray()) { DispatchStarted = true };
        d = d with { Sync = d.Sync with { Publish = progress } };
        var remote = Remote(d, d.Baseline with { Rows = [PlanValues.Set(d.Baseline.Rows[0], "Project:X", "1.5")] });
        var root = Path.Combine(Path.GetTempPath(), "ghpb-number-readback-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), d, Day);
        try {
            await session.AcceptPublished(remote, Day);
            Assert.That(session.Document.Sync.Failures, Is.Empty);
            Assert.That(session.Changes(Day).TaskCount, Is.Zero);
            Assert.That(PlanPublishPlan.Build(session.Document, remote, Day, Guid.NewGuid().ToString("N")).Writes, Is.Empty);
        } finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [TestCase(false), TestCase(true)]
    public void DeletedFieldDropsItsRowValueWithoutBlockingOtherPublication(bool edited)
    {
        var d = Document("TEXT", "\"original\"");
        var local = d.State.Rows[0] with { Title = "Renamed" };
        if (edited) local = PlanValues.Set(local, "Project:X", "\"draft\"");
        d = d with { State = d.State with { Rows = [local] } };
        var remote = d.Baseline with { Columns = [], Rows = [d.Baseline.Rows[0] with { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty }] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.State.Rows[0].ExtraValues, Is.Empty);
        merged = PlanJson.Read<PlanDocument>(System.Text.Encoding.UTF8.GetBytes(PlanJson.Text(merged)));
        Assert.That(merged.Sync.Conflicts.Length, Is.EqualTo(edited ? 1 : 0));
        if (edited) {
            Assert.That(merged.Sync.Conflicts[0].Local, Is.EqualTo("\"draft\""));
            Assert.That(PlanJson.Text(merged.Sync.Conflicts[0]), Does.Contain("Notes"));
        }
        Assert.That(PlanOperations.BlockingConflicts(merged), Is.Empty);
        Assert.That(PlanOperations.Changes(merged, Day).Fields["I1"], Is.EqualTo(new[] { PlanField.Title }));
        var writes = PlanPublishPlan.Build(merged, Remote(merged, remote), Day, Guid.NewGuid().ToString("N")).Writes;
        Assert.That(writes.Select(w => w.Mutation), Is.EqualTo(new[] { "updateIssue" }));
        Assert.That(PlanMerge.Merge(merged, remote).Sync.Conflicts, Is.EqualTo(merged.Sync.Conflicts));
    }

    [Test]
    public void DeletionKeepsTheLatestLocalEditEvenWhenTheFieldAlreadyHadAConflict()
    {
        var d = Document("TEXT", "\"original\"");
        d = d with { State = d.State with { Rows = [PlanValues.Set(d.State.Rows[0], "Project:X", "\"latest\"")] },
            Sync = d.Sync with { Conflicts = [new("I1", "Project:X", "\"original\"", "\"earlier\"", "\"remote\"")] } };
        var remote = d.Baseline with { Columns = [], Rows = [d.Baseline.Rows[0] with { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty }] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.Sync.Conflicts.Single().Local, Is.EqualTo("\"latest\""));
        Assert.That(PlanMerge.Merge(merged, remote).Sync.Conflicts.Single().Local, Is.EqualTo("\"latest\""));
    }

    [Test]
    public async Task DeletedFieldEditSurvivesOtherPublicationAndCanBeDiscardedWithUndo()
    {
        var d = Document("TEXT", "\"original\"");
        d = d with { State = d.State with { Rows = [PlanValues.Set(d.State.Rows[0] with { Title = "Renamed" }, "Project:X", "\"draft\"")] } };
        var remote = d.Baseline with { Columns = [], Rows = [d.Baseline.Rows[0] with { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty }] };
        d = PlanMerge.Merge(d, remote);
        Assert.That(d.Sync.Conflicts, Has.Length.EqualTo(1));
        var writes = PlanPublishPlan.Build(d, Remote(d, remote), Day, Guid.NewGuid().ToString("N")).Writes;
        d = d with { Sync = d.Sync with { Publish = new(Guid.NewGuid().ToString("N"), writes.Select(w => w with { State = PlanWriteState.Succeeded }).ToImmutableArray()) { DispatchStarted = true } } };
        var root = Path.Combine(Path.GetTempPath(), "ghpb-deleted-field-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), d, Day);
        try {
            await session.AcceptPublished(Remote(d, remote with { Rows = [remote.Rows[0] with { Title = "Renamed" }] }), Day);
            Assert.That(session.Document.Sync.Conflicts, Is.EqualTo(d.Sync.Conflicts));
            Assert.That(session.Changes(Day).TaskCount, Is.Zero);
            Assert.That(() => session.ResolveConflict("I1", "Project:X", false, Day), Throws.InvalidOperationException);
            await session.ResolveConflict("I1", "Project:X", true, Day);
            Assert.That(session.Document.Sync.Conflicts, Is.Empty);
            Assert.That(session.Document.State.Rows[0].ExtraValues, Is.Empty);
            await session.Undo(Day);
            Assert.That(session.Document.Sync.Conflicts, Is.EqualTo(d.Sync.Conflicts));
            await session.Redo(Day);
            Assert.That(session.Document.Sync.Conflicts, Is.Empty);
        } finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [TestCase("Repository", "acme/repo"), TestCase("Project:X", "Sprint 2")]
    public void ReadOnlyColumnsCopyHeldValuesButRejectPasteTargets(string field, string expected)
    {
        var d = Document("ITERATION", "\"Sprint 2\"");
        Assert.That(PlanSheetEditing.CopyValue(d.State.Rows[0], field, expected), Is.EqualTo(expected));
        Assert.That(() => PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Paste,
            [new("I1", field, expected)]), Day), Throws.ArgumentException);
    }

    [Test]
    public void CopyUsesHeldNamesAndCalculatedValuesWithoutEditValidation()
    {
        var d = Document("SINGLE_SELECT", "\"Old option\"");
        var row = d.State.Rows[0] with { Labels = ["Old label"], MilestoneTitle = "Old milestone" };
        Assert.That(PlanSheetEditing.CopyValue(row, "Project:X", "Old option"), Is.EqualTo("Old option"));
        Assert.That(PlanSheetEditing.CopyValue(row, PlanField.Labels, "Old label"), Is.EqualTo(new[] { "Old label" }));
        Assert.That(PlanSheetEditing.CopyValue(row, PlanField.MilestoneTitle, "Old milestone"), Is.EqualTo("Old milestone"));
        Assert.That(PlanSheetEditing.CopyValue(row, PlanField.Start, "2026-10-14"), Is.EqualTo(new DateOnly(2026,10,14)));
        Assert.That(PlanSheetEditing.CopyValue(row, PlanField.Estimate, 12.5m.ToString(System.Globalization.CultureInfo.CurrentCulture)), Is.EqualTo(12.5m));
        Assert.That(PlanSheetEditing.CopyValue(row, PlanField.Remaining, ""), Is.Null);
    }
}
