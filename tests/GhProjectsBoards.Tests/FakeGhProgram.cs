using System.Text;
using System.Text.Json;
using System.IO;

namespace GhProjectsBoards.Tests;

// This entry point is only in the test executable, never in the shipped app.
internal static class FakeGhProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--performance" && args.Length == 9) return await PerformanceRun.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), bool.Parse(args[4]), int.Parse(args[5]), bool.Parse(args[6]), args[7], args[8]);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (Environment.GetEnvironmentVariable("GHPB_CREATION_PROXY") is { } proxyRoot)
            return await LiveCreationProxy.Run(args, proxyRoot);
        if (args.FirstOrDefault() is "--seed-editing" or "--seed-bulk")
        {
            if (args.Length is not (2 or 4)) return 2;
            var count = 101; var fieldCount = 1;
            if (args.Length == 4 && (!int.TryParse(args[2], out count) || count is < 1 or > 1000
                || !int.TryParse(args[3], out fieldCount) || fieldCount is < 1 or > 12)) return 2;
            var root = args[1];
            var bulkSeed = args[0] == "--seed-bulk";
            if (!Path.IsPathFullyQualified(root) || Directory.Exists(root) || File.Exists(root)) return 2;
            var store = new GhProjectsBoards.Core.Projects.RegistrationStore(root);
            var expected = new[] { "P1", "P2" }.Select(projectId =>
            {
                var registration = EditingTests.Registration(projectId, count: count);
                var original = registration.Snapshot.Fields[0];
                if (bulkSeed) original = original with { Name = "Status", Options = [new("todo", "Backlog"), new("done", "Ready")] };
                var fields = Enumerable.Range(0, fieldCount).Select(index => index == 0 ? original : original with
                {
                    Id = new(original.Id.Scope, projectId + "-diagnostic-" + index),
                    Name = "Diagnostic field " + (index + 1).ToString("D2"),
                    Options = bulkSeed ? [new("todo-" + index, "Backlog"), new("done-" + index, "Ready")]
                        : [new("todo-" + index, "Todo"), new("done-" + index, "Done")]
                }).ToArray();
                return registration with { Snapshot = registration.Snapshot with { Fields = fields,
                    Items = registration.Snapshot.Items.Select(item => item with
                    {
                        Values = fields.Select(field => item.Values[0] with
                        {
                            FieldId = field.Id, OptionId = field.Options[0].Id,
                            ValueId = item.Id.NodeId + "/" + field.Id.NodeId
                        }).ToArray()
                    }).ToArray() } };
            }).ToArray();
            foreach (var seededRegistration in expected) await store.SaveAsync(seededRegistration);
            var reloaded = await store.LoadAsync();
            if (reloaded.Problems.Count != 0 || reloaded.Registrations.Count != 2)
                throw new InvalidDataException("Synthetic registration readback failed.");
            foreach (var wanted in expected)
            {
                var actual = reloaded.Registrations.Single(r => r.Snapshot.Id == wanted.Snapshot.Id).Snapshot;
                if (actual.Items.Count != count || actual.Fields.Count != fieldCount || actual.Issues.Count != count
                    || !actual.Items.Select(i => i.Id).SequenceEqual(wanted.Snapshot.Items.Select(i => i.Id))
                    || !actual.Items.Select(i => i.ContentId).SequenceEqual(wanted.Snapshot.Items.Select(i => i.ContentId))
                    || !actual.Issues.Keys.OrderBy(id => id.NodeId).SequenceEqual(wanted.Snapshot.Issues.Keys.OrderBy(id => id.NodeId))
                    || !actual.Fields.Select(f => f.Id).SequenceEqual(wanted.Snapshot.Fields.Select(f => f.Id))
                    || actual.Items.Any(item => item.Values.Count != fieldCount
                        || item.Values.Select(v => v.FieldId).Distinct().Count() != fieldCount
                        || item.Values.Any(value => actual.Fields.Single(f => f.Id == value.FieldId).Options[0].Id != value.OptionId)))
                    throw new InvalidDataException("Synthetic row/field identity readback failed.");
            }
            var evidence = Path.Combine(root, "diagnostics"); Directory.CreateDirectory(evidence);
            string Hash(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            var assembly = typeof(FakeGhProgram).Assembly.Location;
            await File.WriteAllTextAsync(Path.Combine(evidence, "editing-seed.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, count, selectFieldCount = fieldCount, totalColumns = fieldCount + 2, bulkSeed,
                scope = "Isolated synthetic cached Projects; no authentication or GitHub requests.",
                seedAssembly = assembly, seedAssemblySha256 = Hash(assembly), validatedReadback = true,
                projects = expected.Select(p => new { projectId = p.Snapshot.Id.NodeId,
                    firstItem = p.Snapshot.Items[0].Id.NodeId, lastItem = p.Snapshot.Items[^1].Id.NodeId,
                    firstIssue = p.Snapshot.Items[0].ContentId?.NodeId, lastIssue = p.Snapshot.Items[^1].ContentId?.NodeId,
                    fields = p.Snapshot.Fields.Select(f => f.Id.NodeId),
                    file = store.FileFor(p.Snapshot.Id), sha256 = Hash(store.FileFor(p.Snapshot.Id)) })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
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
        string? query = null;
        if (api && args[1] == "graphql" && input is not null)
        {
            using var payload = JsonDocument.Parse(input);
            query = payload.RootElement.GetProperty("query").GetString();
            mutation = query?.TrimStart().StartsWith("mutation", StringComparison.Ordinal) == true;
        }
        File.AppendAllText(Path.Combine(directory, "calls.jsonl"), JsonSerializer.Serialize(new
        {
            operation = args[0], endpoint = api ? args[1] : null, host, method, mutation, pid = Environment.ProcessId
        }) + "\n");
        var id = settings.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 42;
        var state = settings.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : "success";
        if (args.FirstOrDefault() == "auth")
        {
            if (state == "notLoggedIn") { Console.Write("[]"); return 0; }
            var source = settings.TryGetProperty("plaintext", out var plain) && plain.GetBoolean() ? Path.Combine(directory, "hosts.yml") : "keyring";
            Console.Write(JsonSerializer.Serialize(new[] { new { host, login = "fixture-user", active = true, state, tokenSource = source, scopes = "repo,project,read:org" } }));
            return 0;
        }
        if (query is not null && input is not null && settings.TryGetProperty("creation", out var creation) && creation.GetBoolean())
        {
            using var creationPayload = JsonDocument.Parse(input);
            var handled = FakeCreation.Handle(query, creationPayload.RootElement.GetProperty("variables"), directory, host);
            if (handled.Handled)
            {
                if (query.Contains("CreateWorkspaceIssue") && settings.TryGetProperty("creationResponseDelayMs", out var creationDelay)) await Task.Delay(creationDelay.GetInt32());
                if (query.Contains("CreateWorkspaceIssue") && settings.TryGetProperty("loseCreationResponse", out var lost) && lost.GetBoolean())
                { await Task.Delay(TimeSpan.FromSeconds(60)); return 1; }
                WriteHttp(handled.Response!); return 0;
            }
        }
        if (mutation && query is not null && input is not null && settings.TryGetProperty("apply", out var allowApply) && allowApply.GetBoolean())
        {
            using var payload = JsonDocument.Parse(input);
            var value = payload.RootElement.GetProperty("variables").GetProperty("input");
            var stateFile = Path.Combine(directory, "apply-state.json");
            var saved = File.Exists(stateFile) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stateFile))! : new System.Text.Json.Nodes.JsonObject();
            File.AppendAllText(Path.Combine(directory, "apply-requests.jsonl"), value.GetRawText() + "\n");
            if (settings.TryGetProperty("applyDelayMs", out var applyDelay)) await Task.Delay(applyDelay.GetInt32());
            if (settings.TryGetProperty("rejectApplyId", out var rejected) && value.TryGetProperty("id", out var target) && rejected.GetString() == target.GetString())
            { Console.WriteLine("HTTP/2 403 Forbidden\n\n{\"message\":\"Synthetic permission denial\"}"); return 1; }
            if (query.Contains("ApplyTitle") && value.GetProperty("id").GetString() == "I1")
            {
                saved["title"] = value.GetProperty("title").GetString(); File.WriteAllText(stateFile, saved.ToJsonString());
                WriteHttp(new { data = new { updateIssue = new { issue = new { id = "I1", title = value.GetProperty("title").GetString() } } } }); return 0;
            }
            if ((query.Contains("ApplySelect") || query.Contains("ApplyClear")) && value.GetProperty("projectId").GetString() == "P1"
                && value.GetProperty("itemId").GetString() == "P1-T1" && value.GetProperty("fieldId").GetString() == "P1-status")
            {
                saved["option"] = query.Contains("ApplyClear") ? null : value.GetProperty("value").GetProperty("singleSelectOptionId").GetString();
                File.WriteAllText(stateFile, saved.ToJsonString());
                if (query.Contains("ApplyClear")) WriteHttp(new { data = new { clearProjectV2ItemFieldValue = new { projectV2Item = new { id = "P1-T1" } } } });
                else WriteHttp(new { data = new { updateProjectV2ItemFieldValue = new { projectV2Item = new { id = "P1-T1" } } } });
                return 0;
            }
            return 2;
        }
        if (!api || mutation) return 2;
        if (args[1] == "user")
        {
            if (settings.TryGetProperty("delayMs", out var delay)) await Task.Delay(delay.GetInt32());
            WriteHttp(new { id, login = "fixture-user" });
            return 0;
        }
        if (query is not null && input is not null && settings.TryGetProperty("registration", out var registration) && registration.GetBoolean())
        {
            using var payload = JsonDocument.Parse(input);
            if (query.Contains("ProjectFields") && settings.TryGetProperty("readDelayMs", out var readDelay)) await Task.Delay(readDelay.GetInt32());
            if (query.Contains("ProjectItems") && payload.RootElement.GetProperty("variables").TryGetProperty("after", out var cursor)
                && cursor.ValueKind == JsonValueKind.String && settings.TryGetProperty("partial", out var partial) && partial.GetBoolean())
            { Console.Write("HTTP/2.0 403 Forbidden\r\nContent-Type: application/json\r\n\r\n{}"); return 1; }
            var response = RegistrationResponses.Query(query, payload.RootElement.GetProperty("variables"), host, settings.TryGetProperty("itemCount", out var itemCount) ? itemCount.GetInt32() : 101,
                settings.TryGetProperty("columns", out var columns) && columns.GetBoolean(),
                settings.TryGetProperty("bulk", out var bulk) && bulk.GetBoolean(),
                settings.TryGetProperty("reviewInformation", out var reviewInformation) && reviewInformation.GetBoolean(),
                settings.TryGetProperty("reviewDetails", out var reviewDetails) && reviewDetails.GetBoolean());
            if (response is not null)
            {
                if (query.Contains("ProjectItems") || query.Contains("ApplyItem") || query.Contains("ApplyObservation"))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(response))!;
                    var items = query.Contains("ApplyObservation") ? new[] { node["data"]!["item"]! }
                        : query.Contains("ApplyItem") ? new[] { node["data"]!["node"]! } : node["data"]!["node"]!["items"]!["nodes"]!.AsArray().ToArray();
                    foreach (var item in items.Where(i => i!["content"]?["id"]?.ToString() == "I1"))
                    {
                        if (settings.TryGetProperty("remoteTitle", out var title) && title.ValueKind == JsonValueKind.String) item!["content"]!["title"] = title.GetString();
                        if (settings.TryGetProperty("remoteOption", out var option) && option.ValueKind == JsonValueKind.String)
                            item!["fieldValues"]!["nodes"]![0]!["optionId"] = option.GetString();
                        var applyState = Path.Combine(directory, "apply-state.json");
                        if (File.Exists(applyState))
                        {
                            var applied = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(applyState))!.AsObject();
                            if (applied.ContainsKey("title")) item!["content"]!["title"] = applied["title"]!.ToString();
                            if (applied.ContainsKey("option"))
                            {
                                if (applied["option"] is null) { item!["fieldValues"]!["nodes"] = new System.Text.Json.Nodes.JsonArray(); item["fieldValues"]!["totalCount"] = 0; }
                                else item!["fieldValues"]!["nodes"]![0]!["optionId"] = applied["option"]!.ToString();
                            }
                        }
                    }
                    response = query.Contains("ProjectItems") && settings.TryGetProperty("creation", out var enabledCreation) && enabledCreation.GetBoolean() ? FakeCreation.Augment(node, directory, host) : node;
                }
                if (query.Contains("viewer { databaseId }"))
                {
                    var observed = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(response))!;
                    observed["data"]!["viewer"] = JsonSerializer.SerializeToNode(new { databaseId = id });
                    response = observed;
                }
                WriteHttp(response); return 0;
            }
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

    private static void WriteHttp(object body) => Console.Write("HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n" + JsonSerializer.Serialize(body));
}
