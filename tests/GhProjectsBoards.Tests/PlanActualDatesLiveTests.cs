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
// against GitHub, records the read timings R13 asks for, and restores the items it touched. Run it only with
// GHPB_LIVE_PROJECT3=1 (it is in the LiveGitHub category):
//   $env:GHPB_LIVE_PROJECT3='1'; dotnet <runner> <tests dll> 'PlanActualDatesLiveTests' (without the LiveGitHub exclusion)
[TestFixture, Category("LiveGitHub")]
[NonParallelizable]
internal sealed class PlanActualDatesLiveTests
{
    private const string ProjectNodeId = "PVT_kwHOBGPKL84BjFYc";
    private const string Gh = @"C:\Program Files\GitHub CLI\gh.exe";
    private const string SandboxUrl = "https://github.com/users/fukuda-yuki/projects/3";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "live-actual-dates-" + Guid.NewGuid().ToString("N"));

    private static void RequireLive()
    {
        if (Environment.GetEnvironmentVariable("GHPB_LIVE_PROJECT3") != "1")
            Assert.Ignore("set GHPB_LIVE_PROJECT3=1 to run the live Project 3 validation");
    }

    [Test]
    public async Task Project3RecordsStatusesClosesAndPlanDatesEndToEnd()
    {
        RequireLive();
        var project = new ScopedId(new("github.com", 42), ProjectNodeId);
        var service = new GhConnectionService(Gh, "github.com", timeout: TimeSpan.FromSeconds(180));
        var report = await service.ConnectAsync();
        Assert.That(report.IsConnected, Is.True, report.Result.Failure.ToString());
        var context = report.Context!;
        project = new(new(context.Host, context.ViewerId), ProjectNodeId);
        Directory.CreateDirectory(Root);
        var store = new PlanStore(Root);
        Directory.CreateDirectory(store.Root);
        var timings = new JsonObject();
        var evidence = new JsonObject();
        // 1. The read #308 must not regress: a full Project read without the history read (the before-#308 cost).
        var baselineRead = Stopwatch.StartNew();
        var direct = await new ProjectReader(service).ReadAsync(context, project);
        baselineRead.Stop();
        Assert.That(direct.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), string.Join(", ", direct.Problems));
        timings["fullReadWithoutHistoryMs"] = baselineRead.ElapsedMilliseconds;
        timings["items"] = direct.Project!.Items.Count(i => i.Kind == ProjectItemKind.Issue && !i.IsArchived);
        // 2. First open reads every history (R13).
        var open = Stopwatch.StartNew();
        var workspace = new PlanWorkspace(store, () => Today);
        await workspace.Connect(service);
        var choice = new ProjectChoice(new(new(context.Host, context.ViewerId), ProjectNodeId),
            new(new(context.Host, context.ViewerId), "O-sandbox"), "fukuda-yuki", "User", 3, SandboxUrl, "第2027.04版");
        await workspace.Open(choice);
        open.Stop();
        var session = workspace.Session!;
        timings["firstOpenWithAllHistoriesMs"] = open.ElapsedMilliseconds;
        Assert.That(session.Document.Sync.IssueHistory, Is.Not.Empty, "a first open reads every Issue history");
        timings["histories"] = session.Document.Sync.IssueHistory.Count;
        var notStarted = PlanActualDates.NotStarted(session.Document.State.Settings).ToHashSet(StringComparer.Ordinal);
        // 3. Pick open items in a not-started Status for the Status/close cases, and a row with plan dates for R9.
        var openItems = session.Document.State.Rows.Where(r => !r.Closed && !r.Cancelled &&
            !string.IsNullOrEmpty(r.Status) && notStarted.Contains(r.Status)).Take(3).ToArray();
        Assert.That(openItems.Length, Is.EqualTo(3), "Project 3 has open not-started Issues to exercise");
        var a = openItems[0].Identity;
        var b = openItems[1].Identity;
        var c = openItems[2].Identity;
        var dated = session.Document.State.Rows
            .Where(r => !r.Closed && !r.Cancelled && r.End is not null && r.Identity != a && r.Identity != b && r.Identity != c)
            .Select(r => r.Identity).FirstOrDefault();
        Assert.That(dated, Is.Not.Null, "Project 3 has an open Issue with plan dates for the R9 case");
        var status = await FieldIds(service, context, project);
        File.WriteAllText(Path.Combine(Root, "touched.json"), JsonSerializer.Serialize(new { a, b, c, dated, status = openItems.Select(r => r.Status) }));
        evidence["touched"] = new JsonArray(JsonValue.Create(a), JsonValue.Create(b), JsonValue.Create(c), JsonValue.Create(dated));
        try
        {
            // R4: Ready -> In progress gives 着手日; In progress -> In review keeps it.
            await SetStatus(service, context, project, a, status.Option("In progress"));
            await workspace.Refresh();
            var start = Scheduled(session, a).Input.ActualStart;
            Assert.That(start, Is.Not.Null, "a Status write becomes 着手日 on the next read");
            Assert.That((start!.Day, start.To, start.Automated), Is.EqualTo((Today, "In progress", false)));

            await SetStatus(service, context, project, a, status.Option("Ready"));
            await workspace.Refresh();
            await SetStatus(service, context, project, a, status.Option("In progress"));
            await workspace.Refresh();
            Assert.That((Scheduled(session, a).Input.ActualStart!.From, Scheduled(session, a).Input.ActualStart!.To),
                Is.EqualTo(("Ready", "In progress")), "Ready -> In progress again is the latest start transition");
            await SetStatus(service, context, project, a, status.Option("In review"));
            await workspace.Refresh();
            Assert.That(Scheduled(session, a).Input.ActualStart!.To, Is.EqualTo("In progress"),
                "In progress -> In review keeps the 着手日");
            evidence["R4"] = new JsonObject { ["startDay"] = start.Day.ToString("yyyy-MM-dd"), ["startTo"] = start.To, ["actor"] = start.Actor };

            // R5/R7: close as completed, reopen clears; a close through the app needs the read-back.
            await Close(service, context, b, "COMPLETED");
            await workspace.Refresh();
            Assert.That(Scheduled(session, b).Input.ActualFinish!.Day, Is.EqualTo(Today), "a completed close becomes 完了日");
            await Reopen(service, context, b);
            await workspace.Refresh();
            Assert.That(Scheduled(session, b).Input.ActualFinish, Is.Null, "a reopen clears 完了日");
            evidence["R5"] = new JsonObject { ["finishAfterClose"] = Today.ToString("yyyy-MM-dd"), ["clearedByReopen"] = true };

            // R5: a NOT_PLANNED close (中止) has no 完了日.
            await Close(service, context, c, "NOT_PLANNED");
            await workspace.Refresh();
            var cancelled = session.Document.State.Rows.Single(r => r.Identity == c);
            Assert.That((cancelled.Closed, cancelled.Cancelled, Scheduled(session, c).Input.ActualFinish), Is.EqualTo((true, true, (PlanActualFinish?)null)));
            await Reopen(service, context, c);
            await workspace.Refresh();
            evidence["R5NotPlanned"] = new JsonObject { ["cancelled"] = true, ["finish"] = null };

            // R9: closing through the app publishes no plan date, and the read-back records 完了日.
            var before = session.Document.State.Rows.Single(r => r.Identity == dated);
            var beforeDates = (before.Start, before.End);
            var beforeRemote = await DateValues(service, context, project, dated);
            await session.Execute(new ClosePlanRows([dated]), Today);
            Assert.That(Scheduled(session, dated).Input.ActualFinish, Is.Null, "a provisional close is not 完了日 (R7)");
            var published = await new PlanPublisher(service, context).PublishAsync(session, Today);
            Assert.That(published.Succeeded, Is.True, published.Error);
            await workspace.Refresh();
            var after = session.Document.State.Rows.Single(r => r.Identity == dated);
            Assert.That((after.Start, after.End), Is.EqualTo(beforeDates), "a close changes no plan date locally (R9)");
            Assert.That(await DateValues(service, context, project, dated), Is.EqualTo(beforeRemote), "a close writes no plan date to GitHub (R9)");
            Assert.That(Scheduled(session, dated).Input.ActualFinish!.Day, Is.EqualTo(Today), "the read-back records the close as 完了日 (R7)");
            evidence["R9"] = new JsonObject {
                ["start"] = before.Start?.ToString("yyyy-MM-dd"), ["end"] = before.End?.ToString("yyyy-MM-dd"),
                ["finish"] = Scheduled(session, dated).Input.ActualFinish!.Day.ToString("yyyy-MM-dd") };
            var reopened = new PlanPublisher(service, context);
            await session.Execute(new ReopenPlanRows([dated]), Today);
            Assert.That((await reopened.PublishAsync(session, Today)).Succeeded, Is.True);
            await workspace.Refresh();
            Assert.That(await DateValues(service, context, project, dated), Is.EqualTo(beforeRemote));

            // R13: an unchanged refresh reads no history; an all-history refresh is the bounded worst case.
            var unchanged = Stopwatch.StartNew();
            await workspace.Refresh();
            unchanged.Stop();
            timings["incrementalRefreshMs"] = unchanged.ElapsedMilliseconds;
            await session.SaveSync(session.Document.Sync with { IssueHistory = ImmutableDictionary<string, PlanIssueHistory>.Empty });
            var all = Stopwatch.StartNew();
            await workspace.Refresh();
            all.Stop();
            timings["refreshReadingAllHistoriesMs"] = all.ElapsedMilliseconds;
            Assert.That(session.Document.Sync.IssueHistory.Count, Is.GreaterThan(0));
            Assert.That(timings["incrementalRefreshMs"]!.GetValue<long>(), Is.LessThan(timings["refreshReadingAllHistoriesMs"]!.GetValue<long>()),
                "the incremental refresh is cheaper than reading every history");
            evidence["R13"] = timings.DeepClone();
        }
        finally
        {
            // Restore every item the run touched, even when a case failed.
            foreach (var (identity, option) in new[] { (a, openItems[0].Status), (b, openItems[1].Status), (c, openItems[2].Status) })
                try { await SetStatus(service, context, project, identity, status.Option(option)); }
                catch (Exception error) { TestContext.Out.WriteLine("could not restore " + identity + ": " + error.Message); }
            foreach (var identity in new[] { a, b, c, dated })
                try { await Reopen(service, context, identity); } catch (Exception error) { TestContext.Out.WriteLine("could not reopen " + identity + ": " + error.Message); }
            var path = Path.Combine(FindRepositoryRoot(), "TestResults", "live-actual-dates.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { runAt = DateTimeOffset.Now, project = SandboxUrl,
                timings, evidence }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            TestContext.Out.WriteLine("evidence: " + path);
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }
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
    private async Task<StatusIds> FieldIds(GhConnectionService service, ConnectionContext context, ScopedId project)
    {
        var data = await Query(service, context, "query LiveFields($id:ID!){node(id:$id){... on ProjectV2{fields(first:50){nodes{__typename ... on ProjectV2SingleSelectField{id name options{id name}}}}}}}", new { id = project.NodeId });
        foreach (var field in data.GetProperty("node").GetProperty("fields").GetProperty("nodes").EnumerateArray())
        {
            if (field.GetProperty("__typename").GetString() != "ProjectV2SingleSelectField") continue;
            var name = field.GetProperty("name").GetString();
            if (name is not ("Status" or "ステータス")) continue;
            var options = field.GetProperty("options").EnumerateArray()
                .ToDictionary(o => o.GetProperty("name").GetString()!, o => o.GetProperty("id").GetString()!, StringComparer.Ordinal);
            return new(field.GetProperty("id").GetString()!, options);
        }
        throw new AssertionException("Project 3 has no readable Status field");
    }
    private async Task SetStatus(GhConnectionService service, ConnectionContext context, ScopedId project, string issueId, string? optionId)
    {
        if (optionId is null) return;
        var item = await ItemId(service, context, project, issueId);
        await Mutate(service, context, "mutation LiveSetStatus($input:UpdateProjectV2ItemFieldValueInput!){updateProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
            new { input = new { projectId = ProjectNodeId, itemId = item, fieldId = (await FieldIds(service, context, project)).Field, value = new { singleSelectOptionId = optionId } } });
    }
    private async Task<string> ItemId(GhConnectionService service, ConnectionContext context, ScopedId project, string issueId)
    {
        var data = await Query(service, context, "query LiveItem($id:ID!){node(id:$id){... on Issue{projectItems(first:20){nodes{id project{id}}}}}}", new { id = issueId });
        foreach (var item in data.GetProperty("node").GetProperty("projectItems").GetProperty("nodes").EnumerateArray())
            if (item.GetProperty("project").GetProperty("id").GetString() == project.NodeId) return item.GetProperty("id").GetString()!;
        throw new AssertionException("no Project 3 item for " + issueId);
    }
    private Task Close(GhConnectionService service, ConnectionContext context, string issueId, string reason)
        => Mutate(service, context, "mutation LiveClose($input:CloseIssueInput!){closeIssue(input:$input){issue{id}}}",
            new { input = new { issueId, stateReason = reason } });
    private Task Reopen(GhConnectionService service, ConnectionContext context, string issueId)
        => Mutate(service, context, "mutation LiveReopen($input:ReopenIssueInput!){reopenIssue(input:$input){issue{id}}}", new { input = new { issueId } });
    private async Task<(string? Start, string? End)> DateValues(GhConnectionService service, ConnectionContext context, ScopedId project, string issueId)
    {
        var item = await ItemId(service, context, project, issueId);
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
    private async Task Mutate(GhConnectionService service, ConnectionContext context, string query, object variables)
    {
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None, mutation: true);
        var result = await lease.SendAsync(ApiRequest.GraphQl(query, variables), CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True, result.Failure.ToString());
    }
    private async Task<JsonElement> Query(GhConnectionService service, ConnectionContext context, string query, object variables)
    {
        using var lease = await service.BeginOperationAsync(context, CancellationToken.None);
        var result = await lease.SendAsync(ApiRequest.GraphQl(query, variables), CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True, result.Failure.ToString());
        return result.Data!.Value.GetProperty("data").Clone();
    }
}
