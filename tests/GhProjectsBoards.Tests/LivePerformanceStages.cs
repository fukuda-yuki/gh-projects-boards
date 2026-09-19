using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

internal sealed partial class LivePerformanceRun
{
    internal sealed record Binary(string Executable, string Source, string CoreSha256, string HarnessSha256);
    internal sealed record Plan(string FixtureRoot, int Fields, string[] Order, Binary Main, Binary Candidate,
        string[] LedgerRoots, string SetupEvidence, bool Diagnostic = false);

    internal static int Initialize(string config)
    {
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(config))!;
        ValidatePlan(plan);
        Require(!Directory.Exists(plan.FixtureRoot), "Fixture root already exists; retain original evidence");
        Directory.CreateDirectory(plan.FixtureRoot);
        WriteFile(Path.Combine(plan.FixtureRoot, "plan.json"), plan);
        WriteFile(Path.Combine(plan.FixtureRoot, "lifecycle-budget.json"), new {
            create = plan.Fields, add = plan.Fields, measured = plan.Order.Length * plan.Fields,
            restore = plan.Order.Length * plan.Fields, remove = plan.Fields, delete = plan.Fields,
            total = plan.Fields * (4 + 2 * plan.Order.Length), hourlyCeiling = 480, minimumMutationIntervalSeconds = 1,
            policy = "Admission reserves restoration and cleanup; pause across rolling windows. No throughput target. No retry of uncertain writes.",
            samples = plan.Order, warmups = 0, field = "Title", interpretation = "Report all fixed pairs, medians and ranges, order effects; no p95 or arbitrary speed threshold. No Select throughput inference." });
        new LivePerformanceRun(plan.FixtureRoot).Save();
        return 0;
    }

    private static void ValidatePlan(Plan plan)
    {
        Require(Path.IsPathFullyQualified(plan.FixtureRoot) && plan.Fields is 1 or 10 or 50, "Invalid fixture plan");
        Require(plan.Diagnostic ? plan.Fields == 1 && plan.Order.Length == 0
            : plan.Order.SequenceEqual(new[] { "main", "candidate", "candidate", "main" }), "Fixed counterbalanced plan required");
        Require(plan.LedgerRoots.Length > 0 && plan.LedgerRoots.All(Path.IsPathFullyQualified), "Explicit ledger roots required");
        foreach (var binary in new[] { plan.Main, plan.Candidate })
            Require(Path.IsPathFullyQualified(binary.Executable) && Regex.IsMatch(binary.CoreSha256, "^[A-Fa-f0-9]{64}$")
                && Regex.IsMatch(binary.HarnessSha256, "^[A-Fa-f0-9]{64}$") && Regex.IsMatch(binary.Source, "^[A-Fa-f0-9]{40}$"), "Pinned binary/source mapping required");
    }

    internal static async Task<int> Stage(string fixtureRoot, string output, string stage, string label,
        IGhProcessRunner? boundary = null, bool verifyBinary = true)
    {
        Require(Path.IsPathFullyQualified(fixtureRoot) && Path.IsPathFullyQualified(output) && !Directory.Exists(output), "New absolute stage output required");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(fixtureRoot, "plan.json")))!;
        ValidatePlan(plan); Require(plan.FixtureRoot == fixtureRoot, "Plan destination mismatch");
        Require(plan.LedgerRoots.Append(fixtureRoot).Any(ledger => Within(ledger, output)), "Stage output must remain in the declared shared ledger roots");
        Require(stage is "Prepare" or "Verify" or "Measure" or "Restore" or "Cleanup", "Unknown lifecycle stage");
        Require(label is "main" or "candidate", "Unknown binary label");
        var binary = label == "main" ? plan.Main : plan.Candidate;
        if (verifyBinary)
        {
            Require(Hash(typeof(ApplyExecutor).Assembly.Location) == binary.CoreSha256.ToUpperInvariant()
                && Hash(typeof(LivePerformanceRun).Assembly.Location) == binary.HarnessSha256.ToUpperInvariant(), "Executing binary differs from fixed source map");
        }
        Directory.CreateDirectory(output);
        // A filesystem lease spans admission, intents, mutation and manifest replacement.
        using var lease = new FileStream(Path.Combine(fixtureRoot, "stage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var run = Load(fixtureRoot, output);
        var planned = stage switch {
            "Prepare" => (plan.Fields - run.fixtures.Count) * 2 + run.fixtures.Count(f => f.ItemId is null),
            "Measure" or "Restore" => plan.Fields,
            "Cleanup" => run.createIntents * 2, _ => 0 };
        var reserve = stage is "Prepare" or "Measure" or "Restore" ? plan.Fields * 2 + (stage == "Measure" ? plan.Fields : 0) : 0;
        run.runner = new(output, run.marker, boundary) { MutationCeiling = planned, LedgerRoots = plan.LedgerRoots.Append(fixtureRoot).Distinct().ToArray(), CooldownPath = Path.Combine(fixtureRoot, "cooldown.json") };
        foreach (var fixture in run.fixtures) run.runner.AllowIssue(fixture.IssueId, fixture.Baseline, fixture.Changed);
        run.service = new(Gh, "github.com", run.runner);
        var continuation = $"--performance-stage \"{fixtureRoot}\" <new-stage-output> {stage} {label}";
        run.Write("stage-plan.json", new { stage, label, planned, reserve, binary, at = DateTimeOffset.UtcNow, continuation });
        var cooldown = LivePerformanceBudget.Cooldown(run.runner.LedgerRoots);
        if (cooldown > DateTimeOffset.UtcNow)
        {
            run.Write("checkpoint.json", new { status = "Deferred", resumeAfter = cooldown, continuation, reason = "Persisted service cooldown; no request dispatched" });
            return 3;
        }
        var budget = LivePerformanceBudget.Check(run.runner.LedgerRoots, planned + reserve, DateTimeOffset.UtcNow);
        run.Write("budget.json", budget);
        if (planned > 0 && !budget.Allowed)
        {
            run.Write("checkpoint.json", new { status = "Deferred", budget.ResumeAfter, continuation, reason = "Rolling lifecycle budget; no request dispatched" });
            return 3;
        }
        try
        {
            Require(!run.cleanup, "Deleted fixture manifests are provenance, not reusable live identities");
            if (stage == "Prepare")
            {
                Require(!run.prepared && !run.samplePending && run.measuredSamples == 0, "Preparation cannot restart measured fixtures");
                Require(plan.Diagnostic || !string.IsNullOrWhiteSpace(plan.SetupEvidence) && File.Exists(plan.SetupEvidence), "Preparation blocked pending supported correction or validated containment evidence");
                Require(!run.stages.Any(s => s == "setup-unverified"), "Unclassified setup failure blocks further preparation; diagnose or clean up");
            }
            if (stage == "Measure") Require(run.prepared && !run.samplePending && run.measuredSamples == run.restoredSamples
                && run.measuredSamples < plan.Order.Length && label == plan.Order[run.measuredSamples], "Measurement order/restoration checkpoint mismatch");
            if (stage == "Restore") Require(run.prepared && !run.samplePending && run.measuredSamples == run.restoredSamples + 1
                && label == "main", "Restoration uses fixed main binary after one successful sample");
            var connected = await run.service.ConnectAsync();
            Require(connected.IsConnected && connected.Context!.Login == "fukuda-yuki" && connected.Authentication!.Store == CredentialStore.Keyring
                && connected.Authentication.HasScope("repo") == true && connected.Authentication.HasScope("project") == true, "Sandbox authentication/scope unavailable");
            run.context = connected.Context!;
            var current = await run.Snapshot("stage-start");
            var baselinePath = Path.Combine(fixtureRoot, "baseline.json");
            if (!File.Exists(baselinePath))
            {
                Require(stage == "Prepare" && run.createIntents == 0, "Missing original baseline");
                WriteFile(baselinePath, current);
            }
            run.baseline = JsonDocument.Parse(File.ReadAllText(baselinePath)).RootElement.Clone();
            if (run.createIntents == run.fixtures.Count) run.VerifyUnrelated(current);
            var quota = await run.Send("quota", ApiRequest.Rest("GET", "rate_limit"));
            // Worst-case main preflights include auth-status's scopes request. Reserve is
            // conservative and includes restoration/cleanup; HTTP activity inside gh is not fully visible.
            var coreReserve = stage is "Measure" or "Restore" ? planned * 14 + (stage == "Measure" ? plan.Fields * 14 : 0) + plan.Fields * 8 + 250
                : (planned + reserve) * 4 + 200;
            try { run.runner.RequirePrimaryBudget(quota.GetProperty("resources"), coreReserve, (planned + reserve) * 5 + 100); }
            catch (InvalidOperationException)
            {
                run.Write("checkpoint.json", new { status = "Deferred", continuation, reason = "Observed primary quota below stage plus restoration/cleanup reserve",
                    quotaResetHeaders = run.runner.Records.Where(r => r.Quota.ContainsKey("x-ratelimit-reset")).Select(r => new { resource = r.Quota.GetValueOrDefault("x-ratelimit-resource"), reset = r.Quota["x-ratelimit-reset"] }).ToArray() });
                return 3;
            }
            switch (stage)
            {
                case "Prepare": await run.Prepare(plan); break;
                case "Verify": await run.VerifyReady(plan); break;
                case "Measure": await run.MeasureNext(plan, label); break;
                case "Restore": await run.RestoreLast(plan); break;
                case "Cleanup": await run.Cleanup(); break;
            }
            run.Save(); run.Write("stage-result.json", new { status = "Passed", stage, run.measuredSamples, run.restoredSamples, run.cleanup });
            return 0;
        }
        catch (Exception error)
        {
            if (stage == "Prepare") run.stages.Add("setup-unverified");
            run.Write("failure.json", new { type = error.GetType().Name, reason = SafeLiveDiagnostic.Render(error.Message) });
            run.Save(); return 1;
        }
        finally { run.runner.OnStop = null; }
    }

    private static LivePerformanceRun Load(string fixtureRoot, string output)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "fixture.json"))); var data = doc.RootElement;
        Require(data.GetProperty("version").GetInt32() == 2 && data.GetProperty("project").GetString() == ProjectId
            && data.GetProperty("repository").GetString() == RepositoryId, "Fixture provenance mismatch");
        var marker = data.GetProperty("marker").GetString()!;
        Require(Regex.IsMatch(marker, "^ghpb-perf51-[0-9a-f]{32}$"), "Invalid ownership marker");
        var run = new LivePerformanceRun(output, marker) { fixtureRoot = fixtureRoot,
            createIntents = data.GetProperty("createIntents").GetInt32(), prepared = data.GetProperty("prepared").GetBoolean(),
            measuredSamples = data.GetProperty("measuredSamples").GetInt32(), restoredSamples = data.GetProperty("restoredSamples").GetInt32(),
            samplePending = data.GetProperty("samplePending").GetBoolean(), cleanup = data.GetProperty("cleanup").GetBoolean() };
        run.fixtures.AddRange(JsonSerializer.Deserialize<Fixture[]>(data.GetProperty("fixtures"))!);
        run.stages.AddRange(JsonSerializer.Deserialize<string[]>(data.GetProperty("stages"))!);
        Require(run.createIntents is >= 0 and <= 50 && run.fixtures.Count <= run.createIntents
            && run.fixtures.All(f => f.Number > 1 && f.Baseline.StartsWith(marker + " ") && f.Changed.StartsWith(marker + " "))
            && run.fixtures.Select(f => f.IssueId).Distinct().Count() == run.fixtures.Count
            && run.fixtures.Select(f => f.Number).Distinct().Count() == run.fixtures.Count, "Invalid run-owned identities");
        return run;
    }

    private async Task Prepare(Plan plan)
    {
        Require(createIntents == fixtures.Count, "Uncertain creation requires independent reconciliation, never replay");
        Require(baseline.GetProperty("user").GetProperty("projectV2").GetProperty("items").GetProperty("totalCount").GetInt32() + plan.Fields <= 100, "Fixture exceeds complete bounded snapshot");
        foreach (var f in fixtures) runner.AllowIssue(f.IssueId, f.Baseline, f.Changed);
        for (var i = 0; i < plan.Fields; i++)
        {
            if (i == fixtures.Count)
            {
                var title = $"{marker} {i:D2} baseline"; runner.AllowCreate(title); createIntents++; Save();
                var created = await Send("create-" + i, ApiRequest.Rest("POST", Endpoint, new { title, body = marker + " Disposable #51 fixture." }));
                var f = new Fixture(created.GetProperty("node_id").GetString()!, created.GetProperty("number").GetInt32(), title, $"{marker} {i:D2} changed");
                Require(f.Number > 1 && !string.IsNullOrWhiteSpace(f.IssueId), "Invalid created identity");
                fixtures.Add(f); runner.AllowIssue(f.IssueId, f.Baseline, f.Changed); Save();
            }
            var fixture = fixtures[i]; await VerifyIssue(fixture, fixture.Baseline);
            var before = await Snapshot("membership-before-" + i);
            var ownedItems = OwnedItems(before, fixture.IssueId);
            Require(ownedItems.Length <= 1, "Ambiguous membership");
            if (ownedItems.Length == 0)
            {
                Require(!stages.Contains("add-" + i + ":intent"), "Uncertain addition must not be replayed");
                await Send("add-" + i, ApiRequest.GraphQl("mutation PerformanceAdd($project:ID!,$issue:ID!) { addProjectV2ItemById(input:{projectId:$project,contentId:$issue}) { item { id } } }", new { project = ProjectId, issue = fixture.IssueId }));
                ownedItems = OwnedItems(await Snapshot("membership-after-" + i), fixture.IssueId);
            }
            Require(ownedItems.Length == 1, "Membership not independently verified");
            fixtures[i] = fixture with { ItemId = ownedItems[0].GetProperty("id").GetString()! }; Save();
        }
        await VerifyReady(plan);
    }

    private async Task VerifyReady(Plan plan)
    {
        Require(fixtures.Count == plan.Fields && createIntents == fixtures.Count, "Incomplete owned fixture");
        var current = await Snapshot("fixture-verification"); VerifyUnrelated(current);
        for (var i = 0; i < fixtures.Count; i++)
        {
            var fixture = fixtures[i]; await VerifyIssue(fixture, fixture.Baseline);
            var found = OwnedItems(current, fixture.IssueId);
            Require(found.Length == 1 && !found[0].GetProperty("isArchived").GetBoolean()
                && (fixture.ItemId is null || found[0].GetProperty("id").GetString() == fixture.ItemId), "Owned membership changed or unavailable");
            fixtures[i] = fixture with { ItemId = found[0].GetProperty("id").GetString()! };
        }
        var readyPath = Path.Combine(fixtureRoot, "fixture-ready.json");
        if (File.Exists(readyPath)) Require(SameSnapshot(current, JsonDocument.Parse(File.ReadAllText(readyPath)).RootElement), "Fixed initial remote data changed");
        else WriteFile(readyPath, current);
        prepared = true; Save();
    }

    private async Task<RegistrationWorkspace> Workspace(string dataRoot)
    {
        var workspace = new RegistrationWorkspace(new(dataRoot)); await workspace.RestoreAsync(); await workspace.BindAsync(context, service);
        var choice = await new ProjectDiscovery(service).ResolveAsync(context, "https://github.com/users/fukuda-yuki/projects/3", default);
        Require(choice.Id.NodeId == ProjectId, "Project resolution mismatch");
        if (!await workspace.SelectAsync(choice.Id)) await workspace.RegisterAsync(choice, null);
        Require(workspace.Selected?.Snapshot.Id.NodeId == ProjectId && await workspace.PrepareLocalRowsAsync(), "Fixture workspace unavailable");
        runner.OnStop = workspace.Cancel; return workspace;
    }

    private async Task MeasureNext(Plan plan, string label)
    {
        await VerifyReady(plan);
        foreach (var f in fixtures) runner.AllowIssue(f.IssueId, f.Baseline, f.Changed);
        var sampleRoot = Path.Combine(root, "sample");
        var seedPath = Path.Combine(fixtureRoot, "edited-seed.json");
        if (!File.Exists(seedPath))
        {
            var seedRoot = Path.Combine(fixtureRoot, "seed"); var seed = await Workspace(seedRoot);
            var rows = seed.Drafts!.Workspace.Open(seed.Selected!);
            foreach (var f in fixtures) seed.Drafts.Workspace.Commit(ProjectId, rows.Single(r => r.ItemId == f.ItemId).Cells[0], f.Changed);
            Require(await seed.FlushDraftsAsync(), "Edited seed did not persist");
            File.Copy(new DraftStore(seedRoot).FileFor(ConnectionScope.From(context)), seedPath, false);
        }
        var store = new DraftStore(sampleRoot); var target = store.FileFor(ConnectionScope.From(context));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(seedPath, target, false);
        var workspace = await Workspace(sampleRoot);
        Require(Hash(target) == Hash(seedPath), "Initial checkpoint changed before timing");
        samplePending = true; Save();
        await Measure(workspace, sampleRoot, fixtures.ToArray(), label, Hash(target));
        foreach (var f in fixtures) await VerifyIssue(f, f.Changed);
        WriteFile(Path.Combine(fixtureRoot, $"sample-{measuredSamples}.json"), new { label, output = root, initialSha256 = Hash(seedPath), at = DateTimeOffset.UtcNow });
        measuredSamples++; samplePending = false; Save();
    }

    private async Task RestoreLast(Plan plan)
    {
        foreach (var f in fixtures) { await VerifyIssue(f, null); runner.AllowIssue(f.IssueId, f.Baseline, f.Changed); }
        // A stable workspace retains dispatch intent and acknowledgement across stage/process
        // restarts. Resume reconciles unknowns; it never creates a fresh approval to replay them.
        var restoreRoot = Path.Combine(fixtureRoot, "restore-" + restoredSamples);
        var workspace = await Workspace(restoreRoot);
        using var trace = new PerformanceTrace(); var timer = Stopwatch.StartNew();
        var retained = workspace.Drafts!.Workspace.Journal.SingleOrDefault();
        string batchId;
        if (retained is not null)
        {
            Require(retained.Operations.Length == plan.Fields && retained.Operations.All(o => fixtures.Any(f => f.ItemId == o.ItemId && f.IssueId == o.IssueId && o.Intended.Value == f.Baseline)), "Retained restoration identity mismatch");
            batchId = retained.Id; await workspace.ResumeApplyAsync(batchId);
        }
        else
        {
            foreach (var f in fixtures) await VerifyIssue(f, f.Changed);
            var rows = workspace.Drafts.Workspace.Open(workspace.Selected!);
            foreach (var f in fixtures) workspace.Drafts.Workspace.Commit(ProjectId, rows.Single(r => r.ItemId == f.ItemId).Cells[0], f.Baseline);
            await workspace.PrepareApplyAsync(fixtures.Select(f => f.ItemId!).ToHashSet());
            var review = workspace.ApplyReview; Require(review?.Batch.Operations.Length == plan.Fields, "Restoration review mismatch");
            batchId = review!.Batch.Id; await workspace.ConfirmApplyAsync(review);
        }
        var durable = await new DraftStore(restoreRoot).LoadAsync(ConnectionScope.From(context));
        Require(durable!.Journal!.Single(b => b.Id == batchId).Operations.All(o => o.State == ApplyState.Succeeded), "Restoration unsettled; no retry");
        Write("restoration.json", new { elapsedMs = timer.Elapsed.TotalMilliseconds, spans = trace.Samples, fields = plan.Fields });
        await VerifyReady(plan); restoredSamples++; complete = restoredSamples == plan.Order.Length; Save();
    }

    private void VerifyUnrelated(JsonElement snapshot)
    {
        var copy = JsonNode.Parse(snapshot.GetRawText())!;
        var connection = copy["user"]!["projectV2"]!["items"]!;
        var nodes = connection["nodes"]!.AsArray();
        foreach (var node in nodes.ToArray())
            if (node?["content"]?["id"] is { } id && fixtures.Any(f => f.IssueId == id.GetValue<string>())) nodes.Remove(node);
        connection["totalCount"] = nodes.Count;
        Require(SameSnapshot(JsonSerializer.SerializeToElement(copy), baseline), "Unrelated sandbox data changed");
    }
    private static JsonElement[] OwnedItems(JsonElement data, string issue)
        => data.GetProperty("user").GetProperty("projectV2").GetProperty("items").GetProperty("nodes").EnumerateArray()
            .Where(i => i.GetProperty("content").ValueKind == JsonValueKind.Object && i.GetProperty("content").TryGetProperty("id", out var id) && id.GetString() == issue).ToArray();
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static bool Within(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void WriteFile(string path, object value)
    {
        var temporary = path + ".new";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
        File.Move(temporary, path, true);
    }
}

internal static class LivePerformanceBudget
{
    internal static DateTimeOffset Cooldown(IEnumerable<string> roots) => roots.Where(Directory.Exists)
        .SelectMany(r => Directory.EnumerateFiles(r, "cooldown.json", SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(p => JsonDocument.Parse(File.ReadAllText(p)).RootElement.GetProperty("NotBefore").GetDateTimeOffset()).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
    internal sealed record Admission(bool Allowed, int RecentWrites, int RequestedWithReserve, DateTimeOffset? ResumeAfter, DateTimeOffset? LastMutation);
    internal static Admission Check(IEnumerable<string> roots, int requested, DateTimeOffset now)
    {
        var times = roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "mutation-intents.jsonl", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(File.ReadLines)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("at").GetDateTimeOffset())
            .Where(t => t > now.AddHours(-1)).Order().ToArray();
        var allowed = requested >= 0 && times.Length + requested <= 480;
        var excess = times.Length + requested - 480;
        return new(allowed, times.Length, requested, !allowed && excess > 0 && excess <= times.Length ? times[excess - 1].AddHours(1).AddSeconds(1) : null, times.Length == 0 ? null : times[^1]);
    }
}
