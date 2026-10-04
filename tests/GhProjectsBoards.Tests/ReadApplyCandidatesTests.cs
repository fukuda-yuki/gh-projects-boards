using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ReadApplyCandidatesTests
{
    [Test]
    public void ReadingUneditedRowsAndObservedDependenciesDoesNotInitializeFields()
    {
        var project = PlanningPathTests.Registration();
        project = project with { Snapshot = project.Snapshot with { Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key,
            pair => pair.Value with { Native = pair.Value.Native! with {
                Predecessors = pair.Key.NodeId == "I2" ? [new(project.Snapshot.Id.Scope, "I1")] : [] } }) } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.SetPlanning(PlanningPathTests.Plan() with { Fields = [] }, 0);
        Assert.That(work.Fields, Is.Empty);
        var before = State(work);

        var first = work.ReadApplyCandidates(project);
        var repeated = work.ReadApplyCandidates(project);

        Assert.That(first, Is.Empty); Assert.That(repeated, Is.Empty);
        Assert.That(State(work), Is.EqualTo(before), "Reading candidates cannot initialize title, select, scalar or dependency baselines.");
        Assert.That(work.Fields, Is.Empty);
    }

    [Test]
    public void ReadingALocalRowWithNewScalarColumnsDoesNotCreateDraftFieldsOrConsumeItsInput()
    {
        var original = EditingTests.Registration(count: 0);
        var current = PlanningPathTests.Registration(0);
        var work = new EditingWorkspace(original.Snapshot.Id.Scope); work.SetRegistrations([original]);
        var id = work.AddRow(original);
        work.SetBuffer(work.ReadRows(original).Single().Cells[0], "unfinished local title");
        work.SetRegistrations([current]);
        work.SetPlanning(PlanningPathTests.Plan(), 0);
        var before = State(work);

        var candidate = work.ReadApplyCandidates(current).Single();

        Assert.That(candidate.Id, Is.EqualTo(id)); Assert.That(candidate.IsCreation, Is.True);
        Assert.That(candidate.Row.Cells.Any(cell => cell.Key?.FieldId == "F-Estimate"), Is.True);
        Assert.That(work.Buffer(candidate.Row.Cells[0]), Is.EqualTo("unfinished local title"));
        Assert.That(State(work), Is.EqualTo(before));
        Assert.That(work.Fields.Any(field => field.Key.FieldId == "F-Estimate"), Is.False);
    }

    [Test]
    public void DependencyOnlyCandidateAndPendingTitleRemainIndependentAndUndoStillRestoresTheOperation()
    {
        var project = PlanningPathTests.Registration();
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Fields = [] }, work.Revision);
        work.CommitPlanning(project, work.Planning("P1")!, work.Revision, dependencies: [new("I2", ["I1"])]);
        var title = work.ReadRows(project)[0].Cells[0]; work.SetBuffer(title, "pending title outside the dependency operation");
        var before = State(work);

        var candidates = work.ReadApplyCandidates(project);

        Assert.That(candidates.Select(candidate => candidate.Id), Is.EquivalentTo(new[] { "P1T1", "P1T2" }));
        Assert.That(candidates.Single(candidate => candidate.Id == "P1T2").Fields.Where(field => field.Change is not null).Select(field => field.Key),
            Is.EqualTo(new[] { new FieldKey("Dependency", "I2", "P1", "I1") }));
        Assert.That(candidates.Single(candidate => candidate.Id == "P1T1").Changes, Is.Zero);
        Assert.That(State(work), Is.EqualTo(before));
        work.Undo("P1");
        Assert.That(work.Fields.Where(field => field.Key.Kind == "Dependency").All(field => field.Change is null), Is.True);
        Assert.That(work.PlanFor(project).Inputs!.Single(input => input.Task.Id == "I2").Predecessors, Is.Empty);
        Assert.That(work.Buffer(title), Is.EqualTo("pending title outside the dependency operation"));
        Assert.That(work.ReadApplyCandidates(project).Select(candidate => candidate.Id), Is.EqualTo(new[] { "P1T1" }));
    }

    [Test]
    public void ManualDatesAndARetainedRemovedFieldStayInTheSameExistingRowCandidate()
    {
        var previous = PlanningPathTests.Registration(1);
        var work = new EditingWorkspace(previous.Snapshot.Id.Scope); work.SetRegistrations([previous]);
        work.CommitPlanning(previous, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual,
            ManualStart: new(2026, 10, 13, 9, 0, 0), ManualFinish: new(2026, 10, 13, 12, 0, 0))] }, work.Revision,
            values: [new("P1T1", "Estimate", "3")]);
        work.Commit("P1", work.ReadRows(previous)[0].Cells.Single(cell => cell.Key?.FieldId == "P1-status"), "done", true);
        var current = previous with { Snapshot = previous.Snapshot with {
            Fields = previous.Snapshot.Fields.Where(field => field.Id.NodeId != "P1-status").ToArray() } };
        work.Reconcile(previous, current); work.SetRegistrations([current]);
        var before = State(work);

        var candidate = work.ReadApplyCandidates(current).Single();

        Assert.That(candidate.Id, Is.EqualTo("P1T1")); Assert.That(candidate.Missing, Is.False);
        Assert.That(candidate.Fields.Where(field => field.Change is not null).Select(field => field.Key), Is.EquivalentTo(new[] {
            new FieldKey("Number", "P1T1", "P1", "F-Estimate"), new FieldKey("Date", "P1T1", "P1", "F-Start"),
            new FieldKey("Date", "P1T1", "P1", "F-Finish"), new FieldKey("Select", "P1T1", "P1", "P1-status") }));
        Assert.That(candidate.Row.Cells.Any(cell => cell.Key?.FieldId == "P1-status"), Is.False,
            "A removed definition cannot erase the retained field from its row's candidate.");
        Assert.That(State(work), Is.EqualTo(before));
    }

    [Test]
    public void ASourceProjectOrphanRemainsVisibleWhenItsItemDisappears()
    {
        var previous = EditingTests.Registration(count: 1);
        var work = new EditingWorkspace(previous.Snapshot.Id.Scope); work.SetRegistrations([previous]);
        var row = work.Open(previous).Single(); work.Commit("P1", row.Cells[1], "done", true);
        work.SetBuffer(row.Cells[1], "pending retained option");
        var current = previous with { Snapshot = previous.Snapshot with { Items = [] } };
        work.Reconcile(previous, current); work.SetRegistrations([current]);
        var before = State(work);

        var candidate = work.ReadApplyCandidates(current).Single();

        Assert.That(candidate.Missing, Is.True); Assert.That(candidate.Changes, Is.EqualTo(1));
        Assert.That(candidate.Fields.Single().Key, Is.EqualTo(new FieldKey("Select", "P1T1", "P1", "P1-status")));
        Assert.That(candidate.Fields.Single().SourceProject, Is.EqualTo(current.Snapshot.Id));
        Assert.That(candidate.Fields.Single().Buffer, Is.EqualTo("pending retained option"));
        Assert.That(State(work), Is.EqualTo(before));
    }

    [Test]
    public void OtherProjectFieldsStaySeparateWhileSharedTitlesFollowActualItemMembership()
    {
        var first = EditingTests.Registration(count: 2);
        var second = EditingTests.Registration("P2", count: 1);
        // A cached Issue dictionary entry alone does not make I2 a P2 member.
        second = second with { Snapshot = second.Snapshot with { Issues = first.Snapshot.Issues } };
        var work = new EditingWorkspace(first.Snapshot.Id.Scope); work.SetRegistrations([first, second]);
        var firstRows = work.Open(first); var secondRow = work.Open(second).Single();
        work.Commit("P1", firstRows[0].Cells[0], "shared title");
        work.Commit("P1", firstRows[1].Cells[0], "only in P1");
        work.Commit("P1", firstRows[0].Cells[1], "done", true);
        work.Commit("P2", secondRow.Cells[1], "done", true);
        var before = State(work);

        var firstCandidates = work.ReadApplyCandidates(first);
        var secondCandidate = work.ReadApplyCandidates(second).Single();

        Assert.That(firstCandidates.Select(candidate => candidate.Id), Is.EquivalentTo(new[] { "P1T1", "P1T2" }));
        Assert.That(secondCandidate.Id, Is.EqualTo("P2T1"));
        Assert.That(secondCandidate.Fields.Where(field => field.Change is not null).Select(field => field.Key), Is.EquivalentTo(new[] {
            new FieldKey("Title", "I1"), new FieldKey("Select", "P2T1", "P2", "P2-status") }));
        Assert.That(firstCandidates.SelectMany(candidate => candidate.Fields).Any(field => field.Key.ProjectId == "P2"), Is.False);
        Assert.That(State(work), Is.EqualTo(before));
    }

    [Test]
    public void SharedTitleKeepsEveryDuplicateAppearanceAndProjectionDecisionNeedsNoChangedValue()
    {
        var project = PlanningPathTests.Registration(2);
        var duplicate = project.Snapshot.Items[0] with { Id = new(project.Snapshot.Id.Scope, "P1T3") };
        project = project with { Snapshot = project.Snapshot with { Items = project.Snapshot.Items.Append(duplicate).ToArray() } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.SetPlanning(PlanningPathTests.Plan(), 0);
        var rows = work.Open(project); work.SetBuffer(rows[0].Cells[0], "Shared unfinished title");
        var decision = rows[1].Cells.Single(c => c.Key?.FieldId == "F-Estimate").Key;
        var snapshot = work.Snapshot();
        work = EditingWorkspace.Restore(snapshot with { Fields = snapshot.Fields.Select(field => field.Key == decision
            ? field with { Observation = new("projection-observation", project.Snapshot.Id, project.RetrievedAt,
                field.Baseline, field.Baseline is null ? ValueAvailability.Empty : ValueAvailability.Present,
                EditingWorkspace.ProjectionDecisionReason, []) } : field).ToArray() });
        var before = State(work);

        var candidates = work.ReadApplyCandidates(project);

        Assert.That(candidates.Select(c => c.Id), Is.EqualTo(new[] { "P1T1", "P1T2", "P1T3" }));
        Assert.That(candidates.Where(c => c.Id != "P1T2").All(c => c.Fields.Single(f => f.Key.Kind == "Title").Buffer == "Shared unfinished title"), Is.True);
        Assert.That(candidates.Single(c => c.Id == "P1T2").Changes, Is.Zero);
        Assert.That(candidates.Single(c => c.Id == "P1T2").Fields.Single(f => f.Key == decision).Conflict, Is.False);
        Assert.That(State(work), Is.EqualTo(before));
    }

    private static string State(EditingWorkspace work) => JsonSerializer.Serialize(work.Snapshot());
}
