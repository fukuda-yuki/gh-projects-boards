using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #263: tasks are named by Issue number, never by row position.
[TestFixture]
internal sealed class PlanTaskNamesTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly ScopedId Project = new(new("github.com", 42), "PVT_names");
    private string root = "";
    private readonly List<PlanSession> sessions = [];

    [SetUp]
    public void SetUp() => root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "task-names-" + Guid.NewGuid().ToString("N"));
    [TearDown]
    public async Task TearDown()
    {
        foreach (var session in sessions) await session.FlushAsync();
        sessions.Clear();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static PlanRow Row(string identity, string? predecessor = null, string repository = "acme/repo")
        => new(identity, "Task " + identity, repository) { Estimate = 8, Predecessors = predecessor is null ? [] : [predecessor] };
    // Rows I18, I19… carry Issues acme/repo#18, #19…; a caption names another repository.
    private static PlanDocument Document(params PlanRow[] rows) => new(Project, new([.. rows], []),
        new([.. rows], new() { DefaultRepository = "acme/repo", StatusDate = Today, ProjectStart = Today }))
    {
        Sync = new() { IssueLinks = rows.Where(r => r.Identity.StartsWith('I')).ToImmutableDictionary(r => r.Identity, r => {
            var number = r.Identity[1..];
            return new PlanIssueLink($"{r.Repository}#{number}", $"https://github.com/{r.Repository}/issues/{number}");
        }) }
    };
    private async Task<PlanSession> Session(PlanDocument document)
    {
        var session = await PlanSession.CreateAsync(new(root), document, Today); sessions.Add(session); return session;
    }
    private static string[] Names(PlanDocument document, params string[] identities)
        => identities.Select(id => PlanTaskNames.Of(document)[id]).ToArray();

    [Test]
    public async Task InsertingAndMovingRowsRenamesNoPublishedTask()
    {
        var session = await Session(Document(Row("I18"), Row("I19", "I18"), Row("I20", "I19")));
        Assert.That(Names(session.Document, "I18", "I19", "I20"), Is.EqualTo(new[] { "#18", "#19", "#20" }));
        var inserted = PlanRow.New("挿入", "acme/web");
        await session.Execute(new InsertPlanRows([inserted], "I18"), Today);
        await session.Execute(new MovePlanSubtrees(["I20"], "I18"), Today);
        Assert.Multiple(() => {
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { inserted.Identity, "I20", "I18", "I19" }), "Both commands moved rows.");
            Assert.That(Names(session.Document, "I18", "I19", "I20"), Is.EqualTo(new[] { "#18", "#19", "#20" }),
                "A local row in another repository adds no repository to Issue names.");
            Assert.That(Names(session.Document, inserted.Identity), Is.EqualTo(new[] { "新1" }));
            Assert.That(PlanTaskNames.Of(session.Document).List(session.Document.State.Rows.Single(r => r.Identity == "I20").Predecessors), Is.EqualTo("#19"));
        });
    }

    [Test]
    public void IssuesOfSeveralRepositoriesAreNamedWithTheRepository()
    {
        var document = Document(Row("I18"), Row("I18x", repository: "acme/web"));
        document = document with { Sync = document.Sync with { IssueLinks = document.Sync.IssueLinks.SetItem("I18x",
            new("acme/web#18", "https://github.com/acme/web/issues/18")) } };
        Assert.That(Names(document, "I18", "I18x"), Is.EqualTo(new[] { "repo#18", "web#18" }));
        Assert.That(PlanTaskNames.Of(document).CarryRepository, Is.True);
    }

    [Test]
    public async Task UndoAndRedoOfAPredecessorTypedByTaskNameRestoreEachState()
    {
        var local = PlanRow.New("新しい作業");
        var session = await Session(Document(Row("I18"), Row("I19")));
        await session.Execute(new InsertPlanRows([local]), Today);
        var typed = PlanSheetEditing.Parse(session.Document, PlanField.Predecessors, "＃１８、新１", Today, "I19");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I19", PlanField.Predecessors, typed)]), Today);
        string[] Predecessors() => [.. session.Document.State.Rows.Single(r => r.Identity == "I19").Predecessors];
        Assert.That(Predecessors(), Is.EqualTo(new[] { "I18", local.Identity }));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(Predecessors(), Is.Empty);
        Assert.That((await session.Redo(Today)).Succeeded, Is.True);
        Assert.That(Predecessors(), Is.EqualTo(new[] { "I18", local.Identity }));
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That((await session.Undo(Today)).Succeeded, Is.True);
        Assert.That(Names(session.Document, "I18", "I19"), Is.EqualTo(new[] { "#18", "#19" }), "Undoing the insert renames nothing.");
    }

    [Test]
    public async Task RefusalsNameTasksByIssueNumber()
    {
        var session = await Session(Document(Row("I16"), Row("I17", "I16")));
        Assert.That(() => session.Execute(new IndentPlanRows(["I17"]), Today), Throws.TypeOf<PlanCycleException>().With.Message
            .EqualTo("#17 は #16 の後に始まるため、#17 を #16 の下にインデントできません。先にリンクを解除してください。"));
        var cycle = PlanSheetEditing.Parse(session.Document, PlanField.Predecessors, "17", Today, "I16");
        Assert.That(() => session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I16", PlanField.Predecessors, cycle)]), Today),
            Throws.TypeOf<PlanCycleException>().With.Message.EqualTo("循環参照: #16, #17"));
        Assert.That(session.UndoCount, Is.Zero, "Refusals change nothing.");
    }

    [Test]
    public void ACycleInGitHubsOwnLinksIsNamedByIssueWhereverThePlanIsValidated()
    {
        // Open, refresh, reset and another window's edits all validate the whole plan; a cycle that GitHub's
        // blocked-by links form must reach the PMO by task name there too, not by row position.
        var document = Document(Row("I17"), Row("I18", "I19"), Row("I19", "I18"));
        Assert.That(() => PlanOperations.ValidateDocument(document, Today), Throws.TypeOf<PlanCycleException>().With.Message
            .EqualTo("循環参照: #18, #19").And.Message.Not.Contain("行目"));
    }

    [Test]
    public void TheChooserOffersTheSameRequirementsPrecedingPhaseFirstAndNoRequirementRow()
    {
        PlanRow Task(string id, string parent, string phase) => Row(id) with { Parent = parent, Phase = phase };
        var rows = new[] { Row("R1") with { Estimate = null }, Task("I2", "R1", "UT"), Task("I3", "R1", "PS"), Task("I4", "R1", "IT"),
            Row("R5") with { Estimate = null }, Task("I6", "R5", "PS"), Row("I7") };
        var document = Document(rows) with { Baseline = new([.. rows], [new("phase", "工程", "SINGLE_SELECT") { Options = ["PS", "UT", "IT"] }]) };
        document = document with { State = document.State with { Settings = document.State.Settings with { Columns = [new(PlanField.Phase, "phase", "工程", "SINGLE_SELECT")] } } };
        Assert.That(PlanPredecessorChoices.Rank(document, "I4"), Is.EqualTo(new[] { "I2", "I3", "I6", "I7" }),
            "UT precedes IT under R1; then the rest of R1; then the plan. R1 and R5 are requirement rows.");
    }

    [Test]
    public async Task APlanSavedByMainOpensUnchangedAndItsTasksAreNamedByIssue()
    {
        // The fixture is a checkpoint written by main (86039fae) before #263; #263 adds nothing to the stored
        // plan, so it opens as saved, and the names are derived on open.
        var store = new PlanStore(root);
        Directory.CreateDirectory(store.Root);
        var path = store.FileFor(Project);
        using (var fixture = typeof(PlanTaskNamesTests).Assembly.GetManifestResourceStream("GhProjectsBoards.Tests.Fixtures.TaskNamesMainPlan.json")!)
        using (var file = File.Create(path)) await fixture.CopyToAsync(file);
        var saved = await File.ReadAllBytesAsync(path);

        var opened = await PlanSession.OpenAsync(store, Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        var session = opened.Session!; sessions.Add(session);
        var document = session.Document;
        Assert.Multiple(() => {
            Assert.That(document.State.Rows.Select(r => PlanTaskNames.Of(document)[r.Identity]), Is.EqualTo(new[] { "#18", "#19", "新1" }));
            Assert.That(PlanTaskNames.Of(document).List(document.State.Rows[2].Predecessors), Is.EqualTo("#19"));
            Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((2, 0)));
            Assert.That(PlanOperations.Changes(document, Today).Fields.Keys, Is.EquivalentTo(new[] { "I19", document.State.Rows[2].Identity }));
        });
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(saved), "Opening rewrites nothing.");
        Assert.That(PlanJson.Text(document), Does.Not.Contain("新1"), "Names are never stored.");
    }
}
