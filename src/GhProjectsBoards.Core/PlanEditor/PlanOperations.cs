using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanOperations
{
    internal static PlanTask TaskInput(PlanRow r, int index = 0) => new(r.Identity, index + 1)
    {
        Parent = r.Parent, Predecessors = r.Predecessors.ToArray(), Assignees = r.Assignees.ToArray(),
        Estimate = r.Estimate, Remaining = r.Remaining, Actual = r.Actual, Closed = r.Closed,
        Start = r.Start, End = r.End, StartNoEarlierThan = r.StartNoEarlierThan, Fixed = r.Fixed
    };
    internal static bool NeedsProgressDate(PlanRow before, PlanRow current, PlanField field)
    {
        if (field is not (PlanField.Start or PlanField.End) || TaskInput(before).KeepsDates) return false;
        var complete = TaskInput(current).IsComplete;
        // Automatic null inputs were not clear commands. Once work starts, its start becomes history.
        return field == PlanField.Start
            ? before.Start is null && current.Start is null && !(before.Actual > 0) && (complete || current.Actual > 0)
            : before.End is null && current.End is null && complete;
    }
    internal static IReadOnlyList<ScheduledTask> Schedule(PlanDocument document, DateOnly today)
    {
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var settings = document.State.Settings;
        return PlanScheduler.Calculate(document.State.Rows.Select((r, i) => TaskInput(r, i) with
        { GitHubStart = baseline.GetValueOrDefault(r.Identity)?.Start, GitHubEnd = baseline.GetValueOrDefault(r.Identity)?.End }).ToArray(),
            new PlanSettings { StatusDate = settings.StatusDate, ProjectStart = settings.ProjectStart,
                Calendar = new() { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() },
                People = settings.People.Select(p => new PlanPerson(p.Identity, p.Rate)).ToArray() }, today);
    }
    internal static bool RowEqual(PlanRow? a, PlanRow? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        a with { Assignees = b.Assignees, Predecessors = b.Predecessors } == b &&
        a.Assignees.SequenceEqual(b.Assignees) && a.Predecessors.SequenceEqual(b.Predecessors);
    internal static bool SettingsEqual(ProjectPlanSettings a, ProjectPlanSettings b) => ReferenceEquals(a, b) || PlanJson.Text(a) == PlanJson.Text(b);
    internal static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    internal static bool Repository(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
    private static void Unique<T>(ImmutableArray<T> values) where T : notnull
        => Require(!values.IsDefault && values.Distinct().Count() == values.Length, "値は重複のない一覧にしてください。");
    internal static void ValidateSettings(ProjectPlanSettings s)
    {
        Require(s is not null, "計画設定がありません。");
        Unique(s!.CompanyDaysOff); Unique(s.People.Select(p => p.Identity).ToImmutableArray());
        Unique(s.Columns.Select(c => c.Role).ToImmutableArray()); Unique(s.Columns.Select(c => c.FieldId).ToImmutableArray());
        Require(s.DefaultRepository is null || Repository(s.DefaultRepository), "リポジトリはowner/repositoryで指定してください。");
        foreach (var p in s.People)
        {
            Require(!string.IsNullOrWhiteSpace(p.Identity) && !string.IsNullOrWhiteSpace(p.Name) && p.Rate > 0 && p.Rate <= 100 && !(p.Allowance < 0), "担当者、稼働率または許容量が不正です。");
        }
        foreach (var c in s.Columns)
        {
            var type = c.Role switch { PlanField.Estimate or PlanField.Remaining or PlanField.Actual => "NUMBER",
                PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan => "DATE", PlanField.Fixed => "SINGLE_SELECT", _ => null };
            Require(type is not null && c.DataType == type && !string.IsNullOrWhiteSpace(c.FieldId) && !string.IsNullOrWhiteSpace(c.Name), "列の役割、識別子または型が不正です。");
        }
        if (s.ImportedHolidays is { } h)
        {
            Require(!string.IsNullOrWhiteSpace(h.Version) && !string.IsNullOrWhiteSpace(h.Source) &&
                h.SourceSha256 is not null && System.Text.RegularExpressions.Regex.IsMatch(h.SourceSha256, "^[a-fA-F0-9]{64}$") &&
                h.FirstYear >= 1 && h.LastYear <= 9999 && h.FirstYear <= h.LastYear, "祝日データの出典または対象年が不正です。");
            Unique(h.Dates.Select(d => d.Date).ToImmutableArray());
            Require(h.Dates.All(d => d.Date.Year >= h.FirstYear && d.Date.Year <= h.LastYear && !string.IsNullOrWhiteSpace(d.Name)), "祝日データが不正です。");
        }
    }
    internal static void ValidateDocument(PlanDocument d, DateOnly today)
    {
        Require(d.Project?.Scope is { ViewerId: > 0 } && !string.IsNullOrWhiteSpace(d.Project.NodeId) &&
            Uri.CheckHostName(d.Project.Scope.Host) != UriHostNameType.Unknown && d.Project.Scope.Host == d.Project.Scope.Host.ToLowerInvariant(), "プロジェクトの識別情報が不正です。");
        ValidateSettings(d.State.Settings);
        ValidateRows(d.Baseline.Rows); ValidateRows(d.State.Rows);
        Unique(d.Baseline.Columns.Select(c => c.Id).ToImmutableArray());
        Require(d.Baseline.Columns.All(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.DataType)), "列定義が不正です。");
        var currentIds = d.State.Rows.Select(r => r.Identity).ToHashSet();
        Require(d.Baseline.Rows.All(r => !r.Identity.StartsWith("local:", StringComparison.Ordinal) && currentIds.Contains(r.Identity)), "基準行を削除またはローカル行に変更できません。");
        var baselineIds = d.Baseline.Rows.Select(r => r.Identity).ToHashSet();
        Require(d.State.Rows.All(r => baselineIds.Contains(r.Identity) || IsLocal(r.Identity)), "新規行にはローカル識別子が必要です。");
        _ = Schedule(d, today);
    }
    private static void ValidateRows(ImmutableArray<PlanRow> rows)
    {
        Unique(rows.Select(r => r.Identity).ToImmutableArray());
        foreach (var r in rows)
        {
            Require(!string.IsNullOrWhiteSpace(r.Identity) && r.Title is not null && Repository(r.Repository), "行の識別子、タイトルまたはリポジトリが不正です。");
            Unique(r.Assignees); Unique(r.Predecessors);
            Require(r.CsvSourceHash is null || System.Text.RegularExpressions.Regex.IsMatch(r.CsvSourceHash, "^[a-f0-9]{64}$"), "CSVの識別情報が不正です。");
            Require(r.Assignees.Concat(r.Predecessors).All(x => !string.IsNullOrWhiteSpace(x)) && (r.Parent is null || !string.IsNullOrWhiteSpace(r.Parent)), "関係の識別子が不正です。");
        }
    }
    private static bool IsLocal(string id) => id.StartsWith("local:", StringComparison.Ordinal) && Guid.TryParseExact(id[6..], "N", out _);
    internal static (PlanState State, PlanOperationKind Kind) Apply(PlanDocument d, PlanCommand command, DateOnly today)
    {
        var state = d.State;
        var rows = state.Rows.ToList();
        var byId = rows.ToDictionary(r => r.Identity);
        PlanOperationKind kind;
        switch (command)
        {
            case FillPlanCells fill:
                Require(fill.Kind is PlanOperationKind.Fill or PlanOperationKind.CtrlD, "コピー操作の種類が不正です。");
                Require(byId.ContainsKey(fill.Source), "コピー元がありません。");
                return Apply(d, new EditPlanCells(fill.Kind, fill.Targets.SelectMany(id => fill.Fields.Select(f => new PlanCellChange(id, f, Value(byId[fill.Source], f)))).ToImmutableArray()), today);
            case ClearPlanCells clear:
                return Apply(d, new EditPlanCells(PlanOperationKind.Clear, clear.Targets.SelectMany(id => clear.Fields.Select(f => new PlanCellChange(id, f, null))).ToImmutableArray()), today);
            case EditPlanCells edit:
                kind = edit.Kind;
                Require(kind is PlanOperationKind.Cell or PlanOperationKind.Paste or PlanOperationKind.Fill or PlanOperationKind.CtrlD or PlanOperationKind.Clear, "セル操作の種類が不正です。");
                Require(edit.Cells.Select(c => (c.Identity, c.Field)).Distinct().Count() == edit.Cells.Length, "同じセルを重複して指定できません。");
                var summaries = rows.Where(r => r.Parent is not null).Select(r => r.Parent!).ToHashSet();
                foreach (var c in edit.Cells)
                {
                    Require(byId.ContainsKey(c.Identity), "編集先の行がありません。");
                    Require(!summaries.Contains(c.Identity) || c.Field is not (PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Start or PlanField.End or PlanField.StartNoEarlierThan or PlanField.Fixed), "集計行の工数と日付は子タスクから計算します。");
                    byId[c.Identity] = Edit(byId[c.Identity], c.Field, c.Value);
                }
                foreach (var group in edit.Cells.GroupBy(c => c.Identity))
                {
                    var priorRow = state.Rows.First(r => r.Identity == group.Key);
                    var edited = byId[group.Key];
                    var observed = d.Baseline.Rows.FirstOrDefault(r => r.Identity == group.Key);
                    foreach (var field in new[] { PlanField.Start, PlanField.End })
                        if (observed is not null && !group.Any(c => c.Field == field) &&
                            !d.Sync.Conflicts.Any(c => c.Identity == group.Key && c.Field == field) && NeedsProgressDate(priorRow, edited, field))
                            edited = PlanValues.Set(edited, field, PlanValues.Get(observed, field));
                    byId[group.Key] = edited;
                    var task = TaskInput(byId[group.Key]);
                    var previous = TaskInput(priorRow);
                    var validatePair = group.Any(c => c.Field is PlanField.Start or PlanField.End or PlanField.Fixed) || !previous.KeepsDates && task.KeepsDates;
                    Require(!validatePair || !task.KeepsDates || !(task.Start > task.End), "終了日は開始日以降にしてください。");
                }
                rows = rows.Select(r => byId[r.Identity]).ToList();
                break;
            case InsertPlanRows insert:
                kind = insert.Kind;
                Require(kind is PlanOperationKind.Insert or PlanOperationKind.CsvImport, "挿入操作の種類が不正です。");
                if (kind == PlanOperationKind.CsvImport)
                {
                    Require(insert.AllowDuplicateCsv || !insert.Rows.Any(r => r.CsvSourceHash is not null && rows.Any(old => old.CsvSourceHash == r.CsvSourceHash)), "同じCSVは追加済みです。重複して追加するか確認してください。");
                    state = state with { Settings = state.Settings with { People = state.Settings.People.AddRange(insert.CsvPeople.Where(p => !state.Settings.People.Any(old => old.Identity == p.Identity))) } };
                }
                var index = insert.Before is null ? rows.Count : rows.FindIndex(r => r.Identity == insert.Before);
                Require(index >= 0, "挿入先がありません。");
                var additions = insert.Rows.Select(r => r with { Repository = r.Repository.Length == 0 ? state.Settings.DefaultRepository ?? "" : r.Repository }).ToArray();
                Require(additions.All(r => IsLocal(r.Identity) && !(r.Estimate < 0 || r.Remaining < 0 || r.Actual < 0) && (!TaskInput(r).KeepsDates || !(r.Start > r.End))), "新規行の識別子、工数または日付が不正です。");
                rows.InsertRange(index, additions);
                break;
            case IndentPlanRows indent:
                kind = indent.Outdent ? PlanOperationKind.Outdent : PlanOperationKind.Indent;
                var selected = Selected(rows, indent.Targets);
                if (selected.Count == 0) return (state, kind);
                var parent = selected[0].Parent;
                Require(selected.All(r => r.Parent == parent), "同じ親のタスクを選択してください。");
                var siblings = rows.Where(r => r.Parent == parent).ToList();
                var first = siblings.FindIndex(r => r.Identity == selected[0].Identity);
                Require(siblings.Skip(first).Take(selected.Count).Select(r => r.Identity).SequenceEqual(selected.Select(r => r.Identity)), "連続するタスクを選択してください。");
                string? newParent;
                if (indent.Outdent)
                {
                    Require(parent is not null && byId.ContainsKey(parent), "親タスクがありません。");
                    newParent = byId[parent!].Parent;
                }
                else
                {
                    Require(first > 0, "直前の同階層タスクがありません。"); newParent = siblings[first - 1].Identity;
                }
                var ids = selected.Select(r => r.Identity).ToHashSet();
                rows = rows.Select(r => ids.Contains(r.Identity) ? r with { Parent = newParent } : r).ToList();
                break;
            case MovePlanRows move:
                kind = PlanOperationKind.Move;
                var moving = Selected(rows, move.Targets); var movingIds = moving.Select(r => r.Identity).ToHashSet();
                Require(move.Before is null || byId.ContainsKey(move.Before) && !movingIds.Contains(move.Before), "移動先は選択外の行にしてください。");
                rows.RemoveAll(r => movingIds.Contains(r.Identity));
                rows.InsertRange(move.Before is null ? rows.Count : rows.FindIndex(r => r.Identity == move.Before), moving);
                break;
            case ReplacePlanSettings settings:
                kind = PlanOperationKind.Settings; state = state with { Settings = settings.Settings }; break;
            default: throw new ArgumentException("未対応の計画操作です。");
        }
        state = state with { Rows = rows.ToImmutableArray() };
        ValidateDocument(d with { State = state }, today);
        return (state, kind);
    }
    private static List<PlanRow> Selected(List<PlanRow> rows, ImmutableArray<string> ids)
    {
        Unique(ids); var set = ids.ToHashSet(); var result = rows.Where(r => set.Contains(r.Identity)).ToList();
        Require(result.Count == ids.Length, "選択行がありません。"); return result;
    }
    internal static object? Value(PlanRow r, PlanField f) => f switch
    {
        PlanField.Title => r.Title, PlanField.Repository => r.Repository, PlanField.Status => r.Status, PlanField.Closed => r.Closed,
        PlanField.Assignees => r.Assignees, PlanField.Predecessors => r.Predecessors, PlanField.Parent => r.Parent,
        PlanField.Estimate => r.Estimate, PlanField.Remaining => r.Remaining, PlanField.Actual => r.Actual,
        PlanField.Start => r.Start, PlanField.End => r.End, PlanField.StartNoEarlierThan => r.StartNoEarlierThan, PlanField.Fixed => r.Fixed,
        _ => throw new ArgumentException("セルとして編集できない項目です。")
    };
    private static T? Optional<T>(object? value) where T : struct => value is null ? null : value is T typed ? typed : throw new ArgumentException("セルの値の型が不正です。");
    private static string? String(object? value) => value is null ? null : value as string ?? throw new ArgumentException("文字列を指定してください。");
    private static ImmutableArray<string> Ids(object? value) => value is null ? [] : value is IEnumerable<string> ids ? ids.ToImmutableArray() : throw new ArgumentException("識別子一覧を指定してください。");
    private static PlanRow Edit(PlanRow r, PlanField field, object? value)
    {
        var task = TaskInput(r);
        switch (field)
        {
            case PlanField.Estimate: task = PlanEdits.Estimate(task, Optional<decimal>(value)); return r with { Estimate = task.Estimate, Remaining = task.Remaining };
            case PlanField.Remaining: return r with { Remaining = PlanEdits.Remaining(task, Optional<decimal>(value)).Remaining };
            case PlanField.Actual: return r with { Actual = PlanEdits.Actual(task, Optional<decimal>(value)).Actual };
            // Validate the final pair after the entire paste, not between its endpoint cells.
            case PlanField.Start: task = PlanEdits.Start(task with { End = null }, Optional<DateOnly>(value)); return r with { Start = task.Start, StartNoEarlierThan = task.StartNoEarlierThan };
            case PlanField.End: task = PlanEdits.End(task with { Start = null }, Optional<DateOnly>(value)); return r with { End = task.End, Fixed = task.Fixed };
            case PlanField.Title: return r with { Title = String(value) ?? "" };
            case PlanField.Repository: return r with { Repository = String(value) ?? "" };
            case PlanField.Status: return r with { Status = String(value) };
            case PlanField.Parent: return r with { Parent = String(value) };
            case PlanField.Assignees: return r with { Assignees = Ids(value) };
            case PlanField.Predecessors: return r with { Predecessors = Ids(value) };
            case PlanField.StartNoEarlierThan: return r with { StartNoEarlierThan = Optional<DateOnly>(value) };
            case PlanField.Fixed: return r with { Fixed = Optional<bool>(value) ?? false };
            default: throw new ArgumentException("この項目は編集できません。");
        }
    }
    internal static PlanPatch Difference(PlanState before, PlanState after, PlanOperationKind kind)
    {
        var old = before.Rows.ToDictionary(r => r.Identity); var current = after.Rows.ToDictionary(r => r.Identity);
        var changes = old.Keys.Union(current.Keys).Where(id => !RowEqual(old.GetValueOrDefault(id), current.GetValueOrDefault(id)))
            .Select(id => new PlanRowChange(id, old.GetValueOrDefault(id), current.GetValueOrDefault(id))).ToImmutableArray();
        var beforeOrder = before.Rows.Select(r => r.Identity).ToImmutableArray(); var afterOrder = after.Rows.Select(r => r.Identity).ToImmutableArray();
        var sameOrder = beforeOrder.SequenceEqual(afterOrder); var sameSettings = SettingsEqual(before.Settings, after.Settings);
        return new(kind, changes, sameOrder ? null : beforeOrder, sameOrder ? null : afterOrder,
            sameSettings ? null : before.Settings, sameSettings ? null : after.Settings);
    }
    internal static PlanState Replay(PlanState state, PlanPatch patch, bool forward)
    {
        Require(Enum.IsDefined(patch.Kind) && !patch.Rows.IsDefault, "操作履歴が不正です。");
        var expectedOrder = forward ? patch.BeforeOrder : patch.AfterOrder; var nextOrder = forward ? patch.AfterOrder : patch.BeforeOrder;
        var expectedSettings = forward ? patch.BeforeSettings : patch.AfterSettings; var nextSettings = forward ? patch.AfterSettings : patch.BeforeSettings;
        Require(expectedOrder.HasValue == nextOrder.HasValue && (expectedSettings is null) == (nextSettings is null), "操作履歴が不完全です。");
        var order = state.Rows.Select(r => r.Identity).ToImmutableArray();
        Require(expectedOrder is null || order.SequenceEqual(expectedOrder.Value), "操作履歴の行順が一致しません。");
        Require(expectedSettings is null || SettingsEqual(state.Settings, expectedSettings), "操作履歴の設定が一致しません。");
        var rows = state.Rows.ToDictionary(r => r.Identity); var seen = new HashSet<string>();
        foreach (var change in patch.Rows)
        {
            var expected = forward ? change.Before : change.After; var next = forward ? change.After : change.Before;
            Require(seen.Add(change.Identity) && (expected is not null || next is not null) &&
                (expected is null || expected.Identity == change.Identity) && (next is null || next.Identity == change.Identity) &&
                RowEqual(rows.GetValueOrDefault(change.Identity), expected), "操作履歴の行が一致しません。");
            if (next is null) rows.Remove(change.Identity); else rows[change.Identity] = next;
        }
        order = nextOrder ?? order;
        Require(order.Length == rows.Count && order.Distinct().Count() == order.Length && order.All(rows.ContainsKey), "操作履歴の行順が不正です。");
        return new(order.Select(id => rows[id]).ToImmutableArray(), nextSettings ?? state.Settings);
    }
    internal static PlanBaseline ReplayBaseline(PlanBaseline baseline, PlanPatch patch, bool forward)
    {
        Require(!patch.DiscardedRows.IsDefault, "操作履歴の利用不可行が不正です。");
        var discarded = patch.DiscardedRows.Select(r => r.Identity).ToHashSet();
        return baseline with { Rows = forward ? baseline.Rows.Where(r => !discarded.Contains(r.Identity)).ToImmutableArray()
            : baseline.Rows.AddRange(patch.DiscardedRows.Where(r => !baseline.Rows.Any(current => current.Identity == r.Identity))) };
    }
    internal static bool IsSummaryEffort(bool summary, PlanField field) => summary && field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual;
    internal static bool IsLocalConstraint(PlanField field, ProjectPlanSettings settings)
        => field is (PlanField.StartNoEarlierThan or PlanField.Fixed) && !settings.Columns.Any(c => c.Role == field);
    internal static ImmutableArray<string> PreviousSiblingOrder(PlanDocument document, string parent)
        => document.Sync.NativeOrders.GetValueOrDefault(parent,
            document.Baseline.Rows.Where(r => r.Parent == parent).Select(r => r.Identity).ToImmutableArray())
            .Where(id => document.State.Rows.Any(r => r.Identity == id)).ToImmutableArray();
    internal static PlanUnpublished Changes(PlanDocument d, DateOnly today)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<PlanField>>();
        var recalculated = ImmutableHashSet.CreateBuilder<(string Identity, PlanField Field)>();
        var baseline = d.Baseline.Rows.ToDictionary(r => r.Identity);
        var oldOrder = d.Baseline.Rows.Select(r => r.Identity).ToArray();
        var order = d.State.Rows.Where(r => baseline.ContainsKey(r.Identity)).Select(r => r.Identity).ToArray();
        var moved = order.Where((id, i) => id != oldOrder[i]).ToHashSet();
        foreach (var calculated in Schedule(d, today))
        {
            var r = d.State.Rows[calculated.Input.RowId - 1];
            if (!baseline.TryGetValue(r.Identity, out var old)) { result[r.Identity] = [PlanField.NewTask]; continue; }
            var fields = ImmutableArray.CreateBuilder<PlanField>();
            foreach (var field in new[] { PlanField.Title, PlanField.Repository, PlanField.Status, PlanField.Closed, PlanField.Parent,
                PlanField.Estimate, PlanField.Remaining, PlanField.Actual, PlanField.StartNoEarlierThan, PlanField.Fixed })
                if (!IsLocalConstraint(field, d.State.Settings) && !IsSummaryEffort(calculated.IsSummary, field) && !Equals(Value(r, field), Value(old, field))) fields.Add(field);
            if (!r.Assignees.ToHashSet().SetEquals(old.Assignees)) fields.Add(PlanField.Assignees);
            if (!r.Predecessors.ToHashSet().SetEquals(old.Predecessors)) fields.Add(PlanField.Predecessors);
            // Historical inputs survive publication; automatic dates can coincidentally match them.
            // Entered dates need a kept origin or an explicit start constraint that actually won.
            if (calculated.Start.Value != old.Start)
            {
                fields.Add(PlanField.Start);
                var kept = calculated.Start.Origin == DateOrigin.Kept && calculated.Start.Value == r.Start;
                var specified = r.StartNoEarlierThan is { } constraint && calculated.Start.Value == constraint
                    && calculated.StartReason == "開始日指定 " + constraint.ToString("M/d", CultureInfo.InvariantCulture);
                if (calculated.IsSummary || !(kept || specified)) recalculated.Add((r.Identity, PlanField.Start));
            }
            if (calculated.End.Value != old.End)
            {
                fields.Add(PlanField.End);
                if (calculated.IsSummary || calculated.End.Origin != DateOrigin.Kept || calculated.End.Value != r.End)
                    recalculated.Add((r.Identity, PlanField.End));
            }
            if (moved.Contains(r.Identity)) fields.Add(PlanField.Order);
            var children = d.State.Rows.Where(child => child.Parent == r.Identity).Select(child => child.Identity).ToArray();
            if (children.Length > 1 && !PreviousSiblingOrder(d, r.Identity).SequenceEqual(children)) fields.Add(PlanField.SubIssueOrder);
            if (fields.Count > 0) result[r.Identity] = fields.ToImmutable();
        }
        return new(result.ToImmutable()) { RecalculatedDates = recalculated.ToImmutable() };
    }
}
