namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningInput(PlanningTask Task, decimal? Estimate, decimal? Remaining,
    string[] Assignees, PlanningLink[] Predecessors, bool RelationshipsComplete = true, bool? RemoteClosed = null, decimal? ActualTotal = null);
internal sealed record TaskPlan(string Id, PlanningMode Mode, long SourceRevision, decimal? RawHours,
    decimal? ScheduledMinutes, DateTime? Start, DateTime? Finish, DateTime? SuggestedStart, DateTime? SuggestedFinish,
    string? Problem, string[] Warnings, string? Controller)
{
    public bool Resolved => Start is not null && Finish is not null;
    public decimal? RawDays => RawHours / 8;
    public decimal? RoundedMinutes => ScheduledMinutes is { } m ? decimal.Ceiling(m) : null;
}
internal sealed record AdoptedPlan(string ProjectId, long SourceRevision, TaskPlan[] Tasks,
    PlanningInput[]? Inputs = null, ProjectPlanning? Configuration = null);

internal sealed class WorkingCalendar(PlanningCalendar calendar)
{
    private readonly HashSet<DateOnly> holidays = calendar.Holidays.Dates.Select(d => d.Date).ToHashSet();
    private readonly Dictionary<(DateOnly, string?), WorkingInterval[]> exceptions = calendar.Exceptions.ToDictionary(e => (e.Date, e.PersonId), e => e.Intervals);
    private static readonly WorkingInterval[] Regular = [new(540, 780), new(840, 1080)];
    public WorkingInterval[] Intervals(DateOnly day, string? person)
    {
        if (person is not null && exceptions.TryGetValue((day, person), out var own)) return own;
        if (exceptions.TryGetValue((day, null), out var common)) return common;
        if (!calendar.HolidaysNotConsidered && (day.Year < calendar.Holidays.FirstYear || day.Year > calendar.Holidays.LastYear))
            throw new InvalidOperationException($"{day.Year}年の祝日を採用していません。例外日または祝日を考慮しない設定が必要です。");
        return day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || !calendar.HolidaysNotConsidered && holidays.Contains(day) ? [] : Regular;
    }
    public (DateTime Start, DateTime Finish) Place(DateTime anchor, decimal minutes, string? person)
    {
        if (minutes == 0) return (anchor, anchor);
        var remaining = decimal.Ceiling(minutes); DateTime? start = null; var date = DateOnly.FromDateTime(anchor);
        // A finite planning horizon also bounds calendars with no future capacity.
        for (var days = 0; days < 3660; days++, date = date.AddDays(1))
        {
            foreach (var span in Intervals(date, person))
            {
                var beginning = date.ToDateTime(TimeOnly.MinValue).AddMinutes(span.StartMinute);
                var end = date.ToDateTime(TimeOnly.MinValue).AddMinutes(span.EndMinute);
                if (beginning < anchor) beginning = anchor;
                if (beginning >= end) continue;
                start ??= beginning;
                var available = (decimal)(end - beginning).TotalMinutes;
                if (remaining <= available) return (start.Value, beginning.AddMinutes((double)remaining));
                remaining -= available;
            }
        }
        throw new InvalidOperationException("10年の計画範囲内に必要な稼働時間がありません。");
    }
    public bool IsStart(DateTime time, string? person) => Intervals(DateOnly.FromDateTime(time), person)
        .Any(i => time.TimeOfDay.TotalMinutes >= i.StartMinute && time.TimeOfDay.TotalMinutes < i.EndMinute);
    public bool IsFinish(DateTime time, string? person) => Intervals(DateOnly.FromDateTime(time), person)
        .Any(i => time.TimeOfDay.TotalMinutes > i.StartMinute && time.TimeOfDay.TotalMinutes <= i.EndMinute);
    public (DateTime Start, DateTime Finish) EndingAt(DateTime finish, decimal minutes, string? person)
    {
        if (minutes == 0) return (finish, finish);
        if (!IsFinish(finish, person)) throw new InvalidOperationException("固定終了は稼働区間の終了境界に指定してください。");
        var remaining = decimal.Ceiling(minutes); var date = DateOnly.FromDateTime(finish);
        for (var days = 0; days < 3660; days++, date = date.AddDays(-1))
        {
            foreach (var span in Intervals(date, person).Reverse())
            {
                var beginning = date.ToDateTime(TimeOnly.MinValue).AddMinutes(span.StartMinute);
                var end = date.ToDateTime(TimeOnly.MinValue).AddMinutes(span.EndMinute);
                if (end > finish) end = finish;
                if (end <= beginning) continue;
                var available = (decimal)(end - beginning).TotalMinutes;
                if (remaining <= available) return (end.AddMinutes(-(double)remaining), finish);
                remaining -= available;
            }
        }
        throw new InvalidOperationException("10年の計画範囲内に必要な稼働時間がありません。");
    }
}

