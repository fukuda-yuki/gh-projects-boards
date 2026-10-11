using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("PlanReset")]
internal sealed class PlanResetTests
{
    [Test]
    public void ConfirmationCountsClassifyEachTaskOnceAndNewTasksAsEntered()
    {
        var changes = new PlanUnpublished(new Dictionary<string, ImmutableArray<string>> {
            ["entered"] = [PlanField.Title, PlanField.Start, PlanField.End],
            ["calculated"] = [PlanField.Start, PlanField.End],
            ["new"] = [PlanField.NewTask],
            ["closed"] = [PlanField.Remaining]
        }.ToImmutableDictionary()) { RecalculatedFields = [("entered", PlanField.Start), ("entered", PlanField.End),
            ("calculated", PlanField.Start), ("calculated", PlanField.End), ("closed", PlanField.Remaining)] };
        Assert.That((changes.TaskCount, changes.EnteredTaskCount, changes.RecalculatedTaskCount, changes.NewTaskCount), Is.EqualTo((4, 2, 2, 1)));
        var empty = new PlanUnpublished(ImmutableDictionary<string, ImmutableArray<string>>.Empty);
        Assert.That((empty.TaskCount, empty.EnteredTaskCount, empty.RecalculatedTaskCount, empty.NewTaskCount), Is.EqualTo((0, 0, 0, 0)));
    }
}
