using System.Text;
using System.Text.Json;
using System.IO;

namespace GhProjectsBoards.Tests;

// This entry point is only in the test executable, never in the shipped app.
internal static class FakeGhProgram
{
    public static async Task<int> Main(string[] args)
    {
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

    private static void WriteHttp(object body) => Console.Write("HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n" + JsonSerializer.Serialize(body));
}
