using System.Globalization;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.App.GitHub;

internal static class TargetDiagnostics
{
    internal static Match? MatchUrl(ConnectionContext context, string value, string pattern)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Host != context.Host)
            return null;
        var match = Regex.Match(uri.AbsolutePath, pattern);
        return match.Success ? match : null;
    }

    internal static bool PositiveNumber(string text, out int number)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
}