internal static class PlanningEngine
{
    public static AdoptedPlan Calculate(ProjectPlanning project, PlanningInput[] inputs, long revision)
    {
        var calendar = new WorkingCalendar(project.Calendar);
        var people = project.People.ToDictionary(p => p.Id);
        var byId = inputs.ToDictionary(i => i.Task.Id);
        var cycles = FindCycles(inputs);
        var results = new Dictionary<string, TaskPlan>();
        TaskPlan CalculateOne(PlanningInput input)
        {
            if (results.TryGetValue(input.Task.Id, out var known)) return known;
            var task = input.Task; var warnings = new List<string>(); string? problem = null;
            var work = task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened ? input.Remaining : input.Estimate;
            decimal? minutes = null; DateTime? start = null, finish = null;
            var controller = task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened ? "残工数・基準日時・配賦・カレンダー" : "見積工数・配賦・カレンダー";
            if (project.Calendar.HolidaysNotConsidered) warnings.Add("祝日を考慮しない計画");
            if ((task.Assignment is null || task.Assignment.Legacy) && task.Mode != PlanningMode.Unplanned)
                warnings.Add(task.OwnerId is null ? "以前の共通カレンダーによる暫定計画を保持" : "以前の独立した計画担当者を保持。変更時は日程を比較してください。");
            if (input.ActualTotal is not null && task.Actuals is null) warnings.Add("実績合計の内訳・報告対象日が未入力です。");
            foreach (var (name, total, sum) in new[] { ("見積", input.Estimate, (task.Contributions ?? []).Sum(c => c.EstimateHours ?? 0)), ("残時間", input.Remaining, (task.Contributions ?? []).Sum(c => c.RemainingHours ?? 0)) })
                if (total is { } knownHours && knownHours != sum) warnings.Add(sum > knownHours ? $"{name}の内訳が合計を超えています。" : $"{name}の未割当: {PlanningContract.CanonicalHours(knownHours - sum)}人時");
            if (input.RemoteClosed is { } closed && closed != (task.Progress == PlanningProgress.Completed)) warnings.Add("GitHub状態と採用進捗が不一致です。進捗と実績を確認してください。");
            try
            {
                if (task.Mode == PlanningMode.Unplanned) throw new InvalidOperationException("Auto または Manual を選んでください。");
                if (task.Progress == PlanningProgress.Completed)
                {
                    if (input.Remaining != 0) throw new InvalidOperationException("完了には残工数0の明示的な更新が必要です。");
                    if (task.ActualStart is null || task.ActualFinish is null) throw new InvalidOperationException("完了の実績開始・終了日時を入力してください。");
                    work = 0; minutes = 0; start = task.ActualStart; finish = task.ActualFinish; controller = "完了・明示的な実績日時";
                }
                else
                {
                if (project.Start is null) throw new InvalidOperationException("Project開始日時が必要です。");
                if (work is null) throw new InvalidOperationException(task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened ? "残工数が未入力です。" : "見積工数が未入力です。");
                if (!input.RelationshipsComplete) throw new InvalidOperationException("担当者・先行Issueが未取得です。最新を取得してください。");
                if (cycles.Contains(task.Id)) throw new InvalidOperationException("先行関係が循環しています。");
                decimal weight;
                if (task.Assignment is { Legacy: false } assignment)
                {
                    if (!assignment.Complete || !input.RelationshipsComplete) throw new InvalidOperationException("担当者の取得が未完了です。最新を取得して日程を比較してください。");
                    if (!assignment.Assignees.ToHashSet().SetEquals(input.Assignees)) throw new InvalidOperationException("GitHub担当者が変わりました。日程を比較して自動計算を採用してください。");
                    if (assignment.Assignees.Length != 1) throw new InvalidOperationException(assignment.Assignees.Length == 0
                        ? "GitHub担当者が未設定です。日時を指定するか、担当者を設定後に最新を取得してください。"
                        : "複数担当者の同時計画は未対応です。日時を指定するか、担当・分担を確認してください。");
                }
                if (task.OwnerId is null)
                {
                    if (input.Assignees.Length != 0) throw new InvalidOperationException("計画担当者を明示的に選んでください。");
                    weight = 100; warnings.Add("担当未設定・共通カレンダー100%の暫定計画");
                }
                else if (!people.TryGetValue(task.OwnerId, out var owner)) throw new InvalidOperationException("計画担当者の配賦が未設定です。");
                else weight = owner.WeightPercent;
                if (work > 0 && weight == 0) throw new InvalidOperationException("配賦0%のため自動終了を計算できません。");
                minutes = work == 0 ? 0 : work * 6000 / weight;
                if (minutes != decimal.Ceiling(minutes.Value)) warnings.Add($"終了境界を{decimal.Ceiling(minutes.Value) - minutes:0.########}分切り上げ（工数は保持）");
                var anchor = project.Start.Value;
                if (task.Progress is PlanningProgress.InProgress or PlanningProgress.Reopened)
                {
                    if (project.Cutoff is null) throw new InvalidOperationException("再計画の基準日時が必要です。");
                    if (anchor < project.Cutoff) anchor = project.Cutoff.Value;
                    if (task.ActualStart is null) warnings.Add("実績開始が未入力です。");
                }
                if (task.EarliestStart is { } earliest && earliest > anchor) { anchor = earliest; controller = "最早開始"; }
                foreach (var link in input.Predecessors)
                {
                    if (link.Kind != "FS") throw new InvalidOperationException($"先行関係 {link.Kind} は自動計算に非対応です。関係は保持しています。");
                    DateTime? boundary = link.ExternalFinish;
                    if (byId.TryGetValue(link.PredecessorId, out var predecessor))
                    {
                        var previous = CalculateOne(predecessor); boundary = previous.Finish;
                        if (previous.Problem is not null || previous.Warnings.Length != 0) warnings.Add($"先行 {link.PredecessorId} に警告があります。");
                    }
                    if (boundary is null) throw new InvalidOperationException($"先行 {link.PredecessorId} の採用終了を確認できません。");
                    if (boundary > anchor) { anchor = boundary.Value; controller = "先行 " + link.PredecessorId; }
                }
                if (task.FixedStart is { } fixedStart)
                {
                    if (fixedStart < anchor) throw new InvalidOperationException("固定開始が先行・最早開始・基準日時に反します。");
                    anchor = fixedStart; controller = "固定開始";
                }
                if (task.FixedFinish is { } fixedFinish)
                {
                    (start, finish) = calendar.EndingAt(fixedFinish, minutes.Value, task.OwnerId);
                    if (start < anchor || task.FixedStart is not null && start != task.FixedStart)
                        throw new InvalidOperationException("固定終了までに必要な稼働時間を確保できません。");
                    controller = "固定終了";
                }
                else (start, finish) = calendar.Place(anchor, minutes.Value, task.OwnerId);
                if (task.FixedStart is not null && start != task.FixedStart) throw new InvalidOperationException("固定開始が稼働区間外です。");
                }
            }
            catch (InvalidOperationException e) { problem = e.Message; start = finish = null; }
            catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException) { problem = "計画範囲を超えています。工数・配賦・日時を確認してください。"; start = finish = null; }
            if (task.Mode == PlanningMode.Manual)
            {
                if (problem is not null) warnings.Add(problem);
                if (task.ManualStart != start || task.ManualFinish != finish) warnings.Add("Manual日時を保持。自動案と異なります。");
                try
                {
                    if (task.ManualStart is { } first && !calendar.IsStart(first, task.OwnerId)
                        || task.ManualFinish is { } last && !calendar.IsFinish(last, task.OwnerId)) warnings.Add("Manual日時に稼働区間外の境界があります。");
                }
                catch (InvalidOperationException e) { warnings.Add(e.Message); }
            }
            var effectiveFinish = task.Mode == PlanningMode.Manual ? task.ManualFinish : finish;
            if (task.Deadline is { } deadline && effectiveFinish > deadline) warnings.Add("期限を超えています。工数と依存関係は保持しています。");
            return results[task.Id] = new(task.Id, task.Mode, revision, work, minutes,
                task.Mode == PlanningMode.Manual ? task.ManualStart : start, task.Mode == PlanningMode.Manual ? task.ManualFinish : finish,
                start, finish, problem, warnings.Distinct().ToArray(), controller);
        }
        return new(project.ProjectId, revision, inputs.Select(CalculateOne).ToArray(), inputs, project);
    }
    private static HashSet<string> FindCycles(PlanningInput[] inputs)
    {
        var graph = inputs.ToDictionary(i => i.Task.Id); var visited = new HashSet<string>(); var active = new Dictionary<string, int>();
        var stack = new List<string>(); var cycles = new HashSet<string>();
        void Visit(string id)
        {
            if (active.TryGetValue(id, out var index)) { cycles.UnionWith(stack.Skip(index)); return; }
            if (!visited.Add(id)) return;
            active[id] = stack.Count; stack.Add(id);
            foreach (var link in graph[id].Predecessors.Where(l => l.Kind == "FS" && graph.ContainsKey(l.PredecessorId))) Visit(link.PredecessorId);
            active.Remove(id); stack.RemoveAt(stack.Count - 1);
        }
        foreach (var input in inputs) Visit(input.Task.Id);
        return cycles;
    }
}
