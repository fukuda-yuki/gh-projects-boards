using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #308 live validation on the authorized sandbox Project 3 (https://github.com/users/fukuda-yuki/projects/3).
// It moves a few items' Status and closed state through gh, verifies the product's record definitions, scoped
// adoption, publish read-back and the "closing writes no plan date" rule against GitHub, and restores the items
// it touched. Only a few items are read, so the run stays light for GitHub's rate limits; the refresh-cost
// measurement below does one full Project read. Run only with GHPB_LIVE_PROJECT3=1, without the LiveGitHub filter:
//   $env:GHPB_LIVE_PROJECT3='1'; dotnet <runner> <tests dll> 'PlanActualDatesLiveTests'
[TestFixture, Category("LiveGitHub")]
[NonParallelizable]
internal sealed class PlanActualDatesLiveTests
{
    private const string ProjectNodeId = "PVT_kwHOBGPKL84BjFYc";
    private const string Gh = @"C:\Program Files\GitHub CLI\gh.exe";
    private const string SandboxUrl = "https://github.com/users/fukuda-yuki/projects/3";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    [Test]
    public async Task Project3StatusesClosesPublishAndRestoresTheTouchedItems()
    {
        Live();
        var (service, context, project) = await Connect();
        var candidates = await Candidates(service, context);
        var notStarted = new HashSet<string>(PlanActualDates.DefaultNotStarted, StringComparer.Ordinal);
        var open = candidates.Where(c => !c.Closed && c.Status is not null && notStarted.Contains(c.Status)).Take(3).ToArray();
        Assert.That(open.Length, Is.EqualTo(3), "Project 3 has open not-started Issues to exercise");
        var dated = candidates.FirstOrDefault(c => !c.Closed && c.End is not null && open.All(o => o.Id != c.Id));
        Assert.That(dated, Is.Not.Null, "Project 3 has an open Issue with a plan end for the R9 case");
        var a = open[0].Id;
        var b = open[1].Id;
        var c = open[2].Id;
        var ids = new[] { a, b, c, dated!.Id };
        var evidence = new JsonObject { ["touched"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        Directory.CreateDirectory(Root);
        var store = new PlanStore(Root);
        Directory.CreateDirectory(store.Root);
        var status = await StatusField(service, context);
        var session = await OpenScoped(service, context, project, store, ids, evidence);
        try
        {
            // R4: a not-started -> started move gives 着手日; In progress -> In review keeps it, then a repeat
            // Ready -> In progress is the latest start transition.
            await SetStatus(service, context, status, a, status.Option("In progress"));
            await SetStatus(service, context, status, a, status.Option("In review"));
            await SyncScoped(session, service, context, project, [a]);
            var first = Scheduled(session, a).Input.ActualStart;
            Assert.That(first, Is.Not.Null, "a Status write becomes 着手日 on the next read");
            Assert.That((first!.Day, first.To), Is.EqualTo((Today, "In progress")), "In review keeps the first started day");
            await SetStatus(service, context, status, a, status.Option("Ready"));
            await SetStatus(service, context, status, a, status.Option("In progress"));
            await SyncScoped(session, service, context, project, [a]);
            var again = Scheduled(session, a).Input.ActualStart!;
            Assert.That((again.From, again.To, again.Day, again.Automated), Is.EqualTo(("Ready", "In progress", Today, false)),
                "Ready -> In progress again gives the newer start transition");
            evidence["R4"] = new JsonObject { ["startDay"] = again.Day.ToString("yyyy-MM-dd"), ["from"] = again.From, ["to"] = again.To, ["actor"] = again.Actor };

            // R5/R7: a completed close becomes 完了日 on the read; a reopen clears it.
            await Close(service, context, b, "COMPLETED");
            await SyncScoped(session, service, context, project, [b]);
            var finish = Scheduled(session, b).Input.ActualFinish;
            Assert.That(finish, Is.Not.Null, "a completed close becomes 完了日");
            Assert.That(finish!.Day, Is.EqualTo(Today));
            await Reopen(service, context, b);
            await SyncScoped(session, service, context, project, [b]);
            Assert.That(Scheduled(session, b).Input.ActualFinish, Is.Null, "a reopen clears 完了日");
            evidence["R5"] = new JsonObject { ["finishAfterClose"] = Today.ToString("yyyy-MM-dd"), ["clearedByReopen"] = true };

            // R5: a NOT_PLANNED close (中止) has no 完了日.
            await Close(service, context, c, "NOT_PLANNED");
            await SyncScoped(session, service, context, project, [c]);
            var cancelled = session.Document.State.Rows.Single(r => r.Identity == c);
            Assert.That((cancelled.Closed, cancelled.Cancelled, Scheduled(session, c).Input.ActualFinish),
                Is.EqualTo((true, true, (PlanActualFinish?)null)));
            await Reopen(service, context, c);
            await SyncScoped(session, service, context, project, [c]);
            evidence["R5NotPlanned"] = new JsonObject { ["cancelled"] = true, ["finish"] = null };

            // R9/R7: closing through the app plans no plan-date write, publishes none, and records 完了日 from the
            // read-back the close needs.
            var datedId = dated.Id;
            var before = session.Document.State.Rows.Single(r => r.Identity == datedId);
            var beforeDates = (before.Start, before.End);
            Assert.That(before.End, Is.Not.Null, "the plan carries the GitHub end date the R9 case protects");
            var beforeRemote = await DateValues(service, context, datedId);
            await session.Execute(new ClosePlanRows([datedId]), Today);
            var planned = session.Changes(Today).Fields[datedId];
            Assert.That(planned.Contains(PlanField.Start) || planned.Contains(PlanField.End), Is.False,
                "a close plans no 開始日/終了日 write (R9)");
            Assert.That(Scheduled(session, datedId).Input.ActualFinish, Is.Null, "a provisional close is not 完了日");
            var published = await new PlanPublisher(service, context).PublishAsync(session, Today);
            Assert.That(published.Succeeded, Is.True, published.Error);
            var after = session.Document.State.Rows.Single(r => r.Identity == datedId);
            Assert.That((after.Start, after.End), Is.EqualTo(beforeDates), "a close changes no plan date locally (R9)");
            Assert.That(await DateValues(service, context, datedId), Is.EqualTo(beforeRemote), "a close writes no plan date to GitHub (R9)");
            var recorded = Scheduled(session, datedId).Input.ActualFinish;
            Assert.That(recorded, Is.Not.Null, "the publish read-back records the close as 完了日 (R7)");
            Assert.That(recorded!.Day, Is.EqualTo(Today));
            evidence["R9"] = new JsonObject { ["start"] = before.Start?.ToString("yyyy-MM-dd"), ["end"] = before.End?.ToString("yyyy-MM-dd"),
                ["finish"] = recorded.Day.ToString("yyyy-MM-dd") };
            await session.Execute(new ReopenPlanRows([datedId]), Today);
            Assert.That((await new PlanPublisher(service, context).PublishAsync(session, Today)).Succeeded, Is.True);
            Assert.That(await DateValues(service, context, datedId), Is.EqualTo(beforeRemote));
        }
        finally
        {
            foreach (var (identity, option) in open.Select(o => (o.Id, o.Status)))
                try { await SetStatus(service, context, status, identity, status.Option(option)); }
                catch (Exception error) { TestContext.Out.WriteLine("could not restore " + identity + ": " + error.Message); }
            foreach (var identity in ids)
                try { await Reopen(service, context, identity); } catch (Exception error) { TestContext.Out.WriteLine("could not reopen " + identity + ": " + error.Message); }
            Write("live-actual-dates.json", evidence);
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }
    }

    // R13: the refresh cost of this change. A complete read without history is the pre-change cost; a refresh
    // reads the history of changed rows only and pays the whole history read only on the first read/worst case.
    [Test]
    public async Task Project3RefreshReadsOnlyChangedHistory()
    {
        Live();
        var (service, context, project) = await Connect();
        var full = Stopwatch.StartNew();
        ProjectReadResult read = null!;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            read = await new ProjectReader(service).ReadAsync(context, project);
            if (read.Outcome == ProjectReadOutcome.Complete) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        full.Stop();
        Assert.That(read.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", read.Problems));
        var ids = read.Project!.Items.Where(i => i.Kind == ProjectItemKind.Issue && !i.IsArchived && i.ContentId is not null)
            .Select(i => i.ContentId!.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        Assert.That(ids.Length, Is.GreaterThan(500), "Project 3 holds the version plan");
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None);
        var all = Stopwatch.StartNew();
        var every = await PlanSnapshot.ReadHistoriesAsync(service, lease, context, project, ids, CancellationToken.None);
        all.Stop();
        Assert.That(every.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", every.Problems));
        var changed = ids.Take(3).ToArray();
        var incremental = Stopwatch.StartNew();
        var some = await PlanSnapshot.ReadHistoriesAsync(service, lease, context, project, changed, CancellationToken.None);
        incremental.Stop();
        Assert.That(some.Outcome, Is.EqualTo(ProjectReadOutcome.Complete));
        var evidence = new JsonObject
        {
            ["issues"] = ids.Length,
            ["fullReadWithoutHistoryMs"] = full.ElapsedMilliseconds,
            ["refreshReadingAllHistoriesMs"] = all.ElapsedMilliseconds,
            ["refreshReadingThreeChangedHistoriesMs"] = incremental.ElapsedMilliseconds,
            ["historiesReadInIncrementalRefresh"] = changed.Length
        };
        Assert.That(incremental.ElapsedMilliseconds, Is.LessThan(Math.Max(500, all.ElapsedMilliseconds / 10)),
            "a refresh that saw a few changed rows pays a fraction of the whole history read");
        TestContext.Out.WriteLine("refresh timings: " + evidence.ToJsonString());
        Write("live-actual-dates-refresh.json", evidence);
    }

    private static string RootName = "live-actual-dates-" + Guid.NewGuid().ToString("N");
    private static string Root => Path.Combine(AppContext.BaseDirectory, RootName);
    private static void Live()
    {
        if (Environment.GetEnvironmentVariable("GHPB_LIVE_PROJECT3") != "1")
            Assert.Ignore("set GHPB_LIVE_PROJECT3=1 to run the live Project 3 validation");
    }
    private static void Write(string file, JsonNode evidence)
    {
        var path = Path.Combine(RepositoryRoot(), "TestResults", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { runAt = DateTimeOffset.Now, project = SandboxUrl, evidence },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        TestContext.Out.WriteLine("evidence: " + path);
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GhProjectsBoards.sln"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
    private static async Task<(GhConnectionService Service, ConnectionContext Context, ScopedId Project)> Connect()
    {
        var service = new GhConnectionService(Gh, "github.com", timeout: TimeSpan.FromSeconds(180));
        var report = await service.ConnectAsync();
        Assert.That(report.IsConnected, Is.True, report.Result.Failure.ToString());
        var context = report.Context!;
        return (service, context, new(new(context.Host, context.ViewerId), ProjectNodeId));
    }
    private sealed record Candidate(string Id, string? Status, bool Closed, string? End);
    private static async Task<Candidate[]> Candidates(GhConnectionService service, ConnectionContext context)
    {
        var data = await Query(service, context, """
            query LiveCandidates($id:ID!){node(id:$id){... on ProjectV2{items(first:100){nodes{
              type
              content{... on Issue{id state}}
              fieldValues(first:50){nodes{
                ... on ProjectV2ItemFieldSingleSelectValue{name field{... on ProjectV2SingleSelectField{name}}}
                ... on ProjectV2ItemFieldDateValue{date field{... on ProjectV2FieldCommon{name}}}
              }}
            }}}}}
            """, new { id = ProjectNodeId });
        var list = new List<Candidate>();
        foreach (var item in data.GetProperty("node").GetProperty("items").GetProperty("nodes").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "ISSUE") continue;
            if (item.GetProperty("content") is not { ValueKind: JsonValueKind.Object } content) continue;
            var id = content.GetProperty("id").GetString()!;
            string? stateName = null, end = null;
            foreach (var value in item.GetProperty("fieldValues").GetProperty("nodes").EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) continue;
                if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    value.TryGetProperty("field", out var field) && field.TryGetProperty("name", out var fieldName) && fieldName.GetString() is "Status" or "ステータス")
                    stateName = name.GetString();
                if (value.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String &&
                    value.TryGetProperty("field", out var dateField) && dateField.TryGetProperty("name", out var dateName) && dateName.GetString() is "Target date" or "終了日")
                    end = date.GetString();
            }
            list.Add(new(id, stateName, content.GetProperty("state").GetString() == "CLOSED", end));
        }
        return list.ToArray();
    }
    // The product's scoped read and history adoption: a mini plan made of only the Issues this run touches.
    private static async Task<PlanSession> OpenScoped(GhConnectionService service, ConnectionContext context, ScopedId project,
        PlanStore store, string[] ids, JsonObject evidence)
    {
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None);
        var read = await new ProjectReader(service, lease).ReadItemsAsync(context, project, ids);
        Assert.That(read.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", read.Problems));
        var history = await PlanSnapshot.ReadHistoriesAsync(service, lease, context, project, ids, CancellationToken.None);
        var seed = new PlanDocument(project, new([], []), new([], new ProjectPlanSettings()));
        // Map the standard columns first (as a first open does), then read the rows with that mapping so the
        // plan really carries 開始日/終了日 and Status for the records cases.
        var provisional = PlanSnapshot.FromItems(read, seed, ids, history: history);
        // The open flow matches the standard columns from the Project's field definitions (PlanWorkspace.Open).
        var fields = read.Project!.Fields.Where(f => (f.ValueOwner == FieldOwner.ProjectItem || f.IssueFieldId is not null) &&
            f.Availability == ValueAvailability.Present).Select(f => new PlanColumnDefinition(f.Id.NodeId, f.Name, f.DataType));
        var settings = new ProjectPlanSettings { StatusDate = Today, Columns = PlanColumnMatching.Match(fields) };
        var mapped = new PlanDocument(project, provisional.Baseline, new(provisional.Baseline.Rows, settings));
        var remote = PlanSnapshot.FromItems(read, mapped, ids, history: history);
        Assert.That(remote.IssueHistory.Count, Is.EqualTo(ids.Length), "the scoped read kept the history of the covered Issues");
        evidence["scopedHistories"] = remote.IssueHistory.Count;
        var document = new PlanDocument(project, remote.Baseline, new(remote.Baseline.Rows, settings))
        {
            Sync = new() { NativeOrders = remote.SubIssueOrders, IssueLinks = remote.IssueLinks, ProgressUpdatedAt = remote.ProgressUpdatedAt,
                IssueHistory = remote.IssueHistory, LastRead = new(PlanSession.ReadTime(Today), []) }
        };
        return await PlanSession.CreateAsync(store, document, Today);
    }
    private static async Task SyncScoped(PlanSession session, GhConnectionService service, ConnectionContext context,
        ScopedId project, string[] ids)
    {
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None);
        var read = await new ProjectReader(service, lease).ReadItemsAsync(context, project, ids);
        Assert.That(read.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", read.Problems));
        var history = await PlanSnapshot.ReadHistoriesAsync(service, lease, context, project, ids, CancellationToken.None);
        var remote = PlanSnapshot.FromItems(read, session.Document, ids, history: history);
        var save = await session.AcceptRefresh(remote, Today);
        Assert.That(save.Succeeded, Is.True, save.Error);
    }
    private static ScheduledTask Scheduled(PlanSession session, string identity)
        => PlanOperations.Schedule(session.Document, Today).Single(t => t.Input.Identity == identity);
    private sealed record StatusIds(string Field, IReadOnlyDictionary<string, string> Options)
    {
        public string? Option(string? name) => name is not null && Options.TryGetValue(name, out var id) ? id : null;
    }
    private static async Task<StatusIds> StatusField(GhConnectionService service, ConnectionContext context)
    {
        var data = await Query(service, context, "query LiveFields($id:ID!){node(id:$id){... on ProjectV2{fields(first:50){nodes{__typename ... on ProjectV2SingleSelectField{id name options{id name}}}}}}}", new { id = ProjectNodeId });
        foreach (var field in data.GetProperty("node").GetProperty("fields").GetProperty("nodes").EnumerateArray())
        {
            if (field.GetProperty("__typename").GetString() != "ProjectV2SingleSelectField") continue;
            if (field.GetProperty("name").GetString() is not ("Status" or "ステータス")) continue;
            var options = field.GetProperty("options").EnumerateArray()
                .ToDictionary(o => o.GetProperty("name").GetString()!, o => o.GetProperty("id").GetString()!, StringComparer.Ordinal);
            return new(field.GetProperty("id").GetString()!, options);
        }
        throw new AssertionException("Project 3 has no readable Status field");
    }
    private static async Task SetStatus(GhConnectionService service, ConnectionContext context, StatusIds status, string issueId, string? optionId)
    {
        if (optionId is null) return;
        var item = await ItemId(service, context, issueId);
        await Mutate(service, context, "mutation LiveSetStatus($input:UpdateProjectV2ItemFieldValueInput!){updateProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
            new { input = new { projectId = ProjectNodeId, itemId = item, fieldId = status.Field, value = new { singleSelectOptionId = optionId } } });
    }
    private static async Task<string> ItemId(GhConnectionService service, ConnectionContext context, string issueId)
    {
        var data = await Query(service, context, "query LiveItem($id:ID!){node(id:$id){... on Issue{projectItems(first:20){nodes{id project{id}}}}}}", new { id = issueId });
        foreach (var item in data.GetProperty("node").GetProperty("projectItems").GetProperty("nodes").EnumerateArray())
            if (item.GetProperty("project").GetProperty("id").GetString() == ProjectNodeId) return item.GetProperty("id").GetString()!;
        throw new AssertionException("no Project 3 item for " + issueId);
    }
    private static Task Close(GhConnectionService service, ConnectionContext context, string issueId, string reason)
        => Mutate(service, context, "mutation LiveClose($input:CloseIssueInput!){closeIssue(input:$input){issue{id}}}",
            new { input = new { issueId, stateReason = reason } });
    private static Task Reopen(GhConnectionService service, ConnectionContext context, string issueId)
        => Mutate(service, context, "mutation LiveReopen($input:ReopenIssueInput!){reopenIssue(input:$input){issue{id}}}", new { input = new { issueId } });
    private static async Task<(string? Start, string? End)> DateValues(GhConnectionService service, ConnectionContext context, string issueId)
    {
        var item = await ItemId(service, context, issueId);
        var data = await Query(service, context, "query LiveDates($id:ID!){node(id:$id){... on ProjectV2Item{fieldValues(first:50){nodes{... on ProjectV2ItemFieldDateValue{date field{... on ProjectV2FieldCommon{name}}}}}}}}", new { id = item });
        string? start = null, end = null;
        foreach (var value in data.GetProperty("node").GetProperty("fieldValues").GetProperty("nodes").EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String) continue;
            var name = value.GetProperty("field").GetProperty("name").GetString();
            if (name is "Start date" or "開始日") start = date.GetString();
            if (name is "Target date" or "終了日") end = date.GetString();
        }
        return (start, end);
    }
    private static async Task Mutate(GhConnectionService service, ConnectionContext context, string query, object variables)
    {
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None, mutation: true);
        var result = await lease.SendAsync(ApiRequest.GraphQl(query, variables), CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True, result.Failure.ToString());
    }
    private static async Task<JsonElement> Query(GhConnectionService service, ConnectionContext context, string query, object variables)
    {
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None);
        var result = await lease.SendAsync(ApiRequest.GraphQl(query, variables), CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True, result.Failure.ToString());
        return result.Data!.Value.GetProperty("data").Clone();
    }
}
