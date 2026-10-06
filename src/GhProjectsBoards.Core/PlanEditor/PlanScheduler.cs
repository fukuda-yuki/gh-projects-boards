using System.Globalization;
using System.Numerics;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanScheduler
{
    public static IReadOnlyList<ScheduledTask> Calculate(IReadOnlyList<PlanTask> tasks, PlanSettings settings, DateOnly today)
        => new Calculation(tasks, settings, today).Run();

    // A decimal quotient cannot retain repeating work/rate fractions across FS links.
    // Keep exact hours until the day-only result is projected; never round each task.
    private readonly struct Hours : IComparable<Hours>
    {
        private readonly BigInteger numerator;
        private readonly BigInteger denominator;

        private Hours(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            var divisor = denominator.IsOne ? BigInteger.One : BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            this.numerator = numerator / divisor;
            this.denominator = denominator / divisor;
        }
        public static implicit operator Hours(int value) => new(value, BigInteger.One);
        public static implicit operator Hours(decimal value)
        {
            Span<int> bits = stackalloc int[4];
            decimal.GetBits(value, bits);
            var numerator = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
            if (bits[3] < 0) numerator = -numerator;
            return new(numerator, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
        }
        public int CompareTo(Hours other)
            => (numerator * other.denominator).CompareTo(other.numerator * denominator);
        public static Hours operator +(Hours a, Hours b) => new(a.numerator * b.denominator + b.numerator * a.denominator, a.denominator * b.denominator);
        public static Hours operator -(Hours a, Hours b) => new(a.numerator * b.denominator - b.numerator * a.denominator, a.denominator * b.denominator);
        public static Hours operator *(Hours a, Hours b) => new(a.numerator * b.numerator, a.denominator * b.denominator);
        public static Hours operator /(Hours a, Hours b) => new(a.numerator * b.denominator, a.denominator * b.numerator);
        public static bool operator <(Hours a, Hours b) => a.CompareTo(b) < 0;
        public static bool operator >(Hours a, Hours b) => a.CompareTo(b) > 0;
        public static bool operator <=(Hours a, Hours b) => a.CompareTo(b) <= 0;
        public static bool operator >=(Hours a, Hours b) => a.CompareTo(b) >= 0;
        public decimal DailyValue()
        {
            // Daily capacity is at most eight hours. Split the integer part so the
            // scaled fraction fits decimal while retaining its available precision.
            var whole = BigInteger.DivRem(numerator, denominator, out var fraction);
            return (decimal)whole + (decimal)(fraction * BigInteger.Pow(10, 28) / denominator) / 10_000_000_000_000_000_000_000_000_000m;
        }
    }
    // Working-hour endpoints deliberately never escape the calculation result.
    private readonly record struct Point(int Day, Hours Hour) : IComparable<Point>
    {
        public int CompareTo(Point other) => Day != other.Day ? Day.CompareTo(other.Day) : Hour.CompareTo(other.Hour);
        public DateOnly Date => DateOnly.FromDayNumber(Day);
        public static Point Morning(DateOnly day) => new(day.DayNumber, 9);
    }
    private sealed record Result(ScheduledTask Row, Point? Start, Point? End, bool Valid = true);

    private sealed class Calculation
    {
        private readonly IReadOnlyList<PlanTask> tasks;
        private readonly PlanSettings settings;
        private readonly DateOnly statusDate;
        private readonly Dictionary<string, PlanTask> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> ancestors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> predecessors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlanPerson> people = new(StringComparer.Ordinal);
        private readonly HashSet<DateOnly> holidays;
        private readonly Dictionary<string, Result> results = new(StringComparer.Ordinal);
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        private readonly List<string> path = [];

        public Calculation(IReadOnlyList<PlanTask> tasks, PlanSettings settings, DateOnly today)
        {
            this.tasks = tasks;
            this.settings = settings;
            statusDate = settings.StatusDate ?? today;
            holidays = settings.Calendar.Holidays.Dates.Select(h => h.Date).ToHashSet();
            if (settings.Calendar.ImportedHolidays is { } imported) holidays.UnionWith(imported.Dates.Select(h => h.Date));
            var rowIds = new HashSet<int>();
            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Identity) || task.RowId <= 0 || !rowIds.Add(task.RowId) || !byId.TryAdd(task.Identity, task))
                    throw new ArgumentException("タスクの識別子とIDは空でない一意の値にしてください。");
                children[task.Identity] = [];
            }
            foreach (var person in settings.People)
                if (string.IsNullOrWhiteSpace(person.Identity) || person.Rate <= 0 || person.Rate > 100 || !people.TryAdd(person.Identity, person))
                    throw new ArgumentException("担当者は一意にし、稼働率は0より大きく100以下にしてください。");
            foreach (var task in tasks)
            {
                if (task.Parent is { } parent && children.TryGetValue(parent, out var list)) list.Add(task.Identity);
                var chain = new List<string> { task.Identity };
                var next = task.Parent;
                while (next is not null && byId.TryGetValue(next, out var ancestor))
                {
                    var cycleIndex = chain.IndexOf(next);
                    if (cycleIndex >= 0) Cycle(chain.Skip(cycleIndex));
                    chain.Add(next);
                    next = ancestor.Parent;
                }
                ancestors[task.Identity] = chain.Skip(1).ToArray();
            }
            foreach (var task in tasks)
            {
                foreach (var predecessor in task.Predecessors.Where(byId.ContainsKey))
                    if (predecessor == task.Identity || ancestors[task.Identity].Contains(predecessor) || ancestors[predecessor].Contains(task.Identity))
                        Cycle([task.Identity, predecessor]);
                predecessors[task.Identity] = task.Predecessors.Concat(ancestors[task.Identity].SelectMany(a => byId[a].Predecessors))
                    .Distinct(StringComparer.Ordinal).OrderBy(id => byId.TryGetValue(id, out var t) ? t.RowId : int.MaxValue).ToArray();
            }
        }

        public IReadOnlyList<ScheduledTask> Run()
        {
            var graph = tasks.ToDictionary(t => t.Identity,
                t => predecessors[t.Identity].Where(byId.ContainsKey).Concat(children[t.Identity]).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                if (results.ContainsKey(task.Identity)) continue;
                var stack = new Stack<(string Id, int Next)>();
                Enter(task.Identity);
                while (stack.Count > 0)
                {
                    var frame = stack.Pop();
                    var dependencies = graph[frame.Id];
                    if (frame.Next == dependencies.Length)
                    {
                        Evaluate(frame.Id);
                        active.Remove(frame.Id);
                        path.RemoveAt(path.Count - 1);
                        continue;
                    }
                    stack.Push((frame.Id, frame.Next + 1));
                    var dependency = dependencies[frame.Next];
                    if (results.ContainsKey(dependency)) continue;
                    if (active.Contains(dependency)) Cycle(path.Skip(path.IndexOf(dependency)));
                    Enter(dependency);
                }
                void Enter(string id)
                {
                    active.Add(id);
                    path.Add(id);
                    stack.Push((id, 0));
                }
            }
            return tasks.Select(task => results[task.Identity].Row).ToArray();
        }

        private void Cycle(IEnumerable<string> ids)
            => throw new ArgumentException("循環参照: " + string.Join(", ", ids.Select(id => byId[id].RowId).Distinct().Order().Select(id => $"#{id}")));

        private Result Evaluate(string id)
        {

            var task = byId[id];
            var warnings = new HashSet<string>(StringComparer.Ordinal);
            if (task.Parent is { } parent && !byId.ContainsKey(parent)) warnings.Add("プロジェクト外の親タスク");
            if (task.Assignees.Distinct(StringComparer.Ordinal).Count() > 1) warnings.Add("担当者が複数");
            var dependencies = new List<(PlanTask Task, Result Result)>();
            foreach (var predecessor in predecessors[id])
            {
                if (byId.TryGetValue(predecessor, out var before)) dependencies.Add((before, results[predecessor]));
                else warnings.Add($"プロジェクト外の先行タスク: {predecessor}");
            }
            var summary = children[id].Count > 0;
            var values = children[id].Select(child => results[child]).ToArray();
            var invalid = false;
            if (task.Estimate < 0 || task.Remaining < 0 || task.Actual < 0)
            { warnings.Add("工数が負の値"); invalid = true; }
            if (task.KeepsDates && task.Start > task.End)
            { warnings.Add("開始日が終了日より後"); invalid = true; }
            if (!task.Closed && !task.Fixed && task.Actual > 0 && task.Estimate == 0 && task.Remaining is null)
            { warnings.Add("残が未入力"); invalid = true; }
            if (values.Any(value => !value.Valid))
            { warnings.Add("子タスクに入力エラー"); invalid = true; }
            Result result;
            try
            {
                if (invalid) result = Isolate(task, warnings, summary);
                else if (summary)
                {
                    static decimal? Sum(IEnumerable<decimal?> values)
                    {
                        var known = values.ToArray();
                        if (known.Any(value => value is null)) return null;
                        decimal sum = 0;
                        foreach (var value in known) sum += value!.Value;
                        return sum;
                    }
                    var starts = values.Where(v => v.Start.HasValue).Select(v => v.Start!.Value).ToArray();
                    var ends = values.Where(v => v.End.HasValue).Select(v => v.End!.Value).ToArray();
                    result = Create(task, starts.Length == 0 ? null : starts.Min(), ends.Length == 0 ? null : ends.Max(),
                        DateOrigin.Calculated, DateOrigin.Calculated, "子タスクの集計", warnings, true,
                        Sum(values.Select(v => v.Row.Estimate)), Sum(values.Select(v => v.Row.Remaining)), Sum(values.Select(v => v.Row.Actual)));
                }
                else result = Leaf(task, dependencies, warnings);
            }
            catch (ArgumentOutOfRangeException)
            {
                warnings.Add("日程が日付の範囲外");
                result = Isolate(task, warnings, summary);
            }
            catch (OverflowException)
            {
                warnings.Add(summary ? "工数の集計が範囲外" : "日程が日付の範囲外");
                result = Isolate(task, warnings, summary);
            }
            results.Add(id, result);
            return result;
        }

        private Result Isolate(PlanTask task, HashSet<string> warnings, bool summary)
        {
            var result = Create(task,
                task.GitHubStart is { } start ? Point.Morning(start) : null,
                task.GitHubEnd is { } end ? new Point(end.DayNumber, 18) : null,
                DateOrigin.Kept, DateOrigin.Kept, "入力エラー", warnings, summary);
            // Retain the visible baseline, but never let invalid dates drive another row.
            return result with { Start = null, End = null, Valid = false };
        }
        private Result Leaf(PlanTask task, List<(PlanTask Task, Result Result)> dependencies, HashSet<string> warnings)
        {
            var earliest = Point.Morning(statusDate);
            var reason = "状況日";
            void Consider(Point candidate, string candidateReason, bool winTie = true)
            {
                if (candidate.CompareTo(earliest) > 0 || winTie && candidate.CompareTo(earliest) == 0)
                { earliest = candidate; reason = candidateReason; }
            }
            if (settings.ProjectStart is { } project) Consider(Point.Morning(project), "プロジェクト開始日");
            if (task.StartNoEarlierThan is { } specified)
                Consider(Point.Morning(specified), "開始日指定 " + specified.ToString("M/d", CultureInfo.InvariantCulture));
            // Reverse row order lets the smallest row ID win equal predecessor endpoints.
            foreach (var (before, result) in dependencies.AsEnumerable().Reverse())
                if (result.End is { } finish) Consider(finish, $"#{before.RowId} の終了後");
            Point? start = task.Start is { } s ? Point.Morning(s) : null;
            Point? end = task.End is { } e ? new Point(e.DayNumber, 18) : null;
            var complete = task.IsComplete;
            if (task.KeepsDates)
            {
                if (complete && end is null) warnings.Add("完了タスクの終了日なし");
                if (!complete && task.Fixed)
                {
                    if (start is null || end is null) warnings.Add("日程固定の日付不足");
                    WarnConflict(start);
                }
                return Create(task, start, end, DateOrigin.Kept, DateOrigin.Kept,
                    complete ? "完了" : task.Fixed ? "日程固定" : "工数なし", warnings);
            }
            var work = (task.Remaining ?? task.Estimate)!.Value;
            var person = task.Assignees.Distinct(StringComparer.Ordinal).ToArray() is [var single] && people.TryGetValue(single, out var p) ? p : null;
            Hours rate = (Hours)(person?.Rate ?? 100m) / 100;
            if (work == 0)
                return Create(task, earliest, earliest, DateOrigin.Calculated, DateOrigin.Calculated, reason, warnings);
            Point remainingStart;
            var startOrigin = DateOrigin.Calculated;
            if (task.Actual > 0 && start is { } actualStart)
            {
                remainingStart = actualStart.CompareTo(Point.Morning(statusDate)) > 0 ? actualStart : Point.Morning(statusDate);
                startOrigin = DateOrigin.Kept;
                reason = "開始日を維持";
                WarnConflict(start);
            }
            else
            {
                if (task.Actual > 0) warnings.Add("進行中タスクの開始日なし");
                remainingStart = earliest;
                start = Normalize(remainingStart, person);
            }
            remainingStart = Normalize(remainingStart, person);
            end = AddWork(remainingStart, work, rate, person, out var daily);
            var scheduled = Create(task, start, end, startOrigin, DateOrigin.Calculated, reason, warnings);
            return scheduled with { Row = scheduled.Row with { PlannedHours = daily } };

            void WarnConflict(Point? value)
            {
                if (value is { } at && dependencies.Any(d => d.Result.End is { } finish && at.CompareTo(finish) < 0))
                    warnings.Add("先行タスクより前に開始");
            }
        }

        private Point Normalize(Point point, PlanPerson? person)
        {
            while (true)
            {
                var date = point.Date;
                var working = date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                    && !holidays.Contains(date) && !settings.Calendar.CompanyDaysOff.Contains(date)
                    && !(person?.DaysOff.Contains(date) ?? false);
                if (working)
                {
                    if (point.Hour < 9) return point with { Hour = 9 };
                    if (point.Hour < 13) return point;
                    if (point.Hour < 14) return point with { Hour = 14 };
                    if (point.Hour < 18) return point;
                }
                point = Point.Morning(date.AddDays(1));
            }
        }

        private Point AddWork(Point start, decimal work, Hours rate, PlanPerson? person, out IReadOnlyDictionary<DateOnly, decimal> daily)
        {
            // Even an always-working calendar cannot place more than this in the supported date range.
            Hours remaining = work;
            if (remaining / rate > (Hours)((DateOnly.MaxValue.DayNumber - start.Day + 1m) * 8m))
                throw new ArgumentOutOfRangeException(nameof(work));
            var point = start;
            var allocated = new Dictionary<DateOnly, Hours>();
            while (true)
            {
                var boundary = point.Hour < 13 ? 13 : 18;
                var capacity = (boundary - point.Hour) * rate;
                var used = remaining <= capacity ? remaining : capacity;
                allocated[point.Date] = (allocated.TryGetValue(point.Date, out var previous) ? previous : (Hours)0) + used;
                if (remaining <= capacity)
                {
                    var values = allocated.ToDictionary(p => p.Key, p => p.Value.DailyValue());
                    values[point.Date] += work - values.Values.Sum();
                    daily = values;
                    return point with { Hour = point.Hour + remaining / rate };
                }
                remaining -= capacity;
                point = Normalize(point with { Hour = boundary }, person);
            }
        }

        private Result Create(PlanTask task, Point? start, Point? end, DateOrigin startOrigin, DateOrigin endOrigin,
            string reason, HashSet<string> warnings, bool summary = false,
            decimal? estimate = null, decimal? remaining = null, decimal? actual = null)
        {
            var firstYear = settings.Calendar.Holidays.FirstYear;
            var lastYear = settings.Calendar.Holidays.LastYear;
            bool Covered(int year) => year >= firstYear && year <= lastYear
                || settings.Calendar.ImportedHolidays is { } imported && year >= imported.FirstYear && year <= imported.LastYear;
            if (start is { } from && !Covered(from.Date.Year) || end is { } to && !Covered(to.Date.Year))
                warnings.Add("祝日データの対象年外");
            var row = new ScheduledTask(task,
                new(start?.Date, startOrigin, start?.Date != task.GitHubStart),
                new(end?.Date, endOrigin, end?.Date != task.GitHubEnd),
                summary ? estimate : task.Estimate, summary ? remaining : task.Remaining, summary ? actual : task.Actual,
                summary, reason, warnings.Order(StringComparer.Ordinal).ToArray());
            return new(row, start, end);
        }
    }
}
