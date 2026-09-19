using System.Text.Json;
using System.IO;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.App.GitHub;

internal enum CredentialStore { Keyring, Plaintext, Unknown }

internal sealed record AuthenticationInfo(CredentialStore Store, string? StoragePath, IReadOnlySet<string>? Scopes)
{
    public bool? HasScope(string scope) => Scopes?.Contains(scope);
    public override string ToString() => $"Authentication storage: {Store}";
}

internal sealed class ConnectionContext(string host, long viewerId, string login, string executable)
{
    public string Host { get; } = host;
    public long ViewerId { get; } = viewerId;
    public string Login { get; } = login;
    public string Executable { get; } = executable;
    public bool IsInvalidated { get; private set; }
    public void Invalidate() => IsInvalidated = true;
}

internal sealed record ConnectionReport(ApiResult Result, string? Version = null,
    AuthenticationInfo? Authentication = null, ConnectionContext? Context = null)
{
    public bool IsConnected => Result.IsSuccess && Context is not null;
}

internal sealed class GhConnectionService(string executable, string host, IGhProcessRunner? processRunner = null, TimeSpan? timeout = null)
{
    private readonly IGhProcessRunner runner = new GhProjectsBoards.Core.Projects.PerformanceTrace.Runner(processRunner ?? new GhProcessRunner());
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ConnectionReport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try { await gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return new ConnectionReport(new ApiResult(ApiOutcome.Cancelled, FailureKind.Cancelled)); }
        try { return await ConnectCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<ConnectionReport> RecheckAsync(ConnectionContext context, CancellationToken cancellationToken = default)
    {
        try { await gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return new ConnectionReport(new ApiResult(ApiOutcome.Cancelled, FailureKind.Cancelled)); }
        try { return await RecheckCoreAsync(context, cancellationToken); }
        finally { gate.Release(); }
    }

    // Evidence lasts only through this immediate read. External gh credential changes
    // are not locked; the scoped reader also verifies the viewer in each response.
    internal async Task<ApiResult> RecheckAndReadAsync(ConnectionContext context, ApiRequest request, string requiredScope, CancellationToken cancellationToken)
    {
        if (request.IsMutation) return new(ApiOutcome.Failed, FailureKind.InvalidInput);
        try { await gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return new(ApiOutcome.Cancelled, FailureKind.Cancelled); }
        try
        {
            var check = await RecheckCoreAsync(context, cancellationToken);
            if (!check.IsConnected) return check.Result;
            if (check.Authentication?.Store != CredentialStore.Keyring) return new(ApiOutcome.Failed, FailureKind.UnknownCredentialStore);
            if (check.Authentication.HasScope(requiredScope) != true) return new(ApiOutcome.Failed, FailureKind.PermissionDenied);
            if (cancellationToken.IsCancellationRequested) return new(ApiOutcome.Cancelled, FailureKind.Cancelled);
            return await new GhApiTransport(runner, executable, timeout).SendAsync(context.Host, request, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<ConnectionReport> RecheckCoreAsync(ConnectionContext context, CancellationToken cancellationToken)
    {
        if (context.IsInvalidated || !GitHubAddress.TryHost(host, out var normalizedHost)
            || normalizedHost != context.Host || !executable.Equals(context.Executable, StringComparison.OrdinalIgnoreCase))
        {
            context.Invalidate();
            return new ConnectionReport(new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged), Context: context);
        }
        var current = await ConnectCoreAsync(cancellationToken);
        if (current.IsConnected && current.Context!.ViewerId != context.ViewerId)
        {
            context.Invalidate();
            return current with { Result = new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged), Context = context };
        }
        return current with { Context = context };
    }

    private async Task<ConnectionReport> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (!GitHubAddress.TryHost(host, out var normalizedHost))
            return new ConnectionReport(new ApiResult(ApiOutcome.Failed, FailureKind.InvalidInput));
        var versionResult = await runner.RunAsync(new GhCommand(executable, ["--version"], timeout: timeout), cancellationToken);
        if (versionResult.Completion != ProcessCompletion.Exited)
            return new ConnectionReport(GhApiTransport.ProcessFailure(versionResult));
        var version = Regex.Match(versionResult.StandardOutput, @"\Agh version (\d+\.\d+\.\d+)(?:\s|$)");
        if (versionResult.ExitCode != 0 || !version.Success)
            return new ConnectionReport(new ApiResult(ApiOutcome.Failed, FailureKind.InvalidResponse, versionResult.ExitCode));
        var (authenticationResult, authentication) = await ReadAuthenticationAsync(normalizedHost, cancellationToken);
        if (!authenticationResult.IsSuccess)
        {
            return new ConnectionReport(authenticationResult, version.Groups[1].Value, authentication);
        }
        var identity = await ReadIdentityAsync(normalizedHost, cancellationToken);
        if (!identity.Result.IsSuccess) return new ConnectionReport(identity.Result, version.Groups[1].Value, authentication);
        return new ConnectionReport(identity.Result, version.Groups[1].Value, authentication,
            new ConnectionContext(normalizedHost, identity.Id, identity.Login!, executable));
    }

    public async Task<ApiResult> SendAsync(ConnectionContext context, ApiRequest request, CancellationToken cancellationToken = default, string? requiredScope = null)
    {
        try { await gate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return new ApiResult(ApiOutcome.Cancelled, FailureKind.Cancelled); }
        try
        {
            if (context.IsInvalidated || !GitHubAddress.TryHost(host, out var normalizedHost)
                || normalizedHost != context.Host || !executable.Equals(context.Executable, StringComparison.OrdinalIgnoreCase))
            {
                context.Invalidate();
                return new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged);
            }
            // Keep preflight and the target operation together. A failed preflight
            // is a known non-dispatch, not an uncertain target mutation.
            var (authenticationResult, authentication) = await ReadAuthenticationAsync(normalizedHost, cancellationToken);
            if (!authenticationResult.IsSuccess) return authenticationResult;
            var identity = await ReadIdentityAsync(normalizedHost, cancellationToken);
            if (!identity.Result.IsSuccess) return identity.Result;
            if (identity.Id != context.ViewerId)
            {
                context.Invalidate();
                return new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged);
            }
            if (request.IsMutation && authentication!.Store != CredentialStore.Keyring)
                return new ApiResult(ApiOutcome.Failed,
                    authentication!.Store == CredentialStore.Plaintext
                        ? FailureKind.PlaintextCredentials : FailureKind.UnknownCredentialStore);
            if (request.IsMutation && requiredScope is not null && authentication!.HasScope(requiredScope) != true)
                return new ApiResult(ApiOutcome.Failed, FailureKind.PermissionDenied);
            if (cancellationToken.IsCancellationRequested) return new ApiResult(ApiOutcome.Cancelled, FailureKind.Cancelled);
            return await new GhApiTransport(runner, executable, timeout).SendAsync(normalizedHost, request, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<(ApiResult Result, AuthenticationInfo? Info)> ReadAuthenticationAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var result = await ReadAuthenticationMetadataAsync(normalizedHost, cancellationToken);
        // gh auth status uses one error state for invalid credentials and
        // some network failures. /user distinguishes them without exposing stderr.
        if (result.Result.Failure == FailureKind.AuthenticationExpired)
        {
            var identityFailure = await ReadIdentityAsync(normalizedHost, cancellationToken);
            if (!identityFailure.Result.IsSuccess) return (identityFailure.Result, null);
        }
        return result;
    }

    private async Task<(ApiResult Result, AuthenticationInfo? Info)> ReadAuthenticationMetadataAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var command = new GhCommand(executable,
            ["auth", "status", "--active", "--hostname", normalizedHost, "--json", "hosts", "--jq",
                "[.hosts[][] | {host,login,active,state,tokenSource,scopes}]"], timeout: timeout);
        var result = await runner.RunAsync(command, cancellationToken);
        if (result.Completion != ProcessCompletion.Exited) return (GhApiTransport.ProcessFailure(result), null);
        if (result.ExitCode != 0) return (new ApiResult(ApiOutcome.Failed, FailureKind.InvalidResponse, result.ExitCode), null);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                return (new ApiResult(ApiOutcome.Failed, FailureKind.InvalidResponse), null);
            if (json.RootElement.GetArrayLength() == 0)
                return (new ApiResult(ApiOutcome.Failed, FailureKind.NotLoggedIn, result.ExitCode), null);
            var entries = json.RootElement.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object
                && Text(entry, "host") == normalizedHost
                && entry.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True).ToArray();
            if (entries.Length != 1) return (new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged), null);
            var entry = entries[0];
            var state = Text(entry, "state");
            if (state != "success")
                return (state == "timeout" ? new ApiResult(ApiOutcome.TimedOut, FailureKind.TimedOut)
                    : new ApiResult(ApiOutcome.Failed, state == "error" ? FailureKind.AuthenticationExpired : FailureKind.InvalidResponse), null);

            var source = Text(entry, "tokenSource") ?? "";
            var fileStorage = !source.Any(char.IsControl) && Path.IsPathFullyQualified(source)
                && Path.GetFileName(source).Equals("hosts.yml", StringComparison.OrdinalIgnoreCase);
            var store = source == "keyring" ? CredentialStore.Keyring : fileStorage ? CredentialStore.Plaintext : CredentialStore.Unknown;
            var scopesText = Text(entry, "scopes");
            var scopes = string.IsNullOrWhiteSpace(scopesText) ? null
                : scopesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return (new ApiResult(ApiOutcome.Success), new AuthenticationInfo(store, fileStorage ? source : null, scopes));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return (new ApiResult(ApiOutcome.Failed, FailureKind.InvalidResponse, result.ExitCode), null);
        }
    }

    private async Task<(ApiResult Result, long Id, string? Login)> ReadIdentityAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var result = await new GhApiTransport(runner, executable, timeout).SendAsync(normalizedHost, ApiRequest.Rest("GET", "user"), cancellationToken);
        if (!result.IsSuccess) return (result, 0, null);
        if (result.Data is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number
            || !idValue.TryGetInt64(out var id) || id <= 0
            || Text(data, "login") is not { } login || !Regex.IsMatch(login, @"\A[A-Za-z0-9_-]{1,255}\z"))
            return (new ApiResult(ApiOutcome.Failed, FailureKind.InvalidResponse), 0, null);
        return (result, id, login);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
