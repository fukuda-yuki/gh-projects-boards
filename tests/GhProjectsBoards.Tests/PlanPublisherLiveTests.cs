using System.Globalization;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("LiveGitHub"), NonParallelizable]
internal sealed class PlanPublisherLiveTests
{
    [Test]
    public async Task ThreeMeasuredCoreWorkloadsPreserveTheSandboxBaseline()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_PLAN_PUBLISH_PROOF") != "1") Assert.Ignore("Explicit #79 live proof opt-in required.");
        var root = Environment.GetEnvironmentVariable("GHPB_PLAN_PUBLISH_ARTIFACTS") ?? throw new InvalidOperationException("Fresh artifacts directory required.");
        Assert.That(await PlanPublisherLive.Run("Run", root), Is.Zero);
    }
}

internal static class PlanPublisherLive
{
    private const string RepositoryName = "fukuda-yuki/codex-sandbox", Owner = "fukuda-yuki", ProjectId = "PVT_kwHOBGPKL84BjFYc", RepositoryId = "R_kgDOUVKgAw";
    private sealed record Manifest(string Marker, string Baseline, string Source, List<Sample> Samples, bool CleanupComplete = false);
    private sealed record Sample(string Workload, int Run, double TotalSeconds, double WriteSeconds, int Processes, int Mutations, bool Passed,
        int Items, int ItemPages, double? ItemPageSeconds, double? SecondsPerItem, double? SecondsPerItemPage, double? Extrapolated1000Seconds, double CreationWaitSeconds, int NumberChanges, int DateChanges);
    internal sealed record Budget(int SeedIssues, int Runs, int IssuesPerRun, int CreatedIssues, int CreateRequests, int PlannedMutationRequests, string ExpectedDuration);
    internal static Budget Estimate(int seeds = 100, int runs = 3, int perRun = 50)
    {
        var created = checked(seeds + runs * perRun);
        if (seeds != 100 || runs < 1 || perRun < 1 || created > 300) throw new InvalidOperationException("Refusing a fixture above 100 seeds or 300 created Issues.");
        var requests = (seeds + 9) / 10 + runs * ((perRun + 9) / 10);
        return new(seeds, runs, perRun, created, requests, requests * 2 + runs * (10 + perRun) + created, "20–30 minutes including settling and about 8–9 minutes cleanup; network/backoff can extend this");
    }
    private sealed record RemoteIssue(string Id, string Title, string Body);
    public static async Task<int> Run(string mode, string root)
    {
        var canRecord = Path.IsPathFullyQualified(root) && (mode == "Run" && !Directory.Exists(root) ||
            mode == "Cleanup" && File.Exists(Path.Combine(root, "plan-publish-live.json")));
        try { return await RunCore(mode, root); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (canRecord)
            {
                try { Directory.CreateDirectory(root); await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), ex + Environment.NewLine); }
                catch (Exception artifactError) { Console.Error.WriteLine("Could not record failure.txt: " + artifactError.Message); }
            }
            return 1;
        }
    }
    private static async Task<int> RunCore(string mode, string root)
    {
        if (mode is not ("Run" or "Cleanup") || Environment.GetEnvironmentVariable("GHPB_RUN_PLAN_PUBLISH_PROOF") != "1" || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Explicit live opt-in, Run/Cleanup and an absolute artifacts path are required.");
        var executable = Environment.GetEnvironmentVariable("GHPB_LIVE_GH_PATH") ?? @"C:\Program Files\GitHub CLI\gh.exe";
        var manifestPath = Path.Combine(root, "plan-publish-live.json");
        if (mode == "Run" && Directory.Exists(root)) throw new InvalidOperationException("Use a fresh artifacts directory.");
        if (mode == "Cleanup" && !File.Exists(manifestPath)) throw new InvalidOperationException("A recorded run is required for cleanup.");
        Directory.CreateDirectory(root);
        var runner = new MeasuredRunner(root);
        var service = new GhConnectionService(executable, "github.com", runner);
        var connected = await service.ConnectAsync();
        if (!connected.IsConnected || connected.Context!.Host != "github.com" || connected.Context.Login != Owner)
            throw new InvalidOperationException("The allowlisted github.com account is required.");
        await File.WriteAllTextAsync(Path.Combine(root, "environment-" + mode.ToLowerInvariant() + ".json"), JsonSerializer.Serialize(new { os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, ghVersion = connected.Version, assemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(PlanSession).Assembly.Location))) }));
        var context = connected.Context!; var project = new ScopedId(ConnectionScope.From(context), ProjectId);
        var transport = new GhApiTransport(runner, executable);
        async Task<JsonElement> Send(ApiRequest request)
        {
            var response = await transport.SendAsync("github.com", request);
            if (!response.IsSuccess || response.Data is null)
            {
                var reason = response.FailureReason();
                await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), reason + Environment.NewLine);
                throw new InvalidOperationException(reason);
            }
            if (response.RetryAfter is { } pause && pause > TimeSpan.Zero) await Task.Delay(pause);
            return response.Data.Value;
        }
        // Exact identities and scope instructions are checked before any mutation.
        var scope = await Send(ApiRequest.Rest("GET", "repos/" + RepositoryName + "/issues/1"));
        await File.WriteAllTextAsync(Path.Combine(root, "sandbox-scope-" + mode.ToLowerInvariant() + ".json"), scope.GetRawText());
        var identities = await Send(ApiRequest.GraphQl("query{repository(owner:\"fukuda-yuki\",name:\"codex-sandbox\"){id nameWithOwner}user(login:\"fukuda-yuki\"){projectV2(number:3){id number url}}}"));
        var data = identities.GetProperty("data");
        if (data.GetProperty("repository").GetProperty("id").GetString() != RepositoryId ||
            data.GetProperty("repository").GetProperty("nameWithOwner").GetString() != RepositoryName ||
            data.GetProperty("user").GetProperty("projectV2").GetProperty("id").GetString() != ProjectId)
            throw new InvalidOperationException("Allowlisted repository/Project identity mismatch.");
        if (mode == "Run")
        {
            await CheckSchema(Send, root);
        }
        async Task<ProjectReadResult> ReadOnce()
        {
            using var lease = await service.BeginOperationAsync(context, default, mutation: false);
            return await new ProjectReader(service, lease).ReadAsync(context, project);
        }
        Task<ProjectReadModel> Read() => ReadSettled(ReadOnce, root);
        async Task<List<RemoteIssue>> Issues()
        {
            var result = new List<RemoteIssue>(); string? after = null; var cursors = new HashSet<string>();
            do
            {
                var response = await Send(ApiRequest.GraphQl("query($id:ID!,$after:String){node(id:$id){... on Repository{issues(first:100,after:$after){nodes{id title body}pageInfo{hasNextPage endCursor}}}}}", new { id = RepositoryId, after }));
                var connection = response.GetProperty("data").GetProperty("node").GetProperty("issues");
                result.AddRange(connection.GetProperty("nodes").EnumerateArray().Select(n => new RemoteIssue(n.GetProperty("id").GetString()!, n.GetProperty("title").GetString()!, n.GetProperty("body").GetString() ?? "")));
                var page = connection.GetProperty("pageInfo"); if (!page.GetProperty("hasNextPage").GetBoolean()) break;
                after = page.GetProperty("endCursor").GetString(); if (after is null || !cursors.Add(after)) throw new InvalidOperationException("Incomplete Issue traversal.");
            } while (true);
            return result;
        }
        var initial = await Read();
        Manifest manifest;
        if (mode == "Cleanup") manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath))!;
        else
        {
            if (initial.Items.Count > 100 || initial.Items.Any(i => i.Kind != ProjectItemKind.Issue || i.IsArchived))
                throw new InvalidOperationException("The measurement requires at most 100 unarchived Issue-only baseline items; no baseline resources are removed.");
            await File.WriteAllTextAsync(Path.Combine(root, "baseline-project.json"), CanonicalPayload(initial));
            manifest = new("ghpb-plan-publish-" + Guid.NewGuid().ToString("N"), Canonical(initial), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(PlanSession).Assembly.Location))), []);
            await SaveManifest(manifestPath, manifest);
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Marker, "^ghpb-plan-publish-[0-9a-f]{32}$")) throw new InvalidOperationException("Invalid ownership marker.");
        runner.Marker = manifest.Marker;
        async Task Save() => await SaveManifest(manifestPath, manifest);
        bool Owned(RemoteIssue issue) => issue.Title.StartsWith(manifest.Marker + "-", StringComparison.Ordinal) &&
            (issue.Body == manifest.Marker || issue.Body.StartsWith("<!-- ghpb-plan:", StringComparison.Ordinal));
        async Task Delete(IEnumerable<RemoteIssue> issues)
        {
            var previous = Stopwatch.StartNew();
            foreach (var issue in issues)
            {
                var pause = TimeSpan.FromSeconds(2) - previous.Elapsed;
                if (pause > TimeSpan.Zero) await Task.Delay(pause);
                previous.Restart();
                if (!Owned(issue)) throw new InvalidOperationException("Refusing non-owned cleanup.");
                runner.OwnedIssues.Add(issue.Id);
                await DeleteOwnedIssue(() => transport.SendAsync("github.com", ApiRequest.GraphQl("mutation($input:DeleteIssueInput!){deleteIssue(input:$input){clientMutationId}}", new { input = new { issueId = issue.Id } })), root);
            }
        }
        async Task Cleanup()
        {
            var owned = (await Issues()).Where(Owned).ToArray();
            Console.WriteLine($"Cleanup: {owned.Length} owned Issues, one delete per request; estimated {owned.Length * 2 / 60d:F1} minutes at two seconds/request, plus verification (network/backoff may extend this).");
            await Delete(owned);
            var leftovers = (await Issues()).Where(Owned).ToArray();
            var after = await Read();
            await File.WriteAllTextAsync(Path.Combine(root, "after-cleanup-project.json"), CanonicalPayload(after));
            if (leftovers.Length != 0 || Canonical(after) != manifest.Baseline) throw new InvalidOperationException("Cleanup or independent baseline verification failed.");
            manifest = manifest with { CleanupComplete = true }; await Save();
        }
        if (mode == "Cleanup") { await Cleanup(); return 0; }
        var budget = Estimate();
        Console.WriteLine($"Before mutation: {budget.CreatedIssues} created Issues in {budget.CreateRequests} create requests; up to {budget.PlannedMutationRequests} planned mutation requests including adds, fields, ordering and cleanup (retries/reconciliation excluded). Expected duration: {budget.ExpectedDuration}.");
        await File.WriteAllTextAsync(Path.Combine(root, "budget.json"), JsonSerializer.Serialize(budget));
        var succeeded = false;
        try
        {
            var settings = CreateSettings(initial.Fields, DateOnly.FromDateTime(DateTime.Today));
            var roles = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Actual };
            var baselineIds = initial.Issues.Keys.Select(id => id.NodeId).ToHashSet();
            ImmutableArray<PlanCreationStart> creationStarts = [];
            // Setup waits are outside workload samples and retained separately.
            for (var offset = 0; offset < budget.SeedIssues; offset += 10)
            {
                var count = Math.Min(10, budget.SeedIssues - offset);
                var next = PlanCreationPacing.NextStart(creationStarts, count, DateTimeOffset.UtcNow);
                var wait = Stopwatch.StartNew();
                while (next > DateTimeOffset.UtcNow) await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (next - DateTimeOffset.UtcNow).TotalMilliseconds)));
                wait.Stop();
                creationStarts = PlanCreationPacing.Reserve(creationStarts, count, DateTimeOffset.UtcNow);
                await File.AppendAllTextAsync(Path.Combine(root, "setup-waits.jsonl"), JsonSerializer.Serialize(new { issues = count, seconds = wait.Elapsed.TotalSeconds }) + "\n");
                var variables = Enumerable.Range(0, count).ToDictionary(i => "v" + i.ToString(CultureInfo.InvariantCulture), i => (object)new { repositoryId = RepositoryId, title = manifest.Marker + "-seed-" + (offset + i).ToString(CultureInfo.InvariantCulture), body = manifest.Marker });
                var declaration = string.Join(',', Enumerable.Range(0, count).Select(i => "$v" + i.ToString(CultureInfo.InvariantCulture) + ":CreateIssueInput!"));
                var selections = string.Join(' ', Enumerable.Range(0, count).Select(i => "w" + i.ToString(CultureInfo.InvariantCulture) + ":createIssue(input:$v" + i.ToString(CultureInfo.InvariantCulture) + "){issue{id}}"));
                await Send(ApiRequest.GraphQl("mutation(" + declaration + "){" + selections + "}", variables));
            }
            var seeded = (await Issues()).Where(Owned).ToArray();
            if (seeded.Length != budget.SeedIssues) throw new InvalidOperationException("Seed Issue count is incomplete.");
            await WaitForSeeds(ReadOnce, root, initial.Items, seeded.Select(i => i.Id).ToHashSet(), async missing =>
            {
                foreach (var id in missing)
                {
                    var response = await transport.SendAsync("github.com", ApiRequest.GraphQl("mutation($input:AddProjectV2ItemByIdInput!){addProjectV2ItemById(input:$input){item{id}}}", new { input = new { projectId = ProjectId, contentId = id } }));
                    await VerifySeedAdd(response, ReadOnce, id);
                }
            });
            for (var run = 1; run <= budget.Runs; run++)
            {
                var read = await Read();
                if (read.Items.Count != initial.Items.Count + budget.SeedIssues + (run - 1) * budget.IssuesPerRun) throw new InvalidOperationException("Fixture membership changed unexpectedly.");
                var remote = PlanSnapshot.From(new(ProjectReadOutcome.Complete, read, []), settings);
                var session = await PlanSession.CreateAsync(new(Path.Combine(root, "run-" + run)), new(project, remote.Baseline, new(PreserveBaselineDates(remote.Baseline.Rows, baselineIds), settings)), settings.StatusDate!.Value);
                await session.SaveSync(session.Document.Sync with { CreationStarts = creationStarts });
                var publisher = new PlanPublisher(service, context);
                await session.Execute(new InsertPlanRows(Enumerable.Range(0, budget.IssuesPerRun).Select(i => PlanRow.New(manifest.Marker + "-run-" + run.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture), RepositoryName)).ToImmutableArray()), settings.StatusDate.Value);
                async Task Measure(string workload, double totalLimit, double writeLimit, Func<Task<PlanPublishResult>> action)
                {
                    var start = runner.Records.Count; var watch = Stopwatch.StartNew(); var result = await action(); watch.Stop();
                    var records = runner.Records.Skip(start).ToArray(); var writes = records.Where(r => workload.StartsWith("create", StringComparison.Ordinal) ? r.Stage is "create" or "add" or "creation-guard" or "add-reconcile" : r.Mutation).ToArray();
                    var writeSeconds = writes.Length == 0 ? 0 : (writes[^1].Finished - writes[0].Started).TotalSeconds;
                    var creating = workload.StartsWith("create", StringComparison.Ordinal);
                    if (creating && publisher.CreationPhaseStarted is { } phaseStart && writes.Length > 0) writeSeconds = (writes[^1].Finished - phaseStart).TotalSeconds;
                    var items = session.Document.Baseline.Rows.Length;
                    var rates = RefreshRates(workload, records, items);
                    var pages = rates.ItemPages;
                    double? extrapolated = workload == "refresh-complete-project" ? watch.Elapsed.TotalSeconds * 1000 / items : null;
                    var passed = result.Succeeded && (extrapolated ?? watch.Elapsed.TotalSeconds) <= totalLimit && writeSeconds <= writeLimit;
                    manifest.Samples.Add(new(workload, run, watch.Elapsed.TotalSeconds, writeSeconds, records.Length, records.Count(r => r.Mutation), passed,
                        items, pages, rates.ItemPageSeconds, rates.SecondsPerItem, rates.SecondsPerItemPage, extrapolated, creating ? publisher.CreationWait.TotalSeconds : 0,
                        result.Review?.Changes.Count(c => c.Field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual) ?? 0,
                        result.Review?.Changes.Count(c => c.Field is PlanField.Start or PlanField.End) ?? 0)); await Save();
                    if (!result.Succeeded)
                    {
                        var sync = session.Document.Sync;
                        await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), JsonSerializer.Serialize(new
                        {
                            workload, run, result.Error, failures = sync.Failures, sync.Unverified,
                            conflicts = sync.Conflicts.Select(c => new { c.Identity, c.Field }), sync.Unavailable,
                            remaining = session.Changes(settings.StatusDate.Value).Fields
                        }) + Environment.NewLine);
                        throw new InvalidOperationException(result.Error);
                    }
                }
                await Measure("create-50", 200, 65, () => publisher.PublishAsync(session, settings.StatusDate.Value));
                await Measure("refresh-complete-project", 60, 0, () => publisher.RefreshAsync(session, settings.StatusDate.Value));
                if (session.Document.State.Rows.Length != initial.Items.Count + budget.SeedIssues + run * budget.IssuesPerRun) throw new InvalidOperationException("Refresh membership is incomplete.");
                var ownedIds = seeded.Select(i => i.Id).ToHashSet();
                var targets = session.Document.State.Rows.Where(r => ownedIds.Contains(r.Identity)).Take(100).ToArray();
                if (targets.Length != 100) throw new InvalidOperationException("At least 100 owned fixture rows are required.");
                await session.Execute(new EditPlanCells(PlanOperationKind.Paste, targets.SelectMany(r => roles.Select((role, i) => new PlanCellChange(r.Identity, role, (decimal)(run * 10 + i + 1)))).ToImmutableArray()), settings.StatusDate.Value);
                await Measure("publish-300-input-fields", 180, 45, () => publisher.PublishAsync(session, settings.StatusDate.Value));
                creationStarts = session.Document.Sync.CreationStarts;
            }
            succeeded = manifest.Samples.Count == 9 && manifest.Samples.All(s => s.Passed);
            if (!succeeded) await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), "Workload timing acceptance failed; inspect the retained samples in plan-publish-live.json." + Environment.NewLine);
        }
        catch (Exception ex) { await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), ex + Environment.NewLine); throw; }
        finally { await Cleanup(); }
        return succeeded ? 0 : 1;
    }
    internal static async Task VerifySeedAdd(ApiResult response, Func<Task<ProjectReadResult>> read, string id)
    {
        if (response.IsSuccess) return;
        // Auto-add can win the race; only complete identity readback establishes membership.
        var reconciled = await read();
        if (reconciled.Outcome != ProjectReadOutcome.Complete || reconciled.Project is null ||
            !reconciled.Project.Items.Any(i => i.ContentId?.NodeId == id && !i.IsArchived && i.Kind == ProjectItemKind.Issue))
            throw new InvalidOperationException("Fixture membership add failed: " + response.FailureReason());
    }
    internal static async Task<ProjectReadModel> WaitForSeeds(Func<Task<ProjectReadResult>> read, string root, IReadOnlyList<ProjectItemReadModel> baseline, IReadOnlySet<string> seeds,
        Func<string[], Task> addMissing, Func<TimeSpan, Task>? delay = null, Func<TimeSpan>? elapsed = null)
    {
        delay ??= pause => Task.Delay(pause);
        var watch = Stopwatch.StartNew(); elapsed ??= () => watch.Elapsed;
        var deadline = TimeSpan.FromSeconds(120); var reconciled = false;
        var baselineIds = baseline.Select(i => i.Id).ToHashSet();
        for (var attempt = 1; ; attempt++)
        {
            var result = await read(); var complete = result.Outcome == ProjectReadOutcome.Complete && result.Project is not null;
            var items = result.Project?.Items ?? [];
            var missing = seeds.Where(id => !items.Any(i => i.ContentId?.NodeId == id)).ToArray();
            var baselineIntact = baseline.All(b => items.Any(i => i.Id == b.Id && i.ContentId == b.ContentId && i.IsArchived == b.IsArchived));
            var unexpected = items.Any(i => !baselineIds.Contains(i.Id) && (i.Kind != ProjectItemKind.Issue || i.IsArchived || !seeds.Contains(i.ContentId?.NodeId ?? "")));
            var exact = complete && baselineIntact && !unexpected && missing.Length == 0 && items.Count == baseline.Count + seeds.Count;
            await File.AppendAllTextAsync(Path.Combine(root, "settling.jsonl"), JsonSerializer.Serialize(new
            { phase = "seed-membership", attempt, complete, items = items.Count, missing = missing.Length, exact, elapsedSeconds = elapsed().TotalSeconds, reconciled }) + "\n");
            if (exact) return result.Project!;
            if (complete && (!baselineIntact || unexpected)) throw new InvalidOperationException("Unexpected fixture membership; baseline or unrelated items changed.");
            if (elapsed() >= deadline)
            {
                if (reconciled || !complete) throw new InvalidOperationException("Fixture membership did not become complete before the deadline.");
                await addMissing(missing); reconciled = true; deadline = elapsed() + TimeSpan.FromSeconds(30);
                continue;
            }
            await delay(TimeSpan.FromSeconds(Math.Max(0, Math.Min(3, (deadline - elapsed()).TotalSeconds))));
        }
    }
    internal static async Task DeleteOwnedIssue(Func<Task<ApiResult>> send, string root, Func<TimeSpan, Task>? delay = null)
    {
        delay ??= pause => Task.Delay(pause);
        for (var attempt = 1; ; attempt++)
        {
            var result = await send();
            if (result.IsSuccess || result.HttpStatus is 404 or 410 || result.GraphQlErrors.Count > 0 && result.GraphQlErrors.All(e => e == "NOT_FOUND")) return;
            var transient = result.HttpStatus is >= 500 and <= 599 || result.Failure == FailureKind.GraphQl &&
                result.GraphQlErrors.Count > 0 && result.GraphQlErrors.All(e => e == "UNCLASSIFIED");
            var pause = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            if (result.RetryAfter is { } server && server > pause) pause = server;
            await File.AppendAllTextAsync(Path.Combine(root, "cleanup-retries.jsonl"), JsonSerializer.Serialize(new
            { attempt, result.HttpStatus, reason = result.FailureReason(), retry = transient && attempt < 4, delaySeconds = transient && attempt < 4 ? pause.TotalSeconds : 0 }) + "\n");
            if (!transient || attempt >= 4) throw new InvalidOperationException(result.FailureReason());
            await delay(pause);
        }
    }
    internal static async Task<ProjectReadModel> ReadSettled(Func<Task<ProjectReadResult>> read, string root, Func<TimeSpan, Task>? delay = null, Func<TimeSpan>? elapsed = null)
    {
        delay ??= pause => Task.Delay(pause);
        var watch = Stopwatch.StartNew(); elapsed ??= () => watch.Elapsed; string? previous = null; ProjectReadResult? last = null;
        var observation = Guid.NewGuid().ToString("N");
        for (var attempt = 1; ; attempt++)
        {
            last = await read();
            var complete = last.Outcome == ProjectReadOutcome.Complete && last.Project is not null;
            if (!complete) await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"),
                $"Independent read {observation}, attempt {attempt}: {PlanSnapshot.ReadDiagnostic(last)}" + Environment.NewLine);
            var membership = complete ? JsonSerializer.Serialize(last.Project!.Items.Select(i => new { i.Id, i.ContentId, i.IsArchived })) : null;
            var settled = complete && membership == previous;
            await File.AppendAllTextAsync(Path.Combine(root, "settling.jsonl"), JsonSerializer.Serialize(new
            {
                observation, attempt, outcome = last.Outcome.ToString(), diagnostic = PlanSnapshot.ReadDiagnostic(last),
                items = complete ? last.Project!.Items.Count : (int?)null, settled, elapsedSeconds = watch.Elapsed.TotalSeconds
            }) + Environment.NewLine);
            if (settled) return last.Project!;
            previous = membership;
            if (elapsed() >= TimeSpan.FromSeconds(120)) break;
            await delay(TimeSpan.FromSeconds(3));
        }
        var reason = "Independent Project read did not settle within 120 seconds: " + PlanSnapshot.ReadDiagnostic(last!);
        await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), reason + Environment.NewLine);
        throw new InvalidOperationException(reason);
    }
    internal static ImmutableArray<PlanRow> PreserveBaselineDates(ImmutableArray<PlanRow> rows, IReadOnlySet<string> baselineIds)
        => rows.Select(r => baselineIds.Contains(r.Identity) ? r with { Fixed = true } : r).ToImmutableArray();
    internal static ProjectPlanSettings CreateSettings(IReadOnlyList<ProjectFieldDefinition> fields, DateOnly today)
    {
        var required = new[] { (PlanField.Estimate, "Estimate", "NUMBER"), (PlanField.Remaining, "Remaining", "NUMBER"),
            (PlanField.Actual, "Actual", "NUMBER"), (PlanField.Start, "Start date", "DATE"), (PlanField.End, "Target date", "DATE") };
        var columns = required.Select(mapping =>
        {
            var matches = fields.Where(f => f.Name == mapping.Item2 && f.DataType == mapping.Item3 &&
                f.ValueOwner == FieldOwner.ProjectItem && f.Availability == ValueAvailability.Present).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"One existing {mapping.Item3} Project field named {mapping.Item2} is required; no baseline schema is changed.");
            return new PlanColumnMapping(mapping.Item1, matches[0].Id.NodeId, mapping.Item2, mapping.Item3);
        }).ToImmutableArray();
        return new ProjectPlanSettings { DefaultRepository = RepositoryName, StatusDate = today, Columns = columns };
    }
    internal static async Task CheckSchema(Func<ApiRequest, Task<JsonElement>> send, string root)
    {
        var requiredInputs = new Dictionary<string, string[]>
        {
            ["AddSubIssueInput"] = ["issueId", "subIssueId"], ["RemoveSubIssueInput"] = ["issueId", "subIssueId"],
            ["ReprioritizeSubIssueInput"] = ["issueId", "subIssueId", "afterId", "beforeId"],
            ["AddBlockedByInput"] = ["issueId", "blockingIssueId"], ["RemoveBlockedByInput"] = ["issueId", "blockingIssueId"],
            ["CreateProjectV2FieldInput"] = ["projectId", "name", "dataType", "singleSelectOptions"],
            ["UpdateProjectV2ItemPositionInput"] = ["projectId", "itemId", "afterId"],
            ["AddAssigneesToAssignableInput"] = ["assignableId", "assigneeIds"]
        };
        var batchNumber = 0;
        foreach (var inputNames in requiredInputs.Keys.Chunk(2))
        {
            var schema = await send(ApiRequest.GraphQl("query PlanPublishSchema{" + string.Join(' ', inputNames.Select((name, i) => "t" + i.ToString(CultureInfo.InvariantCulture) + ":__type(name:\"" + name + "\"){inputFields{name}}")) + "}"));
            await File.WriteAllTextAsync(Path.Combine(root, $"schema-{++batchNumber:D2}.json"), schema.GetRawText());
            for (var i = 0; i < inputNames.Length; i++)
            {
                var fields = schema.GetProperty("data").GetProperty("t" + i).GetProperty("inputFields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToHashSet();
                if (requiredInputs[inputNames[i]].Any(f => !fields.Contains(f))) throw new InvalidOperationException("Live schema mismatch: " + inputNames[i]);
            }
        }
    }
    private static async Task SaveManifest(string path, Manifest manifest)
    {
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await file.WriteAsync(bytes); await file.FlushAsync(); file.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }
    private static string Canonical(ProjectReadModel model) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalPayload(model))));
    private static string CanonicalPayload(ProjectReadModel model)
    {
        var value = new
        {
            model.Id, model.Number, model.Title,
            fields = model.Fields.Select(f => new { f.Id, f.Name, f.DataType, f.Options }).OrderBy(f => f.Id.NodeId),
            items = model.Items.Select(i => new { i.Id, i.Kind, i.ContentId, i.IsArchived, values = i.Values.OrderBy(v => v.FieldId?.NodeId) }),
            issues = model.Issues.Values.OrderBy(i => i.Id.NodeId).Select(i => new { i.Id, i.Title, i.State, i.Repository, i.Native })
        };
        return JsonSerializer.Serialize(value);
    }
    internal sealed record ReadRates(int ItemPages, double? ItemPageSeconds, double? SecondsPerItem, double? SecondsPerItemPage);
    internal static ReadRates RefreshRates(string workload, IReadOnlyList<RequestRecord> records, int items)
    {
        var pages = records.Count(r => r.Stage == "items-page");
        if (workload != "refresh-complete-project") return new(pages, null, null, null);
        var seconds = records.Where(r => r.Stage == "items-page").Sum(r => (r.Finished - r.Started).TotalSeconds);
        return new(pages, seconds, items == 0 ? null : seconds / items, pages == 0 ? null : seconds / pages);
    }
    internal sealed record RequestRecord(DateTimeOffset Started, DateTimeOffset Finished, bool Mutation, ProcessCompletion Completion, string Stage, int? ExitCode = null, int? HttpStatus = null, string[]? ErrorTypes = null);
    private sealed class MeasuredRunner(string root) : IGhProcessRunner
    {
        public List<RequestRecord> Records { get; } = [];
        public string? Marker { get; set; }
        public HashSet<string> OwnedIssues { get; } = [];
        private readonly HashSet<string> ownedItems = [];
        private readonly HashSet<string> creationMarkers = [];
        private int createdIssues;
        private void Guard(JsonElement input)
        {
            if (Marker is null) throw new InvalidOperationException("Ownership must be frozen before mutation.");
            if (input.TryGetProperty("repositoryId", out var repository))
            {
                if (repository.GetString() != RepositoryId || !input.TryGetProperty("title", out var title) || !title.GetString()!.StartsWith(Marker + "-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Non-owned Issue creation blocked.");
                if (++createdIssues > 300) throw new InvalidOperationException("Refusing more than 300 Issue creation attempts.");
                creationMarkers.Add(input.GetProperty("body").GetString()!);
            }
            foreach (var key in new[] { "id", "issueId", "contentId", "subIssueId", "assignableId", "blockingIssueId" })
                if (input.TryGetProperty(key, out var id) && !OwnedIssues.Contains(id.GetString()!)) throw new InvalidOperationException("Baseline Issue mutation blocked.");
            if (input.TryGetProperty("itemId", out var item) && !ownedItems.Contains(item.GetString()!)) throw new InvalidOperationException("Baseline Project item mutation blocked.");
            if (input.TryGetProperty("projectId", out var project) && project.GetString() != ProjectId) throw new InvalidOperationException("Non-allowlisted Project mutation blocked.");
        }
        private void Observe(JsonElement data, string query, JsonElement variables)
        {
            // Capture only identities of owned mutation results or owned Issues in complete Project reads.
            if (query.StartsWith("mutation", StringComparison.Ordinal))
            {
                var values = variables.EnumerateObject().ToArray();
                for (var i = 0; i < values.Length; i++)
                {
                    var input = values[i].Value;
                    if (!data.TryGetProperty("w" + i.ToString(CultureInfo.InvariantCulture), out var alias) || alias.ValueKind != JsonValueKind.Object) continue;
                    if (input.TryGetProperty("repositoryId", out _) && alias.TryGetProperty("issue", out var issue) && issue.ValueKind == JsonValueKind.Object)
                        OwnedIssues.Add(issue.GetProperty("id").GetString()!);
                    if (input.TryGetProperty("contentId", out var content) && OwnedIssues.Contains(content.GetString()!) && alias.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                        ownedItems.Add(item.GetProperty("id").GetString()!);
                }
            }
            if (data.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("issues", out var issues))
                    foreach (var issue in issues.GetProperty("nodes").EnumerateArray())
                        if (issue.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String && creationMarkers.Any(marker => body.GetString()!.Contains(marker, StringComparison.Ordinal))) OwnedIssues.Add(issue.GetProperty("id").GetString()!);
                if (node.TryGetProperty("items", out var items))
                    foreach (var item in items.GetProperty("nodes").EnumerateArray())
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("id", out var issue) && OwnedIssues.Contains(issue.GetString()!)) ownedItems.Add(item.GetProperty("id").GetString()!);
                if (node.TryGetProperty("projectItems", out var memberships) && variables.TryGetProperty("id", out var owner) && OwnedIssues.Contains(owner.GetString()!))
                    foreach (var item in memberships.GetProperty("nodes").EnumerateArray())
                        if (item.GetProperty("project").GetProperty("id").GetString() == ProjectId) ownedItems.Add(item.GetProperty("id").GetString()!);
            }
        }
        private readonly GhProcessRunner runner = new();
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            string? query = null; JsonElement variables = default;
            if (command.StandardInput is { } payload)
            {
                using var request = JsonDocument.Parse(payload);
                query = request.RootElement.GetProperty("query").GetString();
                if (request.RootElement.TryGetProperty("variables", out var input)) variables = input.Clone();
            }
            var mutation = query?.StartsWith("mutation", StringComparison.Ordinal) == true;
            if (mutation) foreach (var variable in variables.EnumerateObject()) Guard(variable.Value);
            var start = DateTimeOffset.UtcNow;
            var result = await runner.RunAsync(command, cancellationToken);
            var separator = result.StandardOutput.IndexOf("\n\n", StringComparison.Ordinal); var length = 2;
            if (separator < 0) { separator = result.StandardOutput.IndexOf("\r\n\r\n", StringComparison.Ordinal); length = 4; }
            var errorTypes = new List<string>();
            if (query is not null && separator >= 0)
            {
                try
                {
                    using var response = JsonDocument.Parse(result.StandardOutput[(separator + length)..]);
                    if (response.RootElement.ValueKind == JsonValueKind.Object && response.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                        foreach (var error in errors.EnumerateArray())
                            errorTypes.Add(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                                System.Text.RegularExpressions.Regex.IsMatch(type.GetString()!, "^[A-Z_0-9]{1,64}$") ? type.GetString()! : "UNCLASSIFIED");
                    if (response.RootElement.ValueKind == JsonValueKind.Object && response.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) Observe(data, query, variables);
                }
                catch (JsonException) { /* The product transport retains malformed-response failure. */ }
            }
            var stage = query?.Contains("createIssue", StringComparison.Ordinal) == true ? "create" :
                query?.Contains("addProjectV2ItemById", StringComparison.Ordinal) == true ? "add" :
                query?.Contains("PlanCreationGuard", StringComparison.Ordinal) == true ? "creation-guard" :
                query?.Contains("PlanExistingItem", StringComparison.Ordinal) == true ? "add-reconcile" :
                query?.Contains("updateProjectV2ItemPosition", StringComparison.Ordinal) == true ? "position" : mutation ? "write" : query?.Contains("items(first:", StringComparison.Ordinal) == true ? "items-page" : query is null ? "preflight" : "read";
            var statusMatch = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"\AHTTP/\S+ (\d{3})(?:\s|$)");
            int? status = statusMatch.Success ? int.Parse(statusMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            var record = new RequestRecord(start, DateTimeOffset.UtcNow, mutation, result.Completion, stage, result.ExitCode, status, errorTypes.ToArray()); Records.Add(record);
            if (result.Completion != ProcessCompletion.Exited || result.ExitCode != 0 || status >= 400 || errorTypes.Count > 0)
                await File.AppendAllTextAsync(Path.Combine(root, "failure.txt"), "Request outcome: " + JsonSerializer.Serialize(record) + Environment.NewLine, cancellationToken);
            await File.AppendAllTextAsync(Path.Combine(root, "processes.jsonl"), JsonSerializer.Serialize(record) + "\n", cancellationToken);
            return result;
        }
    }
}
