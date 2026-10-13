using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using GhProjectsBoards.App.GitHub;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanWorkspaceTests
{
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
            await workspace.Open(workspace.Available[0]);
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
            Assert.That(remote.AddedFields, Is.Empty);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(false, true, true)]
    public async Task RecalculationOriginPersistsUntilSuccessfulRefreshOrPublish(bool refresh, bool noWrites, bool undoBeforePublish = false)
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
            await workspace.Open(workspace.Available[0]);
            var session = workspace.Session!;
            if (noWrites) await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { ProjectStart = day }), day);
            else await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Edited")]), day);
            session = (await PlanSession.OpenAsync(new(root), session.Document.Project, day.AddDays(1))).Session!;
            Assert.That(session.Document.Dates.Recalculated, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
            if (undoBeforePublish) await session.Undo(day);
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            var result = await (refresh ? publisher.RefreshAsync(session, day.AddDays(2)) : publisher.PublishAsync(session, day.AddDays(2)));
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(session.Document.Dates.Recalculated, Is.Null);
            Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(refresh ? day.AddDays(2) : day));
            if (!undoBeforePublish) await session.Undo(day);
            await session.Redo(day);
            Assert.That(session.Document.Dates.Recalculated, Is.Null, "Publication or refresh must also clear stale origin notices in retained history.");
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
            await workspace.Open(workspace.Available[0]);
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
    public async Task FirstTakeInKeepsGitHubDatesAndZeroUnpublishedAcrossLaterReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-held-dates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            var start = new DateOnly(2026, 10, 5);
            FakePlanEditor.Save(root, new([
                new(new("I1", "Summary", "acme/repo") { Start = null, End = start }, "", true),
                new(new("I2", "Task", "acme/repo") { Parent = "I1", Start = start, End = start, Remaining = 16 }, "", true),
                new(new("I3", "Closed", "acme/repo") { Start = start, End = start, Closed = true, CloseDate = start.AddDays(1), Remaining = 8 }, "", true)
            ], 4));
            var store = new PlanStore(root);
            var day = new DateOnly(2026, 10, 6);
            var workspace = new PlanWorkspace(store, () => day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open(workspace.Available[0]);
            var session = workspace.Session!;
            Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(day));
            void AssertHeld(PlanSession plan, DateOnly today)
            {
                Assert.That(plan.Schedule(today).Select(t => (t.Start.Value, t.End.Value)),
                    Is.EqualTo(plan.Document.Baseline.Rows.Select(r => (r.Start, r.End))));
                Assert.That(plan.Changes(today).TaskCount, Is.Zero);
            }
            AssertHeld(session, day);
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            var published = await publisher.PublishAsync(session, day.AddDays(5));
            Assert.That(published.Succeeded, Is.True, published.Error);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero, "A held plan with 未発行 0 must not publish the deferred closed Remaining calculation.");
            Assert.That(session.Document.Dates.Held, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
            var reopened = (await PlanSession.OpenAsync(store, session.Document.Project, day.AddDays(5))).Session!;
            AssertHeld(reopened, day.AddDays(5));
            Assert.That(reopened.Document.State.Settings.StatusDate, Is.EqualTo(day));
            await reopened.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Title, "Edited")]), day.AddDays(5));
            Assert.That(reopened.Schedule(day).Single(t => t.Input.Identity == "I3").End.Value, Is.EqualTo(start.AddDays(1)));
            Assert.That(reopened.Changes(day).TaskCount, Is.GreaterThan(0));
            Assert.That(reopened.UndoCount, Is.EqualTo(1));
            await reopened.Undo(day);
            AssertHeld(reopened, day);
            await reopened.Redo(day);
            Assert.That(reopened.Schedule(day).Single(t => t.Input.Identity == "I3").End.Value, Is.EqualTo(start.AddDays(1)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FreshUnassignedProjectResolvesLatePageAssigneeWithoutCreationPermissionOrEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-intake-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"planFault\":\"repository-denied\"}");
            FakePlanEditor.Save(root, new([new(new("I1", "Existing", "acme/repo"), "", true)], 2));
            var workspace = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
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
            Assert.That(PlanSheetEditing.Parse(session.Document, PlanField.Assignees, "late-user", DateOnly.FromDateTime(DateTime.Today)), Is.EqualTo(new[] { "U101" }));
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
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
            var workspace = new PlanWorkspace(store, () => DateOnly.FromDateTime(DateTime.Today));
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
            var workspace = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
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
            var first = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            var second = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
            await first.Connect(Service()); await second.Connect(Service());
            await first.Open(first.Available[0]);
            await second.Open(second.Available[1]);
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
            var workspace = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
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
