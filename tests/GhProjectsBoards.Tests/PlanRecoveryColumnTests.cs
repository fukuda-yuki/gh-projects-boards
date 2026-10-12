using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanRecoveryColumnTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);

    [TestCase("TEXT"), TestCase("NUMBER"), TestCase("DATE"), TestCase("SINGLE_SELECT"), TestCase("ITERATION")]
    public void UnenteredLocalExtraValueIsKnownEmpty(string type)
    {
        var row = PlanRow.New("New task", "acme/repo");
        var d = new PlanDocument(new(new("github.com", 42), "P1"), new([], [new("X", "Extra", type)]), new([row], new()));
        Assert.That(PlanExtraEditing.Read(row, "Project:X"), Is.EqualTo(new PlanExtraValue(ValueAvailability.Empty, "null")));
        Assert.That(PlanSheetEditing.CopyValue(row, "Project:X", ""), Is.Null);
        Assert.That(PlanOperations.Changes(d, Day).Fields[row.Identity], Does.Not.Contain("Project:X"));
    }

    [TestCase(ValueAvailability.Unavailable), TestCase(ValueAvailability.Unsupported), TestCase(ValueAvailability.NotLoaded)]
    public void ExplicitReadFaultsOnLocalRowsNeverBecomeEmpty(ValueAvailability availability)
    {
        var observation = new PlanExtraValue(availability, null);
        var row = PlanRow.New("New task", "acme/repo") with { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty.Add("Project:X", observation) };
        var d = new PlanDocument(new(new("github.com", 42), "P1"), new([], [new("X", "Notes", "TEXT")]), new([row], new()));
        Assert.That(PlanExtraEditing.Read(row, "Project:X"), Is.EqualTo(observation));
        Assert.That(PlanExtraEditing.ReadOnlyReason(d, row, "Project:X"), Is.Not.Null);
        Assert.That(PlanExtraEditing.Read(new("I1", "Remote", "acme/repo"), "Project:X").Availability, Is.EqualTo(ValueAvailability.Unavailable));
    }

    [Test]
    public async Task RecoveryCopyDropsReadOnlyExtraValuesAndStillPublishesEditableValues()
    {
        var row = PlanValues.Set(PlanValues.Set(new("I1", "Recover", "acme/repo"), "Project:I", "\"Sprint 2\""), "Project:T", "\"keep\"");
        var columns = ImmutableArray.Create(new PlanColumnDefinition("I", "Sprint", "ITERATION"), new PlanColumnDefinition("T", "Notes", "TEXT"));
        var d = new PlanDocument(new(new("github.com", 42), "P1"), new([row], columns), new([row], new())) { Sync = new() { Unavailable = [row.Identity] } };
        var root = Path.Combine(Path.GetTempPath(), "ghpb-recovery-columns-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), d, Day);
        try {
            await session.ResolveUnavailable("I1", true, Day);
            var copied = session.Document.State.Rows.Single();
            var remote = new PlanRemoteSnapshot(new([], columns), ImmutableDictionary<string, string>.Empty,
                columns.Select(c => new ProjectFieldDefinition(new(d.Project.Scope, c.Id), d.Project, c.Name, "ProjectV2Field", c.DataType, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray(), 0, 0);
            var plan = PlanPublishPlan.Build(session.Document, remote, Day, Guid.NewGuid().ToString("N"));
            Assert.That(copied.ExtraValues.Keys, Is.EqualTo(new[] { "Project:T" }));
            Assert.That(plan.Writes.Any(w => w.Mutation == "createIssue"), Is.True);
            Assert.That(plan.Writes.Any(w => w.Field == "Project:T"), Is.True);
            Assert.That(plan.Writes.Any(w => w.Field == "Project:I"), Is.False);
            await session.Undo(Day);
            Assert.That(session.Document.State.Rows.Single().ExtraValues, Is.EqualTo(row.ExtraValues));
        } finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
}
