using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Tests;

// The only live runner used by #51. Metadata is durable; CLI streams/tokens are never logged.
internal sealed class LivePerformanceRunner(string root, string marker, IGhProcessRunner? inner = null) : IGhProcessRunner
{
    private readonly IGhProcessRunner process = inner ?? new GhProcessRunner();
    private readonly HashSet<string> createTitles = [];
    private readonly Dictionary<string, HashSet<string>> titles = [];
    private readonly Dictionary<string, string> items = [];
    private long lastMutation;
    private int inFlight;
    public int Mutations { get; private set; }
    public int MutationCeiling { get; init; } = 480;
    public string[] LedgerRoots { get; init; } = [];
    public string? CooldownPath { get; init; }
    public bool Stopped { get; private set; }
    public string? StopReason { get; private set; }
    public Action? OnStop { get; set; }
    public List<ProcessRecord> Records { get; } = [];
    public sealed record ProcessRecord(string Kind, long Start, long End, bool Started, ProcessCompletion Completion,
        int? ExitCode, int? HttpStatus, Dictionary<string, string> Quota, int RequestBytes, int ResponseBytes, int InFlight, bool Throttled, string? StopReason,
        string? RequestId, string[] ErrorTypes, string[] Messages);
    public void AllowCreate(string title) { LivePerformanceRun.Require(title.StartsWith(marker + " ") && title.EndsWith(" baseline"), "Invalid fixture title"); createTitles.Add(title); }
    public void AllowIssue(string id, string original, string changed)
    {
        LivePerformanceRun.Require(!string.IsNullOrWhiteSpace(id) && original.StartsWith(marker + " ") && changed.StartsWith(marker + " "), "Invalid owned Issue");
        titles[id] = [original, changed];
    }
    public void AllowItem(string id, string issue)
    {
        LivePerformanceRun.Require(!string.IsNullOrWhiteSpace(id) && titles.ContainsKey(issue), "Unowned Project item"); items[id] = issue;
    }
    public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
    {
        if (Stopped) return new(ProcessCompletion.Cancelled);
        LivePerformanceRun.Require(command.Executable == LivePerformanceRun.Gh, "Unexpected live executable");
        var args = command.Arguments;
        LivePerformanceRun.Require(args.Count > 0, "Empty live command");
        if (args[0] != "--version")
        {
            var host = args.ToList().IndexOf("--hostname");
            LivePerformanceRun.Require(host >= 0 && host + 1 < args.Count && args[host + 1] == "github.com", "Unexpected live host");
        }
        var mutation = false; string? operation = null; string? identity = null;
        var kind = args[0] == "--version" ? "version" : args[0] == "auth" ? "auth" : args.Contains("user") ? "identity" : "query";
        if (args[0] == "api")
        {
            var methodIndex = args.ToList().IndexOf("--method");
            LivePerformanceRun.Require(methodIndex >= 0 && methodIndex + 1 < args.Count, "Missing explicit method");
            if (args[1] == "graphql")
            {
                using var document = JsonDocument.Parse(command.StandardInput!);
                var query = document.RootElement.GetProperty("query").GetString()!;
                mutation = query.TrimStart().StartsWith("mutation", StringComparison.Ordinal);
                if (mutation)
                {
                    var variables = document.RootElement.GetProperty("variables");
                    operation = Regex.Match(query, @"^\s*mutation\s+([A-Za-z0-9_]+)").Groups[1].Value;
                    switch (operation)
                    {
                        case "PerformanceAdd":
                            identity = variables.GetProperty("issue").GetString();
                            LivePerformanceRun.Require(variables.GetProperty("project").GetString() == LivePerformanceRun.ProjectId && titles.ContainsKey(identity!), "Add target outside owned sandbox fixtures"); break;
                        case "PerformanceRemove":
                            identity = variables.GetProperty("item").GetString();
                            LivePerformanceRun.Require(variables.GetProperty("project").GetString() == LivePerformanceRun.ProjectId && items.ContainsKey(identity!), "Removal target outside owned sandbox fixtures"); break;
                        case "PerformanceDelete":
                            identity = variables.GetProperty("issue").GetString();
                            LivePerformanceRun.Require(titles.ContainsKey(identity!), "Deletion target outside owned sandbox fixtures"); break;
                        case "ApplyTitle":
                            var input = variables.GetProperty("input"); identity = input.GetProperty("id").GetString();
                            LivePerformanceRun.Require(input.EnumerateObject().Count() == 2 && titles.TryGetValue(identity!, out var allowed)
                                && allowed.Contains(input.GetProperty("title").GetString()!), "Apply target/value outside approved title-only fixture"); break;
                        default: throw new InvalidOperationException("Unapproved live mutation");
                    }
                }
            }
            else if (args[methodIndex + 1] != "GET")
            {
                mutation = true; operation = "CreateFixture";
                LivePerformanceRun.Require(args[1] == LivePerformanceRun.Endpoint && args[methodIndex + 1] == "POST", "REST mutation outside sandbox fixture creation");
                using var payload = JsonDocument.Parse(command.StandardInput!);
                var title = payload.RootElement.GetProperty("title").GetString()!;
                LivePerformanceRun.Require(createTitles.Remove(title) && payload.RootElement.GetProperty("body").GetString()!.StartsWith(marker + " "), "Unapproved or repeated create");
                identity = title;
            }
        }
        else LivePerformanceRun.Require(args[0] is "auth" or "--version", "Unapproved live command");
        if (mutation)
        {
            kind = "mutation";
            LivePerformanceRun.Require(MutationCeiling is > 0 and <= 480 && Mutations < MutationCeiling, "Local live mutation budget exhausted");
            var admission = LivePerformanceBudget.Check(LedgerRoots.Append(root), 1, DateTimeOffset.UtcNow);
            LivePerformanceRun.Require(admission.Allowed, "Rolling mutation budget exhausted");
            var elapsed = lastMutation == 0 ? 1000d : Stopwatch.GetElapsedTime(lastMutation).TotalMilliseconds;
            if (admission.LastMutation is { } previous) elapsed = Math.Min(elapsed, (DateTimeOffset.UtcNow - previous).TotalMilliseconds);
            if (elapsed < 1000) { using var wait = PerformanceTrace.Span("live-fixture-pacing-wait"); await Task.Delay(TimeSpan.FromMilliseconds(1000 - elapsed), cancellationToken); }
            Mutations++;
            Append("mutation-intents.jsonl", new { number = Mutations, operation, identity, at = DateTimeOffset.UtcNow });
            lastMutation = Stopwatch.GetTimestamp();
        }
        var start = Stopwatch.GetTimestamp();
        var concurrent = Interlocked.Increment(ref inFlight);
        GhProcessResult result;
        try { result = await process.RunAsync(command, cancellationToken); }
        finally { Interlocked.Decrement(ref inFlight); }
        var end = Stopwatch.GetTimestamp();
        var (status, quota, throttled, requestId, errorTypes) = Classify(result);
        var messages = SafeMessages(result);
        // auth status intentionally omits its content-bearing error field. Its failed state cannot
        // prove throttling; stop measurement rather than let the normal fallback send another read.
        if (throttled) StopReason = "RateLimited";
        else if (kind == "auth" && !AuthenticationSucceeded(result)) StopReason = "AuthenticationUnverified";
        // Unknown protocol errors may include an unclassified limit. Do not let a failed
        // measurement fall through into more sends or immediate cleanup mutations.
        // Read-only absence responses are expected when verifying owned Issue deletion.
        else if (args[0] == "api" && !(kind != "mutation" && status is 404 or 410)
            && (result.Completion != ProcessCompletion.Exited || result.ExitCode != 0 || status >= 400 || errorTypes.Length > 0 || !ValidProtocol(result.StandardOutput, status, args[1] == "graphql")))
            StopReason = "UnverifiedApiOutcome";
        var record = new ProcessRecord(kind, start, end, result.Started, result.Completion, result.ExitCode, status, quota,
            Encoding.UTF8.GetByteCount(command.StandardInput ?? ""), Encoding.UTF8.GetByteCount(result.StandardOutput), concurrent, throttled, StopReason, requestId, errorTypes, messages);
        Records.Add(record); Append("processes.jsonl", record);
        if (throttled && CooldownPath is not null)
        {
            var now = DateTimeOffset.UtcNow; var until = now.AddMinutes(1);
            if (long.TryParse(quota.GetValueOrDefault("retry-after"), out var seconds))
                try { until = now.AddSeconds(Math.Max(60, seconds)); } catch (ArgumentOutOfRangeException) { until = DateTimeOffset.MaxValue; }
            if (quota.GetValueOrDefault("x-ratelimit-remaining") == "0" && long.TryParse(quota.GetValueOrDefault("x-ratelimit-reset"), out var reset))
                try { var resetAt = DateTimeOffset.FromUnixTimeSeconds(reset); if (resetAt > until) until = resetAt; } catch (ArgumentOutOfRangeException) { until = DateTimeOffset.MaxValue; }
            using var file = new FileStream(CooldownPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, new { NotBefore = until, reason = "RateLimited", at = now }); file.Flush(true);
        }
        if (StopReason is not null) { Stopped = true; OnStop?.Invoke(); }
        return result;
    }
    private static bool AuthenticationSucceeded(GhProcessResult result)
    {
        if (result.Completion != ProcessCompletion.Exited || result.ExitCode != 0) return false;
        try
        {
            using var data = JsonDocument.Parse(result.StandardOutput);
            return data.RootElement.ValueKind == JsonValueKind.Array && data.RootElement.GetArrayLength() == 1
                && data.RootElement[0].TryGetProperty("state", out var state) && state.GetString() == "success";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }
    internal void RequirePrimaryBudget(JsonElement resources, int core, int graphql)
    {
        foreach (var (resource, required) in new[] { ("core", core), ("graphql", graphql) })
        {
            var latest = Records.LastOrDefault(r => r.Quota.GetValueOrDefault("x-ratelimit-resource") == resource);
            // The rate_limit body may disagree with the actual request headers. Both must
            // establish headroom; a missing header cannot be treated as unused quota.
            LivePerformanceRun.Require(latest is not null
                && int.TryParse(latest.Quota.GetValueOrDefault("x-ratelimit-remaining"), out var remaining) && remaining >= required
                && resources.GetProperty(resource).GetProperty("remaining").GetInt32() >= required,
                "Insufficient observed " + resource + " quota for fixed live budget");
        }
    }
    internal static (int? Status, Dictionary<string, string> Quota, bool Throttled, string? RequestId, string[] ErrorTypes) Classify(GhProcessResult result)
    {
        var output = result.StandardOutput;
        var match = Regex.Match(output, @"\AHTTP/\S+ (\d{3})(?:\s|$)");
        int? status = match.Success ? int.Parse(match.Groups[1].Value) : null;
        var separator = output.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separator < 0) { separator = output.IndexOf("\n\n", StringComparison.Ordinal); separatorLength = 2; }
        var headers = separator >= 0 ? output[..separator] : "";
        var quota = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "x-ratelimit-resource", "x-ratelimit-limit", "x-ratelimit-used", "x-ratelimit-remaining", "x-ratelimit-reset", "retry-after" })
        {
            var line = headers.Split('\n').Select(s => s.TrimEnd('\r')).FirstOrDefault(s => s.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                var value = line[(name.Length + 1)..].Trim();
                if (name == "x-ratelimit-resource" ? value is "core" or "graphql" : Regex.IsMatch(value, @"\A[0-9]{1,16}\z")) quota[name] = value;
                else if (name == "retry-after" && DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retryAt))
                    quota[name] = Math.Max(0, Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }
        }
        var errorText = new List<string>();
        var errorTypes = new List<string>();
        var requestIdHeader = headers.Split('\n').Select(s => s.TrimEnd('\r'))
            .FirstOrDefault(s => s.StartsWith("x-github-request-id:", StringComparison.OrdinalIgnoreCase));
        var requestId = requestIdHeader?[(requestIdHeader.IndexOf(':') + 1)..].Trim();
        if (requestId is not null && !Regex.IsMatch(requestId, @"\A[0-9A-Fa-f:]{1,128}\z")) requestId = null;
        if (result.ExitCode != 0) errorText.Add(result.StandardError);
        if (separator >= 0)
        {
            try
            {
                using var body = JsonDocument.Parse(output[(separator + separatorLength)..]);
                if (body.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (status >= 400 && body.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        errorText.Add(message.GetString()!);
                    if (body.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                        foreach (var error in errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
                        {
                            foreach (var field in new[] { "type", "message" })
                                if (error.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String) errorText.Add(value.GetString()!);
                            string? type = error.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String ? errorType.GetString() : null;
                            if (error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
                                && extensions.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                            {
                                type ??= code.GetString(); errorText.Add(code.GetString()!);
                            }
                            // Persist only protocol codes from a closed vocabulary. Error messages,
                            // unknown codes and successful response data may contain private content.
                            errorTypes.Add(type is "RATE_LIMITED" or "ABUSE_DETECTED" or "FORBIDDEN" or "NOT_FOUND"
                                or "UNPROCESSABLE" or "INTERNAL" or "INTERNAL_SERVER_ERROR" or "GRAPHQL_VALIDATION_FAILED"
                                or "SERVICE_UNAVAILABLE" or "MAX_NODE_LIMIT_EXCEEDED" ? type : "Unrecognized");
                        }
                }
            }
            catch (JsonException) { }
        }
        // Successful data can contain arbitrary Issue text describing an error. Only protocol
        // error locations are evidence of throttling; never classify returned user content.
        var transient = string.Join("\n", errorText);
        var throttled = status == 429 || quota.GetValueOrDefault("x-ratelimit-remaining") == "0" || status == 403 && quota.ContainsKey("retry-after")
            || transient.Contains("RATE_LIMITED", StringComparison.OrdinalIgnoreCase)
            || transient.Contains("ABUSE_DETECTED", StringComparison.OrdinalIgnoreCase)
            || transient.Contains("abuse detection", StringComparison.OrdinalIgnoreCase)
            || transient.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
            || transient.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase);
        return (status, quota, throttled, requestId, errorTypes.ToArray());
    }
    private void Append(string name, object value)
    {
        using var file = new FileStream(Path.Combine(root, name), FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"); file.Write(bytes); file.Flush(true);
    }

    private static string[] SafeMessages(GhProcessResult result)
    {
        var separator = result.StandardOutput.IndexOf("\r\n\r\n", StringComparison.Ordinal); var length = 4;
        if (separator < 0) { separator = result.StandardOutput.IndexOf("\n\n", StringComparison.Ordinal); length = 2; }
        if (separator < 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput[(separator + length)..]);
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return [];
            return errors.EnumerateArray().Take(4).Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                .Select(e => SafeLiveDiagnostic.Render(e.GetProperty("message").GetString())).ToArray();
        }
        catch (JsonException) { return []; }
    }

    private static bool ValidProtocol(string output, int? status, bool graphql)
    {
        if (status is not (>= 100 and <= 599)) return false;
        var separator = output.IndexOf("\r\n\r\n", StringComparison.Ordinal); var length = 4;
        if (separator < 0) { separator = output.IndexOf("\n\n", StringComparison.Ordinal); length = 2; }
        if (separator < 0) return false;
        try {
            using var body = JsonDocument.Parse(output[(separator + length)..]);
            return graphql ? body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                : body.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }
}
