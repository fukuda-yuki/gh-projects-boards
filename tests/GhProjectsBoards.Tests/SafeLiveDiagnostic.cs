using System.Text.RegularExpressions;

namespace GhProjectsBoards.Tests;

// Diagnostic output is a lossy rendering, never a raw service message. Unknown words,
// identifiers, numbers, credentials, quoted content and URLs have no persistence path.
internal static class SafeLiveDiagnostic
{
    private static readonly HashSet<string> Words = new(("a an the and or to from with without for of on in is are was were be been being "
        + "has have had cannot can not could failed failure error errors invalid unknown missing unavailable unsupported denied forbidden "
        + "permission permissions resource node global id issue project item field value already exists exist added add adding update updating "
        + "delete deleted remove removed request response query mutation process data result resolve resolved resolving input inputted "
        + "something went wrong while executing your please try again later could not be found must required read only "
        + "limit rate secondary exceeded abuse detected too many requests validation unprocessable internal server service "
        + "budget exhausted stopped blocked pending diagnostic correction containment evidence fixture preparation membership "
        + "unverified uncertain replay baseline changed mismatch incomplete complete snapshot identity owned unrelated quota "
        + "authentication scope version cancellation title unique duplicate content number maximum minimum than more less "
        + "this it its that operation at least one no valid attempted state allowed available invalidated held check retry").Split(' '), StringComparer.OrdinalIgnoreCase);

    internal static string Render(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "[no message]";
        // Cap work before tokenization; the renderer never emits an input substring unless
        // it is a complete allowlisted word. Long tokens are not truncated into safe words.
        if (message.Length > 8192) return "[message omitted: too long]";
        // A quoted tail is content, including mixed/unclosed quotes. Keep only the
        // diagnostic prefix rather than attempt to parse an arbitrary quoting dialect.
        var input = Regex.Replace(message, "[\"'`][\\s\\S]*", " [redacted] ");
        input = Regex.Replace(input, @"\b(?:Bearer|token|password|secret)\b[\s\S]*", " [redacted] ", RegexOptions.IgnoreCase);
        input = Regex.Replace(input, @"https?://\S+", " [redacted] ", RegexOptions.IgnoreCase);
        var output = new List<string>();
        foreach (Match token in Regex.Matches(input, @"[A-Za-z0-9_@./:+\-]+"))
        {
            var word = token.Value.TrimEnd('.', ':');
            var safe = Words.Contains(word) ? word.ToLowerInvariant() : "[redacted]";
            if (safe != "[redacted]" || output.LastOrDefault() != safe) output.Add(safe);
            if (output.Sum(w => w.Length + 1) >= 384) break;
        }
        return string.Join(" ", output).Length is > 0 and <= 400 ? string.Join(" ", output) : "[redacted]";
    }
}
