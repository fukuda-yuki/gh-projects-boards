using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using GhProjectsBoards.App.GitHub;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanWorkspaceTests
{
    [TestCase("acme", "acme"), TestCase("missing", "fixture-user")]
    public async Task DefaultOwnerUsesMostRecentRegisteredOwnerWhenStillAccessible(string recentOwner, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-default-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true}""");
            FakePlanEditor.Save(root, new([], 1));
            var store = new PlanStore(root);
            Directory.CreateDirectory(store.Root);
            var scope = new ConnectionScope("github.com", 42);
            ProjectChoice Choice(string owner, int number) => new(new(scope, "P" + number), new(scope, "O" + number), owner, "Organization", number, $"https://github.com/orgs/{owner}/projects/{number}", "Plan " + number);
            File.WriteAllText(Path.Combine(store.Root, "workspace.json"), PlanJson.Text(new PlanWorkspaceCatalog(1, [Choice("fixture-user", 1), Choice(recentOwner, 2)], null)));
            var workspace = new PlanWorkspace(store, () => new(2026, 10, 17));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            Assert.That(workspace.DefaultOwner!.Login, Is.EqualTo(expected));
            Assert.That(workspace.Registered.Select(p => p.Number), Is.EqualTo(new[] { 2, 1 }));
            Assert.That(File.Exists(Path.Combine(root, "listing-requests.txt")), Is.False);
        } finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ConnectionReadsOwnersOnlyAndListingsCacheReloadReconnectAndIsolateFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-chooser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"chooserCatalog":true,"chooserProjectCount":8}""");
            FakePlanEditor.Save(root, new([], 1));
            GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
            var workspace = new PlanWorkspace(new(root), () => new(2026, 10, 17));
            int Requests(string owner) => File.ReadAllLines(Path.Combine(root, "listing-requests.txt")).Count(l => l == owner);
            await workspace.Connect(Service());
            Assert.That(workspace.Owners.Select(o => o.Login), Is.EqualTo(new[] { "fixture-user", "acme", "broken" }));
            Assert.That(File.Exists(Path.Combine(root, "listing-requests.txt")), Is.False);
            Assert.That(workspace.DefaultOwner!.Login, Is.EqualTo("fixture-user"));
            var first = await workspace.Listings("fixture-user");
            Assert.That(await workspace.Listings("FIXTURE-USER"), Is.EqualTo(first));
            Assert.That(Requests("fixture-user"), Is.EqualTo(1));
            await workspace.Listings("fixture-user", reload: true);
            Assert.That(Requests("fixture-user"), Is.EqualTo(2));
            Assert.ThrowsAsync<DiscoveryException>(async () => await workspace.Listings("broken"));
            Assert.ThrowsAsync<DiscoveryException>(async () => await workspace.Listings("broken"));
            Assert.That(Requests("broken"), Is.EqualTo(2));
            Assert.That((await workspace.Listings("acme")).Single().Choice.Id.NodeId, Is.EqualTo("P2"));
            Assert.That(Requests("acme"), Is.EqualTo(1));
            await workspace.Connect(Service());
            await workspace.Listings("fixture-user");
            Assert.That(Requests("fixture-user"), Is.EqualTo(3));
        } finally { Directory.Delete(root, true); }
    }
    [TestCase(false), TestCase(true)]
    public async Task FirstOpenInitializesPeriodAndMilestonesThenPreservesUserSettings(bool clear)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-first-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            var day = new DateOnly(2026, 10, 13);
            FakePlanEditor.Save(root, new([
                new(new("I1", "Later", "acme/repo") { Start = day.AddDays(2), End = day.AddDays(30), Remaining = 8, MilestoneTitle = "SA 完了" }, "", true) { Phase = "SA" },
                new(new("I2", "Earlier", "acme/other") { Start = day, End = day.AddDays(3), Actual = 4, Remaining = 8 }, "", true) { Phase = "PS" },
                new(new("I3", "Undated", "acme/repo") { Remaining = 8, MilestoneTitle = "ps 完了" }, "", true)
            ], 4) { Phases = ["SA", "P", "PS"], Milestones = [
                new("M1", "acme/repo", "SA 完了", day.AddDays(5)),
                new("M2", "acme/other", "SA 完了", day.AddDays(6)),
                new("M3", "acme/repo", "ps 完了", day.AddDays(20)),
                // Another version's milestone in the same repository: no Issue of this Project uses it.
                new("M4", "acme/repo", "2027.10 SA 完了", day.AddDays(90))] });
            var store = new PlanStore(root);
            var workspace = new PlanWorkspace(store, () => day);
            GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
            await workspace.Connect(Service());
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            var initial = session.Document.State.Settings;
            Assert.Multiple(() => {
                Assert.That(initial.ProjectStart, Is.EqualTo(day));
                Assert.That(initial.ProjectEnd, Is.EqualTo(day.AddDays(20)));
                Assert.That(initial.PhaseMilestones, Is.EquivalentTo(new Dictionary<string, string> { ["SA"] = "SA 完了", ["PS"] = "ps 完了" }));
                Assert.That(session.Document.Baseline.Milestones.Single(m => m.Title == "SA 完了").DueDate, Is.EqualTo(day.AddDays(6)));
                Assert.That(session.UndoCount, Is.Zero);
                Assert.That(session.Changes(day).TaskCount, Is.Zero);
                Assert.That(session.Schedule(day).Select(t => (t.Start.Value, t.End.Value)),
                    Is.EqualTo(session.Document.Baseline.Rows.Select(r => (r.Start, r.End))));
            });
            var export = Path.Combine(root, "settings.json");
            await session.ExportSettingsAsync(export);
            var chosen = initial with { ProjectStart = clear ? null : day.AddDays(-7), ProjectEnd = clear ? null : day.AddDays(40),
                PhaseMilestones = clear ? initial.PhaseMilestones.Clear() : initial.PhaseMilestones.SetItem("SA", "ps 完了").Remove("PS") };
            var saved = await session.Execute(new ReplacePlanSettings(chosen), day);
            Assert.That(saved.Succeeded, Is.True, saved.Error);
            var expected = PlanJson.Text(chosen);
            await workspace.Refresh();
            Assert.That(PlanJson.Text(session.Document.State.Settings), Is.EqualTo(expected));
            await workspace.Reset();
            Assert.That(PlanJson.Text(session.Document.State.Settings), Is.EqualTo(expected));
            await workspace.Open(workspace.Selected!);
            Assert.That(PlanJson.Text(workspace.Session!.Document.State.Settings), Is.EqualTo(expected));
            var restarted = new PlanWorkspace(store, () => day.AddDays(1));
            await restarted.Connect(Service());
            Assert.That(PlanJson.Text(restarted.Session!.Document.State.Settings), Is.EqualTo(expected));
            var undo = restarted.Session.UndoCount;
            Assert.That((await restarted.Session.ImportSettingsAsync(export, day)).Applied, Is.True);
            Assert.That(PlanJson.Text(restarted.Session.Document.State.Settings), Is.EqualTo(PlanJson.Text(initial)));
            Assert.That(restarted.Session.UndoCount, Is.EqualTo(undo + 1));
            var remote = FakePlanEditor.Load(root);
            Assert.That(remote.MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task AnEditPersistsWithoutARecalculationNoticeAndRefreshAdvancesTheStatusDate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo"), "", true)], 2));
            var day = new DateOnly(2026, 10, 6);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            var edit = await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Edited")]), day);
            Assert.That(edit.Recalculation, Is.Null, "An ordinary edit shows unpublished dates instead of a recalculation notice.");
            session = (await PlanSession.OpenAsync(new(root), session.Document.Project, day.AddDays(1))).Session!;
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            var result = await publisher.RefreshAsync(session, day.AddDays(2));
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(day.AddDays(2)));
            await session.Undo(day);
            await session.Redo(day);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FirstTakeInOfADivergentNativeChildOrderLeavesNothingUnpublished()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-sibling-intake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([
                new(new("I1", "Summary", "acme/repo"), "", true),
                new(new("I2", "Task A", "acme/repo") { Parent = "I1" }, "", true),
                new(new("I3", "Task B", "acme/repo") { Parent = "I1" }, "", true)
            ], 4) { SubOrders = System.Collections.Immutable.ImmutableDictionary<string, System.Collections.Immutable.ImmutableArray<string>>.Empty.Add("I1", ["I3", "I2"]) });
            var day = new DateOnly(2026, 10, 6);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", "I3" }));
                Assert.That(session.Changes(day).TaskCount, Is.Zero);
                Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FirstTakeInTakesGitHubDatesAsThePlanAndMovesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-held-dates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            var start = new DateOnly(2026, 10, 5);
            FakePlanEditor.Save(root, new([
                new(new("I1", "Summary", "acme/repo") { Start = start, End = start.AddDays(1) }, "", true),
                new(new("I2", "Task", "acme/repo") { Parent = "I1", Start = start, End = start.AddDays(1), Remaining = 16 }, "", true)
            ], 3));
            var store = new PlanStore(root);
            var day = new DateOnly(2026, 10, 6);
            var workspace = new PlanWorkspace(store, () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(day));
            void AssertUnchanged(PlanSession plan, DateOnly today)
            {
                Assert.That(plan.Schedule(today).Select(t => (t.Start.Value, t.End.Value)),
                    Is.EqualTo(plan.Document.Baseline.Rows.Select(r => (r.Start, r.End))));
                Assert.That(plan.Changes(today).TaskCount, Is.Zero);
            }
            AssertUnchanged(session, day);
            var reopened = (await PlanSession.OpenAsync(store, session.Document.Project, day.AddDays(5))).Session!;
            AssertUnchanged(reopened, day.AddDays(5));
            Assert.That(reopened.Document.State.Settings.StatusDate, Is.EqualTo(day));
            // An edit reschedules only the task it reaches; every other plan date stays where it was.
            await reopened.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Remaining, 24m)]), day.AddDays(5));
            Assert.That(reopened.Schedule(day.AddDays(5)).Single(t => t.Input.Identity == "I1").End.Value, Is.EqualTo(start.AddDays(2)));
            Assert.That(reopened.UndoCount, Is.EqualTo(1));
            await reopened.Undo(day);
            AssertUnchanged(reopened, day);
            await reopened.Redo(day);
            Assert.That(reopened.Schedule(day.AddDays(5)).Single(t => t.Input.Identity == "I1").End.Value, Is.EqualTo(start.AddDays(2)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FirstOpenSendsNoAssignableUserRequestAndStoresExactlyTheIssueAssignees()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-intake-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([
                new(new("I1", "Assigned", "acme/repo") { Assignees = ["U1"] }, "", true),
                new(new("I2", "Also assigned", "acme/repo") { Assignees = ["U2"] }, "", true),
                new(new("I3", "Unassigned", "acme/repo"), "", true)
            ], 4));
            var store = new PlanStore(root);
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(store, () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            var choice = (await workspace.Listings("fixture-user"))[0].Choice;
            File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
            await workspace.Open(choice);
            var queries = RecordedQueries(root);
            Assert.Multiple(() =>
            {
                // Acceptance bullet 2: opening a Project requests no assignable-user list at all.
                Assert.That(queries, Has.None.Contains("assignableUsers"), "Opening a Project must not read every assignable user.");
                Assert.That(queries, Has.None.Contains("PlanAssignableUsers"));
                Assert.That(queries, Has.Some.Contains("ProjectFields"), "The ordinary Project read still happens.");
                Assert.That(queries, Has.Some.Contains("ProjectItems("), "The ordinary item read still happens.");
                Assert.That(queries, Has.Some.Contains("ProjectItemDetails"), "The Issue assignees still arrive with that read.");
            });
            var session = workspace.Session!;
            var people = session.Document.State.Settings.People;
            Assert.Multiple(() =>
            {
                Assert.That(people.Select(p => p.Identity), Is.EquivalentTo(new[] { "U1", "U2" }), "The people are exactly the distinct Issue assignees.");
                Assert.That(people, Has.All.Matches<PlanResource>(p => p.Rate == 100 && p.Allowance is null), "Each intake person starts at 100% with no allowance.");
                Assert.That(session.Document.Sync.PeopleNames.Keys, Is.EquivalentTo(new[] { "U1", "U2" }), "No catalog-only login is retained as metadata.");
                Assert.That(session.Document.Sync.PeopleNames["U1"], Is.EqualTo("person-U1"));
            });
            var stored = await store.LoadAsync(choice.Id);
            Assert.That(stored.Status, Is.EqualTo(PlanLoadStatus.Loaded));
            Assert.That(stored.Checkpoint!.Document.State.Settings.People.Select(p => p.Identity), Is.EquivalentTo(new[] { "U1", "U2" }),
                "The adopted checkpoint holds no person outside the Issues' assignees.");
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var original = workspace.Session!;
            await original.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local edit")]), DateOnly.FromDateTime(DateTime.Today));
            await original.FlushAsync();
            var undo = original.UndoCount;
            var redo = original.RedoCount;
            var document = original.Document;
            var selected = workspace.Selected;
            var saved = File.ReadAllBytes(store.FileFor(document.Project));
            var choice = (await workspace.Listings("acme"))[0].Choice;
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            var choice = (await workspace.Listings("fixture-user"))[0].Choice;
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("acme"))[0].Choice);
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
                Assert.That(File.Exists(store.FileFor((await workspace.Listings("fixture-user"))[0].Choice.Id)), Is.False);
            }
            finally {
                cancellation.Cancel();
                File.WriteAllText(Path.Combine(root, "release-gh"), "release");
                try { await open; } catch (Exception ex) when (ex is OperationCanceledException or DiscoveryException) { }
            }
            await workspace.OpenUrl("https://github.com/users/fixture-user/projects/3");
            Assert.That(workspace.Selected!.Id, Is.EqualTo((await workspace.Listings("fixture-user"))[0].Choice.Id));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task SettingsPeopleFollowAssignmentAndKeepStoredRateAndAllowance()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-people-assignment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            Assert.Multiple(() =>
            {
                Assert.That(workspace.People.Select(p => p.Identity), Is.EqualTo(new[] { "U1" }), "A fresh document lists exactly the Issue assignee.");
                Assert.That((workspace.People.Single().Name, workspace.People.Single().Rate), Is.EqualTo(("person-U1", 100m)));
            });
            var stored = await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { People = [new("U1", "person-U1", 50, 80)] }), day);
            Assert.That(stored.Succeeded, Is.True, stored.Error);
            Assert.That((workspace.People.Single().Rate, workspace.People.Single().Allowance), Is.EqualTo((50m, (decimal?)80)));
            var unassign = await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Assignees, Array.Empty<string>())]), day);
            Assert.That(unassign.Succeeded, Is.True, unassign.Error);
            Assert.That(workspace.People, Is.Empty, "A person no row holds leaves the settings list.");
            var kept = session.Document.State.Settings.People.Single(p => p.Identity == "U1");
            Assert.That((kept.Rate, kept.Allowance), Is.EqualTo((50m, (decimal?)80)), "Their stored rate and allowance stay in settings.");
            var reassign = await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Assignees, new[] { "U1", "U2" })]), day);
            Assert.That(reassign.Succeeded, Is.True, reassign.Error);
            Assert.That(workspace.People.Select(p => p.Identity), Is.EqualTo(new[] { "U1", "U2" }),
                "A stored person keeps settings order; a row assignee with no stored entry follows.");
            Assert.That((workspace.People[0].Rate, workspace.People[0].Allowance), Is.EqualTo((50m, (decimal?)80)),
                "The stored values return unchanged when the person is assigned again.");
            var added = workspace.People.Single(p => p.Identity == "U2");
            Assert.That((added.Rate, added.Allowance), Is.EqualTo((100m, (decimal?)null)), "A new row assignee starts at 100% with no allowance.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task PeopleIncludeCancelledPendingRemovalAndRequirementRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-people-rowstates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([
                new(new("I1", "Requirement", "acme/repo") { Assignees = ["U3"] }, "", true),
                new(new("I2", "Task", "acme/repo") { Parent = "I1", Assignees = ["U1"] }, "", true),
                new(new("I3", "Other", "acme/repo") { Assignees = ["U2"] }, "", true)
            ], 4));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            Assert.That(workspace.People.Select(p => p.Identity), Is.EquivalentTo(new[] { "U1", "U2", "U3" }),
                "A requirement row's assignee is a real assignment.");
            var cancel = await session.Execute(new ClosePlanRows(["I2"], Cancelled: true), day);
            Assert.That(cancel.Succeeded, Is.True, cancel.Error);
            var remove = await session.Execute(new RemovePlanRows(["I3"]), day);
            Assert.That(remove.Succeeded, Is.True, remove.Error);
            Assert.That(workspace.People.Select(p => p.Identity), Is.EquivalentTo(new[] { "U1", "U2", "U3" }),
                "A 中止 row and a row pending この版から外す still hold their assignees.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task LegacyStoredPeopleStayStoredButTheListShowsTheAssignedOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-people-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
            var store = new PlanStore(root);
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(store, () => day);
            await workspace.Connect(Service());
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            // A plan created before #253 holds every assignable user as a stored person.
            var people = new[] { new PlanResource("U1", "person-U1", 50, 80) }
                .Concat(Enumerable.Range(0, 999).Select(i => new PlanResource("legacy-" + i, "legacy-" + i, 100, null))).ToImmutableArray();
            var stored = await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { People = people }), day);
            Assert.That(stored.Succeeded, Is.True, stored.Error);
            await workspace.Flush();
            var restarted = new PlanWorkspace(store, () => day);
            await restarted.Connect(Service());
            Assert.That(restarted.Session, Is.Not.Null);
            Assert.That(restarted.Session!.Document.State.Settings.People, Has.Length.EqualTo(1000), "No migration prunes the stored entries.");
            Assert.That(restarted.People.Select(p => p.Identity), Is.EqualTo(new[] { "U1" }),
                "The settings list shows only the assigned person.");
            Assert.That((restarted.People.Single().Rate, restarted.People.Single().Allowance), Is.EqualTo((50m, (decimal?)80)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ASavedPlanReopensWithNoCatalogOrProjectRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-intake-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Assignees = ["U1"], Remaining = 8 }, "", true)], 2));
            GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
            var store = new PlanStore(root);
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(store, () => day);
            await workspace.Connect(Service());
            var choice = (await workspace.Listings("fixture-user"))[0].Choice;
            await workspace.Open(choice);
            var local = await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "未発行の設計")]), day);
            Assert.That(local.Succeeded, Is.True, local.Error);
            await workspace.Flush();
            var people = workspace.People.Select(p => p.Identity).ToArray();
            File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
            var restarted = new PlanWorkspace(store, () => day);
            await restarted.Connect(Service());
            var queries = RecordedQueries(root);
            Assert.Multiple(() =>
            {
                Assert.That(queries, Has.None.Contains("assignableUsers"), "Reopening a saved document reads no catalog.");
                Assert.That(queries, Has.None.Contains("ProjectFields"), "A saved document reopens from its checkpoint.");
                Assert.That(queries, Has.None.Contains("ProjectItems("));
                Assert.That(queries, Has.None.Contains("ProjectItemDetails"));
            });
            Assert.That(restarted.Session, Is.Not.Null);
            Assert.That(restarted.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"), "Local work survives the reopen.");
            Assert.That(restarted.People.Select(p => p.Identity), Is.EqualTo(people));
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
            var workspace = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
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
            var first = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            var second = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            await first.Connect(Service()); await second.Connect(Service());
            await first.Open((await first.Listings("fixture-user"))[0].Choice);
            await second.Open((await second.Listings("acme"))[0].Choice);
            var reopened = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
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
            var workspace = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            await workspace.Connect(Service());
            Assert.That(workspace.Owners.Count, Is.EqualTo(2));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            Assert.That(workspace.Session!.Document.Sync.DraftCount, Is.EqualTo(2));
            Assert.That(workspace.Session.Document.Sync.PullRequestCount, Is.EqualTo(1));
            Assert.That(workspace.Session.Document.State.Rows.Length, Is.EqualTo(1));
            Assert.That(workspace.Session.Document.State.Settings.Columns.Length, Is.EqualTo(5));
            await workspace.Session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "未発行の設計")]), new(2026, 10, 6));
            await workspace.Open((await workspace.Listings("acme"))[0].Choice);
            Assert.That(workspace.Registered.Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P2", "P1" }));
            Assert.That(workspace.DefaultOwner!.Login, Is.EqualTo("acme"));
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("設計"));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
            Assert.That(workspace.Registered.Select(p => p.Id.NodeId), Is.EqualTo(new[] { "P1", "P2" }));
            var restarted = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
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
        // #268: 開始日指定 and 日程固定 are no planning roles, so a Project that still has them maps neither.
        Assert.That(result.Select(c => c.Role), Is.EquivalentTo(new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start, PlanField.End }));
        Assert.That(result.Single(c => c.Role == PlanField.Start).Name, Is.EqualTo("Start date"));
        Assert.That(PlanColumnMatching.Match([new("one", "Estimate", "NUMBER"), new("two", "Estimate", "NUMBER")]), Is.Empty);
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
            var workspace = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var state = FakePlanEditor.Load(root);
            FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Assignees = ["U2"] } }] });
            await workspace.Refresh();
            Assert.That(workspace.People.Select(p => p.Identity), Is.EqualTo(new[] { "U2" }),
                "The person no row holds is hidden; the newly assigned one appears.");
            Assert.That(workspace.People.Single(p => p.Identity == "U2").Name, Is.EqualTo("person-U2"));
            Assert.That(workspace.People.Single(p => p.Identity == "U2").Rate, Is.EqualTo(100));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task OnDemandResolutionAssignsAndRegistersAsOneUndoStepWithoutMutations()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-assign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
            var resolved = await workspace.ResolveAssigneeAsync("acme/repo", "late-user");
            Assert.That(resolved, Is.EqualTo(new PlanResource("U101", "late-user", 100, null)));
            var calls = RecordedCalls(root);
            var lookups = calls.Where(call => QueryOf(call).Contains("PlanResolveAssignee")).ToArray();
            Assert.That(lookups, Has.Length.EqualTo(1), "Exactly one lookup is sent for the typed login.");
            var variables = lookups[0].GetProperty("variables");
            Assert.Multiple(() =>
            {
                Assert.That(variables.GetProperty("owner").GetString(), Is.EqualTo("acme"));
                Assert.That(variables.GetProperty("name").GetString(), Is.EqualTo("repo"));
                Assert.That(variables.GetProperty("login").GetString(), Is.EqualTo("late-user"));
                Assert.That(RecordedQueries(root).Count(query => query.Contains("assignableUsers(")), Is.EqualTo(1), "The lookup is the only assignable-user read, and it is bounded to the typed login.");
            });
            var save = await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell,
                [new("I1", PlanField.Assignees, new[] { "U1", "U101" })]) { NewPeople = [resolved] }, day);
            Assert.That(save.Succeeded, Is.True, save.Error);
            var session = workspace.Session;
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.State.Rows.Single().Assignees, Is.EqualTo(new[] { "U1", "U101" }));
                Assert.That(session.Document.State.Settings.People.Single(p => p.Identity == "U101"),
                    Is.EqualTo(new PlanResource("U101", "late-user", 100, null)), "The looked-up person is stored at 100% in the same command.");
                Assert.That(workspace.People.Select(p => p.Identity), Is.EquivalentTo(new[] { "U1", "U101" }));
                Assert.That(session.UndoCount, Is.EqualTo(1), "The assignment and the registration are one Undo step.");
            });
            await session.Undo(day);
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.State.Rows.Single().Assignees, Is.EqualTo(new[] { "U1" }), "Undo removes the assignment.");
                Assert.That(session.Document.State.Settings.People.Any(p => p.Identity == "U101"), Is.False, "Undo removes the registration in the same step.");
            });
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero, "A lookup never sends a mutation.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task UnknownLoginIsRefusedWithoutWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            var before = PlanJson.Text(session.Document);
            var undo = session.UndoCount;
            var error = Assert.ThrowsAsync<ArgumentException>(() => workspace.ResolveAssigneeAsync("acme/repo", "nobody"));
            Assert.That(error!.Message, Is.EqualTo("担当者を確認できません: nobody"));
            Assert.Multiple(() =>
            {
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before), "A refused lookup changes nothing.");
                Assert.That(session.UndoCount, Is.EqualTo(undo));
            });
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase("resolve-network", "担当者の確認に失敗しました。再試行してください。")]
    [TestCase("resolve-identity", "接続先のアカウントが変わりました。再接続してください。")]
    [TestCase("resolve-denied", "担当者のリポジトリを確認できません。")]
    public async Task FailedLookupIsRefusedWithItsMessageWithoutWriting(string fault, string message)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            var before = PlanJson.Text(session.Document);
            var undo = session.UndoCount;
            File.WriteAllText(Path.Combine(root, "scenario.json"), $"{{\"planEditor\":true,\"workspace\":true,\"planFault\":\"{fault}\"}}");
            var error = Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ResolveAssigneeAsync("acme/repo", "late-user"));
            Assert.That(error!.Message, Is.EqualTo(message));
            Assert.Multiple(() =>
            {
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before), "A failed lookup changes nothing.");
                Assert.That(session.UndoCount, Is.EqualTo(undo));
            });
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task AResolvedPersonPublishesTheirNodeId()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var resolved = await workspace.ResolveAssigneeAsync("acme/repo", "late-user");
            var session = workspace.Session!;
            var save = await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
                [new("I1", PlanField.Assignees, new[] { "U1", "U101" })]) { NewPeople = [resolved] }, day);
            Assert.That(save.Succeeded, Is.True, save.Error);
            var result = await new PlanPublisher(workspace.Service!, workspace.Context!).PublishAsync(session, day);
            Assert.That(result.Succeeded, Is.True, result.Error);
            var mutations = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl"));
            Assert.That(mutations, Has.Some.Contains("addAssigneesToAssignable"));
            Assert.That(mutations, Has.Some.Contains("U101"), "The publish sends the node id the lookup returned.");
            Assert.That(session.Changes(day).TaskCount, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task LargeAssignableRepositoryResolvesOneTypedLoginWithOneRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            // A fixture can declare a repository with 1,000 assignable people; the resolver still asks only for
            // the typed login and takes that login's exact match.
            var users = Enumerable.Range(1, 1000).Select(i => new PlanFakeUser("BU" + i, "bulk-user-" + i)).ToImmutableArray();
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2)
                { AssignableUsers = users });
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
            var resolved = await workspace.ResolveAssigneeAsync("acme/repo", "BULK-USER-500");
            Assert.That(resolved, Is.EqualTo(new PlanResource("BU500", "bulk-user-500", 100, null)));
            var queries = RecordedQueries(root);
            Assert.That(queries.Count(query => query.Contains("PlanResolveAssignee")), Is.EqualTo(1), "One lookup for one login.");
            Assert.That(queries.Where(query => query.Contains("assignableUsers(")).All(query => query.Contains("query:$login")), Is.True,
                "Never a page of the 1,000 assignable users, only a search for the typed login.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ATypedLoginThatOtherLoginsContainIsStillFound()
    {
        // GitHub searches assignable users by substring and orders them by login, so in a large organisation
        // `rob` arrives after `a-rob` and `b-rob`. The exact login must still resolve, never be refused.
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-substring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            var users = new[] { "a-rob", "b-rob", "c-rob", "rob", "roberta" }.Select((login, i) => new PlanFakeUser("R" + i, login)).ToImmutableArray();
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2) { AssignableUsers = users });
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
            var resolved = await workspace.ResolveAssigneeAsync("acme/repo", "rob");
            Assert.That(resolved, Is.EqualTo(new PlanResource("R3", "rob", 100, null)));
            Assert.That(RecordedQueries(root).Count(query => query.Contains("PlanResolveAssignee")), Is.EqualTo(1), "One lookup for one login.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task NewRowAssignmentRegistersTheResolvedPersonWithTheInsert()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-resolve-newrow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { Assignees = ["U1"] }, "", true)], 2));
            var day = new DateOnly(2026, 10, 13);
            var workspace = new PlanWorkspace(new(root), () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var session = workspace.Session!;
            var row = PlanRow.New("New task", "acme/repo") with { Assignees = ["U101"] };
            var save = await session.Execute(new InsertPlanRows([row]) { NewPeople = [new PlanResource("U101", "late-user", 100, null)] }, day);
            Assert.That(save.Succeeded, Is.True, save.Error);
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == row.Identity).Assignees, Is.EqualTo(new[] { "U101" }));
                Assert.That(session.Document.State.Settings.People.Single(p => p.Identity == "U101").Rate, Is.EqualTo(100),
                    "A person resolved for a new row is registered with the insert.");
                Assert.That(session.UndoCount, Is.EqualTo(1));
            });
            await session.Undo(day);
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.State.Rows.Any(r => r.Identity == row.Identity), Is.False);
                Assert.That(session.Document.State.Settings.People.Any(p => p.Identity == "U101"), Is.False, "One Undo removes the row and its person.");
            });
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    // The GraphQL queries the fake received, so a case can assert the requests a flow does and does not send.
    private static string[] RecordedQueries(string root)
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Select(call => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString()! : "")
                .Where(query => query.Length > 0).ToArray()
            : [];
    }

    private static JsonElement[] RecordedCalls(string root)
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray()
            : [];
    }

    private static string QueryOf(JsonElement call)
        => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString()! : "";

}
