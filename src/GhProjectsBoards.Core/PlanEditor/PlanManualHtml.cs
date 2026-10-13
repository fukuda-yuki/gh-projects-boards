using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanManualHtml
{
    private static readonly Regex Heading = new(@"^(#{1,6}) +(.+)$");
    private static readonly Regex ListItem = new(@"^( *)([-+*]|\d+\.) +(.+)$");
    private static readonly Regex Inline = new(@"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|(?<image>!)?\[(?<label>[^\]]*)\]\((?<url>[^\s)]+)\)");

    internal static string Render(string markdown, string manualDirectory)
    {
        var directory = new Uri(Path.GetFullPath(manualDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var body = new StringBuilder();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length;) {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            var heading = Heading.Match(lines[i]);
            if (heading.Success) {
                var level = heading.Groups[1].Length;
                var text = heading.Groups[2].Value;
                var slug = Slug(text); var id = slug;
                for (var suffix = 1; !ids.Add(id); suffix++) id = slug + "-" + suffix;
                body.Append($"<h{level} id=\"{Escape(id)}\">{Format(text, directory)}</h{level}>\n");
                i++; continue;
            }
            if (IsTable(lines, i)) {
                body.Append("<table><thead><tr>");
                var headers = Cells(lines[i]);
                foreach (var cell in headers) body.Append("<th>" + Format(cell, directory) + "</th>");
                body.Append("</tr></thead><tbody>"); i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) {
                    var cells = Cells(lines[i++]);
                    body.Append("<tr>");
                    for (var column = 0; column < headers.Length; column++)
                        body.Append("<td>" + Format(column < cells.Length ? cells[column] : "", directory) + "</td>");
                    body.Append("</tr>");
                }
                body.Append("</tbody></table>\n"); continue;
            }
            var item = ListItem.Match(lines[i]);
            if (item.Success) { WriteList(body, lines, ref i, item.Groups[1].Length, directory); continue; }
            body.Append("<p>");
            var first = true;
            do {
                if (!first) body.Append("<br />\n");
                body.Append(Format(lines[i++].TrimEnd(), directory)); first = false;
            } while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !Heading.IsMatch(lines[i]) && !ListItem.IsMatch(lines[i]) && !IsTable(lines, i));
            body.Append("</p>\n");
        }
        return """
            <!DOCTYPE html>
            <html lang="ja"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>プロジェクトの計画と設定</title>
            <style>
            body { font-family: 'Yu Gothic UI', 'Segoe UI', sans-serif; max-width: 960px; margin: 32px auto; padding: 0 24px; line-height: 1.8; color: #242424; background: #fff; overflow-wrap: anywhere; }
            h1, h2, h3 { line-height: 1.4; margin-top: 1.8em; } h2 { border-bottom: 1px solid #ccc; padding-bottom: .35em; }
            a { color: #005a9e; } a:focus-visible { outline: 2px solid currentColor; }
            table { border-collapse: collapse; width: 100%; margin: 1em 0; } th, td { border: 1px solid #bbb; padding: 8px 12px; text-align: left; vertical-align: top; } th { background: #f2f4f6; }
            img { max-width: 100%; height: auto; border: 1px solid #ddd; box-sizing: border-box; } code { background: #f2f4f6; padding: .1em .3em; } li { margin: .35em 0; }
            @media print { body { max-width: none; margin: 0; } img, tr { break-inside: avoid; } }
            </style></head><body>
            """ + body + "</body></html>";
    }

    private static void WriteList(StringBuilder html, string[] lines, ref int index, int indent, Uri directory, int depth = 0)
    {
        var first = ListItem.Match(lines[index]);
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        var tag = ordered ? "ol" : "ul";
        html.Append('<').Append(tag).Append('>');
        while (index < lines.Length) {
            var item = ListItem.Match(lines[index]);
            if (!item.Success || item.Groups[1].Length != indent || char.IsDigit(item.Groups[2].Value[0]) != ordered) break;
            html.Append("<li>").Append(Format(item.Groups[3].Value, directory)); index++;
            while (index < lines.Length) {
                var nested = ListItem.Match(lines[index]);
                if (!nested.Success || nested.Groups[1].Length <= indent || depth >= 1) break;
                WriteList(html, lines, ref index, nested.Groups[1].Length, directory, depth + 1);
            }
            html.Append("</li>");
        }
        html.Append("</").Append(tag).Append(">\n");
    }

    private static bool IsTable(string[] lines, int index) => index + 1 < lines.Length && lines[index].TrimStart().StartsWith('|')
        && lines[index + 1].TrimStart().StartsWith('|') && Cells(lines[index + 1]).All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"));

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    private static string Format(string text, Uri directory)
    {
        var html = new StringBuilder(); var offset = 0;
        foreach (Match token in Inline.Matches(text)) {
            html.Append(Escape(text[offset..token.Index]));
            if (token.Groups["code"].Success) html.Append("<code>" + Escape(token.Groups["code"].Value) + "</code>");
            else if (token.Groups["bold"].Success) html.Append("<strong>" + Format(token.Groups["bold"].Value, directory) + "</strong>");
            else {
                var label = token.Groups["label"].Value;
                var target = Resolve(token.Groups["url"].Value, directory);
                if (target is null) html.Append(Escape(label));
                else if (token.Groups["image"].Success) html.Append($"<img src=\"{Escape(target)}\" alt=\"{Escape(label)}\" />");
                else html.Append($"<a href=\"{Escape(target)}\">{Escape(label)}</a>");
            }
            offset = token.Index + token.Length;
        }
        return html.Append(Escape(text[offset..])).ToString();
    }

    private static string? Resolve(string target, Uri directory)
    {
        if (target.StartsWith('#')) return target;
        if (!Uri.TryCreate(directory, target, out var uri)) return null;
        return uri.Scheme is "file" or "https" or "http" ? uri.AbsoluteUri : null;
    }

    private static string Slug(string text)
    {
        var plain = Inline.Replace(text, m => m.Groups["code"].Success ? m.Groups["code"].Value
            : m.Groups["bold"].Success ? m.Groups["bold"].Value : m.Groups["label"].Value);
        var slug = new StringBuilder();
        foreach (var rune in plain.EnumerateRunes()) {
            if (rune.Value == ' ') slug.Append('-');
            else if (rune.Value is >= 'A' and <= 'Z') slug.Append((char)(rune.Value + 'a' - 'A'));
            else if (Rune.IsLetterOrDigit(rune)) slug.Append(rune);
        }
        return slug.ToString();
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
