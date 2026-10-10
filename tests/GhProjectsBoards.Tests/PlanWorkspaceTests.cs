using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using GhProjectsBoards.App.GitHub;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanWorkspaceTests
{
    [Test]
    public async Task FreshUnassignedProjectResolvesLatePageAssigneeWithoutCreationPermissionOrEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-intake-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"planFault\":\"repository-denied\"}");
            FakePlanEditor.Save(root, new([new(new("I1", "Existing", "acme/repo"), "", true)], 2));
            var workspace = new PlanWorkspace(new(root));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            var reports = new List<RemoteProgress>();
            await workspace.Open(workspace.Available[0], progress: new InlineProgress<RemoteProgress>(reports.Add));
            Assert.That(reports, Does.Contain(new RemoteProgress("プロジェクトの項目を取得", 1, 1)));
            Assert.That(reports.Last().Stage, Is.EqualTo("日程を計算"));
            reports.Clear();
            await workspace.Refresh(progress: new InlineProgress<RemoteProgress>(reports.Add));
            Assert.That(reports, Does.Contain(new RemoteProgress("プロジェクトの項目を取得", 1, 1)));
            Assert.That(reports.Last().Stage, Is.EqualTo("日程を計算"));
            var session = workspace.Session!;
            Assert.That(PlanSheetEditing.Parse(session.Document, PlanField.Assignees, "late-user"), Is.EqualTo(new[] { "U101" }));
            Assert.That(workspace.People.Single(p => p.Identity == "U101").Rate, Is.EqualTo(100));
            Assert.That(session.Document.State.Rows.Single().Assignees, Is.Empty);
            Assert.That(session.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount, Is.Zero);
            Assert.That(session.UndoCount, Is.Zero);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    public async Task CancellationBeforeAdoptionKeepsPreviousProjectPlanHistoryAndStorage(bool refresh, bool afterRead)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-progress-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Original", "acme/repo"), "", true), new(new("I2", "Second", "acme/repo"), "", true)], 3));
            var store = new PlanStore(root);
            var workspace = new PlanWorkspace(store);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[0]);
            var original = workspace.Session!;
            await original.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local edit")]), DateOnly.FromDateTime(DateTime.Today));
            await original.FlushAsync();
            var undo = original.UndoCount;
            var redo = original.RedoCount;
            var document = original.Document;
            var selected = workspace.Selected;
            var saved = File.ReadAllBytes(store.FileFor(document.Project));
            var choice = workspace.Available[1];
            using var cancellation = new CancellationTokenSource();
            var reports = new List<RemoteProgress>();
            var reporter = new InlineProgress<RemoteProgress>(value => {
                reports.Add(value);
                if (afterRead ? value.Stage == "日程を計算" : value.Completed == 1) cancellation.Cancel();
            });
            Assert.ThrowsAsync<OperationCanceledException>(() => refresh
                ? workspace.Refresh(cancellation.Token, reporter) : workspace.Open(choice, cancellation.Token, reporter));
            Assert.That(reports, Does.Contain(new RemoteProgress("プロジェクトの項目を取得", 1, 2)));
            Assert.That(workspace.Session, Is.SameAs(original));
            Assert.That(workspace.Selected, Is.EqualTo(selected));
            Assert.That(original.Document, Is.EqualTo(document));
            Assert.That(original.UndoCount, Is.EqualTo(undo));
            Assert.That(original.RedoCount, Is.EqualTo(redo));
            Assert.That(File.ReadAllBytes(store.FileFor(document.Project)), Is.EqualTo(saved));
            Assert.That(File.Exists(store.FileFor(choice.Id)), Is.False);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FirstOpenCancelledBeforeAdoptionCreatesNoDocumentOrSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-first-open-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Original", "acme/repo"), "", true)], 2));
            var store = new PlanStore(root);
            var workspace = new PlanWorkspace(store);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            var choice = workspace.Available[0];
            using var cancellation = new CancellationTokenSource();
            var reporter = new InlineProgress<RemoteProgress>(value => {
                if (value.Stage == "日程を計算") cancellation.Cancel();
            });
            Assert.ThrowsAsync<OperationCanceledException>(() => workspace.Open(choice, cancellation.Token, reporter));
            Assert.That(workspace.Session, Is.Null);
            Assert.That(workspace.Selected, Is.Null);
            Assert.That(File.Exists(store.FileFor(choice.Id)), Is.False);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await workspace.Open(choice);
            Assert.That(workspace.Selected, Is.EqualTo(choice));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task CancelledUrlResolutionKeepsSelectionAndCanBeRetried()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-url-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Original", "acme/repo"), "", true)], 2));
            var store = new PlanStore(root);
            var workspace = new PlanWorkspace(store);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[1]);
            var original = workspace.Session;
            var selected = workspace.Selected;
            var saved = File.ReadAllBytes(store.FileFor(selected!.Id));
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"holdQuery\":\"RegistrationResolve\"}");
            using var cancellation = new CancellationTokenSource();
            var open = workspace.OpenUrl("https://github.com/users/fixture-user/projects/3", cancellation.Token);
            try {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(Path.Combine(root, "held-gh.pid")) && !open.IsCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                Assert.That(File.Exists(Path.Combine(root, "held-gh.pid")), Is.True);
                cancellation.Cancel();
                Assert.ThrowsAsync<OperationCanceledException>(async () => await open);
                Assert.That(workspace.Session, Is.SameAs(original));
                Assert.That(workspace.Selected, Is.EqualTo(selected));
                Assert.That(File.ReadAllBytes(store.FileFor(selected.Id)), Is.EqualTo(saved));
                Assert.That(File.Exists(store.FileFor(workspace.Available[0].Id)), Is.False);
            }
            finally {
                cancellation.Cancel();
                File.WriteAllText(Path.Combine(root, "release-gh"), "release");
                try { await open; } catch (Exception ex) when (ex is OperationCanceledException or DiscoveryException) { }
            }
            await workspace.OpenUrl("https://github.com/users/fixture-user/projects/3");
            Assert.That(workspace.Selected!.Id, Is.EqualTo(workspace.Available[0].Id));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase("catalog-network")]
    [TestCase("catalog-incomplete")]
    [TestCase("catalog-identity")]
    public async Task FailedAssignableCatalogDoesNotAdoptOrSaveAPartialProject(string fault)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-intake-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Existing", "acme/repo"), "", true)], 2));
            var store = new PlanStore(root);
            var workspace = new PlanWorkspace(store);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[0]);
            var original = workspace.Session;
            var choice = workspace.Available[1];
            File.WriteAllText(Path.Combine(root, "scenario.json"), $"{{\"planEditor\":true,\"workspace\":true,\"planFault\":\"{fault}\"}}");
            Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Open(choice));
            Assert.That(workspace.Session, Is.SameAs(original));
            Assert.That(workspace.Registered.Select(p => p.Id), Does.Not.Contain(choice.Id));
            Assert.That(File.Exists(store.FileFor(choice.Id)), Is.False);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public async Task CsvPreviewResolvesAssignableUsersAcrossPagesWithoutWritingOrEditing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-csv-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Existing", "acme/repo"), "", true)], 2));
            var workspace = new PlanWorkspace(new(root));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[0]);
            var good = await workspace.PreviewCsv(PlanCsvImportTests.Read("a,A,8,alice;late-user,,,,"));
            Assert.That(good.Errors, Is.Empty);
            Assert.That(good.Command!.Rows.Single().Assignees, Is.EqualTo(new[] { "U1", "U101" }));
            var bad = await workspace.PreviewCsv(PlanCsvImportTests.Read("a,A,8,nobody,,,,\nb,B,8,,,,,unknown/repo"));
            Assert.That(bad.Command, Is.Null);
            Assert.That(bad.Errors.Select(e => e.Line), Is.EquivalentTo(new[] { 2, 3 }));
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"planFault\":\"csv-network\"}");
            Assert.ThrowsAsync<InvalidOperationException>(() => workspace.PreviewCsv(PlanCsvImportTests.Read("a,A,8,,,,,")));
            Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public async Task TwoInstancesPreserveBothRegistrationsWhenOpeningDifferentProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo"), "", true)], 2));
            GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
            var first = new PlanWorkspace(new(root));
            var second = new PlanWorkspace(new(root));
            await first.Connect(Service()); await second.Connect(Service());
            await first.Open(first.Available[0]);
            await second.Open(second.Available[1]);
            var reopened = new PlanWorkspace(new(root));
            await reopened.Connect(Service());
            Assert.That(reopened.Registered.Select(p => p.Id.NodeId), Is.EquivalentTo(new[] { "P1", "P2" }));
            Assert.That(reopened.Selected!.Id.NodeId, Is.EqualTo("P2"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public async Task WorkspaceRestoresScopedUnpublishedDocumentsWithoutRefreshingOnReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Remaining = 8, Assignees = ["U1"] }, "", true)], 2) { Drafts = 2, PullRequests = 1 });
            GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
            var workspace = new PlanWorkspace(new(root));
            await workspace.Connect(Service());
            Assert.That(workspace.Available.Count, Is.EqualTo(2));
            await workspace.Open(workspace.Available[0]);
            Assert.That(workspace.Session!.Document.Sync.DraftCount, Is.EqualTo(2));
            Assert.That(workspace.Session.Document.Sync.PullRequestCount, Is.EqualTo(1));
            Assert.That(workspace.Session.Document.State.Rows.Length, Is.EqualTo(1));
            Assert.That(workspace.Session.Document.State.Settings.Columns.Length, Is.EqualTo(5));
            await workspace.Session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "未発行の設計")]), new(2026, 10, 6));
            await workspace.Open(workspace.Available[1]);
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("設計"));
            await workspace.Open(workspace.Available[0]);
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
            var restarted = new PlanWorkspace(new(root));
            await restarted.Connect(Service());
            Assert.That(restarted.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Test]
    public void MatchingUsesNamesAndTypesAndDoesNotGuessAmbiguousColumns()
    {
        var result = PlanColumnMatching.Match([
            new("e", "estimate", "NUMBER"), new("r", "Remaining", "NUMBER"),
            new("a", "Actual", "NUMBER"), new("s", "Start date", "DATE"),
            new("t", "Target date", "DATE"), new("n", "開始日指定", "DATE"),
            new("f", "日程固定", "SINGLE_SELECT"), new("wrong", "Remaining", "TEXT")]);
        Assert.That(result.Length, Is.EqualTo(7));
        Assert.That(result.Single(c => c.Role == PlanField.Start).Name, Is.EqualTo("Start date"));
        Assert.That(PlanColumnMatching.Match([new("one", "Estimate", "NUMBER"), new("two", "Estimate", "NUMBER")]), Is.Empty);
        Assert.That(PlanColumnMatching.Match([new("wrong", "日程固定", "TEXT")]), Is.Empty);
    }
    [Test]
    public async Task RefreshRetainsReadableNamesForNewAssigneesWithoutChangingExistingRates()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-workspace-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var workspace = new PlanWorkspace(new(root));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[0]);
            var state = FakePlanEditor.Load(root);
            FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Assignees = ["U2"] } }] });
            await workspace.Refresh();
            Assert.That(workspace.People.Single(p => p.Identity == "U2").Name, Is.EqualTo("person-U2"));
            Assert.That(workspace.People.Single(p => p.Identity == "U2").Rate, Is.EqualTo(100));
        }
        finally { Directory.Delete(root, true); }
    }

}
