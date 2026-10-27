using System.Globalization;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Reports;

// #271: one Project's items.csv, printed from the latest record only. Every item keeps its row (drafts and REDACTED
// items included), the identity columns come first, and each Project field that holds a value of its own adds one
// column after them. A built-in column repeats the Issue's data, which the item columns already print. Excel reads the
// file only with a BOM and CRLF; RFC 4180 quoting keeps a comma, quote or line break in a title inside one cell.
internal static class SiteCsv
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] FixedHeaders =
    [
        "種類", "タスク", "リポジトリ", "番号", "タイトル", "状態", "閉じた理由", "閉じた日",
        "着手日", "着手した人", "完了日", "完了した人",
        "親", "担当者", "ラベル", "マイルストーン", "Issue の種類", "ブロック元", "アーカイブ", "追加日", "URL",
    ];
    private static readonly char[] NeedsQuotes = ['"', ',', '\r', '\n'];
    private const string AutomatedActor = "自動（Project のワークフロー）";

    internal static byte[] Render(RawProject project, IReadOnlyList<RawItem> items, IReadOnlySet<string> taskIds,
        IReadOnlyList<string> notStartedStatuses)
    {
        var zone = RawDay.Zone(project);
        var notStarted = new HashSet<string>(notStartedStatuses, StringComparer.Ordinal);
        var fields = project.Fields.Where(field => RawDay.ValueTypes.Contains(field.DataType)).ToArray();
        var csv = new StringBuilder();
        AppendRow(csv, FixedHeaders.Concat(fields.Select(field => field.Name)));
        foreach (var item in InOrder(items))
        {
            var content = item.Content;
            var actual = Actual(item, zone, notStarted);
            // FixedHeaders order, then the Project fields in the record's order.
            var cells = new List<string?>(FixedHeaders.Length + fields.Length)
            {
                Kind(item.Type),
                taskIds.Contains(item.Id) ? "はい" : "いいえ",
                content?.Repository,
                content?.Number?.ToString(CultureInfo.InvariantCulture),
                content?.Title,
                State(content),
                content?.StateReason,
                RawDay.CloseDate(content, zone)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                actual.Start,
                actual.StartBy,
                actual.Finish,
                actual.FinishBy,
                content?.Parent,
                Joined(content?.Assignees),
                Joined(content?.Labels),
                content?.Milestone?.Title,
                content?.IssueType,
                Joined(content?.BlockedBy),
                item.Archived ? "はい" : "いいえ",
                TimeZoneInfo.ConvertTime(item.AddedAt, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                content?.Url,
            };
            foreach (var field in fields) cells.Add(RawDay.Value(item, field));
            AppendRow(csv, cells);
        }
        return [0xEF, 0xBB, 0xBF, .. Utf8.GetBytes(csv.ToString())];
    }

    private sealed record ActualDates(string? Start, string? StartBy, string? Finish, string? FinishBy)
    {
        internal static readonly ActualDates None = new(null, null, null, null);
    }

    // #308: 着手日 and 完了日 from the recorded Issue timeline, with the same rules the planning editor uses. The
    // latest Status change from not started to started decides the start while the Issue was open; the close and
    // reopen events bound that state, so the item-closed workflow's own Status write is not a start. 完了日 is the
    // latest close as completed while the Issue is closed, a close with no reason included; a reopen or a 中止
    // close gives none. Days are the record's time zone, as the close day already is. Items that are not Issues
    // keep no history and print blanks.
    private static ActualDates Actual(RawItem item, TimeZoneInfo zone, IReadOnlySet<string> notStarted)
    {
        if (item.Content is not { Type: "Issue" } content || item.Events.Count == 0) return ActualDates.None;
        RawEvent? start = null;
        var closed = false;
        foreach (var change in item.Events.OrderBy(change => change.At))
        {
            switch (change.Kind)
            {
                case "closed":
                    closed = true;
                    break;
                case "reopened":
                    closed = false;
                    break;
                case "status" when !closed:
                    if (NotStarted(change.From, notStarted) && !NotStarted(change.To, notStarted)) start = change;
                    break;
            }
        }
        var lastClose = content.State == "CLOSED"
            ? item.Events.Where(change => change.Kind is "closed" or "reopened").OrderBy(change => change.At).LastOrDefault()
            : null;
        var finish = lastClose is { Kind: "closed" } close && PlanActualDates.IsCompletedClose(close.StateReason) ? close : null;
        return new(Day(start, zone), Who(start), Day(finish, zone), Who(finish));
    }

    // No Status always means not started, whatever options a Project configures.
    private static bool NotStarted(string? option, IReadOnlySet<string> notStarted)
        => string.IsNullOrEmpty(option) || notStarted.Contains(option);

    private static string? Day(RawEvent? change, TimeZoneInfo zone)
        => change is null ? null : TimeZoneInfo.ConvertTime(change.At, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Who(RawEvent? change)
        => change is null ? null
            : change.Automated ? AutomatedActor
            : string.IsNullOrWhiteSpace(change.Actor) ? null : change.Actor;

    // Items with a repository and number sort by repository ordinal then number; drafts and REDACTED items, which
    // have neither, follow by item id. The order is total, so the same record always gives the same file.
    private static IEnumerable<RawItem> InOrder(IReadOnlyList<RawItem> items)
        => items
            .OrderBy(item => RawDay.Reference(item.Content) is null ? 1 : 0)
            .ThenBy(item => item.Content?.Repository ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Content?.Number ?? int.MaxValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal);

    private static string? Kind(string type) => type switch
    {
        "ISSUE" => "Issue",
        "PULL_REQUEST" => "PR",
        "DRAFT_ISSUE" => "下書き",
        "REDACTED" => "非公開",
        _ => null,
    };

    // A merged pull request also carries GitHub's CLOSED state; the merge is what happened to it, so it reads MERGED.
    private static string? State(RawContent? content)
        => content?.MergedAt is not null ? "MERGED" : content?.State;

    private static string? Joined(IReadOnlyList<string>? values)
        => values is { Count: > 0 } ? string.Join("; ", values) : null;

    private static void AppendRow(StringBuilder csv, IEnumerable<string?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) csv.Append(',');
            first = false;
            AppendCell(csv, cell);
        }
        csv.Append("\r\n");
    }

    private static void AppendCell(StringBuilder csv, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var text = LineBreaks(value);
        if (text.IndexOfAny(NeedsQuotes) < 0)
        {
            csv.Append(text);
            return;
        }
        csv.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
    }

    // RFC 4180 writes a line break inside a cell as CRLF. The record keeps a title as JSON text, which may hold a lone
    // LF, so normalize here: every line break in the file is then CRLF and no bare LF or CR is left.
    private static string LineBreaks(string value)
        => value.Contains('\r') || value.Contains('\n')
            ? value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n")
            : value;
}
