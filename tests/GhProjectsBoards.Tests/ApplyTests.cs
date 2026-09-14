using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ApplyTests
{
    // Contract list: selected existing fields only; immutable revisions; durable dispatch;
    // uncertain recovery; coherent acknowledgement; competing executors; guarded transport.
    [Test]
    public void CheckpointExplicitlyVersionsExecutionHistory()
    {
        var workspace = new EditingWorkspace(new("github.com", 42));
        Assert.That(workspace.Snapshot().Version, Is.EqualTo(5));
    }
    [Test]
    public async Task TenOfOneHundredTitlesDispatchExactlyTenTitleOnlyPayloads()
    {
        var h = await Harness.Create(); var session = h.Workspace.Drafts!;
        var rows = session.Workspace.Open(h.Workspace.Selected!);
        Assert.That(rows.Length, Is.EqualTo(100));
        for (var i = 0; i < 10; i++) session.Workspace.Commit("P1", rows[i * 10].Cells[0], "Changed " + i);
        await h.Workspace.PrepareApplyAsync(rows.Select(r => r.ItemId).ToHashSet());
        Assert.That(h.Workspace.ApplyReview!.Batch.Operations.Length, Is.EqualTo(10));
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);
        Assert.That(h.Writes.Count, Is.EqualTo(10), h.Workspace.Status);
        for (var i = 0; i < 10; i++)
        {
            Assert.That(h.Writes[i].GetProperty("id").GetString(), Is.EqualTo("I" + (i * 10 + 1)));
            Assert.That(h.Writes[i].EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "id", "title" }));
        }
        Assert.That(session.Workspace.DifferenceCount, Is.Zero);
        Assert.That(session.Workspace.Journal.Single().Operations.All(o => o.State == ApplyState.Succeeded), Is.True);
        await h.Workspace.ResumeApplyAsync(session.Workspace.Journal.Single().Id);
        Assert.That(h.Writes.Count, Is.EqualTo(10));
    }
    [TestCase(false), TestCase(true)]
    public async Task NoDifferenceOrRemoteAlreadyEqualPerformsZeroWrites(bool alreadyEqual)
    {
        var h = await Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var rows = w.Open(h.Workspace.Selected!);
        if (alreadyEqual) { w.Commit("P1", rows[0].Cells[0], "Equal"); h.Titles["I1"] = "Equal"; }
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        Assert.That(h.Workspace.ApplyReview!.Batch.Operations, Is.Empty);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);
        Assert.That(h.Writes, Is.Empty);
    }
    [TestCase(false), TestCase(true)]
    public async Task SelectSetAndClearUseIdsAndDoNotWriteTitles(bool clear)
    {
        var h = await Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var rows = w.Open(h.Workspace.Selected!);
        if (clear) w.Clear("P1", [rows[0].Cells[1]]); else w.Commit("P1", rows[0].Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Count, Is.EqualTo(1), h.Workspace.Status);
        Assert.That(h.Writes[0].GetProperty("fieldId").GetString(), Is.EqualTo("P1-status"));
        Assert.That(h.Writes[0].GetProperty("itemId").GetString(), Is.EqualTo("P1-T1"));
        Assert.That(h.Writes[0].TryGetProperty("title", out _), Is.False);
        Assert.That(h.Workspace.Drafts.Workspace.DifferenceCount, Is.Zero);
    }
    [TestCase("before"), TestCase("after"), TestCase("local")]
    public async Task ConflictOrStaleReviewCannotWrite(string when)
    {
        var h = await Harness.Create(); var session = h.Workspace.Drafts!; var rows = session.Workspace.Open(h.Workspace.Selected!);
        session.Workspace.Commit("P1", rows[0].Cells[0], "B");
        if (when == "before") h.Titles["I1"] = "C";
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }); var review = h.Workspace.ApplyReview!;
        if (when == "after") h.Titles["I1"] = "C";
        if (when == "local") session.Workspace.Commit("P1", rows[0].Cells[0], "C");
        await h.Workspace.ConfirmApplyAsync(review);
        Assert.That(h.Writes, Is.Empty);
        Assert.That(session.Workspace.DifferenceCount, Is.EqualTo(1));
    }
    [Test]
    public async Task PendingTextAndLaterCommittedRevisionSurviveOlderAcknowledgement()
    {
        var h = await Harness.Create(); var session = h.Workspace.Drafts!; var rows = session.Workspace.Open(h.Workspace.Selected!);
        session.Workspace.Commit("P1", rows[0].Cells[0], "B"); session.Workspace.SetBuffer(rows[0].Cells[0], "pending\ninvalid");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        Assert.That(h.Workspace.ApplyReview!.PendingBuffers, Is.EqualTo(1));
        h.OnMutation = () => { session.Workspace.SetBuffer(rows[0].Cells[0], null); session.Workspace.Commit("P1", rows[0].Cells[0], "C"); };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview);
        var field = session.Workspace.Field(rows[0].Cells[0])!;
        Assert.That(h.Titles["I1"], Is.EqualTo("B"));
        Assert.That(field.Baseline, Is.EqualTo("B")); Assert.That(field.Change!.Value, Is.EqualTo("C"));
        session.Workspace.Undo("P1"); Assert.That(session.Workspace.Field(rows[0].Cells[0])!.Baseline, Is.EqualTo("B"));
    }
    [TestCase("B"), TestCase("Issue 1"), TestCase("C"), TestCase("unreadable")]
    public async Task RestartNeverBlindlyReplaysAnUncertainWrite(string remoteValue)
    {
        var h = await Harness.Create(); var session = h.Workspace.Drafts!; var rows = session.Workspace.Open(h.Workspace.Selected!);
        session.Workspace.Commit("P1", rows[0].Cells[0], "B");
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.LoseResponse = true; await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(session.Workspace.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Unknown));
        h.Titles["I1"] = remoteValue; h.Unreadable = remoteValue == "unreadable";
        var restart = new RegistrationWorkspace(new(h.Root)); await restart.RestoreAsync();
        await restart.BindAsync(h.Context, h.Service); await restart.SelectAsync(new(new("github.com", 42), "P1"));
        Assert.That(h.Writes.Count, Is.EqualTo(1));
        await restart.ResumeApplyAsync(restart.Drafts!.Workspace.Journal.Single().Id);
        Assert.That(h.Writes.Count, Is.EqualTo(1));
        Assert.That(restart.Drafts.Workspace.Journal.Single().Operations.Single().State,
            Is.EqualTo(remoteValue == "B" ? ApplyState.Succeeded : remoteValue == "unreadable" ? ApplyState.Unknown : ApplyState.Blocked));
    }
    [Test]
    public async Task CompetingExecutorCannotDispatch()
    {
        var h = await Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var rows = w.Open(h.Workspace.Selected!);
        w.Commit("P1", rows[0].Cells[0], "B"); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        using var competing = new DraftStore(h.Root).AcquireExecution(w.Scope);
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Is.Empty);
    }
    [Test]
    public async Task SharedTitleAppliesOnceAndLeavesOtherProjectSelectUnapplied()
    {
        var h = await Harness.Create(); var s = h.Workspace.Drafts!; var first = h.Workspace.Selected!;
        var rows = s.Workspace.Open(first); s.Workspace.Commit("P1", rows[0].Cells[0], "B");
        var choice = await new ProjectDiscovery(h.Service).ResolveAsync(h.Context, "https://github.com/users/sample-user/projects/2", default);
        await h.Workspace.RegisterAsync(choice, null);
        var other = s.Workspace.Open(h.Workspace.Selected!); s.Workspace.Commit("P2", other[0].Cells[1], "done", true);
        await h.Workspace.SelectAsync(first.Snapshot.Id); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Count, Is.EqualTo(1));
        Assert.That(s.Workspace.Field(other[0].Cells[0])!.Baseline, Is.EqualTo("B"));
        Assert.That(s.Workspace.Field(other[0].Cells[1])!.Change!.Value, Is.EqualTo("done"));
        Assert.That(s.Workspace.CheckpointRegistrations.Single(r => r.Snapshot.Id.NodeId == "P2").Snapshot.Issues[new(new("github.com", 42), "I1")].Title.Value, Is.EqualTo("B"));
    }
    [Test]
    public async Task CancelDuringDispatchKeepsUncertaintyAndCancelsRemainingFields()
    {
        var h = await Harness.Create(); var s = h.Workspace.Drafts!; var rows = s.Workspace.Open(h.Workspace.Selected!);
        s.Workspace.Commit("P1", rows[0].Cells[0], "B"); s.Workspace.Commit("P1", rows[0].Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }); h.OnMutation = h.Workspace.Cancel;
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Count, Is.EqualTo(1));
        var ops = s.Workspace.Journal.Single().Operations;
        Assert.That(ops[0].State, Is.EqualTo(ApplyState.Unknown)); Assert.That(ops[1].State, Is.EqualTo(ApplyState.Cancelled));
    }
    [TestCase(1), TestCase(2)]
    public async Task OlderProfileMigratesWithRecoveryBackupAndJournal(int version)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-apply-migrate-" + Guid.NewGuid()); var store = new DraftStore(root);
        var w = new EditingWorkspace(new("github.com", 42)); var registration = EditingTests.Registration(); w.Open(registration);
        await store.SaveAsync(w.Snapshot() with { Version = version, Journal = null }, 0);
        var restored = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!); restored.SetRegistrations([registration]);
        await store.SaveAsync(restored.Snapshot(), w.Revision);
        Assert.That((await store.LoadAsync(w.Scope))!.Version, Is.EqualTo(5)); Assert.That(File.Exists(store.FileFor(w.Scope) + ".bak"), Is.True);
    }
    [Test]
    public async Task CorruptJournalCannotRestoreOrOverwriteRecoveredData()
    {
        var h = await Harness.Create(); var s = h.Workspace.Drafts!; var rows = s.Workspace.Open(h.Workspace.Selected!);
        s.Workspace.Commit("P1", rows[0].Cells[0], "B"); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        var record = s.Workspace.Snapshot(); var batch = record.Journal![0];
        record.Journal[0] = batch with { Project = new(new("other.test", 42), "P1") };
        Assert.Throws<InvalidDataException>(() => EditingWorkspace.Restore(record));
        Assert.That((await new DraftStore(h.Root).LoadAsync(s.Workspace.Scope))!.Journal![0].Project.Scope.Host, Is.EqualTo("github.com"));
    }
    [Test]
    public async Task UnresolvedHistoryBlocksUnregisterAndExplicitNewReviewPreservesAttempts()
    {
        var h = await Harness.Create(); var s = h.Workspace.Drafts!; var rows = s.Workspace.Open(h.Workspace.Selected!);
        s.Workspace.Commit("P1", rows[0].Cells[0], "B"); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.LoseResponse = true; await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        await h.Workspace.UnregisterAsync(discardDrafts: true); Assert.That(h.Workspace.Selected, Is.Not.Null);
        var batch = s.Workspace.Journal.Single();
        Assert.Throws<InvalidOperationException>(() => s.Workspace.RecordApply(batch.Id, batch.Operations[0] with { Intended = new("wrong") }));
        h.Titles["I1"] = "Issue 1"; h.LoseResponse = false;
        await h.Workspace.ResumeApplyAsync(batch.Id); Assert.That(h.Writes.Count, Is.EqualTo(1));
        await h.Workspace.SupersedeApplyAsync(batch.Id);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" }); await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Count, Is.EqualTo(2)); Assert.That(s.Workspace.Journal.Count, Is.EqualTo(2));
        Assert.That(s.Workspace.Journal[0].Operations[0].Attempts[0].State, Is.EqualTo(ApplyState.Unknown));
        Assert.That(s.Workspace.Journal[1].Operations[0].State, Is.EqualTo(ApplyState.Succeeded));
    }
    [TestCase("permission"), TestCase("graphql"), TestCase("wrong-return"), TestCase("verification")]
    public async Task PartialFieldFailureDoesNotAcknowledgeTheUnverifiedField(string mode)
    {
        var h = await Harness.Create(); var s = h.Workspace.Drafts!; var rows = s.Workspace.Open(h.Workspace.Selected!);
        s.Workspace.Commit("P1", rows[0].Cells[0], "B"); s.Workspace.Commit("P1", rows[0].Cells[1], "done", true);
        await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.MutationResult = (q, _) => !q.Contains("ApplyTitle") ? mode switch {
            "permission" => ScriptedRunner.Http("{}", 403),
            "graphql" => ScriptedRunner.Http("{\"data\":null,\"errors\":[{\"type\":\"INTERNAL\"}]}"),
            "wrong-return" => ScriptedRunner.Http("{\"data\":{\"updateProjectV2ItemFieldValue\":{\"projectV2Item\":{\"id\":\"wrong\"}}}}"),
            _ => null } : null;
        if (mode == "verification") h.OnMutation = () => { if (h.Writes.Count == 2) h.Unreadable = true; };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        var ops = s.Workspace.Journal.Single().Operations;
        Assert.That(ops[0].State, Is.EqualTo(ApplyState.Succeeded));
        Assert.That(ops[1].State, Is.EqualTo(mode == "permission" ? ApplyState.Failed : ApplyState.Unknown));
        Assert.That(s.Workspace.Field(rows[0].Cells[1])!.Change, Is.Not.Null);
    }
    [Test]
    public async Task RateLimitWaitRechecksBeforeReschedulingKnownRejectedWrite()
    {
        var h = await Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var rows = w.Open(h.Workspace.Selected!);
        w.Commit("P1", rows[0].Cells[0], "B"); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        h.MutationResult = (_, _) => { h.Titles["I1"] = "C"; return ScriptedRunner.Http("{}", 429, 1, "Retry-After: 0\r\n"); };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes.Count, Is.EqualTo(1));
        Assert.That(h.Workspace.Drafts.Workspace.Journal.Single().Operations.Single().State, Is.EqualTo(ApplyState.Blocked));
    }
    [TestCase("permission"), TestCase("option"), TestCase("item"), TestCase("identity")]
    public async Task ChangedStructureOrIdentityPreventsDispatch(string mode)
    {
        var h = await Harness.Create(); var w = h.Workspace.Drafts!.Workspace; var rows = w.Open(h.Workspace.Selected!);
        w.Commit("P1", rows[0].Cells[1], "done", true); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        if (mode == "identity") h.Boundary.ViewerId = 999;
        h.ChangeResponse = (q, data) => {
            if (q.Contains("ProjectFields") && mode == "permission") data["data"]!["node"]!["viewerCanUpdate"] = false;
            if (q.Contains("ProjectFields") && mode == "option") data["data"]!["node"]!["fields"]!["nodes"]![0]!["options"] = new JsonArray();
            if (q.Contains("ProjectItems") && mode == "item") data["data"]!["node"]!["items"]!["nodes"]![0]!["content"]!["id"] = "other";
        };
        await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!);
        Assert.That(h.Writes, Is.Empty);
    }
    [TestCase("dispatch"), TestCase("dispatch-intent"), TestCase("acknowledgement")]
    public async Task SaveFailureStopsDispatchAndPreservesRecovery(string stage)
    {
        var h = await Harness.Create(); var s = h.Workspace.Drafts!; var rows = s.Workspace.Open(h.Workspace.Selected!);
        s.Workspace.Commit("P1", rows[0].Cells[0], "B"); await h.Workspace.PrepareApplyAsync(new HashSet<string> { "P1-T1" });
        FileStream? locked = null;
        if (stage == "dispatch") locked = new(Path.Combine(h.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        else if (stage == "dispatch-intent") h.ChangeResponse = (q, _) => { if (q.Contains("ProjectItems") && locked is null) locked = new(Path.Combine(h.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); };
        else h.OnMutation = () => locked = new(Path.Combine(h.Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { await h.Workspace.ConfirmApplyAsync(h.Workspace.ApplyReview!); }
        finally { locked?.Dispose(); }
        Assert.That(h.Writes.Count, Is.EqualTo(stage == "acknowledgement" ? 1 : 0));
        if (stage == "acknowledgement")
        {
            Assert.That(h.Workspace.Status, Does.Contain("GitHubの読み戻しは一致"));
            var record = await new DraftStore(h.Root).LoadAsync(s.Workspace.Scope);
            Assert.That(record!.Journal!.Single().Operations.Single().State, Is.EqualTo(ApplyState.Running));
            Assert.That(record.Fields.Single(f => f.Key.Kind == "Title" && f.Key.NodeId == "I1").Change!.Value, Is.EqualTo("B"));
        }
    }
    internal sealed class Harness
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "ghpb-apply-" + Guid.NewGuid());
        public readonly Dictionary<string, string> Titles = [];
        public readonly Dictionary<string, string?> Selects = [];
        public readonly List<JsonElement> Writes = [];
        public Action? OnMutation;
        public Func<string, JsonElement, GhProcessResult?>? MutationResult;
        public Action<string, JsonNode>? ChangeResponse;
        public ProjectReaderTests.ProjectBoundary Boundary = null!;
        public bool LoseResponse, Unreadable;
        public RegistrationWorkspace Workspace = null!;
        public GhConnectionService Service = null!;
        public ConnectionContext Context = null!;
        public static async Task<Harness> Create()
        {
            var h = new Harness(); var boundary = h.Boundary = new ProjectReaderTests.ProjectBoundary();
            boundary.Override = (q, v) =>
            {
                if (q.StartsWith("mutation"))
                {
                    var input = v.GetProperty("input"); h.Writes.Add(input.Clone());
                    h.OnMutation?.Invoke();
                    var custom = h.MutationResult?.Invoke(q, input); if (custom is not null) return custom;
                    object response;
                    if (q.Contains("ApplyTitle")) { var id = input.GetProperty("id").GetString()!; var title = input.GetProperty("title").GetString()!;
                        h.Titles[id] = title; response = new { data = new { updateIssue = new { issue = new { id, title } } } }; }
                    else
                    {
                        var id = input.GetProperty("itemId").GetString()!;
                        h.Selects[id] = q.Contains("ApplyClear") ? null : input.GetProperty("value").GetProperty("singleSelectOptionId").GetString();
                        response = q.Contains("ApplyClear") ? (object)new { data = new { clearProjectV2ItemFieldValue = new { projectV2Item = new { id } } } }
                            : new { data = new { updateProjectV2ItemFieldValue = new { projectV2Item = new { id } } } };
                    }
                    return h.LoseResponse ? new(ProcessCompletion.TimedOut, true, null) : ScriptedRunner.Http(JsonSerializer.Serialize(response));
                }
                if (h.Unreadable && q.Contains("ProjectItems")) return ScriptedRunner.Http("{}", 503);
                var source = RegistrationResponses.Query(q, v); if (source is null) return null;
                var data = JsonNode.Parse(JsonSerializer.Serialize(source))!;
                if (q.Contains("ProjectItems"))
                {
                    var page = data["data"]!["node"]!["items"]!;
                    page["totalCount"] = 100; page["pageInfo"]!["hasNextPage"] = false; page["pageInfo"]!["endCursor"] = null;
                    foreach (var item in page["nodes"]!.AsArray())
                    {
                        if (h.Titles.TryGetValue(item!["content"]!["id"]!.ToString(), out var title)) item["content"]!["title"] = title;
                        if (h.Selects.TryGetValue(item!["id"]!.ToString(), out var option))
                        {
                            var values = item["fieldValues"]!;
                            if (option is null) { values["nodes"] = new JsonArray(); values["totalCount"] = 0; }
                            else values["nodes"]![0]!["optionId"] = option;
                        }
                    }
                }
                h.ChangeResponse?.Invoke(q, data);
                return ScriptedRunner.Http(data.ToJsonString());
            };
            h.Service = new("gh.exe", "github.com", boundary.Runner); h.Context = (await h.Service.ConnectAsync()).Context!;
            h.Workspace = new(new(h.Root)); await h.Workspace.BindAsync(h.Context, h.Service);
            var choice = await new ProjectDiscovery(h.Service).ResolveAsync(h.Context, "https://github.com/users/sample-user/projects/1", default);
            await h.Workspace.RegisterAsync(choice, null); return h;
        }
    }
}
