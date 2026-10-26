using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.Assist;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class AssistStorageTests
{
    private string root = null!;
    private readonly List<PlanSession> sessions = [];
    [SetUp] public void Setup() => root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "assist-" + Guid.NewGuid().ToString("N"));
    [TearDown] public async Task Cleanup() { foreach (var s in sessions) await s.FlushAsync(); sessions.Clear(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private async Task<PlanSession> Create(string suffix = "plan")
    {
        var session = await PlanSession.CreateAsync(new(Path.Combine(root, suffix)), AssistRulesTests.Document(), AssistRulesTests.Today);
        sessions.Add(session); Assert.That((await session.FlushAsync()).Succeeded, Is.True); return session;
    }
    [Test]
    public async Task ProposalAndExplicitDetailReferencesPrecedeSearchResultsAndResetEachTurn()
    {
        var tools = new AssistPlanTools(await Create(), AssistRulesTests.Today, "");
        tools.BeginTurn(); tools.FindTasks(new()); tools.GetTasks(["B"]);
        Assert.That(tools.References.Select(r => r.Identity), Is.EqualTo(new[] { "B", "R", "A" }));
        tools.ProposeChanges([new("A", PlanField.Title, JsonSerializer.SerializeToElement("設計を見直す"))]);
        tools.SummarizePlan();
        Assert.That(tools.References.Select(r => r.Identity), Is.EqualTo(new[] { "A", "B", "R" }));
        tools.BeginTurn();
        Assert.That(tools.References, Is.Empty);
        tools.ProposeChanges([new("B", PlanField.Title, JsonSerializer.SerializeToElement("検証を見直す"))]);
        Assert.That(tools.References.Select(r => r.Identity), Is.EqualTo(new[] { "B" }));
    }

    [Test]
    public async Task PlanSummaryAddsNoTaskReferences()
    {
        var tools = new AssistPlanTools(await Create(), AssistRulesTests.Today, "");
        tools.BeginTurn(); tools.SummarizePlan();
        Assert.That(tools.References, Is.Empty);
    }

    [Test]
    public async Task ReadAndProposeLeaveHistoryStorageAndUnpublishedStateUntouchedThenApplyIsOneTypingUndo()
    {
        var session = await Create(); var typed = await Create("typed");
        var before = PlanJson.Text(session.Document); var count = session.Changes(AssistRulesTests.Today).TaskCount;
        var path = new PlanStore(Path.Combine(root, "plan")).FileFor(session.Document.Project); var bytes = await File.ReadAllBytesAsync(path);
        var tools = new AssistPlanTools(session, AssistRulesTests.Today, "# 手順\n編集");
        tools.BeginTurn(); tools.FindTasks(new() { Title = "PS-004" }); tools.GetTasks(["B"]); tools.SummarizePlan(); tools.ReadManual("編集");
        var result = tools.ProposeChanges([new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(40m)), new("B", PlanField.Title, JsonSerializer.SerializeToElement("変更"))]);
        Assert.That(result.Errors, Is.Empty);
        Assert.Multiple(() => { Assert.That(session.UndoCount, Is.Zero); Assert.That(session.RedoCount, Is.Zero);
            Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before)); Assert.That(session.Changes(AssistRulesTests.Today).TaskCount, Is.EqualTo(count));
            Assert.That(tools.References.Select(r => r.Identity), Does.Contain("A").And.Contain("B")); });
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
        var applied = await tools.ApplyProposal(result.Proposal!.Id);
        Assert.That(applied.Succeeded, Is.True, applied.Error);
        await typed.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("A", PlanField.Remaining, 40m), new("B", PlanField.Title, "変更")]), AssistRulesTests.Today);
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(PlanJson.Text(typed.Document))); Assert.That(session.UndoCount, Is.EqualTo(1));
        await session.Undo(AssistRulesTests.Today); Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        tools.BeginTurn(); Assert.That(tools.References, Is.Empty);
    }
    [Test]
    public async Task ProposalMoveCountMatchesTheEffectShownAfterApplying()
    {
        var session = await PlanSession.CreateAsync(new(Path.Combine(root, "held")),
            AssistRulesTests.Document(), AssistRulesTests.Today);
        sessions.Add(session);
        var tools = new AssistPlanTools(session, AssistRulesTests.Today, "");
        var proposal = tools.ProposeChanges([new("B", PlanField.Remaining, JsonSerializer.SerializeToElement(40m))]).Proposal!;
        var applied = await tools.ApplyProposal(proposal.Id);
        Assert.That(applied.Recalculation, Is.Null, "An ordinary edit reports unpublished dates, not a whole-plan recalculation.");
        Assert.That(applied.Succeeded, Is.True, applied.Error);
        Assert.That(proposal.MovedTasks, Is.EqualTo(proposal.ScheduleEffect.Total).And.Positive);
    }
    [TestCase("edit"), TestCase("undo-redo"), TestCase("status-date"), TestCase("refresh"), TestCase("publish"), TestCase("reset")]
    public async Task EveryDocumentRevisionRefusesStaleProposal(string action)
    {
        var session = await Create(); var tools = new AssistPlanTools(session, AssistRulesTests.Today, "");
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("B", PlanField.Title, "先の変更")]), AssistRulesTests.Today);
        var proposal = tools.ProposeChanges([new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(40m))]).Proposal!;
        var remote = new PlanRemoteSnapshot(session.Document.Baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0);
        switch (action) {
            case "edit": await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("B", PlanField.Title, "次の変更")]), AssistRulesTests.Today); break;
            case "undo-redo": await session.Undo(AssistRulesTests.Today); await session.Redo(AssistRulesTests.Today); break;
            case "status-date": await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = AssistRulesTests.Today.AddDays(1) }), AssistRulesTests.Today); break;
            case "refresh": await session.AcceptRefresh(remote, AssistRulesTests.Today); break;
            case "publish":
                await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), []) });
                await session.AcceptPublished(remote, AssistRulesTests.Today); break;
            case "reset": await session.AcceptRefresh(remote, AssistRulesTests.Today, reset: true); break;
        }
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await tools.ApplyProposal(proposal.Id));
        Assert.That(ex!.Message, Is.EqualTo(AssistProposal.StaleMessage));
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before)); Assert.That(session.UndoCount, Is.EqualTo(undo));
    }
    [Test]
    public async Task NewProposalReplacesPendingAndDiscardCannotApply()
    {
        var tools = new AssistPlanTools(await Create(), AssistRulesTests.Today, "");
        var first = tools.ProposeChanges([new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(24m))]).Proposal!;
        var second = tools.ProposeChanges([new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(40m))]).Proposal!;
        Assert.ThrowsAsync<InvalidOperationException>(async () => await tools.ApplyProposal(first.Id));
        tools.DiscardProposal(); Assert.ThrowsAsync<InvalidOperationException>(async () => await tools.ApplyProposal(second.Id));
    }

    [Test]
    public async Task LaterTurnReportsNoOldProposalButKeepsItAvailableForApplication()
    {
        var tools = new AssistPlanTools(await Create(), AssistRulesTests.Today, "");
        tools.BeginTurn();
        var first = tools.ProposeChanges([new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(24m))]).Proposal!;
        Assert.That(tools.TurnProposal, Is.SameAs(first));
        tools.BeginTurn(); tools.GetTasks(["A"]);
        Assert.That(tools.TurnProposal, Is.Null);
        Assert.That(tools.PendingProposal, Is.SameAs(first));
        var second = tools.ProposeChanges([new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(40m))]).Proposal!;
        Assert.That(tools.TurnProposal, Is.SameAs(second));
        Assert.That(tools.PendingProposal, Is.SameAs(second));
    }
    [Test]
    public async Task ModelReceivesOnlyFiveCustomToolsAndNoApplyOrPublishTool()
    {
        var tools = new AssistPlanTools(await Create(), AssistRulesTests.Today, "");
        var config = AssistConversation.SessionOptions(new() { Mode = AssistMode.Copilot, Model = "model" }, null, "conversation", tools);
        Assert.That(config.AvailableTools, Is.EquivalentTo(new[] { "custom:find_tasks", "custom:get_tasks", "custom:summarize_plan", "custom:read_manual", "custom:propose_changes" }));
        Assert.That(config.Tools!.Select(t => t.Name), Is.EquivalentTo(new[] { "find_tasks", "get_tasks", "summarize_plan", "read_manual", "propose_changes" }));
    }
    [Test]
    public async Task SelectedContextAcceptsEveryRowOfTheOpenPlanAndRejectsAnotherIdentity()
    {
        var tools = new AssistPlanTools(await Create(), AssistRulesTests.Today, "");
        Assert.DoesNotThrow(() => tools.ValidateSelectedIdentities([]), "No selection is a valid context.");
        Assert.DoesNotThrow(() => tools.ValidateSelectedIdentities(["A", "B"]), "Every selected plan row is accepted.");
        var error = Assert.Throws<ArgumentException>(() => tools.ValidateSelectedIdentities(["A", "other"]));
        Assert.That(error!.Message, Is.EqualTo("開いている計画にないタスクです。"));
    }
    [Test]
    public async Task SettingsRoundTripIsAppWideAtomicAndContainsNoKey()
    {
        var store = new AssistSettingsStore(root); Assert.That((await store.LoadAsync()).Settings.Mode, Is.EqualTo(AssistMode.None));
        var settings = new AssistSettings { Mode = AssistMode.Byok, Model = "deepseek-v4.1-flash", Endpoint = "https://example.test/v1", ConversationIdHeader = "x-opencode-session" };
        await store.SaveAsync(settings); Assert.That((await store.LoadAsync()).Settings, Is.EqualTo(settings));
        var changed = settings with { Endpoint = "https://other.example/v1" };
        await store.SaveAsync(changed); Assert.That((await store.LoadAsync()).Settings, Is.EqualTo(changed));
        var json = await File.ReadAllTextAsync(store.FilePath); Assert.That(json, Does.Not.Contain("apiKey").And.Not.Contain("ApiKey"));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"), Is.Empty);
    }
    [TestCase("{bad"), TestCase("{\"version\":1,\"mode\":\"byok\",\"apiKey\":\"sentinel\"}"), TestCase("{\"version\":1,\"mode\":\"unknown\"}")]
    public async Task UnreadableSettingsFallBackToNoneAndCannotBeOverwritten(string content)
    {
        var store = new AssistSettingsStore(root); Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!); await File.WriteAllTextAsync(store.FilePath, content);
        var result = await store.LoadAsync(); Assert.That(result.Settings.Mode, Is.EqualTo(AssistMode.None)); Assert.That(result.Error, Is.Not.Null);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(new()));
        Assert.That(await File.ReadAllTextAsync(store.FilePath), Is.EqualTo(content));
    }
    [Test]
    public async Task EvaluationVersionReadToolsPreserveStoredPlanAndExposeSummaryAndEveryTask()
    {
        var plan = EvaluationFixture.ReadPlan(); var snapshot = EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
        var session = await PlanSession.CreateAsync(new(root), snapshot.Document, plan.StatusDate); sessions.Add(session);
        Assert.That((await session.FlushAsync()).Succeeded, Is.True);
        var before = PlanJson.Text(session.Document); var tools = new AssistPlanTools(session, plan.StatusDate, "# 手順\nマニュアル");
        var all = new List<AssistTaskReference>();
        for (var offset = 0; offset < snapshot.Rows.Length; offset += 20) {
            var page = tools.FindTasks(new() { Offset = offset, Limit = 20 }); all.AddRange(page.Items);
            var full = tools.GetTasks(page.Items.Select(t => t.Identity).ToArray()); Assert.That(full.Items.Count, Is.EqualTo(page.Items.Count));
        }
        Assert.That(all.Count, Is.EqualTo(snapshot.Rows.Length)); Assert.That(tools.SummarizePlan().Measures.Total.Count, Is.GreaterThan(0));
        Assert.That(tools.ReadManual("マニュアル").Total, Is.EqualTo(1)); Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.Zero); Assert.That(session.RedoCount, Is.Zero);
    }
    [Test]
    public void RuntimeCleanupDeletesLeftoversAndPreservesOtherActiveConversations()
    {
        using var active = new AssistRuntimeDirectory(root, Guid.NewGuid().ToString("N"));
        var stale = Path.Combine(root, "PlanningEditor", "v1", "assist", "conversation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stale, "session-state"));
        File.WriteAllText(Path.Combine(stale, "session-state", "history.json"), "nonsecret test state");
        AssistConversation.CleanupLeftovers(root);
        Assert.That(Directory.Exists(stale), Is.False); Assert.That(Directory.Exists(active.Path), Is.True);
        using var other = new AssistRuntimeDirectory(root, Guid.NewGuid().ToString("N"));
        Assert.That(Directory.Exists(active.Path), Is.True);
        var otherPath = other.Path; other.Dispose(); Assert.That(Directory.Exists(otherPath), Is.False);
    }
}
