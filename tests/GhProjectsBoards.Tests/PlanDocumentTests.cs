using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanDocumentTests
{
    [TestCase(null, "Backlog")]
    [TestCase("Done", "Done")]
    public async Task CreationAdoptionKeepsExplicitStatusAndAdoptsUnsetWorkflowValue(string? local, string expected)
    {
        var row = PlanRow.New("New", "acme/work") with { Status = local };
        var session = await Create(new(Project, new([], []), new([row], new())));
        var writes = ImmutableArray.Create(
            new PlanWrite("0", row.Identity, PlanPublishStage.Create, "createIssue", "CreateIssueInput", "{\"repositoryId\":\"repository:acme/work\",\"title\":\"New\",\"body\":\"marker\"}", "issue { id }") { State = PlanWriteState.Succeeded, ResultId = "I1" },
            new PlanWrite("1", row.Identity, PlanPublishStage.Add, "addProjectV2ItemById", "AddProjectV2ItemByIdInput", PlanJson.Text(new { projectId = "P1", contentId = row.Identity }), "item { id }") { State = PlanWriteState.Succeeded, ResultId = "T1" });
        await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), writes) });
        var observed = new PlanRow("I1", "New", "acme/work") { Status = "Backlog" };
        await session.AcceptPublished(new(new([observed], []), ImmutableDictionary<string, string>.Empty.Add("I1", "T1"), [], 0, 0), Today);
        Assert.That(session.Document.State.Rows.Single().Status, Is.EqualTo(expected));
        Assert.That(session.Document.Baseline.Rows.Single().Status, Is.EqualTo("Backlog"));
    }
    [Test]
    public async Task NeverDispatchedWriteRetainsNotSentReasonAfterReadbackAndReopen()
    {
        var session = await Create();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("issue:1", PlanField.Estimate, 16m)]), Today);
        var write = new PlanWrite("0", "issue:1", PlanPublishStage.Fields, "updateProjectV2ItemFieldValue", "UpdateProjectV2ItemFieldValueInput",
            "{\"projectId\":\"P1\",\"itemId\":\"item:issue:1\",\"fieldId\":\"estimate\",\"value\":{\"number\":16}}", "projectV2Item { id }");
        await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), [write]) });
        await session.AcceptPublished(new(session.Document.Baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0), Today);
        session = await Reopen();
        Assert.That(session.Document.Sync.Failures.Single().Reason, Is.EqualTo("NotDispatched"));
        Assert.That(session.Document.State.Rows[0].Estimate, Is.EqualTo(16));
        Assert.That(session.Document.Baseline.Rows[0].Estimate, Is.EqualTo(8));
    }
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };
    private string root = null!;
    private readonly List<PlanSession> sessions = [];
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-plan-document-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public async Task Cleanup()
    {
        foreach (var session in sessions) await session.FlushAsync();
        sessions.Clear();
        Directory.Delete(root, true);
    }
    private static string Text<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static PlanDocument Initial(int count = 3)
    {
        var rows = Enumerable.Range(1, count).Select(i => new PlanRow($"issue:{i}", $"Task {i}", "acme/work")
        { Estimate = 8, Start = Today, End = Today, Assignees = i == 1 ? ["p1"] : [] }).ToImmutableArray();
        var settings = new ProjectPlanSettings { DefaultRepository = "acme/work", StatusDate = Today,
            Columns = [new(PlanField.Estimate, "estimate", "Estimate", "NUMBER")], People = [new("p1", "Alice", 100, null, [])] };
        return new(Project, new(rows, [new("estimate", "Estimate", "NUMBER")]), new(rows, settings));
    }
    private async Task<PlanSession> Create(PlanDocument? document = null, PlanStore? store = null)
    {
        var session = await PlanSession.CreateAsync(store ?? new(root), document ?? Initial(), Today);
        sessions.Add(session);
        Assert.That((await session.FlushAsync()).Succeeded, Is.True);
        return session;
    }
    private async Task<PlanSession> Reopen(PlanStore? store = null, ScopedId? project = null)
    {
        var opened = await PlanSession.OpenAsync(store ?? new(root), project ?? Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        sessions.Add(opened.Session!);
        return opened.Session!;
    }
    private static EditPlanCells Edit(PlanField field, object? value, string id = "issue:1") => new(PlanOperationKind.Cell, [new(id, field, value)]);

    [TestCase(PlanOperationKind.Cell), TestCase(PlanOperationKind.Paste), TestCase(PlanOperationKind.Fill), TestCase(PlanOperationKind.CtrlD)]
    [TestCase(PlanOperationKind.Clear), TestCase(PlanOperationKind.Insert), TestCase(PlanOperationKind.CsvImport)]
    [TestCase(PlanOperationKind.Indent), TestCase(PlanOperationKind.Outdent), TestCase(PlanOperationKind.Move), TestCase(PlanOperationKind.Settings)]
    public async Task EachOperationIsAtomicAndOneExactUndoRedoStep(PlanOperationKind kind)
    {
        var initial = Initial();
        if (kind == PlanOperationKind.Outdent)
        {
            var rows = initial.State.Rows.SetItem(1, initial.State.Rows[1] with { Parent = "issue:1" });
            initial = initial with { Baseline = initial.Baseline with { Rows = rows }, State = initial.State with { Rows = rows } };
        }
        var session = await Create(initial);
        var before = Text(session.Document);
        PlanCommand command = kind switch
        {
            PlanOperationKind.Cell => Edit(PlanField.Title, "Changed"),
            PlanOperationKind.Paste => new EditPlanCells(kind, [new("issue:1", PlanField.Estimate, 12m), new("issue:1", PlanField.Remaining, 4m), new("issue:2", PlanField.Title, "Pasted")]),
            PlanOperationKind.Fill or PlanOperationKind.CtrlD => new FillPlanCells(kind, "issue:1", ["issue:2", "issue:3"], [PlanField.Estimate]),
            PlanOperationKind.Clear => new ClearPlanCells(["issue:2", "issue:3"], [PlanField.Estimate]),
            PlanOperationKind.Insert => new InsertPlanRows([PlanRow.New("New")], "issue:2"),
            PlanOperationKind.CsvImport => new InsertPlanRows([PlanRow.New("A"), PlanRow.New("B")], Kind: kind),
            PlanOperationKind.Indent => new IndentPlanRows(["issue:2"]),
            PlanOperationKind.Outdent => new IndentPlanRows(["issue:2"], true),
            PlanOperationKind.Move => new MovePlanRows(["issue:1"]),
            _ => new ReplacePlanSettings(initial.State.Settings with { ProjectStart = Today.AddDays(1) })
        };
        var saving = session.Execute(command, Today);
        Assert.That(Text(session.Document), Is.Not.EqualTo(before), "Committed memory is current before awaiting persistence.");
        Assert.That((await saving).Succeeded, Is.True);
        Assert.That(Text(session.Document.Baseline), Is.EqualTo(Text(initial.Baseline)));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        var after = Text(session.Document);
        var rowsNow = session.Document.State.Rows;
        switch (kind)
        {
            case PlanOperationKind.Cell: Assert.That(rowsNow[0].Title, Is.EqualTo("Changed")); break;
            case PlanOperationKind.Paste: Assert.That((rowsNow[0].Estimate, rowsNow[0].Remaining, rowsNow[1].Title), Is.EqualTo((12m, 4m, "Pasted"))); break;
            case PlanOperationKind.Fill: case PlanOperationKind.CtrlD: Assert.That(rowsNow.Skip(1).Select(r => r.Remaining), Is.All.EqualTo(8)); break;
            case PlanOperationKind.Clear: Assert.That(rowsNow.Skip(1).Select(r => r.Estimate), Is.All.Null); break;
            case PlanOperationKind.Insert: Assert.That(rowsNow[1].Identity, Does.StartWith("local:")); Assert.That(rowsNow[1].Repository, Is.EqualTo("acme/work")); break;
            case PlanOperationKind.CsvImport: Assert.That(rowsNow.Length, Is.EqualTo(5)); break;
            case PlanOperationKind.Indent: Assert.That(rowsNow[1].Parent, Is.EqualTo("issue:1")); break;
            case PlanOperationKind.Outdent: Assert.That(rowsNow[1].Parent, Is.Null); break;
            case PlanOperationKind.Move: Assert.That(rowsNow.Select(r => r.Identity), Is.EqualTo(new[] { "issue:2", "issue:3", "issue:1" })); break;
            case PlanOperationKind.Settings: Assert.That(session.Schedule(Today).Select(r => r.Start.Value), Is.All.EqualTo(Today.AddDays(1))); break;
        }
        await session.Undo(Today);
        Assert.That(Text(session.Document), Is.EqualTo(before));
        Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((0, 1)));
        await session.Redo(Today);
        Assert.That(Text(session.Document), Is.EqualTo(after));
        Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((1, 0)));
    }

    [TestCase(PlanField.Estimate), TestCase(PlanField.Start), TestCase(PlanField.End)]
    public async Task CellCommandsApplySchedulingInputTransformations(PlanField field)
    {
        var session = await Create();
        await session.Execute(Edit(field, field == PlanField.Estimate ? 12m : Today.AddDays(1)), Today);
        var row = session.Document.State.Rows[0];
        if (field == PlanField.Estimate) Assert.That(row.Remaining, Is.EqualTo(12));
        if (field == PlanField.Start) Assert.That(row.StartNoEarlierThan, Is.EqualTo(Today.AddDays(1)));
        if (field == PlanField.End) Assert.That(row.Fixed, Is.True);
    }

    [Test]
    public async Task PasteMovesBothFixedDatesBeforeValidatingTheFinalPair()
    {
        var initial = Initial();
        initial = initial with { State = initial.State with { Rows = initial.State.Rows.SetItem(0, initial.State.Rows[0] with { Fixed = true }) } };
        var session = await Create(initial);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("issue:1", PlanField.Start, Today.AddDays(1)), new("issue:1", PlanField.End, Today.AddDays(2))]), Today);
        Assert.That(session.Document.State.Rows[0].End, Is.EqualTo(Today.AddDays(2)));
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }

    [TestCase("negative"), TestCase("duplicate"), TestCase("missing"), TestCase("cycle"), TestCase("invalid settings")]
    public async Task InvalidWholeOperationChangesNoDocumentHistoryOrSavedBytes(string invalid)
    {
        var store = new PlanStore(root); var session = await Create(store: store);
        var before = Text(session.Document); var bytes = await File.ReadAllBytesAsync(store.FileFor(Project));
        PlanCommand command = invalid switch
        {
            "negative" => new EditPlanCells(PlanOperationKind.Paste, [new("issue:1", PlanField.Title, "Do not keep"), new("issue:2", PlanField.Remaining, -1m)]),
            "duplicate" => new EditPlanCells(PlanOperationKind.Paste, [new("issue:1", PlanField.Title, "A"), new("issue:1", PlanField.Title, "B")]),
            "missing" => Edit(PlanField.Title, "Do not insert", "missing"),
            "cycle" => new EditPlanCells(PlanOperationKind.Paste, [new("issue:1", PlanField.Predecessors, new[] { "issue:2" }), new("issue:2", PlanField.Predecessors, new[] { "issue:1" })]),
            _ => new ReplacePlanSettings(session.Document.State.Settings with { People = [new("p1", "Alice", 0, null, [])] })
        };
        Assert.Catch<ArgumentException>(() => session.Execute(command, Today));
        Assert.That(Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(Project)), Is.EqualTo(bytes));
    }

    [Test]
    public async Task NoOpKeepsRedoAndNewEffectiveCommandDiscardsIt()
    {
        var session = await Create();
        await session.Execute(Edit(PlanField.Title, "A"), Today); await session.Undo(Today);
        await session.Execute(Edit(PlanField.Title, "Task 1"), Today);
        Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((0, 1)));
        await session.Execute(Edit(PlanField.Title, "B"), Today);
        Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo((1, 0)));
    }

    [Test]
    public async Task UndoAndRedoSurviveRestartIncludingLocalIdentities()
    {
        var session = await Create(); var before = Text(session.Document);
        var local = PlanRow.New("Local");
        await session.Execute(new InsertPlanRows([local]), Today);
        await session.Execute(Edit(PlanField.Estimate, 12m, local.Identity), Today);
        var after = Text(session.Document);
        session = await Reopen(); await session.Undo(Today); await session.Undo(Today);
        Assert.That(Text(session.Document), Is.EqualTo(before));
        session = await Reopen(); await session.Redo(Today); await session.Redo(Today);
        Assert.That(Text(session.Document), Is.EqualTo(after));
    }

    [Test]
    public async Task HistoryIsBoundedWhileBurstAutosaveKeepsLatestRevisionAndEveryRetainedStep()
    {
        var session = await Create();
        for (var i = 1; i <= 205; i++) _ = session.Execute(Edit(PlanField.Title, i.ToString()), Today);
        Assert.That((await session.FlushAsync()).Succeeded, Is.True);
        session = await Reopen(); Assert.That(session.UndoCount, Is.EqualTo(200));
        for (var i = 0; i < 200; i++) _ = session.Undo(Today);
        await session.FlushAsync(); Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("5"));
        session = await Reopen(); Assert.That(session.RedoCount, Is.EqualTo(200));
        for (var i = 0; i < 200; i++) _ = session.Redo(Today);
        await session.FlushAsync(); Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("205"));
    }

    [Test]
    public async Task MarkersCountTasksAndCalculatedDatesWithoutCountingLocalSettingsOrInsertedRowShifts()
    {
        var session = await Create(); Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        await session.Execute(Edit(PlanField.Estimate, 12m), Today);
        var changes = session.Changes(Today);
        Assert.That(changes.TaskCount, Is.EqualTo(1));
        Assert.That(changes.Fields["issue:1"], Is.EquivalentTo(new[] { PlanField.Estimate, PlanField.Remaining, PlanField.End }));
        await session.Undo(Today);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { People = [new("p1", "Alice", 100, 80, [])] }), Today);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { ProjectStart = Today.AddDays(1) }), Today);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(3));
        await session.Undo(Today);
        var local = PlanRow.New("New"); await session.Execute(new InsertPlanRows([local], "issue:1"), Today);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
        Assert.That(session.Changes(Today).Fields[local.Identity], Does.Contain(PlanField.NewTask));
    }

    [Test]
    public async Task AssigneeAndPredecessorSetOrderDoesNotCreateMarkers()
    {
        var initial = Initial(); var first = initial.State.Rows[0] with { Assignees = ["a", "b"], Predecessors = ["outside-a", "outside-b"] };
        var rows = initial.State.Rows.SetItem(0, first);
        var session = await Create(initial with { Baseline = initial.Baseline with { Rows = rows }, State = initial.State with { Rows = rows } });
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("issue:1", PlanField.Assignees, new[] { "b", "a" }), new("issue:1", PlanField.Predecessors, new[] { "outside-b", "outside-a" })]), Today);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task CheckpointStoresInputsRatherThanCalculatedDatesOrCredentials()
    {
        var initial = Initial(); var rows = initial.State.Rows.SetItem(0, initial.State.Rows[0] with { Estimate = 16 });
        var session = await Create(initial with { State = initial.State with { Rows = rows } });
        Assert.That(session.Schedule(Today)[0].End.Value, Is.EqualTo(Today.AddDays(1)));
        var json = await File.ReadAllTextAsync(new PlanStore(root).FileFor(Project));
        var node = JsonNode.Parse(json)!;
        Assert.That(node["document"]!["state"]!["rows"]![0]!["end"]!.GetValue<string>(), Is.EqualTo("2026-10-05"));
        Assert.That(json, Does.Not.Contain("startReason").And.Not.Contain("differsFromGitHub").And.Not.Contain("token").And.Not.Contain("credential"));
    }

    [Test]
    public async Task FailedSaveKeepsLatestMemoryAndHistoryAndCanRetry()
    {
        var store = new PlanStore(root); var session = await Create(store: store);
        var bytes = await File.ReadAllBytesAsync(store.FileFor(Project));
        using (var locked = new FileStream(store.FileFor(Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await session.Execute(Edit(PlanField.Title, "A"), Today);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Retryable, Is.True);
            result = await session.Execute(Edit(PlanField.Title, "B"), Today);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("B"));
            Assert.That(session.UndoCount, Is.EqualTo(2));
            Assert.That(await File.ReadAllBytesAsync(store.FileFor(Project)), Is.EqualTo(bytes));
        }
        Assert.That((await session.RetrySaveAsync()).Succeeded, Is.True);
        session = await Reopen(); Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("B"));
        await session.Undo(Today); Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("A"));
    }

    [Test]
    public async Task CompetingSessionCannotOverwriteNewerFileAndRetainsItsOwnMemory()
    {
        var a = await Create(); var b = await Reopen();
        await a.Execute(Edit(PlanField.Title, "Writer A"), Today);
        var result = await b.Execute(Edit(PlanField.Title, "Writer B"), Today);
        Assert.That(result.Failure, Is.EqualTo(PlanSaveFailure.ChangedFile));
        Assert.That(result.Retryable, Is.False);
        Assert.That(b.Document.State.Rows[0].Title, Is.EqualTo("Writer B"));
        Assert.That((await Reopen()).Document.State.Rows[0].Title, Is.EqualTo("Writer A"));
    }

    [TestCase("json"), TestCase("version"), TestCase("scope"), TestCase("history"), TestCase("unknown property")]
    public async Task CorruptCheckpointIsPreservedAndCannotCreateANewSessionOverIt(string problem)
    {
        var store = new PlanStore(root); var session = await Create(store: store); await session.Execute(Edit(PlanField.Title, "Saved"), Today);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(store.FileFor(Project)))!;
        if (problem == "version") node["version"] = 99;
        if (problem == "scope") node["document"]!["project"]!["nodeId"] = "different";
        if (problem == "history") node["undo"]![0]!["rows"]![0]!["after"]!["title"] = "Not current";
        if (problem == "unknown property") node["unexpected"] = true;
        var bad = problem == "json" ? "{broken" : node.ToJsonString(); await File.WriteAllTextAsync(store.FileFor(Project), bad);
        var opened = await PlanSession.OpenAsync(store, Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Blocked)); Assert.That(opened.Session, Is.Null);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await PlanSession.CreateAsync(store, Initial(), Today));
        Assert.That(await File.ReadAllTextAsync(store.FileFor(Project)), Is.EqualTo(bad));
    }

    [Test]
    public async Task UnreadableCheckpointIsBlockedRatherThanMissing()
    {
        var store = new PlanStore(root); await Create(store: store);
        using var locked = new FileStream(store.FileFor(Project), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var opened = await PlanSession.OpenAsync(store, Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Blocked)); Assert.That(opened.Session, Is.Null);
    }

    [Test]
    public async Task InterruptedCandidateIsPreservedInsteadOfSilentlyAdoptingOldPrimary()
    {
        var store = new PlanStore(root); await Create(store: store);
        var candidate = store.FileFor(Project) + ".unknown.tmp"; await File.WriteAllTextAsync(candidate, "interrupted");
        Assert.That((await store.LoadAsync(Project)).Status, Is.EqualTo(PlanLoadStatus.Blocked));
        Assert.That(await File.ReadAllTextAsync(candidate), Is.EqualTo("interrupted"));
    }

    [Test]
    public async Task OldFormatFilesAreNeverReadOrChanged()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "legacy.json"), "not valid JSON");
        Directory.CreateDirectory(Path.Combine(root, "Drafts"));
        await File.WriteAllTextAsync(Path.Combine(root, "Drafts", "old.json"), "old bytes");
        var store = new PlanStore(root); Assert.That((await store.LoadAsync(Project)).Status, Is.EqualTo(PlanLoadStatus.Missing));
        await Create(store: store);
        Assert.That(store.FileFor(Project), Does.StartWith(Path.Combine(root, "PlanningEditor", "v1")));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "legacy.json")), Is.EqualTo("not valid JSON"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(root, "Drafts", "old.json")), Is.EqualTo("old bytes"));
    }

    [TestCase("host"), TestCase("viewer"), TestCase("project")]
    public async Task HostViewerAndProjectScopesNeverShareAFile(string dimension)
    {
        var initial = Initial(); var other = dimension switch { "host" => new ScopedId(new("company.example", 42), "P1"), "viewer" => new(new("github.com", 43), "P1"), _ => new(new("github.com", 42), "P2") };
        await Create(initial); var second = await Create(initial with { Project = other }); await second.Execute(Edit(PlanField.Title, "Other"), Today);
        Assert.That((await Reopen()).Document.State.Rows[0].Title, Is.EqualTo("Task 1"));
        Assert.That((await Reopen(project: other)).Document.State.Rows[0].Title, Is.EqualTo("Other"));
    }

    [Test]
    public async Task PortableSettingsImportPreservesUnknownIdentitiesAsOneUndoStep()
    {
        var first = await Create(); var settings = first.Document.State.Settings with
        {
            ProjectStart = Today.AddDays(1), StatusDate = null, CompanyDaysOff = [Today.AddDays(2)],
            ImportedHolidays = PlanHolidayData.FromPreset(PlanningContract.BundledHolidays()),
            People = [new("p1", "Alice", 50, 80, [Today.AddDays(3)]), new("unknown-person", "Bob", 75, null, [])],
            Columns = [new(PlanField.Estimate, "estimate", "Estimate", "NUMBER"), new(PlanField.Remaining, "unknown-field", "Remaining", "NUMBER")]
        };
        await first.Execute(new ReplacePlanSettings(settings), Today);
        var exported = Path.Combine(root, "settings.json"); await first.ExportSettingsAsync(exported);
        var text = await File.ReadAllTextAsync(exported);
        Assert.That(text, Does.Contain("\n").And.Not.Contain("viewerId").And.Not.Contain("Task 1"));
        var secondStore = new PlanStore(Path.Combine(root, "second")); var second = await Create(store: secondStore); var before = Text(second.Document);
        var result = await second.ImportSettingsAsync(exported, Today);
        Assert.That(result.Applied, Is.True, result.Error);
        Assert.That(result.Warnings, Is.EquivalentTo(new[] { "プロジェクトに未確認の担当者: Bob", "プロジェクトに未確認の列: Remaining" }));
        Assert.That(Text(second.Document.State.Settings), Is.EqualTo(Text(settings)));
        Assert.That(second.UndoCount, Is.EqualTo(1));
        second = await Reopen(secondStore); Assert.That(Text(second.Document.State.Settings), Is.EqualTo(Text(settings)));
        await second.Undo(Today); Assert.That(Text(second.Document), Is.EqualTo(before));
    }

    [TestCase("rate"), TestCase("allowance"), TestCase("people"), TestCase("days"), TestCase("mapping"), TestCase("repository"), TestCase("version"), TestCase("unknown property"), TestCase("missing property")]
    public async Task InvalidSettingsImportChangesNothing(string defect)
    {
        var session = await Create(); var before = Text(session.Document);
        var node = JsonNode.Parse(Text(new PlanSettingsFile(1, session.Document.State.Settings)))!;
        var settings = node["settings"]!;
        switch (defect)
        {
            case "rate": settings["people"]![0]!["rate"] = 0; break;
            case "allowance": settings["people"]![0]!["allowance"] = -1; break;
            case "people": settings["people"]!.AsArray().Add(settings["people"]![0]!.DeepClone()); break;
            case "days": settings["companyDaysOff"] = new JsonArray("2026-10-05", "2026-10-05"); break;
            case "mapping": settings["columns"]![0]!["dataType"] = "DATE"; break;
            case "repository": settings["defaultRepository"] = "https://wrong.example/repo"; break;
            case "version": node["version"] = 2; break;
            case "unknown property": settings["mystery"] = 1; break;
            case "missing property": settings.AsObject().Remove("people"); break;
        }
        var path = Path.Combine(root, "invalid-settings.json"); await File.WriteAllTextAsync(path, node.ToJsonString());
        var result = await session.ImportSettingsAsync(path, Today);
        Assert.That(result.Applied, Is.False); Assert.That(result.Error, Is.Not.Null);
        Assert.That(Text(session.Document), Is.EqualTo(before)); Assert.That(session.UndoCount, Is.Zero);
    }

    [TestCase(PlanField.Title), TestCase(PlanField.Estimate)]
    public async Task UnrelatedEditPreservesInvalidRefreshedDatesAndTheirWarnings(PlanField field)
    {
        var initial = Initial();
        var rows = initial.State.Rows.SetItem(0, initial.State.Rows[0] with { Fixed = true, Start = Today.AddDays(2), Estimate = -1 });
        var session = await Create(initial with { Baseline = initial.Baseline with { Rows = rows }, State = initial.State with { Rows = rows } });
        await session.Execute(Edit(field, field == PlanField.Title ? "Still editable" : 4m), Today);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo(field == PlanField.Title ? "Still editable" : "Task 1"));
        Assert.That(session.Document.State.Rows[0].Estimate, Is.EqualTo(field == PlanField.Title ? -1m : 4m));
        Assert.That(session.Schedule(Today)[0].Warnings, Is.Not.Empty);
        await session.Undo(Today);
        Assert.That(Text(session.Document.State.Rows), Is.EqualTo(Text(rows)));
    }

    [TestCase("summary"), TestCase("closed"), TestCase("foreign identity"), TestCase("duplicate identity"), TestCase("date pair")]
    public async Task ProhibitedEditsPreserveTheWholeDocument(string problem)
    {
        var initial = Initial();
        if (problem == "summary")
            initial = initial with { State = initial.State with { Rows = initial.State.Rows.SetItem(1, initial.State.Rows[1] with { Parent = "issue:1" }) } };
        var session = await Create(initial); var before = Text(session.Document);
        var local = PlanRow.New("New");
        PlanCommand command = problem switch
        {
            "summary" => Edit(PlanField.Estimate, 4m),
            "closed" => Edit(PlanField.Closed, true),
            "foreign identity" => new InsertPlanRows([new("issue:other", "Foreign", "acme/work")]),
            "duplicate identity" => new InsertPlanRows([local, local]),
            _ => new EditPlanCells(PlanOperationKind.Paste, [new("issue:1", PlanField.End, Today.AddDays(-1)), new("issue:2", PlanField.Title, "Do not retain")])
        };
        Assert.Catch<ArgumentException>(() => session.Execute(command, Today));
        Assert.That(Text(session.Document), Is.EqualTo(before)); Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task MultiRowHierarchyAndMoveRestoreRelationshipsAndRelativeOrder()
    {
        var session = await Create(Initial(4)); var before = Text(session.Document);
        await session.Execute(new IndentPlanRows(["issue:2", "issue:3"]), Today);
        Assert.That(session.Document.State.Rows.Skip(1).Take(2).Select(r => r.Parent), Is.All.EqualTo("issue:1"));
        await session.Execute(new MovePlanRows(["issue:3", "issue:2"]), Today);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "issue:1", "issue:4", "issue:2", "issue:3" }));
        Assert.That(session.Document.State.Rows.Skip(2).Select(r => r.Parent), Is.All.EqualTo("issue:1"));
        session = await Reopen(); await session.Undo(Today); await session.Undo(Today);
        Assert.That(Text(session.Document), Is.EqualTo(before));
    }

    [Test]
    public async Task MissingPrimaryWithBackupIsBlockedAndPreserved()
    {
        var store = new PlanStore(root); var session = await Create(store: store);
        await session.Execute(Edit(PlanField.Title, "Saved"), Today);
        var backup = store.FileFor(Project) + ".bak"; var bytes = await File.ReadAllBytesAsync(backup);
        File.Delete(store.FileFor(Project));
        Assert.That((await store.LoadAsync(Project)).Status, Is.EqualTo(PlanLoadStatus.Blocked));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await PlanSession.CreateAsync(store, Initial(), Today));
        Assert.That(await File.ReadAllBytesAsync(backup), Is.EqualTo(bytes));
    }

    [Test]
    public async Task ExternalRewriteWithSameRevisionIsNeverOverwritten()
    {
        var store = new PlanStore(root); var session = await Create(store: store);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(store.FileFor(Project)))!;
        node["document"]!["state"]!["rows"]![0]!["title"] = "External";
        var external = node.ToJsonString(); await File.WriteAllTextAsync(store.FileFor(Project), external);
        var result = await session.Execute(Edit(PlanField.Title, "Local"), Today);
        Assert.That(result.Failure, Is.EqualTo(PlanSaveFailure.ChangedFile));
        Assert.That((await session.RetrySaveAsync()).Failure, Is.EqualTo(PlanSaveFailure.ChangedFile));
        Assert.That(await File.ReadAllTextAsync(store.FileFor(Project)), Is.EqualTo(external));
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local"));
    }
    [Test, Explicit("Real isolated-storage timing; not a CI timing gate")]
    public async Task ThousandTasksAutosaveAndRestoreReportLatencyAndBytes()
    {
        var session = await Create(Initial(1000)); var store = new PlanStore(root);
        var changes = session.Document.State.Rows.Select(r => new PlanCellChange(r.Identity, PlanField.Remaining, 16m)).ToImmutableArray();
        var watch = Stopwatch.StartNew(); var pending = session.Execute(new EditPlanCells(PlanOperationKind.Paste, changes), Today);
        var editMs = watch.Elapsed.TotalMilliseconds;
        Assert.That((await pending).Succeeded, Is.True); watch.Stop();
        var bytes = new FileInfo(store.FileFor(Project)).Length;
        var saved = Text(session.Document); var reopenWatch = Stopwatch.StartNew(); var reopened = await Reopen(); reopenWatch.Stop();
        Assert.That(Text(reopened.Document), Is.EqualTo(saved)); Assert.That(reopened.UndoCount, Is.EqualTo(1));
        Assert.That(reopened.Document.State.Rows.Length, Is.EqualTo(1000));
        await reopened.Undo(Today); Assert.That(reopened.Document.State.Rows.Select(r => r.Remaining), Is.All.Null);
        TestContext.Out.WriteLine($"1000 tasks; edit_return_ms={editMs:F3}; edit_through_durable_save_ms={watch.Elapsed.TotalMilliseconds:F3}; restore_ms={reopenWatch.Elapsed.TotalMilliseconds:F3}; checkpoint_bytes={bytes}; runtime={Environment.Version}; OS={Environment.OSVersion}");
    }
}
