using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #260 / #227 / #238: one 遅れ set, the attention sets, the one filter model, the chase list, the summary
// verdict and the read observations the strip shows.
[TestFixture]
internal sealed class PlanAttentionTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    [SetUp] public void Setup() => root = Path.Combine(Path.GetTempPath(), "ghpb-attention-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static readonly ProjectPlanSettings Settings = new() { StatusDate = Day,
        People = [new("U1", "person-U1", 100, null), new("U2", "person-U2", 100, null)],
        Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE"),
            new(PlanField.Actual, "actual", "Actual", "NUMBER"), new(PlanField.Remaining, "remaining", "Remaining", "NUMBER")] };
    private static readonly ImmutableArray<PlanColumnDefinition> ProgressColumns =
        [new("actual", "Actual", "NUMBER"), new("remaining", "Remaining", "NUMBER")];
    private static PlanRow Row(string id, string? phase = null, params string[] assignees)
        => new(id, id, "acme/repo") { Phase = phase, Assignees = [.. assignees] };
    private static PlanDocument Document(params PlanRow[] rows) => Document(Settings, rows);
    private static PlanDocument Document(ProjectPlanSettings settings, params PlanRow[] rows)
        => new(Project, new(rows.ToImmutableArray(), ProgressColumns), new(rows.ToImmutableArray(), settings));
    private static (PlanAttention Attention, PlanLatenessResult Lateness, ImmutableArray<ScheduledTask> Scheduled) Analyze(PlanDocument document, TimeZoneInfo? zone = null)
    {
        var scheduled = PlanOperations.Schedule(document, Day).ToImmutableArray();
        var lateness = PlanLateness.Classify(scheduled, new PlanCalendar(), Day);
        return (PlanAttention.From(document, scheduled, lateness, Day, zone ?? TimeZoneInfo.Utc), lateness, scheduled);
    }
    private static ImmutableHashSet<string> Set(IEnumerable<string> identities) => identities.ToImmutableHashSet(StringComparer.Ordinal);
    private static ImmutableDictionary<string, DateTimeOffset?> ProgressMap(params (string Identity, DateTimeOffset? At)[] entries)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var (identity, at) in entries) builder[identity] = at;
        return builder.ToImmutable();
    }

    [Test]
    public void OneLateSetHoldsBothTheRedAndTheOchreCase()
    {
        // The owner decision D0: one 「遅れ」 count. A passed 終了日 stays red and a later forecast stays ochre,
        // but both are in the same set and counted once.
        var document = Document(
            Row("overdue") with { Start = Day.AddDays(-9), End = Day.AddDays(-2) },
            Row("late") with { Start = Day.AddDays(-4), End = Day, Estimate = 24, Actual = 8, Remaining = 16 },
            Row("ontime") with { Start = Day.AddDays(-4), End = Day.AddDays(2), Estimate = 16, Actual = 8, Remaining = 8 });

        var (attention, lateness, _) = Analyze(document);

        Assert.Multiple(() =>
        {
            Assert.That(lateness.Tasks["overdue"].Level, Is.EqualTo(PlanLatenessLevel.Overdue));
            Assert.That(lateness.Tasks["late"].Level, Is.EqualTo(PlanLatenessLevel.Later));
            Assert.That(attention.Late, Is.EqualTo(Set(["overdue", "late"])), "One set holds both cases.");
            Assert.That(lateness.LateTasks, Is.EqualTo(2));
            Assert.That(attention.Cause, Is.Empty, "No late task delays a successor.");
        });
    }

    [Test]
    public void TheCauseIsTheRootOfADelayChainNotEveryLateMember()
    {
        var document = Document(
            Row("a") with { Start = Day.AddDays(-4), End = Day.AddDays(-2), Estimate = 24, Actual = 8, Remaining = 16 },
            Row("b") with { Start = Day, End = Day, Estimate = 8, Remaining = 8, Predecessors = ["a"] },
            Row("c") with { Start = Day.AddDays(1), End = Day.AddDays(1), Estimate = 8, Remaining = 8, Predecessors = ["b"] });

        var (attention, lateness, _) = Analyze(document);

        Assert.Multiple(() =>
        {
            Assert.That(attention.Late, Is.EqualTo(Set(["a", "b", "c"])));
            Assert.That(attention.Cause, Is.EqualTo(Set(["a"])), "Only the root of the chain is a cause.");
            Assert.That(lateness.DelayedSuccessors("a"), Is.EqualTo(new[] { "b", "c" }));
            Assert.That(lateness.DelayedSuccessors("c"), Is.Empty);
        });
    }

    [Test]
    public void OverEstimateIsAnIndependentSet()
    {
        var document = Document(
            Row("over") with { Start = Day, End = Day.AddDays(3), Estimate = 8, Actual = 6, Remaining = 6 },
            Row("fits") with { Start = Day, End = Day.AddDays(3), Estimate = 16, Actual = 8, Remaining = 8 });

        var (attention, _, _) = Analyze(document);

        Assert.Multiple(() =>
        {
            Assert.That(attention.OverEstimate, Is.EqualTo(Set(["over"])));
            Assert.That(attention.Late, Is.Empty);
        });
    }

    [Test]
    public void NotUpdatedNeedsAnOpenTaskStartedByTheStatusDateAndSevenStaleDays()
    {
        // UTC keeps the change date independent of the machine's zone.
        var stale = new DateTimeOffset(Day.AddDays(-7).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var recent = new DateTimeOffset(Day.AddDays(-6).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        var document = Document(
            Row("stale") with { Start = Day.AddDays(-10) },
            Row("recent") with { Start = Day.AddDays(-10) },
            Row("notstarted") with { Start = Day.AddDays(1) },
            Row("closed") with { Start = Day.AddDays(-10), Closed = true },
            Row("novalues") with { Start = Day.AddDays(-10) },
            Row("unobserved") with { Start = Day.AddDays(-10) }) with
        {
            Sync = new()
            {
                ProgressUpdatedAt = ProgressMap(("stale", stale), ("recent", recent), ("notstarted", stale), ("closed", stale), ("novalues", null))
            }
        };

        var (attention, _, _) = Analyze(document);

        Assert.Multiple(() =>
        {
            Assert.That(attention.NotUpdatedUnavailable, Is.Null, "Both progress values are Project fields.");
            Assert.That(attention.NotUpdated, Is.EqualTo(Set(["stale", "novalues"])),
                "Exactly 7 days counts as stale; a row GitHub holds neither value for is stale; an unobserved row is unknown.");
        });
    }

    [Test]
    public void NotUpdatedIsNotJudgedForAnIssueFieldOrAnUnmappedProgressColumnAndSaysWhy()
    {
        var issueField = Document(Settings with { Columns = Settings.Columns }, Row("task") with { Start = Day.AddDays(-10) });
        issueField = issueField with { Baseline = issueField.Baseline with { Columns = [new("actual", "Actual", "NUMBER") { IssueField = true }, new("remaining", "Remaining", "NUMBER")] } };
        var unmapped = Document(Settings with { Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "Target date", "DATE")] },
            Row("task") with { Start = Day.AddDays(-10) });

        Assert.Multiple(() =>
        {
            var (issueAttention, _, _) = Analyze(issueField);
            Assert.That(issueAttention.NotUpdated, Is.Empty);
            Assert.That(issueAttention.NotUpdatedUnavailable, Is.EqualTo(PlanAttention.IssueFieldProgress));
            var (unmappedAttention, _, _) = Analyze(unmapped);
            Assert.That(unmappedAttention.NotUpdatedUnavailable, Is.EqualTo(PlanAttention.UnmappedProgress));
        });
    }

    [Test]
    public void FilterCombinesPartsWithAndAndChoicesInsideAPartWithOr()
    {
        var document = Document(
            Row("a", "SS", "U1") with { Title = "設計レビュー" },
            Row("b", "SS", "U2") with { Title = "実装" },
            Row("c", "PS", "U1") with { Title = "試験" },
            Row("d", "PS") with { Title = "総括" },
            Row("e") with { Title = "工程なしの設計" });
        var (attention, _, _) = Analyze(document);

        var people = attention.Apply(new PlanFilter { Assignees = Set(["U1", "U2"]) }, document.State.Rows, Set([]));
        var phase = attention.Apply(new PlanFilter { Phases = Set(["SS"]) }, document.State.Rows, Set([]));
        var both = attention.Apply(new PlanFilter { Assignees = Set(["U1"]), Phases = Set(["PS"]) }, document.State.Rows, Set([]));
        var title = attention.Apply(new PlanFilter { Title = "設計" }, document.State.Rows, Set([]));
        var unassigned = attention.Apply(new PlanFilter { Assignees = Set([PlanPeople.Unassigned]) }, document.State.Rows, Set([]));
        var noPhase = attention.Apply(new PlanFilter { Phases = Set([""]) }, document.State.Rows, Set([]));

        Assert.Multiple(() =>
        {
            Assert.That(people.Matches, Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(phase.Matches, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(both.Matches, Is.EqualTo(new[] { "c" }), "AND across parts.");
            Assert.That(title.Matches, Is.EqualTo(new[] { "a", "e" }), "Title search is text, not a count.");
            Assert.That(unassigned.Matches, Is.EqualTo(new[] { "d", "e" }), "担当者なし is a value.");
            Assert.That(noPhase.Matches, Is.EqualTo(new[] { "e" }), "工程なし is a value while an unmapped task exists.");
            Assert.That(new PlanFilter().IsActive, Is.False);
            Assert.That(new PlanFilter { Title = "a" }.IsActive, Is.True);
        });
    }

    [Test]
    public void LatenessFiltersMatchTasksOnlyWhileTitleAndTheReadFiltersMayMatchRequirementRows()
    {
        var document = Document(
            Row("R") with { Start = Day.AddDays(-10), End = Day.AddDays(-2) },
            Row("T") with { Parent = "R", Start = Day.AddDays(-10), End = Day.AddDays(-2) }) with
        {
            Sync = new()
            {
                Conflicts = [new("R", PlanField.Title, "旧", "新", "GitHub")],
                LastRead = new(new(Day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero), ["R"])
            }
        };
        var (attention, _, _) = Analyze(document);
        var summaries = Set(["R"]);

        var late = attention.Apply(new PlanFilter { Toggles = [PlanToggle.Late] }, document.State.Rows, summaries);
        var title = attention.Apply(new PlanFilter { Title = "R" }, document.State.Rows, summaries);
        var conflict = attention.Apply(new PlanFilter { Toggles = [PlanToggle.Conflict] }, document.State.Rows, summaries);
        var added = attention.Apply(new PlanFilter { Toggles = [PlanToggle.Added] }, document.State.Rows, summaries);

        Assert.Multiple(() =>
        {
            Assert.That(late.Matches, Is.EqualTo(new[] { "T" }), "A lateness filter matches tasks only.");
            Assert.That(late.Context, Is.EqualTo(Set(["R"])), "The requirement row stays as context.");
            Assert.That(title.Matches, Is.EqualTo(new[] { "R" }));
            Assert.That(conflict.Matches, Is.EqualTo(new[] { "R" }));
            Assert.That(added.Matches, Is.EqualTo(new[] { "R" }));
        });
    }

    [Test]
    public void RequirementParentsAreContextForEveryFilterAndAreNeverCounted()
    {
        var document = Document(
            Row("R1"),
            Row("T1") with { Parent = "R1", Start = Day.AddDays(-4), End = Day },
            Row("R2"),
            Row("T2", "SS", "U1") with { Parent = "R2", Title = "設計", Start = Day, End = Day.AddDays(2) });
        var (attention, _, _) = Analyze(document);
        var summaries = Set(["R1", "R2"]);

        var filter = new PlanFilter { Title = "設計" };
        var result = attention.Apply(filter, document.State.Rows, summaries);

        Assert.Multiple(() =>
        {
            Assert.That(result.Matches, Is.EqualTo(new[] { "T2" }));
            Assert.That(result.Context, Is.EqualTo(Set(["R2"])), "Context rows are not matches and are not counted.");
        });
    }

    [Test]
    public void ANamedSetMatchesExactlyItsIdentitiesAndCanIncludeRequirementRows()
    {
        var document = Document(Row("R"), Row("T") with { Parent = "R" }, Row("U"));
        var (attention, _, _) = Analyze(document);
        var summaries = Set(["R"]);

        var tasks = attention.Apply(new PlanFilter { Named = new("後続 1", Set(["T", "R"])) }, document.State.Rows, summaries);
        var withSummaries = attention.Apply(new PlanFilter { Named = new("サマリーのタスク", Set(["T", "R"]), IncludeSummaries: true) }, document.State.Rows, summaries);

        Assert.Multiple(() =>
        {
            Assert.That(tasks.Matches, Is.EqualTo(new[] { "T" }));
            Assert.That(tasks.Context, Is.EqualTo(Set(["R"])));
            Assert.That(withSummaries.Matches, Is.EqualTo(new[] { "R", "T" }));
        });
    }

    [Test]
    public void TheChaseListGroupsMatchesPerAssigneeMostFirstAndCopiesIssueLinks()
    {
        var document = Document(
            Row("a", null, "U1"),
            Row("b", null, "U1", "U2"),
            Row("c", null, "U2"),
            Row("d")) with
        {
            Sync = new()
            {
                IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty
                    .Add("a", new("acme/repo#1", "https://github.com/acme/repo/issues/1"))
                    .Add("b", new("acme/repo#2", "https://github.com/acme/repo/issues/2"))
            }
        };
        var (attention, _, _) = Analyze(document);
        var matches = attention.Apply(new PlanFilter { Title = "" }, document.State.Rows, Set([])).Matches;
        Assert.That(matches, Is.Empty, "An inactive filter has no matches.");
        var all = new[] { "a", "b", "c", "d" };

        var groups = PlanChase.Groups(document, all, Set([]));

        Assert.Multiple(() =>
        {
            Assert.That(groups.Select(group => group.Name), Is.EqualTo(new[] { "person-U1", "person-U2", "担当者なし" }));
            Assert.That(groups[0].Tasks, Is.EqualTo(new[] { "a", "b" }), "A task with two assignees counts for each.");
            Assert.That(groups[1].Tasks, Is.EqualTo(new[] { "b", "c" }));
            Assert.That(PlanChase.Line(groups), Is.EqualTo("person-U1 2 · person-U2 2 · 担当者なし 1"));
            var text = PlanChase.Text(document, groups);
            Assert.That(text, Does.StartWith("person-U1（2）\r\n"));
            Assert.That(text, Does.Contain("#1 a https://github.com/acme/repo/issues/1"));
            Assert.That(text, Does.Contain("#2 b https://github.com/acme/repo/issues/2"));
            Assert.That(text, Does.Contain("担当者なし（1）"), "A task without an Issue carries no URL.");
        });
    }

    [Test]
    public void VerdictNamesNoSlipWhenNothingSlips()
    {
        var document = Document(Settings with { ProjectStart = Day, ProjectEnd = Day.AddDays(20) },
            Row("task", "SS", "U1") with { Start = Day, End = Day.AddDays(2), Estimate = 8 });
        var scheduled = PlanOperations.Schedule(document, Day).ToArray();
        var plan = PlanSummary.Calculate(document, scheduled, Day);

        var verdict = PlanSummary.Verdict(plan, document.State.Settings);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Slipping, Is.False);
            Assert.That(verdict.Headline, Is.EqualTo("目標日と終了日に遅れはありません"));
            Assert.That(verdict.SlippingPhases, Is.Empty);
        });
    }

    [Test]
    public void VerdictStatesTheVersionSlipFirstInPlainWords()
    {
        var document = Document(Settings with { ProjectStart = Day, ProjectEnd = Day.AddDays(2) },
            Row("task", "SS", "U1") with { Start = Day, End = Day.AddDays(2), Estimate = 40, Remaining = 40 });
        var scheduled = PlanOperations.Schedule(document, Day).ToArray();
        var plan = PlanSummary.Calculate(document, scheduled, Day);

        var verdict = PlanSummary.Verdict(plan, document.State.Settings);

        Assert.Multiple(() =>
        {
            Assert.That(plan.FinishDifference, Is.GreaterThan(0));
            Assert.That(verdict.Slipping, Is.True);
            Assert.That(verdict.Headline, Does.StartWith("完了見込 "));
            Assert.That(verdict.Headline, Does.Contain("終了日"));
            Assert.That(verdict.Headline, Does.Contain("稼働日遅れ"));
        });
    }

    [Test]
    public async Task AReadStoresTheReadTimeThePlacedIssuesAndTheProgressChangeTimes()
    {
        var a = Row("a", null, "U1") with { Start = Day, End = Day.AddDays(2), Estimate = 8 };
        var document = Document(a);
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        var changed = new DateTimeOffset(Day.AddDays(-8).ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero);
        var remote = Remote(document, ImmutableDictionary<string, DateTimeOffset?>.Empty.Add("a", changed));

        await session.AcceptRefresh(remote, Day);

        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Sync.LastRead, Is.Not.Null);
            Assert.That(DateOnly.FromDateTime(session.Document.Sync.LastRead!.At.Date), Is.EqualTo(Day), "The read time is on the read day.");
            Assert.That(session.Document.Sync.LastRead.Added, Is.Empty, "Opening a known plan places nothing.");
            Assert.That(session.Document.Sync.ProgressUpdatedAt["a"], Is.EqualTo(changed));
        });

        // The next complete read places the Issue it meets for the first time and clears the previous list.
        var b = Row("b") with { Start = Day, End = Day.AddDays(1), Estimate = 8 };
        var readBaseline = session.Document.Baseline with { Rows = session.Document.Baseline.Rows.Add(b) };
        var readAgain = new PlanRemoteSnapshot(readBaseline,
                readBaseline.Rows.ToImmutableDictionary(row => row.Identity, row => "T-" + row.Identity), [], 0, 0)
            { ProgressUpdatedAt = ImmutableDictionary<string, DateTimeOffset?>.Empty.Add("a", changed) };
        await session.AcceptRefresh(readAgain, Day);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Sync.LastRead!.Added, Is.EqualTo(new[] { "b" }));
            Assert.That(session.Document.Sync.ProgressUpdatedAt.ContainsKey("b"), Is.False, "The snapshot observed no 実績/残 time for it.");
        });
    }

    [Test]
    public async Task AScopedReadKeepsTheLastCompleteReadForTheStrip()
    {
        var document = Document(Row("a") with { Start = Day, End = Day.AddDays(2), Estimate = 8 });
        var session = await PlanSession.CreateAsync(new(root), document, Day);
        var changed = new DateTimeOffset(Day.AddDays(-8).ToDateTime(new TimeOnly(9, 30)), TimeSpan.Zero);
        await session.AcceptRefresh(Remote(document, ImmutableDictionary<string, DateTimeOffset?>.Empty.Add("a", changed)), Day);
        var complete = session.Document.Sync.LastRead;

        var scoped = Remote(document, ImmutableDictionary<string, DateTimeOffset?>.Empty) with { Scoped = true };
        await session.AcceptRefresh(scoped, Day, refresh: false);

        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Sync.LastRead, Is.EqualTo(complete), "A publish read is not a complete read.");
            Assert.That(session.Document.Sync.ProgressUpdatedAt, Is.Empty, "Only the rows the scoped read observed are replaced.");
        });
    }

    private static PlanRemoteSnapshot Remote(PlanDocument document, ImmutableDictionary<string, DateTimeOffset?> progress)
        => new(document.Baseline with { Rows = document.State.Rows }, document.State.Rows.ToImmutableDictionary(row => row.Identity, row => "T-" + row.Identity),
            [], 0, 0) { ProgressUpdatedAt = progress };
}
