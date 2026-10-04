using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class WeeklyBulkEditingTests
{
    private static readonly DateOnly PreviousDay = new(2026, 10, 6);
    private static readonly DateOnly ReportingDay = new(2026, 10, 13);

    internal static (ProjectRegistration Project, EditingWorkspace Work) Work(int count = 4)
    {
        var project = PlanningPathTests.Registration(count);
        project = project with { Snapshot = project.Snapshot with {
            Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key, pair => pair.Value with {
                Native = pair.Value.Native! with { Assignees = [new(new(project.Snapshot.Id.Scope, "U2"), "Current worker")] } }) } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with {
            Tasks = Enumerable.Range(1, count).Select(i => new PlanningTask("I" + i,
                Actuals: i == 1 ? [new("U1", 5, PreviousDay)] : i == 3 ? [new(null, 2, PreviousDay)] : null)).ToArray() },
            work.Revision, Enumerable.Range(1, count).SelectMany(i => new[] {
                new PlanningValueEdit("P1T" + i, "Estimate", "16"), new PlanningValueEdit("P1T" + i, "Remaining", "4") }).ToArray());
        return (project, work);
    }

    private static int Column(EditRow[] rows, string role) => Array.FindIndex(rows[0].Cells, c => c.Key?.FieldId == "F-" + role);
    private static string Snapshot(EditingWorkspace work) => JsonSerializer.Serialize(work.Snapshot());
    private static ActualContribution Report(EditingWorkspace work, string id) => work.Planning("P1")!.Tasks.Single(task => task.Id == id).Actuals!.Single();

    [Test]
    public async Task WeeklyRectangleUsesDisplayedIdsAndEachWorkerThenDurableUndoRestoresAllWorkTogether()
    {
        var (project, work) = Work(); var rows = work.Open(project);
        work.SetBuffer(rows[3].Cells[0], "Keep this unfinished title");
        var before = work.Snapshot();
        var displayed = new[] { rows[2], rows[0], rows[1] };

        work.PasteSelection("P1", displayed, new(0, Column(rows, "Remaining")), "3\t7\n\t0\n2\t9", reportedThrough: ReportingDay);

        Assert.That(Report(work, "I3"), Is.EqualTo(new ActualContribution(null, 7, ReportingDay)));
        Assert.That(Report(work, "I1"), Is.EqualTo(new ActualContribution("U1", 0, ReportingDay)));
        Assert.That(Report(work, "I2"), Is.EqualTo(new ActualContribution("U2", 9, ReportingDay)));
        Assert.That(rows.Take(3).Select(row => work.Value(row.Cells[Column(rows, "Remaining")])), Is.EqualTo(new[] { "4", "2", "3" }));
        Assert.That(work.Planning("P1")!.Tasks.Select(task => task.Progress), Is.All.EqualTo(PlanningProgress.Unstarted));
        Assert.That(work.Snapshot().History, Has.Length.EqualTo(before.History.Length + 1));
        Assert.That(work.Buffer(rows[3].Cells[0]), Is.EqualTo("Keep this unfinished title"));

        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-weekly-bulk-" + Guid.NewGuid().ToString("N")));
        Assert.That(await new DraftSession(store, work, 0).FlushAsync(), Is.True);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(work.Scope))!);
        restored.Undo("P1");
        Assert.That(restored.Planning("P1")!.Tasks.Select(task => task.Actuals), Is.EqualTo(before.Planning!.Single().Tasks.Select(task => task.Actuals)));
        Assert.That(restored.Open(project).Select(row => restored.Value(row.Cells[Column(rows, "Remaining")])), Is.All.EqualTo("4"));
        Assert.That(restored.Buffer(rows[3].Cells[0]), Is.EqualTo("Keep this unfinished title"));
        Assert.That(restored.Journal, Is.Empty);
    }

    [TestCase("fill"), TestCase("down"), TestCase("broadcast")]
    public void CopyingActualHoursRetainsEachTargetWorkerAndTheEarlierSourceUndo(string route)
    {
        var (project, work) = Work(3); var rows = work.Open(project); var actual = Column(rows, "Actual");
        work.CommitActualInput(project, rows[0].ItemId, "7", ReportingDay, "U1", work.Revision);
        var afterSource = work.Snapshot();

        if (route == "broadcast") work.PasteSelection("P1", rows, new(0, actual, 3), "7", work.CopyCells("P1", rows, new(0, actual)), ReportingDay);
        else work.Fill("P1", rows, 0, actual, route == "fill" ? 1 : 0, 2, ReportingDay);

        Assert.That(Report(work, "I1"), Is.EqualTo(new ActualContribution("U1", 7, ReportingDay)));
        Assert.That(Report(work, "I2"), Is.EqualTo(new ActualContribution("U2", 7, ReportingDay)));
        Assert.That(Report(work, "I3"), Is.EqualTo(new ActualContribution(null, 7, ReportingDay)));
        Assert.That(work.Snapshot().History, Has.Length.EqualTo(afterSource.History.Length + 1));
        work.Undo("P1");
        Assert.That(Report(work, "I1").Hours, Is.EqualTo(7));
        Assert.That(work.Value(rows[1].Cells[actual]), Is.Null);
        Assert.That(Report(work, "I3"), Is.EqualTo(new ActualContribution(null, 2, PreviousDay)));
        work.Undo("P1");
        Assert.That(Report(work, "I1"), Is.EqualTo(new ActualContribution("U1", 5, PreviousDay)));
    }

    [TestCase("date"), TestCase("invalid-final"), TestCase("missing-worker"), TestCase("multiple-reports"), TestCase("pending-actual"), TestCase("pending-remaining")]
    public void InvalidWeeklyDestinationRejectsTheWholeRectangleAndPreservesInputAndHistory(string problem)
    {
        var (project, work) = Work(3);
        if (problem == "missing-worker")
        {
            project = project with { Snapshot = project.Snapshot with { Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key,
                pair => pair.Key.NodeId == "I2" ? pair.Value with { Native = pair.Value.Native! with { Assignees = [] } } : pair.Value) } };
            work.SetRegistrations([project]);
        }
        if (problem == "multiple-reports") work.CommitActualReports(project, "P1T2", [new("U1", 2, PreviousDay), new("U2", 3, PreviousDay)], work.Revision);
        var rows = work.Open(project); var remaining = Column(rows, "Remaining"); var actual = Column(rows, "Actual");
        if (problem == "pending-actual") work.SetPlanningBuffer(rows[1].Cells[actual], "unfinished actual");
        if (problem == "pending-remaining") work.SetBuffer(rows[1].Cells[remaining], "unfinished remaining");
        var before = Snapshot(work);

        Assert.That(() => work.PasteSelection("P1", rows, new(0, remaining),
            problem == "invalid-final" ? "3\t7\n2\t9\n1\t-1" : "3\t7\n2\t9\n1\t8",
            reportedThrough: problem == "date" ? null : ReportingDay), Throws.InvalidOperationException);

        Assert.That(Snapshot(work), Is.EqualTo(before));
    }

    [Test]
    public void ActualRectangleCannotAlsoWriteUnrelatedFields()
    {
        var (project, work) = Work(3); var rows = work.Open(project); var actual = Column(rows, "Actual");
        var projected = rows.Select(row => row with { Cells = [row.Cells[0], row.Cells[actual]] }).ToArray();
        var before = Snapshot(work);

        Assert.That(() => work.PasteSelection("P1", projected, new(0, 0), "New title\t7\nAnother title\t9", reportedThrough: ReportingDay), Throws.InvalidOperationException);

        Assert.That(Snapshot(work), Is.EqualTo(before));
    }

    [Test]
    public void WeeklyInternalRectangleRejectsUnfinishedSourceInputAddedAfterCopy()
    {
        var (project, work) = Work(); var rows = work.Open(project); var actual = Column(rows, "Actual");
        var sources = new[] { rows[0], rows[2] }; var destinations = new[] { rows[1], rows[3] };
        var copied = work.CopyCells("P1", sources, new(0, actual, 2));
        work.SetPlanningBuffer(rows[0].Cells[actual], "unfinished source actual");
        var before = Snapshot(work);

        Assert.That(() => work.PasteSelection("P1", destinations, new(0, actual, 2), "5\n2", copied, ReportingDay), Throws.InvalidOperationException);

        Assert.That(Snapshot(work), Is.EqualTo(before));
    }
}
