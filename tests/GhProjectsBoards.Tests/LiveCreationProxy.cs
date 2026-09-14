using System.Diagnostics;
using System.Text.Json;

namespace GhProjectsBoards.Tests;

internal static class LiveCreationProxy
{
    public static async Task<int> Run(string[] args, string root)
    {
        var input = args.Contains("--input") ? await Console.In.ReadToEndAsync() : null;
        string? query = null; JsonElement value = default;
        if (input is not null)
        {
            using var payload = JsonDocument.Parse(input); query = payload.RootElement.GetProperty("query").GetString();
            var variables = payload.RootElement.GetProperty("variables"); variables.TryGetProperty("input", out value); value = value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();
        }
        var marker = File.ReadAllText(Path.Combine(root, "marker.txt"));
        var mutation = query?.StartsWith("mutation") == true;
        if (mutation)
        {
            var identities = Path.Combine(root, "harness-identities.jsonl");
            var issues = File.Exists(identities) ? File.ReadAllLines(identities).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetString()).ToHashSet() : [];
            var memberships = Path.Combine(root, "harness-items.jsonl");
            var items = File.Exists(memberships) ? File.ReadAllLines(memberships).ToHashSet() : [];
            // Project automation can add a run-owned Issue before the product's add stage.
            if ((query!.Contains("ApplySelect") || query.Contains("ApplyClear")) && value.TryGetProperty("itemId", out var candidate))
            {
                var check = new ProcessStartInfo("C:\\Program Files\\GitHub CLI\\gh.exe") { UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG" }) check.Environment.Remove(name);
                foreach (var arg in new[] { "api", "graphql", "--hostname", "github.com", "--input", "-" }) check.ArgumentList.Add(arg);
                using var verify = Process.Start(check)!;
                var read = verify.StandardOutput.ReadToEndAsync(); var errors = verify.StandardError.ReadToEndAsync();
                await verify.StandardInput.WriteAsync(JsonSerializer.Serialize(new { query = "query($id:ID!){node(id:$id){... on ProjectV2Item{id project{id} content{... on Issue{id}}}}}", variables = new { id = candidate.GetString() } }));
                verify.StandardInput.Close(); await verify.WaitForExitAsync(); await errors;
                if (verify.ExitCode == 0)
                {
                    using var verified = JsonDocument.Parse(await read);
                    var node = verified.RootElement.GetProperty("data").GetProperty("node");
                    if (node.ValueKind == JsonValueKind.Object && node.GetProperty("project").GetProperty("id").GetString() == "PVT_kwHOBGPKL84BjFYc"
                        && issues.Contains(node.GetProperty("content").GetProperty("id").GetString())) items.Add(candidate.GetString()!);
                }
            }
            var allowed = query!.Contains("CreateWorkspaceIssue")
                ? value.GetProperty("repositoryId").GetString() == "R_kgDOUVKgAw" && value.GetProperty("title").GetString()!.StartsWith(marker, StringComparison.Ordinal)
                : value.TryGetProperty("projectId", out var project) && project.GetString() == "PVT_kwHOBGPKL84BjFYc"
                    && (query.Contains("AddWorkspaceIssue") ? issues.Contains(value.GetProperty("contentId").GetString())
                        : (query.Contains("ApplySelect") || query.Contains("ApplyClear")) && items.Contains(value.GetProperty("itemId").GetString()!));
            if (!allowed) return 2;
            File.AppendAllText(Path.Combine(root, "product-intents.jsonl"), JsonSerializer.Serialize(new { query, input = value }) + "\n");
        }
        var start = new ProcessStartInfo("C:\\Program Files\\GitHub CLI\\gh.exe") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_DEBUG", "GHPB_CREATION_PROXY" }) start.Environment.Remove(name);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        if (input is not null) await p.StandardInput.WriteAsync(input); p.StandardInput.Close();
        await p.WaitForExitAsync(); var stdout = await output;
        if (mutation && query!.Contains("AddWorkspaceIssue"))
        {
            try
            {
                var offset = stdout.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                using var response = JsonDocument.Parse(stdout[(offset + 4)..]);
                var item = response.RootElement.GetProperty("data").GetProperty("addProjectV2ItemById").GetProperty("item");
                File.AppendAllText(Path.Combine(root, "harness-items.jsonl"), item.GetProperty("id").GetString() + "\n");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        if (mutation && query!.Contains("CreateWorkspaceIssue"))
        {
            try
            {
                var offset = stdout.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                using var response = JsonDocument.Parse(stdout[(offset + 4)..]);
                var issue = response.RootElement.GetProperty("data").GetProperty("createIssue").GetProperty("issue");
                File.AppendAllText(Path.Combine(root, "harness-identities.jsonl"), issue.GetRawText() + "\n");
                if (value.GetProperty("title").GetString() == marker + " Interrupted") return 1;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        await Console.Out.WriteAsync(stdout); await Console.Error.WriteAsync(await error); return p.ExitCode;
    }
}
