using System.Text.RegularExpressions;

namespace GhProjectsBoards.App;

internal static class AssistAnswerText
{
    internal static string Plain(string value)
    {
        var lines = new List<string>();
        foreach (var source in value.Replace("\r\n", "\n").Split('\n')) {
            var line = Regex.Replace(source, @"^\s*#{1,6}\s+", "").Replace("**", "");
            var trimmed = line.Trim();
            if (trimmed.Contains('|')) {
                var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"))) continue;
                line = string.Join(" / ", cells);
            }
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }
}
