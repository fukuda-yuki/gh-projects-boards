using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #229: a typed 開始日 is the plan start and a predecessor that ends later wins. The commit that produced
// that difference reports which predecessor won and the resulting date, and the command it offers removes
// exactly that link as one ordinary cell operation.
[TestFixture]
internal sealed class PlanStartNoticeTests
{
    private static DateOnly D(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static readonly DateOnly Today = D("2026-01-12"); // Monday
    private static readonly ScopedId Project = new(new("github.com", 42), "PVT_start_notice");
    private string root = null!;
    private readonly List<PlanSession> sessions = [];

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "ghpb-start-notice-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public async Task TearDown()
    {
        foreach (var session in sessions) await session.FlushAsync();
        sessions.Clear();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static PlanRow Row(string identity) => new(identity, "Task " + identity, "acme/repo");
    // A closed Issue ends on its close date, and its successor may start on that day: this one ends 2/17.
    private static PlanRow EndsOnFebruary17() => Row("I44") with
        { Closed = true, CloseDate = D("2026-02-17"), Start = D("2026-02-16"), End = D("2026-02-17") };
    private static PlanRow EndsOnJanuary5() => Row("I43") with
        { Closed = true, CloseDate = D("2026-01-05"), Start = D("2026-01-05"), End = D("2026-01-05") };
    // The task the PMO types a start on.
    private static PlanRow Task(params string[] predecessors) => Row("I45") with
        { Estimate = 8, Remaining = 8, Predecessors = [.. predecessors] };

    private static PlanDocument Document(DateOnly? projectStart = null, params PlanRow[] rows)
        => new(Project, new([.. rows], []), new([.. rows], new()
        {
            DefaultRepository = "acme/repo", StatusDate = Today, ProjectStart = projectStart,
            Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "End date", "DATE")]
        }))
        {
            Sync = new()
            {
                IssueLinks = rows.ToImmutableDictionary(r => r.Identity, r =>
                {
                    var number = r.Identity[1..];
                    return new PlanIssueLink($"{r.Repository}#{number}", $"https://github.com/{r.Repository}/issues/{number}");
                })
            }
        };

    private static PlanDocument Edited(PlanDocument document, string identity, string field, object? value)
        => document with { State = PlanOperations.Apply(document, new EditPlanCells(PlanOperationKind.Cell, [new(identity, field, value)]), Today).State };

    private static ScheduledTask Scheduled(PlanDocument document, string identity)
        => PlanOperations.Schedule(document, Today).Single(t => t.Input.Identity == identity);

    private async Task<PlanSession> Session(PlanDocument document)
    {
        var session = await PlanSession.CreateAsync(new(root), document, Today); sessions.Add(session); return session;
    }

    [Test]
    public void ATypedStartThatAPredecessorPushesLaterReportsThePredecessorAndTheResultingDate()
    {
        var edited = Edited(Document(null, EndsOnFebruary17(), Task("I44")), "I45", PlanField.Start, D("2026-01-12"));

        var notice = PlanStartNotice.Detect(edited, "I45", D("2026-01-12"), Today);

        Assert.That(notice, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(notice!.PredecessorName, Is.EqualTo("#44"));
            Assert.That(notice.PredecessorName, Does.Not.Contain("行目"), "No text names a task by its row position.");
            Assert.That(notice.Typed, Is.EqualTo(D("2026-01-12")));
            Assert.That(notice.Scheduled, Is.EqualTo(D("2026-02-17")));
            Assert.That(edited.State.Rows.Single(r => r.Identity == "I45").Start, Is.EqualTo(D("2026-01-12")),
                "The typed start stays the plan start; only the scheduled date moved.");
            Assert.That(notice.PredecessorIdentity, Is.EqualTo("I44"));
        });
    }

    [Test]
    public void ATypedStartThatHoldsReportsNothing()
    {
        // Later than the predecessor's end.
        var holds = Edited(Document(null, EndsOnFebruary17(), Task("I44")), "I45", PlanField.Start, D("2026-03-02"));
        // No predecessor at all.
        var free = Edited(Document(null, EndsOnFebruary17(), Task()), "I45", PlanField.Start, D("2026-01-12"));

        Assert.Multiple(() =>
        {
            Assert.That(Scheduled(holds, "I45").Start.Value, Is.EqualTo(D("2026-03-02")));
            Assert.That(PlanStartNotice.Detect(holds, "I45", D("2026-03-02"), Today), Is.Null);
            Assert.That(Scheduled(free, "I45").Start.Value, Is.EqualTo(D("2026-01-12")));
            Assert.That(PlanStartNotice.Detect(free, "I45", D("2026-01-12"), Today), Is.Null);
        });
    }

    [Test]
    public void AStartMovedOnlyByTheProjectStartReportsNothing()
    {
        // #229 scope: the notice reports a predecessor that moved a typed start, not the Project start.
        var edited = Edited(Document(D("2026-03-02"), EndsOnFebruary17(), Task()), "I45", PlanField.Start, D("2026-01-12"));

        Assert.Multiple(() =>
        {
            Assert.That(Scheduled(edited, "I45").Start.Value, Is.EqualTo(D("2026-03-02")));
            Assert.That(Scheduled(edited, "I45").StartReason, Is.EqualTo("プロジェクト開始日"));
            Assert.That(PlanStartNotice.Detect(edited, "I45", D("2026-01-12"), Today), Is.Null);
        });
    }

    [Test]
    public async Task TheUnlinkCommandRemovesExactlyTheWinningPredecessorInOneUndoStep()
    {
        var session = await Session(Document(null, EndsOnJanuary5(), EndsOnFebruary17(), Task("I43", "I44")));
        var typed = await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I45", PlanField.Start, D("2026-01-12"))]), Today);
        Assert.That(typed.Succeeded, Is.True, typed.Error);
        var notice = PlanStartNotice.Detect(session.Document, "I45", D("2026-01-12"), Today);
        Assert.That(notice, Is.Not.Null);
        Assert.That(notice!.PredecessorName, Is.EqualTo("#44"), "The predecessor that finished later won, so it is the one reported.");
        var command = PlanStartNotice.Unlink(session.Document, notice);
        Assert.That(command, Is.Not.Null);

        var undo = session.UndoCount;
        Assert.That((await session.Execute(command!, Today)).Succeeded, Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(session.UndoCount, Is.EqualTo(undo + 1), "Removing the link is one ordinary cell operation and one Undo step.");
            Assert.That(Predecessors(session), Is.EqualTo(new[] { "I43" }), "Only the winning link is removed.");
            Assert.That(Scheduled(session.Document, "I45").Start.Value, Is.EqualTo(D("2026-01-12")), "The typed start then holds.");
        });

        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(Predecessors(session), Is.EqualTo(new[] { "I43", "I44" }), "Undo restores the link that was removed.");
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I45").Start, Is.EqualTo(D("2026-01-12")),
            "The typed start was a separate earlier operation and keeps its own Undo step.");

        static string[] Predecessors(PlanSession session)
            => [.. session.Document.State.Rows.Single(r => r.Identity == "I45").Predecessors];
    }
}
