namespace GhProjectsBoards.Core.Assist;

internal static class AssistEnvironment
{
    internal static Dictionary<string, string> Curate(IReadOnlyDictionary<string, string> source)
        => source.Where(p => !Excluded(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    private static bool Excluded(string name)
    {
        var key = name.ToUpperInvariant();
        return key.Contains("API_KEY", StringComparison.Ordinal) || key.Contains("TOKEN", StringComparison.Ordinal)
            || new[] { "COPILOT_", "GITHUB_COPILOT_", "OPENAI_", "ANTHROPIC_", "AZURE_OPENAI_", "OTEL_" }.Any(key.StartsWith)
            || key is "GH_HOST" or "GH_REPO" or "GITHUB_API_URL" or "GITHUB_SERVER_URL" or "NODE_OPTIONS" or "NODE_PATH";
    }
}

internal enum AssistFailureKind { Disabled, Authentication, WrongAccount, InvalidKey, Policy, Unreachable, UnknownModel, Runtime }
internal sealed record AssistFailure(AssistFailureKind Kind, string Message, string NextAction);
internal static class AssistAuthentication
{
    internal static AssistFailure? Validate(bool authenticated, string? authType, string? host, string? login, string connectedHost, string connectedLogin)
    {
        if (!authenticated || authType != "gh-cli") return new(AssistFailureKind.Authentication, "接続済みの gh アカウントを利用できません。gh のログインを確認してください。", "設定を開く");
        if (!string.Equals(host, connectedHost, StringComparison.OrdinalIgnoreCase) || !string.Equals(login, connectedLogin, StringComparison.OrdinalIgnoreCase))
            return new(AssistFailureKind.WrongAccount, "AI アシストのアカウントが、接続中のアカウントと一致しません。", "設定を開く");
        return null;
    }
}
