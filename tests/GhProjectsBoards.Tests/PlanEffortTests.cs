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
        // #308 R9: the closed task keeps its plan dates; the close day is only its effective end.
        Assert.That((tasks[0].Start.Value, tasks[0].End.Value), Is.EqualTo(((DateOnly?)row.Start, (DateOnly?)row.End)));
        Assert.That(tasks[1].Warnings, Is.Empty);
        // A successor waits for the close day and may start on it (#236, R10).
        Assert.That(tasks[1].Start.Value, Is.EqualTo(close));
        Assert.That(tasks[1].End.Value, Is.EqualTo(close));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(mapped
            ? new[] { PlanField.Remaining } : Array.Empty<string>()),
            "The plan end already matches the baseline end, so a close publishes no date.");
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
    [Test]
    public void EarlyClosureKeepsThePlanDatesAndTheSuccessorUsesTheCloseDay()
    {
        var close = new DateOnly(2026, 10, 9);
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = close,
            Remaining = 0, Start = new(2026, 10, 12), End = new(2026, 10, 16) };
        var d = Document(row);
        d = d with { State = d.State with { Settings = d.State.Settings with { Columns = d.State.Settings.Columns.Add(new(PlanField.Start, "S", "Start", "DATE")) } } };
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        // #308 R9: closing early no longer caps the start or substitutes the close day; the plan dates stay.
        Assert.That((scheduled.Start.Value, scheduled.End.Value), Is.EqualTo(((DateOnly?)new DateOnly(2026, 10, 12), (DateOnly?)new DateOnly(2026, 10, 16))));
        Assert.That((scheduled.Start.Origin, scheduled.End.Origin), Is.EqualTo((DateOrigin.Kept, DateOrigin.Kept)));
        Assert.That(scheduled.Warnings, Is.Empty);
        var changes = PlanOperations.Changes(d, Day);
        Assert.That(changes.Fields, Is.Empty, "Closing adds no plan date difference.");
        Assert.That(changes.RecalculatedFields, Is.Empty);
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes, Is.Empty, "A close publishes no date derived from the close day.");
        var successor = PlanScheduler.Calculate([PlanOperations.TaskInput(row), new("I2", 2) { Estimate = 8, Remaining = 0, Predecessors = ["I1"] }], new(), Day)[1];
        Assert.That((successor.Start.Value, successor.End.Value), Is.EqualTo(((DateOnly?)close, (DateOnly?)close)),
            "The close day remains the effective end for the successor (R10).");
    }
    [TestCase(false)]
    [TestCase(true)]
    public void InvertedKeptPairWithoutCloseDateIsStillInvalid(bool closed)
    {
        var result = PlanScheduler.Calculate([new("I1", 1) { Closed = closed, Start = Day.AddDays(2), End = Day }], new(), Day).Single();
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
    public void ReestimatedOverrunConsumesActualAndRemainingAtThePersonsRateFromItsStart()
    {
        // #236 rule 2: an in-progress task keeps its start and its end is the day Actual + Remaining is used
        // up from there. At 50% (4h a day) 8h done + 8h left reaches 10/8, and the load carries only the 8h left.
        var result = PlanScheduler.Calculate([new("I1", 1) { Estimate = 40, Actual = 8, Remaining = 8,
            Start = Day, Assignees = ["U1"] }], new() { People = [new("U1", 50)] }, Day).Single();
        Assert.That(result.Start.Value, Is.EqualTo(Day));
        Assert.That(result.End.Value, Is.EqualTo(Day.AddDays(3)));
        Assert.That(result.PlannedHours!.Values.Sum(), Is.EqualTo(8));
    }
    private static PlanDocument Document(params PlanRow[] rows)
    {
        ImmutableArray<PlanRow> values = [.. rows];
        return new(new(new("github.com", 42), "P1"), new(values, []), new(values, new() {
            StatusDate = Day, Columns = [new(PlanField.Remaining, "R", "Remaining", "NUMBER"), new(PlanField.End, "E", "End", "DATE")]
        }));
    }
    // A read invents no dates (#236 rule 5), so the plan holds the end its start implies only because the edit
    // that set that start laid it out. This does the same, and stamps only the end, because that is what
    // Reschedule writes.
    private static PlanDocument Laid(PlanDocument d)
    {
        var rows = d.State.Rows;
        var settings = d.State.Settings;
        var schedule = PlanScheduler.Calculate(rows.Select((r, i) => PlanOperations.TaskInput(r, i)).ToArray(),
            new PlanSettings { StatusDate = settings.StatusDate, ProjectStart = settings.ProjectStart,
                Calendar = new() { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() },
                People = settings.People.Select(p => new PlanPerson(p.Identity, p.Rate)).ToArray() },
            Day, rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal));
        var byId = schedule.ToDictionary(t => t.Input.Identity);
        return d with { State = d.State with { Rows = rows.Select(r => r with { End = byId[r.Identity].End.Value }).ToImmutableArray() } };
    }
    private static PlanRemoteSnapshot Remote(PlanDocument d) => new(d.Baseline, ImmutableDictionary<string, string>.Empty,
        d.State.Settings.Columns.Select(c => new ProjectFieldDefinition(new(d.Project.Scope, c.FieldId), d.Project, c.Name,
            "ProjectV2Field", c.DataType, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray(), 0, 0);
    [Test]
    public void CommittingCalculatedRemainingStoresPublishableInputThatSurvivesActualEdit()
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Estimate = 40, Actual = 8,
            Start = Day, End = Day };
        var d = Document(row);
        d = d with { State = d.State with { Settings = d.State.Settings with {
            Columns = d.State.Settings.Columns.Add(new(PlanField.Actual, "A", "Actual", "NUMBER")) } } };
        Assert.That(PlanOperations.Schedule(d, Day).Single().Remaining, Is.EqualTo(32));
        var original = PlanSheetEditing.EditBaseline(row, PlanField.Remaining, "32");
        Assert.That(original, Is.Empty, "Calculated text must not be the no-change baseline.");
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Remaining, PlanSheetEditing.Parse(d, PlanField.Remaining, "32", Day))]), Day).State };
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Actual, 16m)]), Day).State };
        Assert.That(d.State.Rows[0].Remaining, Is.EqualTo(32));
        Assert.That(PlanOperations.Schedule(d, Day).Single().Remaining, Is.EqualTo(32));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        // The Remaining edit reschedules the task, so its plan end moves with the work.
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(new[] { PlanField.Remaining, PlanField.Actual, PlanField.End }));
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
            // #236: a task with no dates of its own gets none, so the open case states its plan start and the
            // scheduler lays its end out from there.
            Start = closed ? Day.AddDays(7) : Day, End = closed ? Day.AddDays(11) : null };
        var d = Document(row);
        var columns = d.State.Settings.Columns.Add(new(PlanField.Start, "S", "Start", "DATE"));
        d = d with { State = d.State with { Settings = d.State.Settings with { Columns = mapped ? columns : [] },
            Rows = [row with { Title = "Updated" }] } };
        // The start this row holds was given by a PMO edit, and that edit laid the end out. Without this the
        // plan would hold no end at all and the automatic end field would be display-only, which under #236 is
        // not an unpublished change.
        d = Laid(d);
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        // #308 R9: a closed task keeps its plan start and end; its remaining work still leaves the schedule.
        Assert.That(scheduled.Start.Value, Is.EqualTo(closed ? Day.AddDays(7) : Day));
        // #236 rule 2: an in-progress task consumes Actual + Remaining from its plan start, so 8h done plus
        // 32h left at 8h a day reaches 10/9.
        Assert.That(scheduled.End.Value, Is.EqualTo(closed ? Day.AddDays(11) : Day.AddDays(4)));
        Assert.That(scheduled.Remaining, Is.EqualTo(closed ? 0 : 32));
        // #236: the open row keeps the plan start it already has, so its end is the automatic field here.
        // The closed row's plan dates match its baseline, so only the close's calculated remaining is a change.
        var expected = mapped ? closed ? new[] { PlanField.Title, PlanField.Remaining }
            : new[] { PlanField.Title, PlanField.End } : new[] { PlanField.Title };
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
        var row = new PlanRow("I1", "Work", "acme/repo") { Start = Day, End = Day };
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
        var expected = editTitle ? new[] { PlanField.Title } : Array.Empty<string>();
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
    public void ClosedTaskPublishesOnlyCalculatedRemaining(int? remaining, bool remainingChanged)
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = Day, Estimate = 40,
            Remaining = remaining, Actual = 56, Start = Day.AddDays(-7), End = Day.AddDays(2) };
        var d = Document(row);
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        Assert.That(scheduled.Input.IsComplete, Is.True);
        Assert.That(scheduled.Remaining, Is.Zero);
        Assert.That(scheduled.End.Value, Is.EqualTo(row.End), "The plan end stays a plan value (#308 R9).");
        Assert.That(scheduled.Start.Value, Is.EqualTo(row.Start));
        var changes = PlanOperations.Changes(d, Day);
        var expected = remainingChanged ? new[] { PlanField.Remaining } : Array.Empty<string>();
        Assert.That(changes.Fields.GetValueOrDefault("I1", []), Is.EquivalentTo(expected), "A close publishes no date change of its own.");
        Assert.That(changes.RecalculatedFields, Is.EquivalentTo(expected.Select(f => ("I1", f))));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Changes.Select(c => c.Field), Is.EquivalentTo(expected));
        Assert.That(review.Writes, Has.Length.EqualTo(expected.Length));
        Assert.That(review.Writes.All(write => write.Mutation == "updateProjectV2ItemFieldValue"), Is.True);
        foreach (var write in review.Writes) {
            using var json = JsonDocument.Parse(write.Input);
            Assert.That(json.RootElement.GetProperty("fieldId").GetString(), Is.EqualTo("R"));
            Assert.That(json.RootElement.GetProperty("value").GetProperty("number").GetDecimal(), Is.Zero);
        }
    }
    [Test]
    public void ClosedTaskPublishesItsStoredPlanEndAsAPlanDate()
    {
        // #308 R9: the end is a plan value even after the close, so a difference from GitHub publishes the
        // plan end, never the close day.
        var row = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = Day, Estimate = 8, Remaining = 8,
            Start = Day.AddDays(-2), End = Day.AddDays(2) };
        var d = Document(row);
        d = d with { Baseline = d.Baseline with { Rows = [row with { End = Day.AddDays(4) }] } };
        var scheduled = PlanOperations.Schedule(d, Day).Single();
        Assert.That((scheduled.Start.Value, scheduled.End.Value), Is.EqualTo(((DateOnly?)row.Start, (DateOnly?)row.End)));
        Assert.That(PlanOperations.Changes(d, Day).Fields["I1"], Does.Contain(PlanField.End));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        var end = review.Writes.Single(w => {
            using var json = JsonDocument.Parse(w.Input);
            return json.RootElement.GetProperty("fieldId").GetString() == "E";
        });
        using var input = JsonDocument.Parse(end.Input);
        Assert.That(input.RootElement.GetProperty("value").GetProperty("date").GetString(), Is.EqualTo("2026-10-07"),
            "The plan end is published, not the 2026-10-05 close day.");
    }

    [Test]
    public void ClosedTaskWithoutAPlanEndPublishesNoDateAndKeepsSuccessorsOnTheCloseDay()
    {
        var close = Day.AddDays(2);
        var closed = new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = close, Estimate = 8,
            Remaining = 8, Start = Day };
        // A read invents no dates, so the successor holds the plan start a PMO edit gave it; the close
        // day still pushes it (#236 rule 5, #308 R10).
        var successor = new PlanRow("I2", "Next", "acme/repo") { Estimate = 8, Remaining = 8, Predecessors = ["I1"], Start = Day, End = Day };
        var d = Document(closed, successor);
        var scheduled = PlanOperations.Schedule(d, Day).ToDictionary(t => t.Input.Identity);
        Assert.That(scheduled["I1"].End.Value, Is.Null, "The plan holds no end to publish.");
        Assert.That(scheduled["I2"].Start.Value, Is.EqualTo(close), "The close day is still the effective end (R10).");
        var changes = PlanOperations.Changes(d, Day);
        Assert.That(changes.Fields.GetValueOrDefault("I1", []), Does.Not.Contain(PlanField.End));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes.Any(w => w.Identity == "I1" && w.Input.Contains("\"date\"", StringComparison.Ordinal)), Is.False,
            "No date write is sent for a close.");
    }

    [Test]
    public void CalculatedDefaultFeedsPeopleWithoutBeingPublished()
    {
        var d = Document(new PlanRow("I1", "Work", "acme/repo") { Estimate = 40, Actual = 8, Start = Day, End = Day.AddDays(3) });
        var people = PlanPeople.Calculate(d, Day, Day, PlanPeriodScale.Week, 1).People.Single(p => p.Identity == PlanPeople.Unassigned);
        Assert.That((people.Remaining, people.Forecast), Is.EqualTo((32m, 40m)));
        Assert.That(people.Missing, Does.Not.Contain("見込み残存工数が未入力"));
        var review = PlanPublishPlan.Build(d, Remote(d), Day, Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes, Is.Empty);
        Assert.That(d.State.Rows[0].Remaining, Is.Null);
    }
    [Test]
    public void ReopeningClearsCloseDateAndSchedulesAgain()
    {
        var d = Document(new PlanRow("I1", "Work", "acme/repo") { Closed = true, CloseDate = Day.AddDays(-3), Remaining = 16, Start = Day });
        var remote = d.Baseline with { Rows = [d.Baseline.Rows[0] with { Closed = false, CloseDate = null }] };
        var merged = PlanMerge.Merge(d, remote);
        Assert.That(merged.State.Rows[0].CloseDate, Is.Null);
        var scheduled = PlanOperations.Schedule(merged, Day).Single();
        Assert.That(scheduled.Input.IsComplete, Is.False);
        Assert.That(scheduled.End.Value, Is.EqualTo(Day.AddDays(1)));
    }
}
