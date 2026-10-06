using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;
[TestFixture, Category("Integration")]
internal sealed class PlanPublisherTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly ScopedId Project = new(new("github.com", 42), "P1");
    private string root = null!;
    private PlanSession session = null!;
    private PlanPublisher publisher = null!;
    private CountingRunner runner = null!;
    private static readonly ProjectPlanSettings Settings = new()
    {
        DefaultRepository = "acme/repo", StatusDate = Today,
        Columns = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan, PlanField.Fixed }
            .Select(f => new PlanColumnMapping(f, "F-" + f, f.ToString(), f is PlanField.Estimate or PlanField.Remaining or PlanField.Actual ? "NUMBER" : f == PlanField.Fixed ? "SINGLE_SELECT" : "DATE")).ToImmutableArray()
    };
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-phase4-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public async Task Cleanup()
    {
        if (session is not null) await session.FlushAsync();
        TestContext.Out.WriteLine("Real fake-gh processes: " + (runner?.Count ?? 0));
        Directory.Delete(root, true);
    }
    private void Scenario(string? fault = null, int batch = 1)
    {
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, planFault = fault, faultBatch = batch }));
        if (File.Exists(FakePlanEditor.StatePath(root))) FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { ReadAttempts = 0 });
    }
    private async Task Start(int count, Func<PlanFakeState, PlanFakeState>? fixture = null, ProjectPlanSettings? settings = null)
    {
        Scenario();
        var initial = new PlanFakeState(Enumerable.Range(1, count).Select(i => new PlanFakeIssue(new("I" + i, "Task " + i, "acme/repo"), "", true)).ToImmutableArray(), count + 1);
        FakePlanEditor.Save(root, fixture?.Invoke(initial) ?? initial);
        runner = new(new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }));
        var service = new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner);
        publisher = new(service, new("github.com", 42, "fixture-user", GhProcessTests.FakeExecutable));
        session = await PlanSession.CreateAsync(new(root), new(Project, new([], []), new([], settings ?? Settings)), Today);
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
    }
    private async Task Reopen()
    {
        var loaded = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(loaded.Status, Is.EqualTo(PlanLoadStatus.Loaded), loaded.Error); session = loaded.Session!;
    }
    private static IEnumerable<int> SequenceSeeds => Enumerable.Range(51000, 20);
    private static IEnumerable<int> FullSequenceSeeds => Enumerable.Range(51000, 200);
    [TestCaseSource(nameof(FullSequenceSeeds)), Category("HistorySequence"), Explicit("Full 200-sequence experiment")]
    public Task FullHistorySequences(int seed) => RandomizedOperationsKeepEverySavedHistoryReplayable(seed);
    [TestCaseSource(nameof(SequenceSeeds))]
    public async Task RandomizedOperationsKeepEverySavedHistoryReplayable(int seed)
    {
        await Start(5);
        var random = new Random(seed); var trace = new List<string>(); var audits = 0;
        var counts = new Dictionary<string, int>();
        var operations = Enumerable.Range(0, 30).Select(i => i % 12).OrderBy(_ => random.Next()).ToArray();
        for (var step = 0; step < operations.Length; step++)
        {
            var rows = session.Document.State.Rows;
            var row = rows[random.Next(rows.Length)];
            var other = rows.Where(r => r.Identity != row.Identity).ToArray()[random.Next(rows.Length - 1)];
            var op = operations[step]; var label = op.ToString();
            PlanCommand? command = op switch
            {
                0 => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Title, $"Edit {seed}/{step}")]),
                1 => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Actual, (decimal)random.Next(0, 30))]),
                2 => new IndentPlanRows([row.Identity]),
                3 => new IndentPlanRows([row.Identity], Outdent: true),
                4 => new MovePlanRows([row.Identity], random.Next(2) == 0 ? null : other.Identity),
                5 => new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Predecessors,
                    random.Next(2) == 0 ? ImmutableArray<string>.Empty : ImmutableArray.Create(other.Identity))]),
                6 => new InsertPlanRows([new PlanRow("local:" + seed.ToString("x16") + step.ToString("x16"), $"New {seed}/{step}", "acme/repo")], other.Identity),
                _ => null
            };
            try
            {
                if (command is not null)
                {
                    var before = PlanJson.Text(session.Document);
                    try { var save = await session.Execute(command, Today); Assert.That(save.Succeeded, Is.True, save.Error); }
                    catch (ArgumentException) { Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before), "Invalid user input must reject atomically."); label += "-rejected"; }
                }
                else if (op == 7)
                {
                    Scenario(); var remote = FakePlanEditor.Load(root);
                    var order = remote.Issues.OrderBy(_ => random.Next()).ToArray();
                    var changed = order.Select((issue, i) => issue with { Row = issue.Row with
                    {
                        Parent = i > 0 && random.Next(3) == 0 ? order[random.Next(i)].Row.Identity : null,
                        Predecessors = i > 0 && random.Next(3) == 0 ? [order[random.Next(i)].Row.Identity] : [],
                        Actual = random.Next(3) == 0 ? random.Next(30) : issue.Row.Actual
                    } }).ToImmutableArray();
                    FakePlanEditor.Save(root, remote with { Issues = changed, SubOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty });
                    label += (await publisher.RefreshAsync(session, Today)).Succeeded ? "-adopted" : "-rejected";
                }
                else if (op == 8)
                {
                    foreach (var conflict in session.Document.Sync.Conflicts.ToArray())
                    {
                        try { Assert.That((await session.ResolveConflict(conflict.Identity, conflict.Field, random.Next(2) == 0, Today)).Succeeded, Is.True); }
                        catch (ArgumentException) { /* An invalid relationship resolution remains unresolved. */ }
                    }
                    var fault = new string?[] { null, "partial", "before", "uncertain-verification", "verificationfailure", "repository-denied" }[random.Next(6)];
                    Scenario(fault, FakePlanEditor.Load(root).MutationBatches + 1);
                    var result = await publisher.PublishAsync(session, Today);
                    label += "-" + (fault ?? "complete") + (result.Succeeded ? "-succeeded" : "-unfinished");
                    Assert.That(session.Document.Sync.Publish is not { DispatchStarted: false }, Is.True, "An unsent attempt must not lock editing after Publish returns.");
                }
                else if (op == 9) Assert.That((await session.Undo(Today)).Succeeded, Is.True);
                else if (op == 10) Assert.That((await session.Redo(Today)).Succeeded, Is.True);
                else { Assert.That((await session.FlushAsync()).Succeeded, Is.True); await Reopen(); }
                trace.Add($"{step}:{label}:{row.Identity}"); counts[label] = counts.GetValueOrDefault(label) + 1;
                AssertAcyclic(FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray());
                var creations = FakePlanEditor.Load(root).Issues.Where(i => i.Body.Length > 0).ToArray();
                Assert.That(creations.Select(i => System.Text.RegularExpressions.Regex.Match(i.Body, @"local:[a-f0-9]{32}").Value).Distinct().Count(), Is.EqualTo(creations.Length), "A local identity must never produce duplicate Issues, even under a new publish run marker.");
                await Reopen();
                var current = PlanJson.Text(session.Document.State); var baseline = PlanJson.Text(session.Document.Baseline);
                string Evidence() => session.Document.Sync.Publish is not { } attempt ? "null" : PlanJson.Text(new { attempt.RunId, attempt.DispatchStarted, Writes = attempt.Writes.Select(w => new { w.Key, w.Identity, w.State, w.ResultId }) });
                var evidence = Evidence(); var undo = session.UndoCount; var redo = session.RedoCount;
                var remoteBefore = PlanJson.Text(FakePlanEditor.Load(root));
                async Task Travel(bool forward)
                {
                    var save = await (forward ? session.Redo(Today) : session.Undo(Today));
                    Assert.That(save.Succeeded, Is.True, save.Error);
                    await Reopen(); audits++;
                    Assert.That(PlanJson.Text(session.Document.Baseline), Is.EqualTo(baseline), "I1: Undo cannot roll back verified remote values.");
                    Assert.That(Evidence(), Is.EqualTo(evidence), "I1: Undo cannot reset dispatch, outcomes or identities.");
                }
                for (var i = 0; i < undo; i++) await Travel(false);
                Assert.That(session.UndoCount, Is.Zero);
                for (var i = 0; i < undo + redo; i++) await Travel(true);
                Assert.That(session.RedoCount, Is.Zero);
                for (var i = 0; i < redo; i++) await Travel(false);
                Assert.That(session.UndoCount, Is.EqualTo(undo)); Assert.That(session.RedoCount, Is.EqualTo(redo));
                Assert.That(PlanJson.Text(session.Document.State), Is.EqualTo(current));
                Assert.That(PlanJson.Text(FakePlanEditor.Load(root)), Is.EqualTo(remoteBefore), "History navigation must not write to GitHub.");
            }
            catch (Exception ex) { Assert.Fail($"Seed {seed}, step {step}, operation {label}; trace: {string.Join(" | ", trace)}\n{ex}"); }
        }
        TestContext.Progress.WriteLine($"Completed randomized sequence {seed}: 30 steps, {audits} history traversals.");
        TestContext.Out.WriteLine($"Randomized invariant sequence: seed={seed}; steps=30; historyTraversals={audits}; outcomes={JsonSerializer.Serialize(counts)}");
    }
    private static void AssertAcyclic(PlanRow[] rows)
    {
        var byId = rows.ToDictionary(r => r.Identity);
        foreach (var hierarchy in new[] { true, false })
        {
            var complete = new HashSet<string>(); var active = new HashSet<string>();
            void Visit(string id)
            {
                if (complete.Contains(id) || !byId.TryGetValue(id, out var row)) return;
                Assert.That(active.Add(id), Is.True, "I3: the synthetic remote graph contains a cycle.");
                foreach (var next in hierarchy ? row.Parent is null ? [] : new[] { row.Parent } : row.Predecessors.ToArray()) Visit(next);
                active.Remove(id); complete.Add(id);
            }
            foreach (var row in rows) Visit(row.Identity);
        }
    }
    [Test]
    public async Task CreationBatchesArePacedFromTheirStartsWithNoInitialSleep()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(0, 11).Select(i => PlanRow.New("Paced " + i, "acme/repo")).ToImmutableArray()), Today);
        var started = DateTimeOffset.UtcNow;
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        var creates = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(r => r.GetProperty("query").GetString()!.Contains("createIssue")).Select(r => r.GetProperty("started").GetDateTimeOffset()).ToArray();
        Assert.That((creates[0] - started).TotalSeconds, Is.LessThan(5), "The first create batch needs no pacing wait.");
        Assert.That((creates[1] - creates[0]).TotalSeconds, Is.InRange(9.9, 13), "Ten created Issues reserve the next ten seconds start-to-start, even when the following batch contains only one Issue.");
    }
    [Test]
    public async Task PositionUpdatesCompleteWhenMultiAliasPositionRequestsLoseTheirResponsePartway()
    {
        await Start(8);
        await session.Execute(new MovePlanRows(["I5", "I6", "I7", "I8"], "I1"), Today);
        Scenario("position-batch-interrupted");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Identity), Is.EqualTo(new[] { "I5", "I6", "I7", "I8", "I1", "I2", "I3", "I4" }));
        Assert.That(session.Document.Sync.Failures, Is.Empty);
        await Reopen();
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }
    [TestCase("verificationfailure")]
    [TestCase("uncertain-verification")]
    public async Task CreationEvidenceSurvivesCorrectionHistoryAndRepublish(string fault)
    {
        await Start(0);
        var good = PlanRow.New("Good", "acme/repo"); var rejected = PlanRow.New("Rejected", "acme/repo");
        await session.Execute(new InsertPlanRows([good, rejected]), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(1));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(rejected.Identity, PlanField.Title, "Corrected")]), Today);
        Scenario(fault, FakePlanEditor.Load(root).MutationBatches + 1);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var evidence = session.Document.Sync.Publish!.Writes.Where(w => w.Stage == PlanPublishStage.Create)
            .Select(w => (w.Identity, w.ResultId, w.State)).ToArray();
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(2));
        await Reopen();
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == rejected.Identity).Title, Is.EqualTo("Rejected"));
        Assert.That(session.Document.Sync.Publish!.Writes.Where(w => w.Stage == PlanPublishStage.Create).Select(w => (w.Identity, w.ResultId, w.State)), Is.EqualTo(evidence));
        await session.Redo(Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(rejected.Identity, PlanField.Title, "Final")]), Today);
        Scenario();
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Title), Is.EquivalentTo(new[] { "Good", "Final" }));
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(2));
        await Reopen();
        var identities = session.Document.State.Rows.Select(r => r.Identity).ToArray();
        Assert.That(session.UndoCount, Is.GreaterThan(0));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Single(r => r.Title != "Good").Title, Is.EqualTo("Corrected"));
        Assert.That(session.Document.Baseline.Rows.Single(r => r.Title != "Good").Title, Is.EqualTo("Final"));
        await session.Redo(Today);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(identities));
        Scenario("forbid-mutation");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
    }
    [TestCase("partial")]
    [TestCase("before")]
    [TestCase("after-and-read")]
    public async Task FieldOutcomesAreIndependentOfLocalHistorySequences(string fault)
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 3m)]), Today);
        Scenario(fault); Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var baseline = PlanJson.Text(session.Document.Baseline); var progress = PlanJson.Text(session.Document.Sync.Publish);
        await Reopen(); await session.Undo(Today);
        Assert.That(session.Document.State.Rows.All(r => r.Actual is null), Is.True);
        Assert.That(PlanJson.Text(session.Document.Baseline), Is.EqualTo(baseline));
        Assert.That(PlanJson.Text(session.Document.Sync.Publish), Is.EqualTo(progress));
        await session.Redo(Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 5m), new("I2", PlanField.Actual, 5m)]), Today);
        Scenario(); var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 5m), Is.True);
        await Reopen(); await session.Undo(Today);
        Assert.That(session.Document.State.Rows.All(r => r.Actual == 3m), Is.True);
        Assert.That(session.Document.Baseline.Rows.All(r => r.Actual == 5m), Is.True);
        await session.Redo(Today);
    }
    [Test]
    public async Task PromotedIdentityKeepsItsBaselineDuringUnfinishedSiblingRecovery()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows([PlanRow.New("First", "acme/repo"), PlanRow.New("Second", "acme/repo")]), Today);
        Scenario("partial", 2);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == "I1"), Is.True);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I1" ? i with { Row = i.Row with { Title = "Remote" } } : i).ToImmutableArray() });
        Scenario();
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        var conflict = session.Document.Sync.Conflicts.Single(c => c.Identity == "I1" && c.Field == PlanField.Title);
        Assert.That(conflict.Baseline, Is.EqualTo(PlanJson.Text("First")));
        Assert.That(conflict.Local, Is.EqualTo(PlanJson.Text("Local")));
        Assert.That(conflict.Remote, Is.EqualTo(PlanJson.Text("Remote")));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.Title, Is.EqualTo("Remote"));
        await Reopen();
    }
    [Test]
    public async Task DispatchedAttemptIsNotMistakenForUnsentWhenVerificationIsIncomplete()
    {
        await Start(0);
        var row = PlanRow.New("Initial", "acme/repo"); await session.Execute(new InsertPlanRows([row]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Title, "Edited")]), Today);
        Scenario("before-and-read");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Publish, Is.Not.Null);
        var run = session.Document.Sync.Publish!.RunId;
        await Reopen(); await session.Undo(Today); await session.Redo(Today);
        Assert.That(session.Document.Sync.Publish!.RunId, Is.EqualTo(run));
        Scenario(); var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Title, Is.EqualTo("Edited"));
        await Reopen(); Assert.That(session.Document.State.Rows.Single().Identity, Is.EqualTo("I1"));
    }
    [Test]
    public async Task RestartReleasesAnAttemptWhoseFirstMutationWasNeverDispatched()
    {
        await Start(0);
        var row = PlanRow.New("Pending", "acme/repo"); await session.Execute(new InsertPlanRows([row]), Today);
        var run = Guid.NewGuid().ToString("N");
        var review = PlanPublishPlan.Build(session.Document, new(session.Document.Baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0), Today, run);
        await session.SaveSync(session.Document.Sync with { Publish = new(run, review.Writes), NotBefore = DateTimeOffset.UtcNow.AddHours(1) });
        await Reopen();
        Assert.That(session.Document.Sync.Publish, Is.Null); Assert.That(session.Document.Sync.NotBefore, Is.Null);
        await session.Undo(Today); Assert.That(session.Document.State.Rows, Is.Empty);
        await session.Redo(Today); await session.Execute(new ReplacePlanSettings(Settings with { ProjectStart = Today }), Today);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues, Is.Empty);
    }
    [Test]
    public async Task UndoingUnavailableDispositionNeverRewindsAnotherRowsVerifiedBaseline()
    {
        await Start(2);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = state.Issues.SetItem(0, state.Issues[0] with { Added = false }) });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        await session.ResolveUnavailable("I1", false, Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Actual, 9m)]), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        await Reopen(); await session.Undo(Today); await session.Undo(Today);
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EquivalentTo(new[] { "I1", "I2" }));
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
        Assert.That(session.Document.Baseline.Rows.Single(r => r.Identity == "I2").Actual, Is.EqualTo(9m));
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Actual, Is.EqualTo(9m));
        await session.Redo(Today); await session.Redo(Today); await Reopen();
        Assert.That(session.Document.State.Rows.Single().Actual, Is.EqualTo(9m));
    }
    [TestCase("repository-missing")]
    [TestCase("repository-archived")]
    [TestCase("repository-denied")]
    [TestCase("repository-disabled")]
    [TestCase("mapping")]
    public async Task UnsentPreflightFailuresRemainEditableAcrossRestart(string fault)
    {
        await Start(0, settings: fault == "mapping" ? Settings with { Columns = Settings.Columns.Where(c => c.Role != PlanField.Actual).ToImmutableArray() } : Settings);
        var row = PlanRow.New("New", "acme/repo") with { Actual = 1m };
        await session.Execute(new InsertPlanRows([row]), Today);
        Scenario(fault);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues, Is.Empty);
        Assert.That(session.Document.Sync.Publish, Is.Null);
        await Reopen(); await session.Undo(Today); Assert.That(session.Document.State.Rows, Is.Empty);
        await session.Redo(Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(row.Identity, PlanField.Title, "Corrected")]), Today);
        await session.Execute(new ReplacePlanSettings(Settings), Today);
        Scenario(); Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var result = await publisher.PublishAsync(session, Today); Assert.That(result.Succeeded, Is.True, result.Error);
        await Reopen(); Assert.That(session.Document.State.Rows.Single().Title, Is.EqualTo("Corrected"));
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(1));
    }
    [TestCase(PlanField.Parent, 1)]
    [TestCase(PlanField.Parent, 51)]
    [TestCase(PlanField.Predecessors, 1)]
    [TestCase(PlanField.Predecessors, 51)]
    public async Task RelationshipReversalsStayAcyclicAcrossRowsBatchesAndHistory(PlanField relationship, int pairs)
    {
        await Start(pairs * 2, state => state with { Issues = state.Issues.Select((issue, i) => i % 2 == 0 ? issue : issue with
            { Row = relationship == PlanField.Parent ? issue.Row with { Parent = "I" + i } : issue.Row with { Predecessors = ["I" + i] } }).ToImmutableArray() });
        var changes = Enumerable.Range(0, pairs).SelectMany(pair => new[]
        {
            new PlanCellChange("I" + (pair * 2 + 1), relationship, relationship == PlanField.Parent ? "I" + (pair * 2 + 2) : new[] { "I" + (pair * 2 + 2) }),
            new PlanCellChange("I" + (pair * 2 + 2), relationship, relationship == PlanField.Parent ? null : Array.Empty<string>())
        }).ToImmutableArray();
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, changes), Today);
        if (pairs > 1)
        {
            Scenario("partial"); Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
            Assert.That(FakePlanEditor.Load(root).Issues.Where((_, i) => i % 2 == 0).All(i => i.Row.Parent is null && i.Row.Predecessors.IsEmpty), Is.True);
            Scenario();
        }
        var result = await publisher.PublishAsync(session, Today); Assert.That(result.Succeeded, Is.True, result.Error);
        await session.Undo(Today); await session.Redo(Today);
        Scenario("forbid-mutation"); Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        await Reopen();
        foreach (var pair in Enumerable.Range(0, pairs))
        {
            var first = FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I" + (pair * 2 + 1)).Row;
            var second = FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I" + (pair * 2 + 2)).Row;
            Assert.That(relationship == PlanField.Parent ? first.Parent == second.Identity && second.Parent is null : first.Predecessors.SequenceEqual(new[] { second.Identity }) && second.Predecessors.IsEmpty, Is.True);
        }
    }
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, false)]
    public async Task ConcurrentRefreshUsesOnlyACompleteSnapshotWithinThreeAttempts(int changingReads, bool succeeds)
    {
        await Start(101);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var before = PlanJson.Text(session.Document);
        Scenario("churn", changingReads);
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.EqualTo(succeeds), result.Error);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local"));
        if (!succeeds) { Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before)); Assert.That(result.Error, Does.Contain("ConcurrentChange")); }
        await Reopen();
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExhaustedVerificationKeepsUnverifiedOutcomesUntilReadOnlyRecovery(bool useRefresh)
    {
        await Start(101);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Written")]), Today);
        Scenario("verification-churn");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("Written"));
        Assert.That(session.Document.Sync.Unverified, Is.EqualTo(new[] { "I1" }));
        Assert.That(session.Document.Sync.Failures, Is.Empty);
        Assert.That(session.Document.Baseline.Rows[0].Title, Is.EqualTo("Task 1"));
        await Reopen(); await session.Undo(Today); await session.Redo(Today);
        Assert.That(session.Document.Sync.Unverified, Is.EqualTo(new[] { "I1" }));
        Scenario("forbid-mutation");
        var recovered = useRefresh ? await publisher.RefreshAsync(session, Today) : await publisher.PublishAsync(session, Today);
        Assert.That(recovered.Succeeded, Is.True, recovered.Error);
        Assert.That(session.Document.Sync.Unverified, Is.Empty);
        Assert.That(session.Document.Sync.Publish, Is.Null);
        Assert.That(session.Document.Baseline.Rows[0].Title, Is.EqualTo("Written"));
        await Reopen(); Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Written"));
    }
    [Test]
    public async Task NonConcurrentReadFailureIsNotSilentlyRetried()
    {
        await Start(1); var before = PlanJson.Text(session.Document); Scenario("read-once");
        var result = await publisher.RefreshAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("Api")); Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
    }
    [Test]
    public async Task ArchivedParentsNeverOwnPlanOrderConflictsAndThePlanReopens()
    {
        await Start(4, initial => initial with
        {
            Issues = initial.Issues.Select(i => i.Row.Identity == "I1" ? i with { Archived = true } : i with { Row = i.Row with { Parent = "I1" } }).ToImmutableArray(),
            SubOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", ["I2", "I3", "I4"])
        });
        await session.Execute(new MovePlanRows(["I4"], "I2"), Today);
        var external = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, external with { SubOrders = external.SubOrders.SetItem("I1", ["I3", "I2", "I4"]) });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error); session = opened.Session!;
        Assert.That(session.Document.Sync.NativeOrders.ContainsKey("I1"), Is.False);
        Assert.That(session.Document.Sync.Conflicts, Is.Empty);
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        Assert.That(FakePlanEditor.Load(root).SubOrders["I1"], Is.EqualTo(new[] { "I3", "I2", "I4" }));
    }
    [TestCase(true, null)]
    [TestCase(false, "Done")]
    public async Task CopyingUnavailableTasksResetsReadOnlyIssueStateAndPublishes(bool closed, string? status)
    {
        await Start(1, initial => initial with { Issues = [initial.Issues[0] with { Row = initial.Issues[0].Row with { Closed = closed, Status = status } }] });
        var source = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, source with { Issues = [source.Issues[0] with { Added = false }] });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        await session.ResolveUnavailable("I1", true, Today);
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Closed, Is.EqualTo(closed));
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo(status));
        await session.Redo(Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var copy = FakePlanEditor.Load(root).Issues.Single(i => i.Added).Row;
        Assert.That(copy.Closed, Is.False); Assert.That(copy.Status, Is.Null);
        Assert.That(copy.Title, Is.EqualTo("Task 1"));
    }
    [TestCase("removed-during-write")]
    [TestCase("summary-conflict-during-write")]
    public async Task VerificationProblemsCannotReportSuccessWithZeroUnpublishedTasks(string fault)
    {
        await Start(2);
        if (fault == "summary-conflict-during-write")
        {
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 8m)]), Today);
            await session.Execute(new IndentPlanRows(["I2"]), Today);
        }
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Published")]), Today);
        Scenario(fault);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(result.Succeeded, Is.False); Assert.That(result.Error, Is.Not.Empty);
        if (fault == "removed-during-write") Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I2" }));
        else Assert.That(session.Document.Sync.Conflicts.Select(c => c.Field), Does.Contain(PlanField.Estimate));
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [Test]
    public async Task OptionalUnmappedSchedulingConstraintsStayLocalWhileDatesPublish()
    {
        await Start(1, settings: Settings with { Columns = Settings.Columns.Where(c => c.Role is not (PlanField.StartNoEarlierThan or PlanField.Fixed)).ToImmutableArray() });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 8m)]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Start, Today.AddDays(2))]), Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.End, Today.AddDays(3))]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded)); session = opened.Session!;
        Assert.That(session.Document.State.Rows[0].Fixed, Is.True);
        Assert.That(session.Document.State.Rows[0].StartNoEarlierThan, Is.EqualTo(Today.AddDays(2)));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        var remote = FakePlanEditor.Load(root).Issues[0].Row;
        Assert.That(remote.Start, Is.EqualTo(Today.AddDays(2))); Assert.That(remote.End, Is.EqualTo(Today.AddDays(3)));
        Assert.That(remote.StartNoEarlierThan, Is.Null); Assert.That(remote.Fixed, Is.False);
    }
    [TestCase("In progress")]
    [TestCase("Done")]
    [TestCase(null)]
    public async Task RefreshKeepsTheBuiltInStatusWithoutAPlanningMapping(string? status)
    {
        await Start(1);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Status = status } }] });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo(status));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local title")]), Today);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.State.Rows[0].Status, Is.EqualTo(status));
        Assert.That(session.Document.Baseline.Rows[0].Status, Is.EqualTo(status));
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local title"));
        Assert.That(session.Changes(Today).Fields["I1"], Does.Not.Contain(PlanField.Status));
    }
    [Test]
    public async Task FailedPublishRowsKeepGitHubsReasonAcrossReopen()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 1m), new("I2", PlanField.Actual, 1m)]), Today);
        Scenario("partial");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var reopened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded));
        Assert.That(reopened.Session!.Document.Sync.Failures.Single().Reason, Does.Contain("FORBIDDEN").And.Contain("Synthetic failure"));
    }
    [Test]
    public async Task EffortEditedBeforeIndentDoesNotPreventPublishCompletion()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 8m)]), Today);
        await session.Execute(new IndentPlanRows(["I2"]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Estimate, Is.Null);
        Assert.That(FakePlanEditor.Load(root).Issues[1].Row.Parent, Is.EqualTo("I1"));
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
    }
    [Test]
    public async Task ParentChildSwapPublishesWithoutAnIntermediateCycle()
    {
        await Start(2);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = state.Issues.SetItem(1, state.Issues[1] with { Row = state.Issues[1].Row with { Parent = "I1" } }) });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Parent, "I2"), new("I2", PlanField.Parent, null)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var rows = FakePlanEditor.Load(root).Issues.Select(i => i.Row).ToArray();
        Assert.That(rows[0].Parent, Is.EqualTo("I2")); Assert.That(rows[1].Parent, Is.Null);
    }
    [TestCase("local: setup")]
    [TestCase("item: cleanup")]
    [TestCase("repository: literal")]
    public async Task PlaceholderLikeTitlesRemainLiteral(string title)
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, title)]), Today);
        await session.Execute(new InsertPlanRows([PlanRow.New(title, "acme/repo")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Select(i => i.Row.Title), Is.EqualTo(new[] { title, title }));
    }
    [Test]
    public async Task CompleteReadMergesAllPagesBeforeChangingTheCheckpoint()
    {
        await Start(101);
        Assert.That(session.Document.State.Rows, Has.Length.EqualTo(101));
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [Test]
    public async Task FiftyOneUpdatesUseTwoBatchesAndUndoAfterPublishStaysLocal()
    {
        await Start(51);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows.Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 2m)).ToImmutableArray()), Today);
        var start = runner.Count;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 2), Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(2));
        TestContext.Out.WriteLine("51-update publish processes: " + (runner.Count - start));
        await session.Undo(Today);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(51));
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 2), Is.True);
        Assert.That((await PlanSession.OpenAsync(new(root), Project, Today)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
    }
    [TestCase("before")]
    [TestCase("after")]
    [TestCase("autoadd")]
    [TestCase("autoadd-after")]
    public async Task CreationRestartReconcilesDispatchedIdentitiesBeforeRetry(string fault)
    {
        await Start(0);
        var row = PlanRow.New("Created", "acme/repo");
        await session.Execute(new InsertPlanRows([row]), Today);
        Scenario(fault);
        var first = await publisher.PublishAsync(session, Today);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        var resumed = await publisher.PublishAsync(session, Today);
        Assert.That(resumed.Succeeded, Is.True, first.Error + "; " + resumed.Error);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(1));
        Assert.That(session.Document.State.Rows.Single().Identity, Is.EqualTo("I1"));
        Assert.That(session.UndoCount, Is.Zero);
    }
    [TestCase("partial")]
    [TestCase("resource")]
    [TestCase("after")]
    public async Task PartialUpdateRestartDoesNotResendSuccessfulAliases(string fault)
    {
        await Start(51);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, session.Document.State.Rows.Select(r => new PlanCellChange(r.Identity, PlanField.Actual, 3m)).ToImmutableArray()), Today);
        Scenario(fault);
        var first = await publisher.PublishAsync(session, Today);
        Assert.That(first.Succeeded, Is.False);
        if (fault == "resource") Assert.That(session.Document.Sync.Failures.Any(w => w.Reason.StartsWith("RESOURCE_LIMITS_EXCEEDED:", StringComparison.Ordinal)), Is.True);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.All(i => i.Row.Actual == 3), Is.True);
        var lines = File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl"));
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.That(last.RootElement.GetProperty("variables").EnumerateObject().Count(), Is.EqualTo(fault == "after" ? 1 : 2));
    }
    [Test]
    public async Task VerificationMismatchStaysPendingAndIsResent()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 4m)]), Today);
        Scenario("mismatch");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Failures.Single().Reason, Is.EqualTo("VerificationMismatch"));
        Scenario();
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.Actual, Is.EqualTo(4));
    }
    [Test]
    public async Task ConflictResolutionSurvivesRestartAndUndoRestoresTheConflict()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "Remote" } }] });
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await publisher.RefreshAsync(session, Today);
        Assert.That(session.Document.Sync.Conflicts, Has.Length.EqualTo(1));
        await session.ResolveConflict("I1", PlanField.Title, true, Today);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Remote"));
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Local"));
        Assert.That(session.Document.Sync.Conflicts, Has.Length.EqualTo(1));
    }
    [Test]
    public async Task ElevenCreationsWithPartialFieldFailureResumeWithoutLosingPublishedRows()
    {
        await Start(0);
        await session.Execute(new InsertPlanRows(Enumerable.Range(1, 11).Select(i => PlanRow.New("Created " + i, "acme/repo") with { Actual = 2 }).ToImmutableArray()), Today);
        Scenario("partial", 5);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(11));
        Assert.That(session.Document.State.Rows.All(r => r.Actual == 2 && !r.Identity.StartsWith("local:")), Is.True);
    }
    [Test]
    public async Task FailedReadDoesNotChangeTheCheckpointOrLocalWork()
    {
        await Start(101);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local")]), Today);
        var before = PlanJson.Text(session.Document);
        Scenario("readfailure");
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.False);
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemovedTaskRequiresExplicitDispositionAndResolutionIsUndoable(bool copy)
    {
        await Start(1);
        FakePlanEditor.Save(root, new([], 2));
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        await session.ResolveUnavailable("I1", copy, Today);
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(copy ? 1 : 0));
        Assert.That(session.Document.Sync.Unavailable, Is.Empty);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
        session = opened.Session!;
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Identity, Is.EqualTo("I1"));
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
    }
    [Test]
    public async Task NativeRelationshipsAndProjectOrderAreReadBackAfterPublishing()
    {
        await Start(3);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I2", PlanField.Parent, "I1"), new("I3", PlanField.Parent, "I1"),
            new("I3", PlanField.Predecessors, new[] { "I2" }), new("I2", PlanField.Assignees, new[] { "U1" })]), Today);
        await session.Execute(new MovePlanRows(["I3"], "I2"), Today);
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        var issues = FakePlanEditor.Load(root).Issues;
        Assert.That(issues.Select(i => i.Row.Identity), Is.EqualTo(new[] { "I1", "I3", "I2" }));
        Assert.That(issues.Single(i => i.Row.Identity == "I3").Row.Predecessors, Is.EqualTo(new[] { "I2" }));
        Assert.That(issues.Single(i => i.Row.Identity == "I2").Row.Assignees, Is.EqualTo(new[] { "U1" }));
    }
    [Test]
    public async Task ExplicitFieldSetupAddsTypedFieldsAndRepeatingItDoesNotDuplicateThem()
    {
        await Start(1);
        Assert.That(FakePlanEditor.Load(root).AddedFields, Is.Empty);
        Assert.That((await publisher.AddSchedulingFieldsAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).AddedFields.Length, Is.EqualTo(2));
        Assert.That(session.Document.State.Settings.Columns.Single(m => m.Role == PlanField.StartNoEarlierThan).Name, Is.EqualTo("開始日指定"));
        Assert.That(session.Document.State.Settings.Columns.Single(m => m.Role == PlanField.Fixed).Name, Is.EqualTo("日程固定"));
        Assert.That((await publisher.AddSchedulingFieldsAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(2));
    }
    [Test]
    public async Task ChoosingGitHubDuringResumeRemovesOnlyTheConflictingPendingWrite()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 3m)]), Today);
        Scenario("partial");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I2" ? i with { Row = i.Row with { Actual = 9 } } : i).ToImmutableArray() });
        Scenario();
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        await session.ResolveConflict("I2", PlanField.Actual, true, Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I2").Row.Actual, Is.EqualTo(9));
    }
    [Test]
    public async Task LostUpdateResponseAndFailedVerificationAreReconciledBeforeResumeWithoutLosingUndo()
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 5m)]), Today);
        Scenario("after-and-read");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Scenario();
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error); session = opened.Session!;
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(1));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[0].Actual, Is.Null);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
    }
    [TestCase("rate")]
    [TestCase("reset")]
    public async Task InterruptedRateLimitWaitIsDurableAndResumeHonorsTheDeadline(string fault)
    {
        await Start(1);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Actual, 7m)]), Today);
        using var cancel = new CancellationTokenSource(); runner.RateLimitObserved = cancel.Cancel;
        Scenario(fault);
        Assert.That((await publisher.PublishAsync(session, Today, cancel.Token)).Succeeded, Is.False);
        var deadline = session.Document.Sync.NotBefore;
        Assert.That(deadline, Is.Not.Null);
        Scenario(); runner.RateLimitObserved = null;
        var opened = await PlanSession.OpenAsync(new(root), Project, Today);
        Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error); session = opened.Session!;
        var first = runner.Started.Count;
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(runner.Started[first], Is.GreaterThanOrEqualTo(deadline!.Value));
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Actual, Is.EqualTo(7));
    }
    [Test]
    public async Task RejectedCreationCanBeCorrectedWithoutRecreatingSuccessfulSiblings()
    {
        await Start(0);
        var rows = Enumerable.Range(1, 11).Select(i => PlanRow.New(i == 2 ? "Rejected" : "Task " + i, "acme/repo")).ToImmutableArray();
        await session.Execute(new InsertPlanRows(rows), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(9));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(rows[1].Identity, PlanField.Title, "Corrected")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.That(FakePlanEditor.Load(root).Issues, Has.Length.EqualTo(11));
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Rejected"));
        Assert.That(session.Document.State.Rows[1].Identity.StartsWith("local:", StringComparison.Ordinal), Is.False);
        await session.Redo(Today);
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Corrected"));
    }
    [Test]
    public async Task PartialPublishReleasesVerifiedFailuresForLocalUndoWithoutWriting()
    {
        await Start(2);
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 3m)]), Today);
        Scenario("partial");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.That(session.Document.Sync.Failures, Has.Length.EqualTo(1));
        var before = FakePlanEditor.Load(root);
        await session.Undo(Today);
        Assert.That(session.Document.State.Rows.All(r => r.Actual is null), Is.True);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(before.MutationBatches));
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Actual, Is.EqualTo(3));
    }
    private sealed class CountingRunner(IGhProcessRunner inner) : IGhProcessRunner
    {
        public int Count { get; private set; }
        public List<DateTimeOffset> Started { get; } = [];
        public Action? RateLimitObserved { get; set; }
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            Count++; Started.Add(DateTimeOffset.UtcNow);
            var result = await inner.RunAsync(command, cancellationToken);
            if (result.StandardOutput.StartsWith("HTTP/2 429", StringComparison.Ordinal)) RateLimitObserved?.Invoke();
            return result;
        }
    }
}
