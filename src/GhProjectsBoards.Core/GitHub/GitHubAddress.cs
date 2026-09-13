using System.Text.RegularExpressions;

namespace GhProjectsBoards.App.GitHub;

internal static class GitHubAddress
{
    public static bool TryHost(string? value, out string host)
    {
        host = value?.Trim().ToLowerInvariant() ?? "";
        return host.Length is > 0 and <= 253
            && host.Split('.').All(label => label.Length is > 0 and <= 63
                && Regex.IsMatch(label, @"\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\z"));
    }
}
