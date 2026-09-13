using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace GhProjectsBoards.App.GitHub;

internal enum ApiOutcome { Success, Failed, Cancelled, TimedOut, Unknown }
internal enum FailureKind
{
    None, InvalidInput, MissingExecutable, StartFailed, NotLoggedIn, AuthenticationExpired,
    PermissionDenied, NotFoundOrInaccessible, RateLimited, Network, InvalidResponse, GraphQl,
    Cancelled, TimedOut, IdentityChanged, PlaintextCredentials, UnknownCredentialStore
}

internal sealed class ApiResult(ApiOutcome outcome, FailureKind failure = FailureKind.None,
    int? exitCode = null, int? httpStatus = null, JsonElement? data = null,
    IReadOnlyList<string>? graphQlErrors = null, TimeSpan? retryAfter = null, TimeSpan elapsed = default)
{
    public ApiOutcome Outcome { get; } = outcome;
    public FailureKind Failure { get; } = failure;
    public int? ExitCode { get; } = exitCode;
    public int? HttpStatus { get; } = httpStatus;
    public JsonElement? Data { get; } = data;
    public IReadOnlyList<string> GraphQlErrors { get; } = graphQlErrors ?? [];
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public TimeSpan Elapsed { get; } = elapsed;
    public bool IsSuccess => Outcome == ApiOutcome.Success;
    public override string ToString() => $"{Outcome}: {Failure}; HTTP {HttpStatus}; exit {ExitCode}";
}

internal sealed class ApiRequest
{
    private ApiRequest(string method, string endpoint, string? payload, bool isMutation, bool isGraphQl)
        => (Method, Endpoint, Payload, IsMutation, IsGraphQl) = (method, endpoint, payload, isMutation, isGraphQl);

    public string Method { get; }
    public string Endpoint { get; }
    public string? Payload { get; }
    public bool IsMutation { get; }
    public bool IsGraphQl { get; }

    public static ApiRequest Rest(string method, string endpoint, object? body = null)
    {
        method = method.ToUpperInvariant();
        var path = endpoint.Split('?')[0];
        if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE")
            || !Regex.IsMatch(endpoint, @"\A[a-zA-Z0-9][a-zA-Z0-9/_.-]*(?:\?[^\s#{}\\:]*)?\z")
            || path.Split('/').Any(part => part is ".." or "." or "")
            || path.Equals("graphql", StringComparison.OrdinalIgnoreCase)
            || (method == "GET" && body is not null))
            throw new ArgumentException("Use an explicit relative REST endpoint and a supported method; GraphQL uses its own factory.");
        return new(method, endpoint, body is null ? null : JsonSerializer.Serialize(body), method != "GET", false);
    }

    public static ApiRequest GraphQl(string document, object? variables = null)
    {
        var operation = Regex.Match(document, @"\A\s*(query|mutation)\b");
        if (!operation.Success && !document.TrimStart().StartsWith('{'))
            throw new ArgumentException("Start a GraphQL document with query, mutation, or a query selection set.");
        return new("POST", "graphql", JsonSerializer.Serialize(new { query = document, variables }),
            operation.Groups[1].Value == "mutation", true);
    }

    public override string ToString() => $"{Method} request (payload omitted)";
}

