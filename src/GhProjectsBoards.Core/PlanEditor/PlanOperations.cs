using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanOperations
{
    internal static PlanTask TaskInput(PlanRow r, int index = 0, PlanIssueHistory? history = null,
        IReadOnlyList<string>? notStarted = null, TimeZoneInfo? zone = null) => new(r.Identity, index + 1)
    {
        Parent = r.Parent, Predecessors = r.Predecessors.ToArray(), Assignees = r.Assignees.ToArray(),
        Estimate = r.Estimate, Remaining = r.Remaining, Actual = r.Actual, Closed = r.Closed, CloseDate = r.CloseDate,
        Cancelled = r.Cancelled, Removed = r.Removed,
        Start = r.Start, End = r.End,
        // #308: the records the sheet and the lateness case read. Resolution happens per calculation, so a
        // changed not-started option list applies without another remote read.
        ActualStart = PlanActualDates.Start(history, notStarted ?? PlanActualDates.DefaultNotStarted, zone ?? TimeZoneInfo.Local),
        ActualFinish = PlanActualDates.Finish(history, r.Closed, zone ?? TimeZoneInfo.Local)
    };
    internal static bool NeedsProgressDate(PlanRow before, PlanRow current, string field)
    {
        if (field is not (PlanField.Start or PlanField.End) || TaskInput(before).KeepsDates) return false;
        // Automatic null inputs were not clear commands. A task that starts work adopts the known GitHub start
        // once; a close no longer derives a plan date from the close day (#308).
        return field == PlanField.Start
            ? before.Start is null && current.Start is null && !(before.Actual > 0) && current.Actual > 0
            : false;
    }
    internal static ImmutableArray<PlanRow> EffectiveRows(PlanDocument document)
    {
        var summaries = document.State.Rows.Where(r => r.Parent is not null).Select(r => r.Parent!).ToHashSet();
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        // Keep local inputs intact for hierarchy Undo; summary consumers use only GitHub-held values.
        return document.State.Rows.Select(r => !summaries.Contains(r.Identity) ? r : r with {
            Assignees = baseline.GetValueOrDefault(r.Identity)?.Assignees ?? [],
            Predecessors = baseline.GetValueOrDefault(r.Identity)?.Predecessors ?? [],
            Status = baseline.GetValueOrDefault(r.Identity)?.Status,
            ExtraValues = baseline.GetValueOrDefault(r.Identity)?.ExtraValues ?? ImmutableDictionary<string, PlanExtraValue>.Empty,
            Labels = baseline.GetValueOrDefault(r.Identity)?.Labels ?? [],
            MilestoneTitle = baseline.GetValueOrDefault(r.Identity)?.MilestoneTitle,
            Phase = baseline.GetValueOrDefault(r.Identity)?.Phase,
            IssueType = baseline.GetValueOrDefault(r.Identity)?.IssueType
        }).ToImmutableArray();
    }
    internal static ImmutableArray<PlanConflict> BlockingConflicts(PlanDocument document)
    {
        var summaries = document.State.Rows.Where(r => r.Parent is not null).Select(r => r.Parent!).ToHashSet();
        return document.Sync.Conflicts.Where(c => c.DeletedFieldName is null && (!summaries.Contains(c.Identity)
            || !(IsSummaryPublishExcluded(true, c.Field) || c.Field is PlanField.Start or PlanField.End))).ToImmutableArray();
    }
    internal static IReadOnlyList<ScheduledTask> Schedule(PlanDocument document, DateOnly today)
    {
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var settings = document.State.Settings;
        var notStarted = PlanActualDates.NotStarted(settings);
        return PlanScheduler.Calculate(EffectiveRows(document).Select((r, i) => TaskInput(r, i,
                document.Sync.IssueHistory.GetValueOrDefault(r.Identity), notStarted) with
        { GitHubStart = baseline.GetValueOrDefault(r.Identity)?.Start, GitHubEnd = baseline.GetValueOrDefault(r.Identity)?.End }).ToArray(),
            new PlanSettings { StatusDate = settings.StatusDate, ProjectStart = settings.ProjectStart,
                Calendar = new() { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() },
                People = settings.People.Select(p => new PlanPerson(p.Identity, p.Rate)).ToArray() }, today, firstDates: false);
    }
    // The tasks whose plan dates the change between two documents moves. A task the change does not
    // reach keeps its dates, so this is exactly the rescheduled subtree (#236).
    internal static ImmutableHashSet<string> ChangedTaskDates(PlanDocument before, PlanDocument after, DateOnly today)
    {
        var previous = Schedule(before, today).ToDictionary(t => t.Input.Identity, t => (Start: t.Start.Value, End: t.End.Value));
        return Schedule(after, today).Where(t => !t.IsSummary &&
            (!previous.TryGetValue(t.Input.Identity, out var old) || old.Start != t.Start.Value || old.End != t.End.Value))
            .Select(t => t.Input.Identity).ToImmutableHashSet();
    }
    internal static bool RowEqual(PlanRow? a, PlanRow? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        a with { Assignees = b.Assignees, Predecessors = b.Predecessors, Labels = b.Labels, ExtraValues = b.ExtraValues } == b &&
        a.Labels.SequenceEqual(b.Labels) && a.ExtraValues.Count == b.ExtraValues.Count && a.ExtraValues.All(p => b.ExtraValues.TryGetValue(p.Key, out var v) && v == p.Value) &&
        a.Assignees.SequenceEqual(b.Assignees) && a.Predecessors.SequenceEqual(b.Predecessors);
    internal static bool SettingsEqual(ProjectPlanSettings a, ProjectPlanSettings b) => ReferenceEquals(a, b) || PlanJson.Text(a) == PlanJson.Text(b);
    internal static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
    internal static bool Repository(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
    private static void Unique<T>(ImmutableArray<T> values) where T : notnull
        => Require(!values.IsDefault && values.Distinct().Count() == values.Length, "値は重複のない一覧にしてください。");
    internal static void ValidateSettings(ProjectPlanSettings s)
    {
        Require(s is not null, "計画設定がありません。");
        Require(!(s!.ProjectEnd < s.ProjectStart), "終了日は開始日以降にしてください。");
        Require(s.PhaseMilestones is not null && s.PhaseMilestones.All(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value)), "工程とマイルストーンの対応が不正です。");
        PlanSheetColumns.Validate(s.SheetColumns);
        Unique(s.CompanyDaysOff); Unique(s.People.Select(p => p.Identity).ToImmutableArray());
        Unique(s.Columns.Select(c => c.Role).ToImmutableArray()); Unique(s.Columns.Select(c => c.FieldId).ToImmutableArray());
        Require(s.DefaultRepository is null || Repository(s.DefaultRepository), "リポジトリはowner/repositoryで指定してください。");
        Require(s.NotStartedStatuses is null || s.NotStartedStatuses.All(name => !string.IsNullOrWhiteSpace(name)) &&
            s.NotStartedStatuses.Distinct(StringComparer.Ordinal).Count() == s.NotStartedStatuses.Length, "着手前の Status の設定が不正です。");
        foreach (var p in s.People)
        {
            Require(!string.IsNullOrWhiteSpace(p.Identity) && !string.IsNullOrWhiteSpace(p.Name) && p.Rate > 0 && p.Rate <= 100 && !(p.Allowance < 0), "担当者、稼働率または上限工数が不正です。");
        }
        foreach (var c in s.Columns)
        {
            var type = c.Role switch { PlanField.Estimate or PlanField.Remaining or PlanField.Actual => "NUMBER",
                PlanField.Start or PlanField.End => "DATE", PlanField.Phase => "SINGLE_SELECT", _ => null };
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
        Require(!d.State.ArchivedIdentities.IsDefault && d.State.ArchivedIdentities.Distinct().Count() == d.State.ArchivedIdentities.Length &&
            d.State.ArchivedIdentities.All(id => id.Length > 0 && !IsLocal(id)), "アーカイブした行の記録が不正です。");
        PlanSheetColumns.ValidateOwnership(d);
        Unique(d.Baseline.Milestones.Select(m => m.Title).ToImmutableArray());
        Require(d.Baseline.Milestones.All(m => !string.IsNullOrWhiteSpace(m.Title)), "マイルストーンの名前が不正です。");
        Unique(d.Baseline.Columns.Select(c => c.Id).ToImmutableArray());
        Unique(d.Baseline.IssueTypes.Select(t => t.Id).ToImmutableArray());
        Unique(d.Baseline.IssueTypes.Select(t => t.Name).ToImmutableArray());
        Require(d.Baseline.IssueTypes.All(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name)), "Issue の種類の設定が不正です。");
        Unique(d.Baseline.IssueTypeRepositories.Select(r => r).ToImmutableArray());
        Require(d.Baseline.IssueTypeRepositories.All(Repository), "Issue の種類を持つリポジトリの設定が不正です。");
        Require(d.Baseline.Columns.All(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.DataType)), "列定義が不正です。");
        var currentIds = d.State.Rows.Select(r => r.Identity).ToHashSet();
        Require(d.Baseline.Rows.All(r => !r.Identity.StartsWith("local:", StringComparison.Ordinal) && currentIds.Contains(r.Identity)), "基準行を削除またはローカル行に変更できません。");
        var baselineIds = d.Baseline.Rows.Select(r => r.Identity).ToHashSet();
        Require(d.State.Rows.All(r => baselineIds.Contains(r.Identity) || IsLocal(r.Identity)), "新規行にはローカル識別子が必要です。");
        // Open, refresh, reset and reload reach the scheduler only here; their cycle is named here (#263).
        try { _ = Schedule(d, today); }
        catch (PlanCycleException cycle) { throw Named(d, cycle); }
    }
    private static void ValidateRows(ImmutableArray<PlanRow> rows)
    {
        Unique(rows.Select(r => r.Identity).ToImmutableArray());
        foreach (var r in rows)
        {
            Require(!string.IsNullOrWhiteSpace(r.Identity) && r.Title is not null && Repository(r.Repository), "行の識別子、タイトルまたはリポジトリが不正です。");
            Unique(r.Assignees); Unique(r.Predecessors); Unique(r.Labels);
            Require(r.ExtraValues is not null && r.ExtraValues.All(p => PlanSheetColumns.IsExtra(p.Key) && p.Value is not null && Enum.IsDefined(p.Value.Availability)), "追加列の値が不正です。");
            foreach (var value in r.ExtraValues!.Values) {
                Require(value.Availability == ValueAvailability.Present ? value.Json is not null && value.Json != "null" : value.Availability != ValueAvailability.Empty || value.Json == "null", "追加列の空値が不正です。");
                if (value.Json is not null) { using var json = System.Text.Json.JsonDocument.Parse(value.Json); Require(json.RootElement.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number, "追加列の型が不正です。"); }
            }
            Require(r.CsvSourceHash is null || System.Text.RegularExpressions.Regex.IsMatch(r.CsvSourceHash, "^[a-f0-9]{64}$"), "CSVの識別情報が不正です。");
            Require(r.Assignees.Concat(r.Predecessors).All(x => !string.IsNullOrWhiteSpace(x)) && (r.Parent is null || !string.IsNullOrWhiteSpace(r.Parent)), "関係の識別子が不正です。");
            Require(r.IssueType is null || !string.IsNullOrWhiteSpace(r.IssueType), "Issue の種類が不正です。");
        }
    }
    private static bool IsLocal(string id) => id.StartsWith("local:", StringComparison.Ordinal) && Guid.TryParseExact(id[6..], "N", out _);
    internal static (PlanState State, PlanOperationKind Kind) Apply(PlanDocument d, PlanCommand command, DateOnly today)
    {
        var state = d.State;
        var rows = state.Rows.ToList();
        var byId = rows.ToDictionary(r => r.Identity);
        // What a PMO edit reschedules (#236): the edited task, its transitive successors and the roll-ups.
        var rescheduled = new HashSet<string>(StringComparer.Ordinal);
        var typedEnds = new HashSet<string>(StringComparer.Ordinal);
        var rescheduleAll = false;
        PlanOperationKind kind;
        // The row the PMO selected and the sibling it would land under. Both are named by a refusal.
        (string Parent, string Root)? indentRefusal = null;
        switch (command)
        {
            case FillPlanCells fill:
                Require(fill.Kind is PlanOperationKind.Fill or PlanOperationKind.CtrlD, "コピー操作の種類が不正です。");
                Require(byId.ContainsKey(fill.Source), "コピー元がありません。");
                var source = EffectiveRows(d).Single(r => r.Identity == fill.Source);
                return Apply(d, new EditPlanCells(fill.Kind, fill.Targets.SelectMany(id => fill.Fields.Select(f => new PlanCellChange(id, f, Value(source, f)))).ToImmutableArray()), today);
            case ClearPlanCells clear:
                // Delete never removes a task name: an accidentally untitled row is the state that holds
                // its own writes back at publish, so a title target is skipped rather than cleared (#240).
                // A read-only column is skipped for the same reason: a range over the default columns spans
                // 見込終了 (the scheduler's value) and 工程 while the Project does not map it, so those are
                // left alone and the rest of the range still clears instead of the whole command being
                // refused (#236 part B). Row-level read-only cells still reach EditPlanCells and refuse.
                return Apply(d, new EditPlanCells(PlanOperationKind.Clear, clear.Targets.SelectMany(id => clear.Fields
                    .Where(f => f != PlanField.Title && !PlanSheetColumns.IsReadOnlyColumn(f, d.State.Settings)).Select(f => new PlanCellChange(id, f, null))).ToImmutableArray()), today);
            case EditPlanCells edit:
                kind = edit.Kind;
                Require(kind is PlanOperationKind.Cell or PlanOperationKind.Paste or PlanOperationKind.Fill or PlanOperationKind.CtrlD or PlanOperationKind.Clear, "セル操作の種類が不正です。");
                Require(edit.Cells.Select(c => (c.Identity, c.Field)).Distinct().Count() == edit.Cells.Length, "同じセルを重複して指定できません。");
                // #253: people an on-demand lookup verified become stored before the cell is validated, so the
                // assignment and their registration are one command, one Undo step and one save.
                if (!edit.NewPeople.IsEmpty)
                    state = state with { Settings = state.Settings with { People = state.Settings.People.AddRange(
                        edit.NewPeople.Where(p => !state.Settings.People.Any(old => old.Identity == p.Identity))) } };
                var summaries = rows.Where(r => r.Parent is not null).Select(r => r.Parent!).ToHashSet();
                foreach (var c in edit.Cells)
                {
                    Require(byId.ContainsKey(c.Identity), "編集先の行がありません。");
                    // GitHub requires an Issue title, so a published row's title cannot be emptied here and then
                    // stop the whole publish; only a new row may stay untitled until publish holds it (#247).
                    if (c.Field == PlanField.Title && string.IsNullOrWhiteSpace(String(c.Value)) && d.Baseline.Rows.Any(r => r.Identity == c.Identity))
                        throw new ArgumentException("GitHub の Issue のタスク名は空にできません。");
                    if (ReadOnlyReason(byId[c.Identity], summaries.Contains(c.Identity), c.Field) is { } reason)
                        throw new ArgumentException(reason);
                    if (PlanSheetColumns.IsExtra(c.Field) && PlanExtraEditing.ReadOnlyReason(d, byId[c.Identity], c.Field) is { } unavailable) throw new ArgumentException(unavailable);
                    if (ScheduleFields.Contains(c.Field)) rescheduled.Add(c.Identity);
                    if (c.Field == PlanField.End && c.Value is not null) typedEnds.Add(c.Identity);
                    var value = c.Field is PlanField.Phase or PlanField.IssueType or PlanField.MilestoneTitle ? PlanSheetEditing.Parse(d, c.Field, String(c.Value) ?? "", today, c.Identity)
                        : c.Field == PlanField.Labels ? PlanSheetEditing.Parse(d, c.Field, string.Join(",", Ids(c.Value)), today, c.Identity) : c.Value;
                    byId[c.Identity] = PlanSheetColumns.IsExtra(c.Field)
                        ? PlanValues.Set(byId[c.Identity], c.Field, PlanJson.Text(PlanExtraEditing.Validate(d, c.Field, c.Value)))
                        : Edit(byId[c.Identity], c.Field, value);
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
                    var validatePair = (group.Any(c => c.Field is PlanField.Start or PlanField.End) || !previous.KeepsDates) && task.KeepsDates
                        || typedEnds.Contains(group.Key);
                    Require(!validatePair || !(task.KeptDates.Start > task.KeptDates.End), "終了日は開始日以降にしてください。");
                }
                rows = rows.Select(r => byId[r.Identity]).ToList();
                break;
            case InsertPlanRows insert:
                kind = insert.Kind;
                Require(kind is PlanOperationKind.Insert or PlanOperationKind.CsvImport, "挿入操作の種類が不正です。");
                // #253: a person resolved for a new row's 担当者 is registered with the insert, as CsvPeople is
                // for CSV import, so the row and its person are one operation.
                if (!insert.CsvPeople.IsEmpty || !insert.NewPeople.IsEmpty)
                    state = state with { Settings = state.Settings with { People = state.Settings.People.AddRange(
                        insert.CsvPeople.Concat(insert.NewPeople).Where(p => !state.Settings.People.Any(old => old.Identity == p.Identity))) } };
                if (kind == PlanOperationKind.CsvImport)
                {
                    Require(insert.AllowDuplicateCsv || !insert.Rows.Any(r => r.CsvSourceHash is not null && rows.Any(old => old.CsvSourceHash == r.CsvSourceHash)), "同じCSVは追加済みです。重複して追加するか確認してください。");
                }
                var index = insert.Before is null ? rows.Count : rows.FindIndex(r => r.Identity == insert.Before);
                Require(index >= 0, "挿入先がありません。");
                var additions = insert.Rows.Select(r => r with { Repository = r.Repository.Length == 0 ? state.Settings.DefaultRepository ?? "" : r.Repository }).ToArray();
                rescheduled.UnionWith(additions.Select(r => r.Identity));
                typedEnds.UnionWith(additions.Where(r => r.End is not null).Select(r => r.Identity));
                Require(additions.All(r => IsLocal(r.Identity) && !(r.Estimate < 0 || r.Remaining < 0 || r.Actual < 0) && (!TaskInput(r).KeepsDates || !(TaskInput(r).KeptDates.Start > TaskInput(r).KeptDates.End))), "新規行の識別子、工数または日付が不正です。");
                if (kind == PlanOperationKind.CsvImport) rows = new PlanOutline(state.Rows).Import([.. additions]).ToList();
                else {
                    var inheritedParent = insert.Before is not null && byId[insert.Before].Parent is { } owner && byId.ContainsKey(owner) ? owner : null;
                    rows.InsertRange(index, additions.Select(r => r with { Parent = inheritedParent }));
                }
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
                    Require(first > 0, "直前の同階層タスクがありません。");
                    newParent = siblings[first - 1].Identity; indentRefusal = (newParent, selected[0].Identity);
                }
                var ids = selected.Select(r => r.Identity).ToHashSet();
                rescheduled.UnionWith(ids);
                rows = indent.Outdent ? new PlanOutline(state.Rows).Outdent(indent.Targets).ToList()
                    : rows.Select(r => ids.Contains(r.Identity) ? r with { Parent = newParent } : r).ToList();
                break;
            case MovePlanSubtrees outlineMove:
                kind = PlanOperationKind.Move;
                rescheduled.UnionWith(outlineMove.Targets);
                rows = new PlanOutline(state.Rows).Move(outlineMove.Targets, outlineMove.Before).ToList();
                break;
            case ArrangePlanOutline:
                kind = PlanOperationKind.Move;
                rescheduleAll = true;
                rows = new PlanOutline(state.Rows).Arrange().ToList();
                break;
            case MovePlanRows move:
                kind = PlanOperationKind.Move;
                var moving = Selected(rows, move.Targets); var movingIds = moving.Select(r => r.Identity).ToHashSet();
                Require(move.Before is null || byId.ContainsKey(move.Before) && !movingIds.Contains(move.Before), "移動先は選択外の行にしてください。");
                rescheduled.UnionWith(movingIds);
                rows.RemoveAll(r => movingIds.Contains(r.Identity));
                rows.InsertRange(move.Before is null ? rows.Count : rows.FindIndex(r => r.Identity == move.Before), moving);
                break;
            case ClosePlanRows close:
                // #231: 完了にする and 中止にする close the Issue; a requirement row takes its subtree.
                kind = close.Cancelled ? PlanOperationKind.Cancel : PlanOperationKind.Close;
                var closing = Eligible(d, close.Targets, close.Cancelled ? PlanRowAction.Cancel : PlanRowAction.Complete);
                Require(!closing.Applies.IsEmpty, NothingEligible);
                var closingIds = closing.Applies.ToHashSet(StringComparer.Ordinal);
                // The local close day is provisional until the publish reads closedAt back.
                var closedDay = d.State.Settings.StatusDate ?? today;
                rows = rows.Select(r => closingIds.Contains(r.Identity) ? r with { Closed = true, Cancelled = close.Cancelled, CloseDate = closedDay } : r).ToList();
                break;
            case ReopenPlanRows reopen:
                kind = PlanOperationKind.Reopen;
                var reopening = Eligible(d, reopen.Targets, PlanRowAction.Reopen);
                Require(!reopening.Applies.IsEmpty, NothingEligible);
                var reopeningIds = reopening.Applies.ToHashSet(StringComparer.Ordinal);
                rows = rows.Select(r => reopeningIds.Contains(r.Identity) ? r with { Closed = false, Cancelled = false, CloseDate = null } : r).ToList();
                break;
            case RemovePlanRows remove:
                kind = PlanOperationKind.Remove;
                var removing = Eligible(d, remove.Targets, PlanRowAction.Remove);
                Require(!removing.Applies.IsEmpty, NothingEligible);
                var removingIds = removing.Applies.ToHashSet(StringComparer.Ordinal);
                rows = rows.Select(r => removingIds.Contains(r.Identity) ? r with { Removed = true } : r).ToList();
                break;
            case DeletePlanRows delete:
                kind = PlanOperationKind.Delete;
                Require(!delete.Targets.IsDefaultOrEmpty && delete.Targets.Distinct().Count() == delete.Targets.Length &&
                    delete.Targets.All(byId.ContainsKey), "削除する行を選択してください。");
                var outline = new PlanOutline(state.Rows);
                var deleting = delete.Targets.SelectMany(id => outline.Subtree(id)).Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
                var published = d.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
                if (state.Rows.FirstOrDefault(r => deleting.Contains(r.Identity) && published.Contains(r.Identity)) is { } refused)
                    throw new ArgumentException(PublishedRowRefusal(PlanTaskNames.Of(d)[refused.Identity]));
                // A deleted predecessor no longer constrains its successors, which are rescheduled below. The
                // parent's roll-up follows its remaining children by itself; rescheduling the parent would
                // move every sibling that never depended on the deleted row (#236).
                var survivors = rows.Where(r => !deleting.Contains(r.Identity)).Select(r => r.Predecessors.Any(deleting.Contains)
                    ? r with { Predecessors = r.Predecessors.Where(id => !deleting.Contains(id)).ToImmutableArray() } : r).ToList();
                rescheduled.UnionWith(survivors.Where(r => r.Predecessors.Length != byId[r.Identity].Predecessors.Length).Select(r => r.Identity));
                rows = survivors;
                break;
            case ReplacePlanSettings settings:
                kind = PlanOperationKind.Settings;
                // Only a change that can move dates reschedules: the calendar, the Project period or people rates.
                rescheduleAll = ScheduleSettings(state.Settings, settings.Settings);
                state = state with { Settings = settings.Settings }; break;
            default: throw new ArgumentException("未対応の計画操作です。");
        }
        state = state with { Rows = rows.ToImmutableArray() };
        try
        {
            // Reschedule runs the scheduler, which raises a cycle itself, so an indent that creates one must
            // be translated here and not only in ValidateDocument (#241).
            state = Reschedule(d, state, rescheduled, typedEnds, rescheduleAll, today);
            ValidateDocument(d with { State = state }, today);
        }
        catch (PlanCycleException cycle) when (indentRefusal is not null)
        {
            // The scheduler reports a cycle by identity; a refused indent must name the link to remove (#241).
            if (IndentRefusal(d with { State = state }, indentRefusal.Value, cycle) is { } reason) throw new PlanCycleException(reason, cycle.Identities);
            throw Named(d with { State = state }, cycle);
        }
        catch (PlanCycleException cycle) { throw Named(d with { State = state }, cycle); }
        return (state, kind);
    }
    // Start and end are plan values: only a PMO edit that reaches a task rewrites them, and that edit
    // reaches the edited task, its transitive successors and the summary roll-ups (#236).
    private static readonly HashSet<string> ScheduleFields = new(StringComparer.Ordinal)
        { PlanField.Start, PlanField.End, PlanField.Estimate, PlanField.Remaining, PlanField.Actual,
          PlanField.Predecessors, PlanField.Parent };
    private static bool ScheduleSettings(ProjectPlanSettings before, ProjectPlanSettings after)
        => before.ProjectStart != after.ProjectStart || before.ProjectEnd != after.ProjectEnd
            || !before.CompanyDaysOff.SequenceEqual(after.CompanyDaysOff) || before.ImportedHolidays != after.ImportedHolidays
            || !before.People.SequenceEqual(after.People);
    // A typed end stays on its task for the edit that typed it while its successors follow it (#268): the
    // task is left out of the tasks the scheduler lays out again, so its stored end is the plan end.
    private static PlanState Reschedule(PlanDocument d, PlanState state, IReadOnlyCollection<string> edited, IReadOnlySet<string> typedEnds, bool all, DateOnly today)
    {
        var affected = Affected(state, edited, all);
        if (affected.Count == 0) return state;
        var settings = state.Settings;
        var scheduled = PlanScheduler.Calculate(state.Rows.Select((r, i) => TaskInput(r, i)).ToArray(),
            new PlanSettings { StatusDate = settings.StatusDate, ProjectStart = settings.ProjectStart,
                Calendar = new() { CompanyDaysOff = settings.CompanyDaysOff.ToHashSet(), ImportedHolidays = settings.ImportedHolidays?.ToPreset() },
                People = settings.People.Select(p => new PlanPerson(p.Identity, p.Rate)).ToArray() }, today, affected.Except(typedEnds));
        var byId = scheduled.ToDictionary(t => t.Input.Identity);
        // Only the end is written: the plan start stays the live value of the row's own start and
        // predecessor finishes, so a typed start keeps its edit form and its classification. A task the
        // scheduler laid no work out for keeps its own dates, so an explicit clear is never resurrected.
        return state with { Rows = state.Rows.Select(r => byId.TryGetValue(r.Identity, out var task) && affected.Contains(r.Identity) && LaidOut(task)
            ? r with { End = task.End.Value } : r).ToImmutableArray() };
        static bool LaidOut(ScheduledTask task) => !task.Input.IsComplete
            && (task.Input.EffectiveRemaining is > 0 || task.Input.Actual is > 0);
    }
    // The edited rows, every row that depends on them through a predecessor link or a roll-up, and the
    // summaries above them. Everything else keeps its plan dates.
    private static ImmutableHashSet<string> Affected(PlanState state, IReadOnlyCollection<string> edited, bool all)
    {
        var rows = state.Rows;
        var present = rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        // A settings change moves the tasks that have dates; a task without dates gets its first dates only
        // from a PMO edit that reaches it (#236 rule 5).
        if (all) return rows.Where(r => r.Start is not null || r.End is not null).Select(r => r.Identity).ToImmutableHashSet(StringComparer.Ordinal);
        var byId = rows.ToDictionary(r => r.Identity);
        var queue = new Queue<string>(edited.Where(present.Contains));
        var affected = new HashSet<string>(queue, StringComparer.Ordinal);
        while (queue.TryDequeue(out var id))
        {
            foreach (var next in rows)
            {
                if (affected.Contains(next.Identity)) continue;
                if (next.Parent == id || EffectivePredecessors(byId, next).Contains(id))
                {
                    affected.Add(next.Identity);
                    queue.Enqueue(next.Identity);
                }
            }
        }
        return affected.ToImmutableHashSet(StringComparer.Ordinal);
    }
    private static HashSet<string> EffectivePredecessors(IReadOnlyDictionary<string, PlanRow> byId, PlanRow row)
    {
        var result = new HashSet<string>(row.Predecessors, StringComparer.Ordinal);
        for (var parent = row.Parent; parent is not null && byId.TryGetValue(parent, out var above); parent = above.Parent)
            result.UnionWith(above.Predecessors);
        return result;
    }
    // An indent adds one relation: the new parent above its new subtree. A link between them in either
    // direction makes the schedule impossible, and that link is what the PMO has to clear. The sentence
    // names the row being indented, the sibling it cannot go under and the direction of the link.
    private static string? IndentRefusal(PlanDocument candidate, (string Parent, string Root) indent, PlanCycleException cycle)
    {
        // The local rows, not the summary view: the new parent's link may be an unpublished edit, which the
        // scheduler still sees and the summary view would replace with the published value.
        var rows = candidate.State.Rows;
        if (Position(rows, indent.Parent) == 0 || Position(rows, indent.Root) == 0) return null;
        var subtree = Descendants(rows, indent.Parent);
        var byId = rows.ToDictionary(r => r.Identity);
        foreach (var identity in cycle.Identities)
        {
            if (!byId.TryGetValue(identity, out var row)) continue;
            foreach (var predecessor in row.Predecessors)
            {
                var dependent = predecessor == indent.Parent && subtree.Contains(identity) ? identity
                    : identity == indent.Parent && subtree.Contains(predecessor) ? indent.Parent : null;
                if (dependent is null) continue;
                var before = dependent == indent.Parent ? predecessor : indent.Parent;
                if (Position(rows, before) == 0 || Position(rows, dependent) == 0) continue;
                var names = PlanTaskNames.Of(candidate);
                return $"{names[dependent]} は {names[before]} の後に始まるため、{names[indent.Root]} を {names[indent.Parent]} の下にインデントできません。先にリンクを解除してください。";
            }
        }
        return null;
    }
    // The scheduler knows only row positions; the PMO reads the cycle by task name (#263).
    private static PlanCycleException Named(PlanDocument candidate, PlanCycleException cycle)
    {
        if (!cycle.Message.StartsWith("循環参照:", StringComparison.Ordinal)) return cycle;
        var names = PlanTaskNames.Of(candidate);
        var members = cycle.Identities.Where(names.Contains).OrderBy(id => Position(candidate.State.Rows, id)).Select(id => names[id]);
        return new PlanCycleException("循環参照: " + string.Join(", ", members), cycle.Identities);
    }
    private static int Position(ImmutableArray<PlanRow> rows, string identity)
    {
        for (var index = 0; index < rows.Length; index++) if (rows[index].Identity == identity) return index + 1;
        return 0;
    }
    private static HashSet<string> Descendants(ImmutableArray<PlanRow> rows, string parent)
    {
        var children = rows.Where(r => r.Parent is not null).ToLookup(r => r.Parent!);
        var found = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(parent);
        while (pending.TryPop(out var id))
            foreach (var child in children[id]) if (found.Add(child.Identity)) pending.Push(child.Identity);
        return found;
    }
    private static List<PlanRow> Selected(List<PlanRow> rows, ImmutableArray<string> ids)
    {
        Unique(ids); var set = ids.ToHashSet(); var result = rows.Where(r => set.Contains(r.Identity)).ToList();
        Require(result.Count == ids.Length, "選択行がありません。"); return result;
    }
    internal static object? Value(PlanRow r, string f) => PlanSheetColumns.IsExtra(f) ? PlanExtraEditing.Value(r, f) : f switch
    {
        PlanField.Labels => r.Labels, PlanField.MilestoneTitle => r.MilestoneTitle,
        PlanField.Title => r.Title, PlanField.Repository => r.Repository, PlanField.Status => r.Status, PlanField.Phase => r.Phase,
        PlanField.IssueType => r.IssueType, PlanField.Closed => r.Closed,
        PlanField.Cancelled => r.Cancelled, PlanField.Removed => r.Removed,
        PlanField.Assignees => r.Assignees, PlanField.Predecessors => r.Predecessors, PlanField.Parent => r.Parent,
        PlanField.Estimate => r.Estimate, PlanField.Remaining => r.Remaining, PlanField.Actual => r.Actual,
        PlanField.Start => r.Start, PlanField.End => r.End,
        _ => throw new ArgumentException("セルとして編集できない項目です。")
    };
    private static T? Optional<T>(object? value) where T : struct => value is null ? null : value is T typed ? typed : throw new ArgumentException("セルの値の型が不正です。");
    private static string? String(object? value) => value is null ? null : value as string ?? throw new ArgumentException("文字列を指定してください。");
    private static ImmutableArray<string> Ids(object? value) => value is null ? [] : value is IEnumerable<string> ids ? ids.ToImmutableArray() : throw new ArgumentException("識別子一覧を指定してください。");
    private static PlanRow Edit(PlanRow r, string field, object? value)
    {
        var task = TaskInput(r);
        switch (field)
        {
            case PlanField.Estimate: task = PlanEdits.Estimate(task, Optional<decimal>(value)); return r with { Estimate = task.Estimate };
            case PlanField.Remaining: return r with { Remaining = PlanEdits.Remaining(task, Optional<decimal>(value)).Remaining };
            case PlanField.Actual: return r with { Actual = PlanEdits.Actual(task, Optional<decimal>(value)).Actual };
            // Validate the final pair after the entire paste, not between its endpoint cells.
            case PlanField.Start: task = PlanEdits.Start(task with { End = null }, Optional<DateOnly>(value)); return r with { Start = task.Start };
            case PlanField.End: task = PlanEdits.End(task with { Start = null }, Optional<DateOnly>(value)); return r with { End = task.End };
            case PlanField.Labels: return r with { Labels = Ids(value) };
            case PlanField.MilestoneTitle: return r with { MilestoneTitle = String(value) };
            case PlanField.Title: return r with { Title = String(value) ?? "" };
            case PlanField.Repository: return r with { Repository = String(value) ?? "" };
            case PlanField.Status: return r with { Status = String(value) };
            case PlanField.Phase: return r with { Phase = String(value) };
            case PlanField.IssueType: return r with { IssueType = String(value) };
            case PlanField.Parent: return r with { Parent = String(value) };
            case PlanField.Assignees: return r with { Assignees = Ids(value) };
            case PlanField.Predecessors: return r with { Predecessors = Ids(value) };
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
        // The remembered archives are a GitHub observation, not an undoable edit.
        return new(order.Select(id => rows[id]).ToImmutableArray(), nextSettings ?? state.Settings) { ArchivedIdentities = state.ArchivedIdentities };
    }
    internal static PlanBaseline ReplayBaseline(PlanBaseline baseline, PlanPatch patch, bool forward)
    {
        Require(!patch.DiscardedRows.IsDefault, "操作履歴の利用不可行が不正です。");
        var discarded = patch.DiscardedRows.Select(r => r.Identity).ToHashSet();
        return baseline with { Rows = forward ? baseline.Rows.Where(r => !discarded.Contains(r.Identity)).ToImmutableArray()
            : baseline.Rows.AddRange(patch.DiscardedRows.Where(r => !baseline.Rows.Any(current => current.Identity == r.Identity))) };
    }
    internal static string SummaryReadOnlyReason(string field) => field is PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Start or PlanField.End
        ? "子タスクから集計（編集不可）" : "要求事項の行では編集できません";
    internal static bool IsSummaryPublishExcluded(bool summary, string field) => summary && field is
        PlanField.Estimate or PlanField.Remaining or PlanField.Actual or PlanField.Assignees or PlanField.Predecessors
        or PlanField.Status or PlanField.Phase or PlanField.IssueType or PlanField.Labels or PlanField.MilestoneTitle || summary && PlanSheetColumns.IsExtra(field);
    internal static string? ReadOnlyReason(PlanRow row, bool summary, string field)
    {
        if (field == PlanField.Repository) return "この列は読み取り専用です。";
        // 見込終了 is the forecast (#236 part B): a derived value, never an input.
        if (field == "ForecastEnd") return "見込終了は先行関係と見込み残存工数から計算します（編集不可）";
        // #308: 着手日 and 完了日 are records of the Issue's history, read-only on every row.
        if (field is "ActualStart" or "ActualFinish") return "Issue の履歴から読み取ります（編集不可）";
        if (summary && field is not (PlanField.Title or PlanField.Repository or PlanField.Parent))
            return SummaryReadOnlyReason(field);
        return row.Closed && field == PlanField.Remaining
            ? "完了したタスクは Issue のクローズで確定します（編集不可）" : null;
    }
    // An Issue cannot be deleted from the plan: #231 owns 完了・中止・この版から外す, and a delete that
    // reaches a published row is refused whole (named by the row it first reaches).
    internal static string PublishedRowRefusal(string name)
        => $"GitHub の Issue は削除できません（{name}）。完了・中止・この版から外す を使ってください";
    // Rows a publish cannot create: a new row left without a title, and any new row that would be
    // created inside one. Everything else still publishes; writes referencing these rows are held (#247).
    internal static ImmutableHashSet<string> HeldRows(PlanDocument document)
    {
        var baseline = document.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        var rows = document.State.Rows;
        var byId = rows.ToDictionary(r => r.Identity);
        var untitled = UntitledNewRows(document).ToHashSet(StringComparer.Ordinal);
        var held = new HashSet<string>(untitled, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (baseline.Contains(row.Identity) || held.Contains(row.Identity)) continue;
            for (var parent = row.Parent; parent is not null && byId.TryGetValue(parent, out var above); parent = above.Parent)
                if (untitled.Contains(parent)) { held.Add(row.Identity); break; }
        }
        return held.ToImmutableHashSet(StringComparer.Ordinal);
    }
    // The new rows the PMO has not titled yet. They are unpublished and cannot be published until
    // they are titled or deleted (#247).
    internal static ImmutableArray<string> UntitledNewRows(PlanDocument document)
    {
        var baseline = document.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        return document.State.Rows.Where(r => !baseline.Contains(r.Identity) && string.IsNullOrWhiteSpace(r.Title))
            .Select(r => r.Identity).ToImmutableArray();
    }
    // A field change this publish cannot send because it would name a row no Issue exists for (#247).
    internal static bool HoldsField(PlanRow row, PlanRow before, string field, ImmutableHashSet<string> held) => field switch
    {
        PlanField.Parent => row.Parent is not null && held.Contains(row.Parent),
        PlanField.Predecessors => row.Predecessors.Any(held.Contains) || before.Predecessors.Any(held.Contains),
        _ => false
    };
    // A remaining unpublished change that is held by design: it can only be sent by naming a row this
    // publish cannot create (#247). The parent's child order counts as held when its changed children
    // name one, exactly as PlanPublishPlan holds that order write whole. The publish review marks the
    // same pairs 保留 from HoldsField/SiblingOrderChange; keeping the answer in one place stops the
    // completeness check from reporting a failure the review already explains as held.
    internal static bool HoldsRemainingChange(PlanDocument document, string identity, string field, ImmutableHashSet<string> held)
    {
        if (document.State.Rows.FirstOrDefault(r => r.Identity == identity) is not { } row) return false;
        if (field is PlanField.Parent or PlanField.Predecessors)
        {
            var before = document.Baseline.Rows.FirstOrDefault(r => r.Identity == identity) ?? new(identity, "", row.Repository);
            return HoldsField(row, before, field, held);
        }
        return field == PlanField.SubIssueOrder && SiblingOrderChange(document, identity) is { } sibling && sibling.After.Any(held.Contains);
    }
    internal const string NothingEligible = "この操作ができる行がありません。";
    // #231: which rows of a selection a row-state command applies to. A requirement row stands for its
    // whole subtree, as with Link; the rest are skipped with the reason that makes them ineligible.
    internal static PlanRowEligibility Eligible(PlanDocument document, ImmutableArray<string> targets, PlanRowAction action)
    {
        Require(!targets.IsDefaultOrEmpty && targets.Distinct().Count() == targets.Length &&
            targets.All(id => document.State.Rows.Any(r => r.Identity == id)), "行を選択してください。");
        var baseline = document.Baseline.Rows.Select(r => r.Identity).ToHashSet(StringComparer.Ordinal);
        var outline = new PlanOutline(document.State.Rows);
        var candidates = targets.SelectMany(id => outline.Subtree(id)).Select(r => r.Identity).Distinct(StringComparer.Ordinal).ToArray();
        var applies = ImmutableArray.CreateBuilder<string>(); var skipped = ImmutableArray.CreateBuilder<(string Identity, string Reason)>();
        foreach (var identity in candidates)
        {
            var row = document.State.Rows.Single(r => r.Identity == identity);
            string? reason = null;
            if (!baseline.Contains(identity)) reason = "未発行の行です";
            else if (row.Removed) reason = "この版から外しています";
            else if (action == PlanRowAction.Remove) { }
            else if (action == PlanRowAction.Reopen) { if (!row.Closed) reason = "未完了の行です"; }
            else if (!row.Closed) { }
            else reason = row.Cancelled ? "すでに中止しています" : "すでに完了しています";
            if (reason is null) applies.Add(identity); else skipped.Add((identity, reason));
        }
        return new(applies.ToImmutable(), skipped.ToImmutable());
    }
    internal static bool IsRecalculatedField(ScheduledTask task, string field) => field switch
    {
        PlanField.Start => task.IsSummary || !(task.Start.Origin == DateOrigin.Kept && task.Start.Value == task.Input.Start),
        PlanField.End => task.IsSummary || task.End.Origin != DateOrigin.Kept || task.End.Value != task.Input.End,
        PlanField.Remaining => task.IsSummary || task.Input.Closed,
        PlanField.Estimate or PlanField.Actual => task.IsSummary,
        _ => false
    };
    internal static bool IsUnmappedCalculation(ScheduledTask task, string field, ProjectPlanSettings settings)
        => IsRecalculatedField(task, field) && !settings.Columns.Any(c => c.Role == field);
    // A parent's children as the plan shows them. Rows the last read could not deliver keep their
    // displayed position in the state but are appended to the baseline, so they are excluded here.
    internal static ImmutableArray<string> PlanChildren(PlanDocument document, string parent)
        => document.State.Rows.Where(r => r.Parent == parent && !document.Sync.Unavailable.Contains(r.Identity))
            .Select(r => r.Identity).ToImmutableArray();
    // The child order the plan read for that parent: the Project item order it adopted.
    internal static ImmutableArray<string> ReadChildOrder(PlanDocument document, string parent)
        => document.Baseline.Rows.Where(r => r.Parent == parent && !document.Sync.Unavailable.Contains(r.Identity))
            .Select(r => r.Identity).Where(id => document.State.Rows.Any(r => r.Identity == id)).ToImmutableArray();
    // The native sub-issue order the plan last read. An absent entry compares against the read order
    // rather than inventing an empty order.
    internal static ImmutableArray<string> NativeChildOrder(PlanDocument document, string parent)
    {
        if (!document.Sync.NativeOrders.TryGetValue(parent, out var order)) return ReadChildOrder(document, parent);
        var current = document.State.Rows.Where(r => !document.Sync.Unavailable.Contains(r.Identity)).Select(r => r.Identity).ToHashSet();
        return order.Where(current.Contains).ToImmutableArray();
    }
    internal static ImmutableArray<string> PreviousSiblingOrder(PlanDocument document, string parent) => NativeChildOrder(document, parent);
    // A child-order change is the PMO's edit: the plan's order differs from both orders GitHub showed
    // for that parent. A pre-existing difference between those GitHub orders is therefore not a change
    // and is never written back. A reorder whose write failed stays pending while the plan still asks
    // for the same order, so an explicit publish retries it.
    internal static PlanSiblingOrderChange? SiblingOrderChange(PlanDocument document, string parent)
    {
        var children = PlanChildren(document, parent);
        if (children.Length < 2) return null;
        var native = NativeChildOrder(document, parent);
        if (!children.SequenceEqual(ReadChildOrder(document, parent)) && !children.SequenceEqual(native)) return new(native, children);
        if (!children.SequenceEqual(native) && document.Sync.LocalOrderChoices.TryGetValue(parent, out var chosen) &&
            children.SequenceEqual(chosen)) return new(native, children);
        var attempted = PlanJson.Text(children);
        return document.Sync.Failures.Any(f => f.Field == PlanField.SubIssueOrder && f.Value == attempted &&
            children.Contains(f.Identity) && !children.SequenceEqual(native)) ? new(native, children) : null;
    }
    internal static PlanUnpublished Changes(PlanDocument d, DateOnly today, IReadOnlyList<ScheduledTask>? schedule = null)
    {
        var result = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>();
        var recalculated = ImmutableHashSet.CreateBuilder<(string Identity, string Field)>();
        var baseline = d.Baseline.Rows.ToDictionary(r => r.Identity);
        var moved = PlanOrder.Moved(d.Baseline.Rows.Select(r => r.Identity), d.State.Rows.Select(r => r.Identity));
        foreach (var calculated in schedule ?? Schedule(d, today))
        {
            var r = d.State.Rows[calculated.Input.RowId - 1];
            if (calculated.Input.Closed && !calculated.IsSummary) r = r with { Remaining = 0 };
            if (!baseline.TryGetValue(r.Identity, out var old)) { result[r.Identity] = [PlanField.NewTask]; continue; }
            var fields = ImmutableArray.CreateBuilder<string>();
            // A pending close or reopen carries the state write on its own: the closed end and the
            // remaining work follow the close day the read reports, not the provisional local day (#231).
            var pendingState = r.Closed != old.Closed;
            foreach (var field in new[] { PlanField.Title, PlanField.Repository, PlanField.Status, PlanField.Phase, PlanField.IssueType, PlanField.MilestoneTitle, PlanField.Closed, PlanField.Cancelled, PlanField.Removed, PlanField.Parent,
                PlanField.Estimate, PlanField.Remaining, PlanField.Actual })
                if (!IsSummaryPublishExcluded(calculated.IsSummary, field) && !IsUnmappedCalculation(calculated, field, d.State.Settings)
                    && !(pendingState && field == PlanField.Remaining) && !Equals(Value(r, field), Value(old, field))) fields.Add(field);
            if (!calculated.IsSummary)
                foreach (var extra in r.ExtraValues.Keys.Union(old.ExtraValues.Keys))
                    if (!PlanSheetColumns.IsMappedExtra(d, extra) && PlanValues.Get(r, extra) != PlanValues.Get(old, extra)) fields.Add(extra);
            if (!calculated.IsSummary && !r.Labels.ToHashSet().SetEquals(old.Labels)) fields.Add(PlanField.Labels);
            if (!calculated.IsSummary && !r.Assignees.ToHashSet().SetEquals(old.Assignees)) fields.Add(PlanField.Assignees);
            if (!calculated.IsSummary && !r.Predecessors.ToHashSet().SetEquals(old.Predecessors)) fields.Add(PlanField.Predecessors);
            // Two independent guards, both required. The plan publishes a date only when the plan holds one: a
            // value the scheduler merely displayed for a row whose plan has no such date is not an unpublished
            // change, while the first dates a PMO edit gives a task are (#236). And a row with a pending close or
            // reopen settles its dates through its own operation, not through a date write (#231).
            if (r.Start is not null && !pendingState && calculated.Start.Value != old.Start && !IsUnmappedCalculation(calculated, PlanField.Start, d.State.Settings))
                fields.Add(PlanField.Start);
            if (r.End is not null && !pendingState && calculated.End.Value != old.End && !IsUnmappedCalculation(calculated, PlanField.End, d.State.Settings))
                fields.Add(PlanField.End);
            foreach (var field in fields.Where(field => IsRecalculatedField(calculated, field)))
                recalculated.Add((r.Identity, field));
            if (moved.Contains(r.Identity)) fields.Add(PlanField.Order);
            if (SiblingOrderChange(d, r.Identity) is not null) fields.Add(PlanField.SubIssueOrder);
            if (fields.Count > 0) result[r.Identity] = fields.ToImmutable();
        }
        return new(result.ToImmutable()) { RecalculatedFields = recalculated.ToImmutable() };
    }
}
