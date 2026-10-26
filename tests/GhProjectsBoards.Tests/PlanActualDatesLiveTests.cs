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
// It moves a few items' Status and closed state through gh, verifies the product's read/refresh/publish behavior
// against GitHub, records the read timings R13 asks for, and restores the items it touched. The intermediate
// adoptions use the product's scoped read and history reader (the path a publish read-back uses), so one run
// stays within GitHub's read budget. Run only with GHPB_LIVE_PROJECT3=1, without the LiveGitHub category filter:
//   $env:GHPB_LIVE_PROJECT3='1'; dotnet <runner> <tests dll> 'PlanActualDatesLiveTests'
[TestFixture, Category("LiveGitHub")]
[NonParallelizable]
internal sealed class PlanActualDatesLiveTests
{
    private const string ProjectNodeId = "PVT_kwHOBGPKL84BjFYc";
    private const string Gh = @"C:\Program Files\GitHub CLI\gh.exe";
    private const string SandboxUrl = "https://github.com/users/fukuda-yuki/projects/3";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "live-actual-dates-" + Guid.NewGuid().ToString("N"));

    [Test]
    public async Task Project3RecordsStatusesClosesAndPlanDatesEndToEnd()
    {
        if (Environment.GetEnvironmentVariable("GHPB_LIVE_PROJECT3") != "1")
            Assert.Ignore("set GHPB_LIVE_PROJECT3=1 to run the live Project 3 validation");
        var service = new GhConnectionService(Gh, "github.com", timeout: TimeSpan.FromSeconds(180));
        var report = await service.ConnectAsync();
        Assert.That(report.IsConnected, Is.True, report.Result.Failure.ToString());
        var context = report.Context!;
        var project = new ScopedId(new(context.Host, context.ViewerId), ProjectNodeId);
        Directory.CreateDirectory(Root);
        var store = new PlanStore(Root);
        Directory.CreateDirectory(store.Root);
        var timings = new JsonObject();
        var evidence = new JsonObject();
        // R13: a full Project read without the history read is the cost this change must not add to.
        var baselineRead = Stopwatch.StartNew();
        ProjectReadResult direct = null!;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            direct = await new ProjectReader(service).ReadAsync(context, project);
            if (direct.Outcome == ProjectReadOutcome.Complete) break;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        baselineRead.Stop();
        Assert.That(direct.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", direct.Problems));
        timings["fullReadWithoutHistoryMs"] = baselineRead.ElapsedMilliseconds;
        timings["items"] = direct.Project!.Items.Count(i => i.Kind == ProjectItemKind.Issue && !i.IsArchived);
        // R13: a first open reads every Issue's history and keeps it.
        var open = Stopwatch.StartNew();
        var workspace = new PlanWorkspace(store, () => Today);
        await workspace.Connect(service);
        var choice = new ProjectChoice(new(new(context.Host, context.ViewerId), ProjectNodeId),
            new(new(context.Host, context.ViewerId), "O-sandbox"), "fukuda-yuki", "User", 3, SandboxUrl, "第2027.04版");
        for (var attempt = 1; ; attempt++)
        {
            try { await workspace.Open(choice); break; }
            catch (InvalidOperationException) when (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(5)); }
        }
        open.Stop();
        var session = workspace.Session!;
        timings["firstOpenWithAllHistoriesMs"] = open.ElapsedMilliseconds;
        timings["histories"] = session.Document.Sync.IssueHistory.Count;
        Assert.That(session.Document.Sync.IssueHistory, Is.Not.Empty, "a first open reads every Issue history");
        var notStarted = PlanActualDates.NotStarted(session.Document.State.Settings).ToHashSet(StringComparer.Ordinal);
        var openItems = session.Document.State.Rows.Where(r => !r.Closed && !r.Cancelled &&
            !string.IsNullOrEmpty(r.Status) && notStarted.Contains(r.Status)).Take(3).ToArray();
        Assert.That(openItems.Length, Is.EqualTo(3), "Project 3 has open not-started Issues to exercise");
        var a = openItems[0].Identity;
        var b = openItems[1].Identity;
        var c = openItems[2].Identity;
        var dated = session.Document.State.Rows
            .Where(r => !r.Closed && !r.Cancelled && r.End is not null && r.Identity != a && r.Identity != b && r.Identity != c)
            .Select(r => r.Identity).FirstOrDefault();
        Assert.That(dated, Is.Not.Null, "Project 3 has an open Issue with a plan end for the R9 case");
        var status = await StatusField(service, context);
        evidence["touched"] = new JsonArray(new[] { a, b, c, dated! }.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
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

            // R9/R7: closing through the app changes no plan date locally or on GitHub, and the read-back
            // records the close as 完了日.
            var before = session.Document.State.Rows.Single(r => r.Identity == dated);
            var beforeDates = (before.Start, before.End);
            var beforeRemote = await DateValues(service, context, dated!);
            await session.Execute(new ClosePlanRows([dated!]), Today);
            Assert.That(Scheduled(session, dated!).Input.ActualFinish, Is.Null, "a provisional close is not 完了日");
            var published = await new PlanPublisher(service, context).PublishAsync(session, Today);
            Assert.That(published.Succeeded, Is.True, published.Error);
            var after = session.Document.State.Rows.Single(r => r.Identity == dated);
            Assert.That((after.Start, after.End), Is.EqualTo(beforeDates), "a close changes no plan date locally (R9)");
            Assert.That(await DateValues(service, context, dated!), Is.EqualTo(beforeRemote), "a close writes no plan date to GitHub (R9)");
            var recorded = Scheduled(session, dated!).Input.ActualFinish;
            Assert.That(recorded, Is.Not.Null, "the publish read-back records the close as 完了日 (R7)");
            Assert.That(recorded!.Day, Is.EqualTo(Today));
            evidence["R9"] = new JsonObject { ["start"] = before.Start?.ToString("yyyy-MM-dd"), ["end"] = before.End?.ToString("yyyy-MM-dd"),
                ["finish"] = recorded.Day.ToString("yyyy-MM-dd") };
            await session.Execute(new ReopenPlanRows([dated!]), Today);
            Assert.That((await new PlanPublisher(service, context).PublishAsync(session, Today)).Succeeded, Is.True);
            Assert.That(await DateValues(service, context, dated!), Is.EqualTo(beforeRemote));

            // R13: an unchanged refresh reads only changed rows' history; reading all histories is the worst case.
            var incremental = Stopwatch.StartNew();
            await workspace.Refresh();
            incremental.Stop();
            timings["incrementalRefreshMs"] = incremental.ElapsedMilliseconds;
            await session.SaveSync(session.Document.Sync with { IssueHistory = ImmutableDictionary<string, PlanIssueHistory>.Empty });
            var all = Stopwatch.StartNew();
            await workspace.Refresh();
            all.Stop();
            timings["refreshReadingAllHistoriesMs"] = all.ElapsedMilliseconds;
            Assert.That(session.Document.Sync.IssueHistory, Is.Not.Empty);
            Assert.That(timings["incrementalRefreshMs"]!.GetValue<long>(), Is.LessThan(timings["refreshReadingAllHistoriesMs"]!.GetValue<long>()),
                "the incremental refresh is cheaper than reading every history");
            evidence["R13"] = timings.DeepClone();
        }
        finally
        {
            // Restore every item the run touched, even when a case failed.
            foreach (var (identity, option) in openItems.Select(r => (r.Identity, r.Status)))
                try { await SetStatus(service, context, status, identity, status.Option(option!)); }
                catch (Exception error) { TestContext.Out.WriteLine("could not restore " + identity + ": " + error.Message); }
            foreach (var identity in new[] { a, b, c, dated! })
                try { await Reopen(service, context, identity); } catch (Exception error) { TestContext.Out.WriteLine("could not reopen " + identity + ": " + error.Message); }
            var path = Path.Combine(FindRepositoryRoot(), "TestResults", "live-actual-dates.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { runAt = DateTimeOffset.Now, project = SandboxUrl,
                timings, evidence }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            TestContext.Out.WriteLine("evidence: " + path);
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }
    }

    // The product's scoped read and adoption path (the one a publish read-back uses): read the items and their
    // Issue history, then adopt them as a refresh would.
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
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GhProjectsBoards.sln"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
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
