using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanEffortTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    [TestCase(false), TestCase(true)]
    public void ClosedNegativeRemainingCannotIsolateTaskOrSuccessorAndOnlyMappedRemainingPublishes(bool mapped)
    {
        var close = Day.AddDays(2);
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = close,
            Estimate = 40, Actual = 8, Remaining = -5, Start = Day, End = Day.AddDays(4) };
        var d = Document(row);
        if (!mapped) d = d with { State = d.State with { Settings = d.State.Settings with {
            Columns = d.State.Settings.Columns.Where(c => c.Role != PlanField.Remaining).ToImmutableArray() } } };
        var tasks = PlanScheduler.Calculate([PlanOperations.TaskInput(row),
            new("I2", 2) { Estimate = 8, Remaining = 8, Predecessors = ["I1"] }], new(), Day);
        Assert.That(tasks[0].Warnings, Is.Empty);
        Assert.That(tasks[0].StartReason, Is.Not.EqualTo("入力エラー"));
        Assert.That(tasks[0].Remaining, Is.Zero);
        Assert.That(tasks[0].End.Value, Is.EqualTo(close));
        Assert.That(tasks[1].Warnings, Is.Empty);
        Assert.That(tasks[1].Start.Value, Is.EqualTo(close.AddDays(1)));
        Assert.That(tasks[1].End.Value, Is.EqualTo(close.AddDays(1)));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(mapped
            ? new[] { PlanField.End, PlanField.Remaining } : new[] { PlanField.End }));
        var writes = review.Writes.Where(w => {
            using var json = JsonDocument.Parse(w.Input);
            return json.RootElement.GetProperty("fieldId").GetString() == "R";
        }).ToArray();
        Assert.That(writes, Has.Length.EqualTo(mapped ? 1 : 0));
        if (mapped) {
            using var json = JsonDocument.Parse(writes[0].Input);
            Assert.That(json.RootElement.GetProperty("value").GetProperty("number").GetDecimal(), Is.Zero);
        }
        Assert.That(d.State.Rows[0].Remaining, Is.EqualTo(-5), "Keep the retrieved input intact.");
    }
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void EarlyClosureCapsStartAndPublishesCalculatedPair(bool fixedDates, bool specified)
    {
        var close = new DateOnly(2026, 10, 9);
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = close,
            Remaining = 0, Start = new(2026, 10, 12), End = new(2026, 10, 16), Fixed = fixedDates,
            StartNoEarlierThan = specified ? new(2026, 10, 12) : null };
        var d = Document(row);
        d = d with { State = d.State with { Settings = d.State.Settings with { Columns = d.State.Settings.Columns.Add(new(PlanField.Start, "S", "Start", "DATE")) } } };
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        Assert.That((scheduled.Start.Value, scheduled.End.Value), Is.EqualTo(((DateOnly?)close, (DateOnly?)close)));
        Assert.That((scheduled.Start.Origin, scheduled.End.Origin), Is.EqualTo((DateOrigin.Calculated, DateOrigin.Calculated)));
        Assert.That(scheduled.Warnings, Is.Empty);
        Assert.That(PlanOperations.Changes(d, Day).RecalculatedFields, Is.EquivalentTo(new[] { ("I1", PlanField.Start), ("I1", PlanField.End) }));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes, Has.Length.EqualTo(2));
        foreach (var write in review.Writes) {
            using var json = JsonDocument.Parse(write.Input);
            Assert.That(json.RootElement.GetProperty("value").GetProperty("date").GetString(), Is.EqualTo("2026-10-09"));
        }
        var successor = PlanScheduler.Calculate([PlanOperations.TaskInput(row), new("I2", 2) { Estimate = 8, Remaining = 0, Predecessors = ["I1"] }], new(), Day)[1];
        Assert.That(successor.Start.Value, Is.EqualTo(close));
        Assert.That(successor.End.Value, Is.EqualTo(close));
    }
    [TestCase(false)]
    [TestCase(true)]
    public void InvertedKeptPairWithoutCloseDateIsStillInvalid(bool closed)
    {
        var result = PlanScheduler.Calculate([new("I1", 1) { Closed = closed, Fixed = true, Start = Day.AddDays(2), End = Day }], new(), Day).Single();
        Assert.That(result.StartReason, Is.EqualTo("入力エラー"));
    }
    [Test]
    public void UnrepresentableForecastDoesNotThrowWhenReadingEffortMarkers()
    {
        var result = PlanScheduler.Calculate([new("I1", 1) { Estimate = 40, Actual = decimal.MaxValue, Remaining = 1 }], new(), Day).Single();
        Assert.That(result.Forecast, Is.Null);
        Assert.That(result.Variance, Is.Null);
        Assert.That(result.IsOverEstimate, Is.True);
    }
    [TestCase(8, 32, 0.2)]
    [TestCase(56, 8, 0.875)]
    [TestCase(8, 0, 1)]
    [TestCase(0, 0, null)]
    [TestCase(null, null, null)]
    public void WorkProgressUsesCurrentForecast(int? actual, int? remaining, double? fraction)
    {
        var result = PlanScheduler.Calculate([new("I1", 1) { Actual = actual, Remaining = remaining }], new(), Day).Single();
        Assert.That(result.WorkCompleteFraction, Is.EqualTo(fraction is null ? null : (decimal?)fraction));
    }
    [TestCase(40, 8, 32)]
    [TestCase(40, 56, 0)]
    [TestCase(0, 8, 0)]
    public void EmptyRemainingCalculatesUnperformedEstimateWithoutChangingInput(int estimate, int actual, int expected)
    {
        var input = new PlanTask("I1", 1) { Estimate = estimate, Actual = actual };
        var result = PlanScheduler.Calculate([input], new(), Day).Single();
        Assert.That(result.Remaining, Is.EqualTo(expected));
        Assert.That(input.Remaining, Is.Null);
        Assert.That(result.StartReason, Is.Not.EqualTo("蜈･蜉帙お繝ｩ繝ｼ"));
    }
    [Test]
    public void EnteringEstimateLeavesRemainingEmpty()
        => Assert.That(PlanEdits.Estimate(new("I1", 1), 40).Remaining, Is.Null);
    [Test]
    public void OpenZeroRemainingWaitsForPredecessorAndDoesNotComplete()
    {
        var input = new PlanTask("I2", 2) { Estimate = 40, Remaining = 0, Predecessors = ["I1"] };
        var result = PlanScheduler.Calculate([new("I1", 1) { Remaining = 16 }, input], new(), Day)[1];
        Assert.That(input.IsComplete, Is.False);
        Assert.That(result.Start.Value, Is.EqualTo(Day.AddDays(1)));
        Assert.That(result.End.Value, Is.EqualTo(result.Start.Value));
    }

    [TestCase(40, 56, 8, 64, 24, true, false)]
    [TestCase(40, 8, null, 40, 0, false, false)]
    [TestCase(40, 56, null, 56, 16, true, true)]
    [TestCase(null, 8, 0, 8, null, false, true)]
    [TestCase(null, null, null, null, null, false, false)]
    [TestCase(0, 0, null, 0, 0, false, false)]
    public void ForecastAndFlagsShareEffectiveRemaining(int? estimate, int? actual, int? remaining,
        int? forecast, int? variance, bool over, bool zero)
    {
        var row = PlanScheduler.Calculate([new("I1", 1) { Estimate = estimate, Actual = actual, Remaining = remaining }], new(), Day).Single();
        Assert.That(row.Forecast, Is.EqualTo(forecast));
        Assert.That(row.Variance, Is.EqualTo(variance));
        Assert.That(row.IsOverEstimate, Is.EqualTo(over));
        Assert.That(row.IsZeroRemainingOpen, Is.EqualTo(zero));
    }
    [Test]
    public void ReestimatedOverrunSchedulesRemainingAtPersonsRateAfterStatusDate()
    {
        var result = PlanScheduler.Calculate([new("I1", 1) { Estimate = 40, Actual = 56, Remaining = 8,
            Start = Day.AddDays(-7), Assignees = ["U1"] }], new() { People = [new("U1", 50)] }, Day).Single();
        Assert.That(result.Start.Value, Is.EqualTo(Day.AddDays(-7)));
        Assert.That(result.End.Value, Is.EqualTo(Day.AddDays(1)));
        Assert.That(result.PlannedHours!.Values.Sum(), Is.EqualTo(8));
    }
    private static PlanDocument Document(PlanRow row)
    {
        ImmutableArray<PlanRow> rows = [row];
        return new(new(new("github.com", 42), "P1"), new(rows, []), new(rows, new() {
            StatusDate = Day, Columns = [new(PlanField.Remaining, "R", "Remaining", "NUMBER"), new(PlanField.End, "E", "End", "DATE")]
        }));
    }
    private static PlanRemoteSnapshot Remote(PlanDocument d) => new(d.Baseline, ImmutableDictionary<string, string>.Empty,
        d.State.Settings.Columns.Select(c => new ProjectFieldDefinition(new(d.Project.Scope, c.FieldId), d.Project, c.Name,
            "ProjectV2Field", c.DataType, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray(), 0, 0);
    [Test]
    public void CommittingCalculatedRemainingStoresPublishableInputThatSurvivesActualEdit()
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Estimate = 40, Actual = 8,
            Start = Day, End = Day, Fixed = true };
        var d = Document(row);
        d = d with { State = d.State with { Settings = d.State.Settings with {
            Columns = d.State.Settings.Columns.Add(new(PlanField.Actual, "A", "Actual", "NUMBER")) } } };
        Assert.That(PlanOperations.Schedule(d, Day).Single().Remaining, Is.EqualTo(32));
        var original = PlanSheetEditing.EditBaseline(row, PlanField.Remaining, "32");
        Assert.That(original, Is.Empty, "Calculated text must not be the no-change baseline.");
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Remaining, PlanSheetEditing.Parse(d, PlanField.Remaining, "32"))]), Day).State };
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Actual, 16m)]), Day).State };
        Assert.That(d.State.Rows[0].Remaining, Is.EqualTo(32));
        Assert.That(PlanOperations.Schedule(d, Day).Single().Remaining, Is.EqualTo(32));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(new[] { PlanField.Remaining, PlanField.Actual }));
        var remaining = review.Writes.Single(w => { using var json = JsonDocument.Parse(w.Input); return json.RootElement.GetProperty("fieldId").GetString() == "R"; });
        using var input = JsonDocument.Parse(remaining.Input);
        Assert.That(input.RootElement.GetProperty("value").GetProperty("number").GetDecimal(), Is.EqualTo(32));
    }

    [TestCase(false, false), TestCase(false, true)]
    [TestCase(true, false), TestCase(true, true)]
    public void AutomaticFieldsRequireMappingWhileLocalScheduleAndTitlePublicationRemainAvailable(bool closed, bool mapped)
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Estimate = 40, Actual = 8,
            Closed = closed, CloseDate = closed ? Day : null,
            Start = closed ? Day.AddDays(7) : null, End = closed ? Day.AddDays(11) : null };
        var d = Document(row);
        var columns = d.State.Settings.Columns.Add(new(PlanField.Start, "S", "Start", "DATE"));
        d = d with { State = d.State with { Settings = d.State.Settings with { Columns = mapped ? columns : [] },
            Rows = [row with { Title = "Updated" }] } };
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        Assert.That(scheduled.Start.Value, Is.EqualTo(Day));
        Assert.That(scheduled.End.Value, Is.EqualTo(closed ? Day : Day.AddDays(3)));
        Assert.That(scheduled.Remaining, Is.EqualTo(closed ? 0 : 32));
        var expected = mapped ? closed ? new[] { PlanField.Title, PlanField.Start, PlanField.End, PlanField.Remaining }
            : new[] { PlanField.Title, PlanField.Start, PlanField.End } : new[] { PlanField.Title };
        var changes = PlanOperations.Changes(d, Day);
        Assert.That(changes.Fields["I1"], Is.EquivalentTo(expected));
        Assert.That(changes.RecalculatedFields.Select(c => c.Field), Is.EquivalentTo(expected.Where(f => f != PlanField.Title)));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(expected));
        Assert.That(review.Writes, Has.Length.EqualTo(expected.Length));
        if (!mapped) Assert.That(review.Writes.Single().Mutation, Is.EqualTo("updateIssue"));
    }
    [Test]
    public void ExplicitUnmappedDateStillRequiresAColumnToPublish()
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Fixed = true, Start = Day, End = Day };
        var d = Document(row);
        d = d with { State = d.State with { Settings = d.State.Settings with { Columns = [] },
            Rows = [row with { Start = Day.AddDays(1), End = Day.AddDays(1) }] } };
        Assert.That(PlanOperations.Changes(d, Day).Fields["I1"], Is.EquivalentTo(new[] { PlanField.Start, PlanField.End }));
        Assert.That(() => PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N")),
            Throws.InvalidOperationException.With.Message.Contains("列を割り当ててください"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ClosedTaskWithoutRemainingMappingPublishesOnlyTitleEdit(bool editTitle)
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = Day,
            Start = Day, End = Day };
        var d = Document(row);
        d = d with { State = d.State with {
            Settings = d.State.Settings with { Columns = d.State.Settings.Columns.Where(c => c.Role != PlanField.Remaining).ToImmutableArray() }
        } };
        if (editTitle) d = d with { State = PlanOperations.Apply(d,
            new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Updated")]), Day).State };
        Assert.That(PlanOperations.Schedule(d, Day).Single().Remaining, Is.Zero);
        Assert.That(d.State.Rows[0].Remaining, Is.Null);
        var expected = editTitle ? new[] { PlanField.Title } : Array.Empty<PlanField>();
        var unpublished = PlanOperations.Changes(d, Day);
        Assert.That(unpublished.Fields.Values.SelectMany(fields => fields), Is.EquivalentTo(expected));
        Assert.That(unpublished.RecalculatedFields, Is.Empty);
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(expected));
        Assert.That(review.Writes.Select(w => w.Mutation), Is.EqualTo(editTitle ? new[] { "updateIssue" } : Array.Empty<string>()));
        if (editTitle) {
            using var json = JsonDocument.Parse(review.Writes.Single().Input);
            Assert.That(json.RootElement.GetProperty("title").GetString(), Is.EqualTo("Updated"));
        }
    }

    [TestCase(null, true)]
    [TestCase(8, true)]
    [TestCase(0, false)]
    public void ClosedTaskPublishesOnlyCalculatedRemainingAndCloseDate(int? remaining, bool remainingChanged)
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = Day, Estimate = 40,
            Remaining = remaining, Actual = 56, Start = Day.AddDays(-7), End = Day.AddDays(2) };
        var d = Document(row);
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        Assert.That(scheduled.Input.IsComplete, Is.True);
        Assert.That(scheduled.Remaining, Is.Zero);
        Assert.That(scheduled.End.Value, Is.EqualTo(Day));
        Assert.That(scheduled.Start.Value, Is.EqualTo(row.Start));
        var changes = PlanOperations.Changes(d, Day);
        var expected = remainingChanged ? new[] { PlanField.Remaining, PlanField.End } : new[] { PlanField.End };
        Assert.That(changes.Fields["I1"], Is.EquivalentTo(expected));
        Assert.That(changes.RecalculatedFields, Is.EquivalentTo(expected.Select(f => ("I1", f))));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(expected));
        Assert.That(review.Writes, Has.Length.EqualTo(expected.Length));
        foreach (var write in review.Writes) {
            Assert.That(write.Mutation, Is.EqualTo("updateProjectV2ItemFieldValue"));
            using var json = JsonDocument.Parse(write.Input);
            var value = json.RootElement.GetProperty("value");
            if (json.RootElement.GetProperty("fieldId").GetString() == "R") Assert.That(value.GetProperty("number").GetDecimal(), Is.Zero);
            else Assert.That(value.GetProperty("date").GetString(), Is.EqualTo("2026-10-05"));
        }
    }
    [Test]
    public void CalculatedDefaultFeedsPeopleWithoutBeingPublished()
    {
        var d = Document(new("I1", "Work", "acme/repo") { Estimate = 40, Actual = 8, Start = Day, End = Day.AddDays(3) });
        var people = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Week, 1).People.Single(p => p.Identity == PlanPeople.Unassigned);
        Assert.That((people.Remaining, people.Forecast), Is.EqualTo((32m, 40m)));
        Assert.That(people.Missing, Does.Not.Contain("残が未入力"));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes, Is.Empty);
        Assert.That(d.State.Rows[0].Remaining, Is.Null);
    }
    [Test]
    public void ReopeningClearsCloseDateAndSchedulesAgain()
    {
        var d = Document(new("I1", "Work", "acme/repo") { Closed = true, CloseDate = Day.AddDays(-3), Remaining = 16 });
        var remote = d.Baseline with { Rows = [d.Baseline.Rows[0] with { Closed = false, CloseDate = null }] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.State.Rows[0].CloseDate, Is.Null);
        var scheduled = PlanOperations.Schedule(merged, Day).Single();
        Assert.That(scheduled.Input.IsComplete, Is.False);
        Assert.That(scheduled.End.Value, Is.EqualTo(Day.AddDays(1)));
    }
}
