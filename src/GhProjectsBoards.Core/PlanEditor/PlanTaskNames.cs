using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GhProjectsBoards.Core.PlanEditor;

// #263: a task is named the way the team names it on GitHub, by its Issue number (MS Project's Unique ID),
// never by its row position, which renumbers on every insert or move. A row without an Issue is 新N,
// derived here in plan order and never stored: stored plans reject unknown properties.
internal sealed partial class PlanTaskNames
{
    internal const string OutsidePlan = "計画外";
    private static readonly ConditionalWeakTable<PlanDocument, PlanTaskNames> Cache = [];
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly List<string> positions = [];
    private readonly List<string> local = [];
    private readonly List<(string Identity, string Owner, string Repository, int Number)> issues = [];

    internal static readonly PlanTaskNames None = new();
    internal static PlanTaskNames Of(PlanDocument document) => Cache.GetValue(document, d => new(d));

    private PlanTaskNames() { }

    private PlanTaskNames(PlanDocument document)
    {
        foreach (var row in document.State.Rows)
        {
            positions.Add(row.Identity);
            if (!row.Identity.StartsWith("local:", StringComparison.Ordinal) && document.Sync.IssueLinks.TryGetValue(row.Identity, out var link)
                && IssueReference().Match(link.Caption) is { Success: true } reference)
                issues.Add((row.Identity, reference.Groups["owner"].Value, reference.Groups["repo"].Value,
                    int.Parse(reference.Groups["number"].Value, CultureInfo.InvariantCulture)));
            else { local.Add(row.Identity); names[row.Identity] = "新" + local.Count.ToString(CultureInfo.InvariantCulture); }
        }
        // Only Issue rows decide the form, so a local row in another repository never renames a published task.
        CarryRepository = issues.Select(i => i.Owner + "/" + i.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        foreach (var issue in issues)
            names[issue.Identity] = (CarryRepository ? issue.Repository : "") + "#" + issue.Number.ToString(CultureInfo.InvariantCulture);
    }

    // Whether Issue names read repo#N rather than #N.
    internal bool CarryRepository { get; }

    internal string this[string identity] => names.GetValueOrDefault(identity) ?? OutsidePlan;
    internal bool Contains(string identity) => names.ContainsKey(identity);
    // 先行 as the sheet shows and copies it, so pasting the text back names the same tasks.
    internal string List(IEnumerable<string> identities) => string.Join(", ", identities.Select(id => this[id]));
    // The scheduler keys a predecessor by its one-based plan position; only its display is a task name.
    internal string AtPosition(int position) => position >= 1 && position <= positions.Count ? this[positions[position - 1]] : OutsidePlan;
    internal string StartReason(string reason)
        => PositionReason().Match(reason) is { Success: true } match
            ? AtPosition(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) + " の終了後" : reason;
    // The task a `{row position} の終了後` reason names, for a caller that needs the task itself (#229).
    internal bool TryPredecessor(string reason, out string identity)
    {
        identity = "";
        if (PositionReason().Match(reason) is not { Success: true } match) return false;
        var position = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (position < 1 || position > positions.Count) return false;
        identity = positions[position - 1];
        return true;
    }

    // One entry of 先行 after #239 folding: N or #N, repo#N or owner/repo#N, or 新N. Nothing is linked by guess.
    internal string Resolve(string entry) => Resolve(entry, "先行タスク");
    // ジャンプ resolves the same grammar and reports it as タスク名 (#228).
    internal string Resolve(string entry, string subject)
    {
        if (LocalName().Match(entry) is { Success: true } localName)
        {
            var index = int.TryParse(localName.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return index >= 1 && index <= local.Count ? local[index - 1] : throw new ArgumentException($"{entry} はこの版にありません。");
        }
        if (Reference().Match(entry) is not { Success: true } reference
            || !int.TryParse(reference.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException($"{subject}は #524、repo#524 または 新2 の形で指定してください: " + entry);
        var owner = reference.Groups["owner"].Value; var repository = reference.Groups["repo"].Value;
        var carrying = issues.Where(i => i.Number == number
            && (repository.Length == 0 || string.Equals(i.Repository, repository, StringComparison.OrdinalIgnoreCase))
            && (owner.Length == 0 || string.Equals(i.Owner, owner, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (carrying.Length == 1) return carrying[0].Identity;
        var named = (repository.Length == 0 ? "" : (owner.Length == 0 ? "" : owner + "/") + repository) + "#" + number.ToString(CultureInfo.InvariantCulture);
        if (carrying.Length == 0) throw new ArgumentException($"{named} はこの版にありません。");
        throw new ArgumentException($"{named} は複数のリポジトリにあります: "
            + string.Join(", ", carrying.Select(i => i.Repository + "#" + number.ToString(CultureInfo.InvariantCulture))));
    }

    [GeneratedRegex(@"^(?<owner>[^/#]+)/(?<repo>[^/#]+)#(?<number>[0-9]+)$")]
    private static partial Regex IssueReference();
    [GeneratedRegex(@"^(?:(?:(?<owner>[^/#\s]+)/)?(?<repo>[^/#\s]+)#|#)?(?<number>[0-9]+)$")]
    private static partial Regex Reference();
    [GeneratedRegex(@"^新([0-9]+)$")]
    private static partial Regex LocalName();
    [GeneratedRegex(@"^([0-9]+) の終了後$")]
    private static partial Regex PositionReason();
}
