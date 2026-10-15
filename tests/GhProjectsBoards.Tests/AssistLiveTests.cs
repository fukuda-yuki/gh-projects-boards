using GhProjectsBoards.Core.Assist;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("LiveAssist"), Explicit("Uses a real provider and an API key; opt in separately from Core tests.")]
internal sealed class AssistLiveTests
{
    [Test]
    public async Task OpenCodeGoExplainsLatenessAndProposesOneLocallyAppliedUndoOnEvaluationVersion()
    {
        var key = Environment.GetEnvironmentVariable("OPENCODE_GO_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Fail("Set OPENCODE_GO_API_KEY in the process environment; do not put it on the command line.");
        var endpoint = Environment.GetEnvironmentVariable("OPENCODE_GO_BASE_URL");
        if (string.IsNullOrWhiteSpace(endpoint)) Assert.Fail("Set OPENCODE_GO_BASE_URL to the approved OpenAI-compatible endpoint.");
        var runtime = Environment.GetEnvironmentVariable("GHPB_ASSIST_RUNTIME_PATH");
        if (string.IsNullOrWhiteSpace(runtime) || !File.Exists(runtime)) Assert.Fail("Set GHPB_ASSIST_RUNTIME_PATH to the cached 1.0.98 copilot-runtime.exe beside runtime.node.");
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "assist-live-" + Guid.NewGuid().ToString("N"));
        var plan = EvaluationFixture.ReadPlan();
        var snapshot = EvaluationFixture.Simulate(plan, plan.StatusDates).Single(s => s.StatusDate == plan.StatusDate);
        var target = snapshot.Document.State.Rows.First(r => r.Phase == "PS" && !r.Closed && r.Remaining > 0);
        var proposedRemaining = target.Remaining!.Value + 32;
        // Seed a local overrun against the evaluation baseline so the question has a concrete later finish.
        var rows = snapshot.Document.State.Rows.Replace(target, target with { Remaining = target.Remaining + 16 });
        var document = snapshot.Document with { State = snapshot.Document.State with { Rows = rows } };
        var session = await PlanSession.CreateAsync(new(root), document, snapshot.StatusDate);
        Assert.That((await session.FlushAsync()).Succeeded, Is.True);
        var tools = new AssistPlanTools(session, snapshot.StatusDate, "# AI アシスト\n変更は提案を確認して適用します。発行は別操作です。");
        var before = PlanJson.Text(session.Document);
        var settings = new AssistSettings { Mode = AssistMode.Byok, Endpoint = endpoint!, Model = "deepseek-v4.1-flash", ConversationIdHeader = "x-opencode-session" };
        try {
            await using (var conversation = new AssistConversation(settings, root, "github.com", "offline-evaluation", tools, key, runtime)) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var events = new List<AssistEvent>();
                await foreach (var e in conversation.SendAsync("選択中のタスクはなぜ遅れていますか。ツールで調べ、日程の理由を説明してください。", target.Identity, timeout.Token)) events.Add(e);
                Assert.That(events.Any(e => e.Kind == AssistEventKind.Error), Is.False, "Live turn failed; raw provider output is intentionally not logged.");
                Assert.That(events.Any(e => e.Kind == AssistEventKind.Message && !string.IsNullOrWhiteSpace(e.Text)), Is.True);
                Assert.That(events.Any(e => e.Kind == AssistEventKind.TurnEnd), Is.True);
                Assert.That(tools.References.Select(r => r.Identity), Does.Contain(target.Identity));
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
                events.Clear();
                await foreach (var e in conversation.SendAsync($"選択中のタスクの残を{proposedRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture)}時間にする変更を propose_changes で提案してください。適用は私が行います。", target.Identity, timeout.Token)) events.Add(e);
                Assert.That(events.Any(e => e.Kind == AssistEventKind.Error), Is.False);
                Assert.That(tools.PendingProposal, Is.Not.Null);
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before)); Assert.That(session.UndoCount, Is.Zero);
                var applied = await tools.ApplyProposal(tools.PendingProposal!.Id);
                Assert.That(applied.Succeeded, Is.True, applied.Error);
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == target.Identity).Remaining, Is.EqualTo(proposedRemaining));
                Assert.That(session.UndoCount, Is.EqualTo(1));
                var undone = await session.Undo(snapshot.StatusDate); Assert.That(undone.Succeeded, Is.True, undone.Error);
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
                TestContext.Out.WriteLine("SDK 1.0.22; CLI 1.0.98; model deepseek-v4.1-flash; answer, task references, proposal, apply and Undo verified. No provider text or credentials recorded.");
            }
            Assert.That(Directory.GetDirectories(Path.Combine(root, "PlanningEditor", "v1", "assist")), Is.Empty);
        }
        finally { await session.FlushAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