internal sealed class GhApiTransport(IGhProcessRunner runner, string executable, TimeSpan? timeout = null)
{
    public async Task<ApiResult> SendAsync(string host, ApiRequest request, CancellationToken cancellationToken = default)
    {
        if (!GitHubAddress.TryHost(host, out host)) return new ApiResult(ApiOutcome.Failed, FailureKind.InvalidInput);
        var arguments = new List<string> { "api", request.Endpoint, "--hostname", host, "--method", request.Method, "--include" };
        if (request.Payload is not null) arguments.AddRange(["--input", "-"]);
        var process = await runner.RunAsync(new GhCommand(executable, arguments, request.Payload, timeout), cancellationToken);
        if (process.Completion != ProcessCompletion.Exited) return ProcessFailure(process, request.IsMutation);
        var separator = process.StandardOutput.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var separatorLength = 4;
        if (separator < 0)
        {
            separator = process.StandardOutput.IndexOf("\n\n", StringComparison.Ordinal);
            separatorLength = 2;
        }
        var statusMatch = Regex.Match(process.StandardOutput, @"\AHTTP/\S+ (\d{3})(?:\s|$)");
        if (separator < 0 || !statusMatch.Success)
            return new ApiResult(request.IsMutation && process.Started ? ApiOutcome.Unknown : ApiOutcome.Failed,
                process.ExitCode == 0 ? FailureKind.InvalidResponse : FailureKind.Network, process.ExitCode, elapsed: process.Elapsed);
        var status = int.Parse(statusMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var headers = process.StandardOutput[..separator];
        var retryAfter = ReadRetryAfter(Header(headers, "Retry-After"));
        if (Header(headers, "X-RateLimit-Remaining") == "0" && long.TryParse(Header(headers, "X-RateLimit-Reset"), out var reset)
            && reset >= 0 && reset <= 253402300799)
        {
            var untilReset = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
            if (untilReset > (retryAfter ?? TimeSpan.Zero)) retryAfter = untilReset;
        }
        var body = process.StandardOutput[(separator + separatorLength)..];
        JsonElement? data = null;
        var validJson = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var json = JsonDocument.Parse(body);
                data = json.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            validJson = false;
        }
        if (status is < 200 or >= 300)
        {
            var failure = status switch
            {
                401 => FailureKind.AuthenticationExpired,
                403 when retryAfter is not null || Header(headers, "X-RateLimit-Remaining") == "0" => FailureKind.RateLimited,
                403 => FailureKind.PermissionDenied,
                404 or 410 => FailureKind.NotFoundOrInaccessible,
                429 => FailureKind.RateLimited,
                >= 500 => FailureKind.Network,
                _ => FailureKind.InvalidInput
            };
            return Result(request.IsMutation && status >= 500 ? ApiOutcome.Unknown : ApiOutcome.Failed, failure);
        }

        if (!validJson || (data is null && status != 204))
            return Result(request.IsMutation ? ApiOutcome.Unknown : ApiOutcome.Failed, FailureKind.InvalidResponse);

        if (request.IsGraphQl)
        {
            if (data is not { ValueKind: JsonValueKind.Object } root)
                return Result(request.IsMutation ? ApiOutcome.Unknown : ApiOutcome.Failed, FailureKind.InvalidResponse);
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                // Error messages can echo request data. Return classified codes;
                // callers may inspect transient data, but diagnostics never use raw messages.
                var codes = errors.EnumerateArray().Select(ErrorCode).ToArray();
                var partial = root.TryGetProperty("data", out var partialData) && partialData.ValueKind != JsonValueKind.Null;
                var failure = !partial && codes.All(code => code == "FORBIDDEN" || code == "INSUFFICIENT_SCOPES")
                    ? FailureKind.PermissionDenied
                    : !partial && codes.All(code => code == "NOT_FOUND") ? FailureKind.NotFoundOrInaccessible
                    : !partial && codes.All(code => code == "UNAUTHORIZED") ? FailureKind.AuthenticationExpired
                    : !partial && codes.All(code => code == "RATE_LIMITED") ? FailureKind.RateLimited : FailureKind.GraphQl;
                return new ApiResult(request.IsMutation && (partial || failure == FailureKind.GraphQl) ? ApiOutcome.Unknown : ApiOutcome.Failed,
                    failure, process.ExitCode, status, data, codes, retryAfter, process.Elapsed);
            }
            if (!root.TryGetProperty("data", out var graphData) || graphData.ValueKind != JsonValueKind.Object)
                return Result(request.IsMutation ? ApiOutcome.Unknown : ApiOutcome.Failed, FailureKind.InvalidResponse);
        }
        if (process.ExitCode != 0)
            return Result(request.IsMutation ? ApiOutcome.Unknown : ApiOutcome.Failed, FailureKind.Network);
        return Result(ApiOutcome.Success, FailureKind.None);

        ApiResult Result(ApiOutcome outcome, FailureKind failure)
            => new(outcome, failure, process.ExitCode, status, data, retryAfter: retryAfter, elapsed: process.Elapsed);
    }

    internal static ApiResult ProcessFailure(GhProcessResult process, bool mutation = false)
    {
        var failure = process.Completion switch
        {
            ProcessCompletion.NotFound => FailureKind.MissingExecutable,
            ProcessCompletion.StartFailed => FailureKind.StartFailed,
            ProcessCompletion.TimedOut => FailureKind.TimedOut,
            ProcessCompletion.Cancelled => FailureKind.Cancelled,
            _ => FailureKind.Network
        };
        var outcome = mutation && process.Started ? ApiOutcome.Unknown : failure switch
        {
            FailureKind.TimedOut => ApiOutcome.TimedOut,
            FailureKind.Cancelled => ApiOutcome.Cancelled,
            _ => ApiOutcome.Failed
        };
        return new ApiResult(outcome, failure, process.ExitCode, elapsed: process.Elapsed);
    }

    private static string? Header(string headers, string name)
        => headers.Split('\n').Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();

    private static TimeSpan? ReadRetryAfter(string? value)
    {
        if (double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return null;
    }

    private static string ErrorCode(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var code = type.GetString();
            if (code is "FORBIDDEN" or "INSUFFICIENT_SCOPES" or "NOT_FOUND" or "UNAUTHORIZED" or "RATE_LIMITED"
                or "UNPROCESSABLE" or "GRAPHQL_VALIDATION_FAILED" or "INTERNAL") return code;
        }
        return "GRAPHQL_ERROR";
    }
}
