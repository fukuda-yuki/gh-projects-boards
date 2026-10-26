using System.Text;
using System.Text.Json;
using System.IO;

namespace GhProjectsBoards.Tests;

// This entry point is only in the test executable, never in the shipped app.
internal static class FakeGhProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--prepare-evaluation" && args.Length is 2 or 3 or 4)
        { await EvaluationFixture.Create(args[1], args.Length > 2 ? args[2] : EvaluationFixture.DefaultVersion, args.Length > 3 ? DateOnly.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : null); return 0; }
        if (args.FirstOrDefault() == "--plan-publish-live" && args.Length == 3) return await PlanPublisherLive.Run(args[1], args[2]);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.FirstOrDefault() == "environment")
        {
            var variables = new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN",
                "GH_DEBUG", "DEBUG", "GH_HOST", "GH_REPO" };
            Console.Write(JsonSerializer.Serialize(new
            {
                present = variables.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))),
                promptDisabled = Environment.GetEnvironmentVariable("GH_PROMPT_DISABLED"),
                configDirectory = Environment.GetEnvironmentVariable("GH_CONFIG_DIR")
            }));
            return 0;
        }
        if (args.FirstOrDefault() == "echo")
        {
            var echoInput = await Console.In.ReadToEndAsync();
            Console.Write(JsonSerializer.Serialize(new { arguments = args.Skip(1), input = echoInput }));
            Console.Error.Write("synthetic stderr");
            return 7;
        }
        if (args.FirstOrDefault() == "wait")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return 0;
        }
        if (args.FirstOrDefault() == "--version")
        {
            Console.Write("gh version 2.100.0 (isolated test fixture)");
            return 0;
        }
        var directory = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, "scenario.json"))) return 2;
        using var scenario = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "scenario.json")));
        var settings = scenario.RootElement;
        var hostIndex = Array.IndexOf(args, "--hostname");
        var host = hostIndex >= 0 && hostIndex + 1 < args.Length ? args[hostIndex + 1] : "missing-host";
        var api = args.FirstOrDefault() == "api";
        var methodIndex = Array.IndexOf(args, "--method");
        var method = methodIndex >= 0 ? args[methodIndex + 1] : "GET";
        var input = api && args.Contains("--input") ? await Console.In.ReadToEndAsync() : null;
        var mutation = api && method != "GET" && args[1] != "graphql";
        string? query = null, detailId = null;
        JsonElement? bodyInput = null;
        // #201: the publication read scope is asserted from the queries and variables a fake run recorded.
        JsonElement? recordedVariables = null;
        if (api && args[1] == "graphql" && input is not null)
        {
            using var payload = JsonDocument.Parse(input);
            query = payload.RootElement.GetProperty("query").GetString();
            recordedVariables = payload.RootElement.GetProperty("variables").Clone();
            mutation = query?.TrimStart().StartsWith("mutation", StringComparison.Ordinal) == true;
            if (query?.StartsWith("query PlanIssueDetail(", StringComparison.Ordinal) == true || query?.StartsWith("query PlanIssueBody(", StringComparison.Ordinal) == true)
                detailId = payload.RootElement.GetProperty("variables").GetProperty("id").GetString();
            if (query?.StartsWith("mutation PlanIssueBodyPublish(", StringComparison.Ordinal) == true)
                bodyInput = payload.RootElement.GetProperty("variables").GetProperty("input").Clone();
        }
        await WriteDiagnosticAsync(Path.Combine(directory, "calls.jsonl"), JsonSerializer.Serialize(new
        {
            operation = args[0], endpoint = api ? args[1] : null, host, method, mutation, pid = Environment.ProcessId, detailId, bodyInput,
            query, variables = recordedVariables is { } recorded ? (object)recorded : null
        }) + "\n", append: true);
        if (settings.TryGetProperty("holdOperation", out var heldOperation) && args[0] == heldOperation.GetString() ||
            settings.TryGetProperty("holdQuery", out var heldQuery) && query?.Contains(heldQuery.GetString()!) == true)
        {
            await WriteDiagnosticAsync(Path.Combine(directory, "held-gh.pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(Path.Combine(directory, "release-gh")) && DateTime.UtcNow < deadline) await Task.Delay(20);
        }
        var id = settings.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 42;
        var state = settings.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : "success";
        if (args.FirstOrDefault() == "auth")
        {
            if (state == "notLoggedIn") { Console.Write("[]"); return 0; }
            var source = settings.TryGetProperty("plaintext", out var plain) && plain.GetBoolean() ? Path.Combine(directory, "hosts.yml") : "keyring";
            Console.Write(JsonSerializer.Serialize(new[] { new { host, login = "fixture-user", active = true, state, tokenSource = source, scopes = "repo,project,read:org" } }));
            return 0;
        }
        var bodiesPath = Path.Combine(directory, "detail-bodies.json");
        var bodies = File.Exists(bodiesPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(bodiesPath))! : new();
        if (bodyInput is { } bodyWrite) {
            var fault = settings.TryGetProperty("detailFault", out var value) ? value.GetString() : null;
            if (fault == "body-write") { Console.Write("HTTP/2.0 503 Unavailable\r\n\r\n{}"); return 1; }
            var body = bodyWrite.GetProperty("body").GetString()!;
            bodies[bodyWrite.GetProperty("id").GetString()!] = body;
            await WriteDiagnosticAsync(bodiesPath, JsonSerializer.Serialize(bodies));
            WriteHttp(new { data = new { updateIssue = new { issue = new { body = fault == "body-mismatch" ? "Different readback" : body } } } });
            return 0;
        }
        if (detailId is not null)
        {
            var fault = settings.TryGetProperty("detailFault", out var detailFault) ? detailFault.GetString() : null;
            var issue = FakePlanEditor.Load(directory).Issues.FirstOrDefault(i => i.Row.Identity == detailId);
            if (fault == "missing" || issue is null) { WriteHttp(new { data = new { node = (object?)null } }); return 0; }
            if (fault == "network") { Console.Write("HTTP/2.0 503 Unavailable\r\n\r\n{}"); return 1; }
            var number = int.Parse(detailId[1..], System.Globalization.CultureInfo.InvariantCulture);
            var issueUrl = $"https://github.com/{issue.Row.Repository}/issues/{number}";
            var empty = fault is "empty" or "marker";
            var overridden = bodies.TryGetValue(detailId, out var savedBody);
            var body = overridden ? savedBody! : fault == "marker" ? "<!-- ghpb-plan:\nfixture marker\n-->\n " : empty ? "" : "<!-- ghpb-plan:fixture -->**Body " + issue.Row.Title + "**";
            if (query!.StartsWith("query PlanIssueBody(", StringComparison.Ordinal)) {
                WriteHttp(new { data = new { node = new { body, viewerCanUpdate = fault != "readonly" } } }); return 0;
            }
            if (overridden && fault == "body-refresh") { Console.Write("HTTP/2.0 503 Unavailable\r\n\r\n{}"); return 1; }
            var commentCount = empty ? 0 : settings.TryGetProperty("detailCommentCount", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 7;
            WriteHttp(new { data = new { node = new {
                number, title = issue.Row.Title, url = issueUrl, state = issue.Row.Closed || fault == "closed" ? "CLOSED" : "OPEN", stateReason = (string?)null,
                body, viewerCanUpdate = fault != "readonly",
                bodyHTML = overridden ? "<p>" + System.Net.WebUtility.HtmlEncode(body) + "</p>" : empty ? "" : "<p><strong>Body " + System.Net.WebUtility.HtmlEncode(issue.Row.Title) + "</strong></p>"
                    + "<a id='fixture-link' href='https://example.com/read'>Read</a> <a id='fixture-new' target='_blank' href='https://example.com/new'>New</a>"
                    + "<a id='fixture-relative' href='/acme/repo/issues/2'>Relative</a>"
                    + "<a id='fixture-fragment' href='#section'>Section</a>"
                    + "<a id='fixture-other' href='file:///ignored'>Other</a><input type='checkbox' checked disabled>",
                updatedAt = "2026-10-15T08:30:00Z", repository = new { nameWithOwner = issue.Row.Repository },
                // GitHub returns comments(last: 5): the newest five, oldest first.
                comments = new { totalCount = commentCount, nodes = Enumerable.Range(Math.Max(commentCount - 4, 1), Math.Min(commentCount, 5)).Select(i => new {
                    author = i == Math.Max(commentCount - 4, 1) ? null : new { login = "fixture-user" }, createdAt = $"2026-10-{10 + i}T08:00:00Z",
                    url = issueUrl + "#issuecomment-" + i, body = "<!-- hidden\ncomment metadata -->Comment " + i, bodyHTML = "<p>Comment " + i + "</p>"
                }).ToArray() }
            } } });
            return 0;
        }
        if (query is not null && input is not null && settings.TryGetProperty("planEditor", out var planEditor) && planEditor.GetBoolean())
        {
            using var planPayload = JsonDocument.Parse(input);
            // Protect the fake's whole read-modify-write transaction across child processes.
            // A ticketed mutation also waits for its predecessor, so batch-numbered faults hit the
            // same request regardless of which concurrently started process wins the lock.
            var ticket = planPayload.RootElement.TryGetProperty("orderTicket", out var ticketNode) ? ticketNode.GetInt32() : 0;
            var orderPath = Path.Combine(directory, "plan-order.txt");
            var orderDeadline = DateTime.UtcNow.AddSeconds(3);
            FileStream stateLock;
            while (true)
            {
                try { stateLock = new FileStream(Path.Combine(directory, "plan-state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { await Task.Delay(10); continue; }
                var last = File.Exists(orderPath) ? int.Parse(File.ReadAllText(orderPath)) : 0;
                if (ticket <= last + 1 || DateTime.UtcNow > orderDeadline) break;
                stateLock.Dispose(); await Task.Delay(5);
            }
            using (stateLock)
            {
                if (ticket > 0) File.WriteAllText(orderPath, ticket.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (mutation && settings.TryGetProperty("mutationDelayMs", out var delay)) await Task.Delay(delay.GetInt32());
                // A test can stretch the first mutation's response while the ticket ordering keeps its peers
                // queued, so the field phase's dispatch bound is observable regardless of host speed. The marker
                // is created under the state lock, so exactly the first mutation since the scenario write is held.
                if (mutation && settings.TryGetProperty("firstMutationDelayMs", out var firstDelay) && firstDelay.GetInt32() > 0 &&
                    !File.Exists(Path.Combine(directory, "first-mutation-delay.done")))
                {
                    await WriteDiagnosticAsync(Path.Combine(directory, "first-mutation-delay.done"), ticket.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(firstDelay.GetInt32());
                }
                return await FakePlanEditor.Handle(query, planPayload.RootElement.GetProperty("variables"), directory, settings);
            }
        }
        if (!api || mutation) return 2;
        if (args[1] == "user")
        {
            if (settings.TryGetProperty("delayMs", out var delay)) await Task.Delay(delay.GetInt32());
            WriteHttp(new { id, login = "fixture-user" });
            return 0;
        }
        if (query?.Contains("projectV2", StringComparison.Ordinal) == true)
        {
            var ownerType = query.Contains("organization(", StringComparison.Ordinal) ? "organization" : "user";
            WriteHttp(new { data = new Dictionary<string, object> { [ownerType] = new { projectV2 = new { id = "P_fixture", viewerCanUpdate = true } } } });
            return 0;
        }
        if (query?.Contains("repository(", StringComparison.Ordinal) == true)
        {
            WriteHttp(new { data = new { repository = new { isPrivate = true, issue = new { id = "I_fixture", viewerCanUpdate = true } } } });
            return 0;
        }
        return 2;
    }

    private static async Task WriteDiagnosticAsync(string path, string text, bool append = false)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        FileStream stream;
        while (true)
        {
            try
            {
                stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                break;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10);
            }
        }
        // Only acquiring the exclusive writer is retried: retrying a write could duplicate a partial record.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text);
    }

    private static void WriteHttp(object body) => Console.Write("HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n" + JsonSerializer.Serialize(body));
}
