using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using System.Text.Json;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningQueryTests
{
    [TestCase(false), TestCase(true)]
    public void ColdPlanAndGanttQueriesDoNotCreateDraftsOrChangeSavedWork(bool gantt)
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        w.SetRegistrations([p]); w.SetPlanning(PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Manual,
            ManualStart: PlanningContractTests.At("2026-10-05 12:07"), ManualFinish: PlanningContractTests.At("2026-10-06 16:19"))] }, 0);
        var before = JsonSerializer.Serialize(w.Snapshot());
        if (gantt) Assert.That(GanttProjection.Create(w, p, []).Rows[0].Plan!.Start, Is.EqualTo(PlanningContractTests.At("2026-10-05 12:07")));
        else Assert.That(w.PlanFor(p).Tasks[0].Start, Is.EqualTo(PlanningContractTests.At("2026-10-05 12:07")));
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before), "Read-only inspection cannot establish draft fields or a new durable revision.");
    }
}
