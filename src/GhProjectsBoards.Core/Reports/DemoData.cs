using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Core.Reports;

// #271: fictional demo data for the management progress site. It is a pure function of the two tracked evaluation
// plans and their calendars: no clock, GUID or random source is read, so two runs write the same bytes. Every day
// goes through the production recorder's own format (RawRecord.Build + RawStore.Write), so the site is exercised on
// the same path as real data instead of a demo-only shortcut.
internal static class DemoData
{
    internal const string SiteFileName = "site.json";

    private const string OwnerLogin = "demo";
    private const string TimeZoneId = "Asia/Tokyo";
    private const string ProjectUrlPrefix = "https://github.com/users/demo/projects/";

    // A fictional team and fictional repositories: nothing here comes from a real Project (#271 is public).
    private static readonly string[] Repositories = ["demo/order-system", "demo/portal", "demo/mobile-app"];
    private static readonly string[] People =
    [
        "yamada", "sato", "suzuki", "tanaka", "ito", "watanabe", "nakamura", "kobayashi", "kato", "yoshida",
        "yamaguchi", "matsumoto", "inoue", "kimura", "hayashi", "shimizu", "sasaki", "mori", "abe", "ikeda",
    ];

    private static readonly string[] Phases = ["SA", "UI", "SS", "PS", "PG", "PT", "IT", "ST", "OT"];
    private static readonly (string Id, string Name)[] PhaseOptions = Phases.Select(phase => (Id: "P_" + phase, Name: phase)).ToArray();
    private static readonly (string Id, string Name)[] WaterfallStatuses =
        [("S_BACKLOG", "Backlog"), ("S_READY", "Ready"), ("S_PROGRESS", "In progress"), ("S_REVIEW", "In review"), ("S_DONE", "Done")];
    private static readonly (string Id, string Name)[] AgileStatuses =
        [("S_TODO", "Todo"), ("S_DOING", "Doing"), ("S_REVIEW", "Review"), ("S_DONE", "Done")];

    private const int WaterfallBacklog = 0;
    private const int WaterfallReady = 1;
    private const int WaterfallInProgress = 2;
    private const int WaterfallInReview = 3;
    private const int WaterfallDone = 4;
    private const int AgileTodo = 0;
    private const int AgileDoing = 1;
    private const int AgileReview = 2;
    private const int AgileDone = 3;

    private const string StatusFieldId = "F_STATUS";
    private const string PhaseFieldId = "F_PHASE";
    private const string EstimateFieldId = "F_ESTIMATE";
    private const string ActualFieldId = "F_ACTUAL";
    private const string RemainingFieldId = "F_REMAINING";
    private const string StartDateFieldId = "F_START";
    private const string EndDateFieldId = "F_END";
    private const string IterationFieldId = "F_ITERATION";

    private const int IterationDuration = 14;
    private static readonly DateOnly FirstIterationStart = new(2026, 4, 6);

    // Every item was added to its Project before the first recorded day; the content's own creation is earlier.
    private const string WaterfallAddedAt = "2026-10-12T00:00:00Z";
    private const string WaterfallCreatedAt = "2026-09-28T00:00:00Z";
    private const string AgileAddedAt = "2026-04-03T00:00:00Z";
    private const string AgileCreatedAt = "2026-03-16T00:00:00Z";
    private const string JanuaryAddedAt = "2026-07-31T00:00:00Z";
    private const string JanuaryCreatedAt = "2026-07-20T00:00:00Z";
    private const string NewProjectAddedAt = "2026-10-12T00:00:00Z";
    private const string NewProjectCreatedAt = "2026-10-01T00:00:00Z";

    private static readonly string[] RequirementTitles =
    [
        "受注データの外部連携", "在庫照会の画面", "請求書の出力", "入金消込の処理",
        "マスタ保守の画面", "帳票レイアウトの改修", "バッチ処理の再実行", "監査ログの出力",
    ];
    private static readonly string[] WorkNames = ["基本設計", "詳細設計", "実装", "単体テスト", "結合テスト", "レビュー"];
    private static readonly string[] AgileTitles =
    [
        "ログイン画面の改善", "検索条件の保存", "注文履歴の絞り込み", "通知メールの文面",
        "在庫一覧の表示速度", "権限設定の整理", "日次レポートの出力", "入力チェックの統一",
    ];

    internal static void Write(string outFolder, string versionPlanJson, string versionPlan12mJson)
    {
        var projects = CreateProjects(versionPlanJson, versionPlan12mJson);
        var raw = Path.Combine(outFolder, "raw");
        Directory.CreateDirectory(raw);
        foreach (var project in projects) project.Write(raw);
        File.WriteAllText(Path.Combine(outFolder, SiteFileName), Site + "\n", new UTF8Encoding(false));
    }

    // The snapshot the writer would store for one recorded day, so a test can write it with RawStore.Write and read
    // it back. This runs the same generator as Write; only the storage step is left to the caller.
    internal static RawSnapshot Snapshot(int projectNumber, DateOnly day, string versionPlanJson, string versionPlan12mJson)
        => CreateProjects(versionPlanJson, versionPlan12mJson).Single(project => project.Number == projectNumber).Snapshot(day);

    private static DemoProject[] CreateProjects(string versionPlanJson, string versionPlan12mJson) =>
    [
        new SteadyWaterfall(Plan.Read(versionPlanJson)),
        new FinalWeekWaterfall(Plan.Read(versionPlan12mJson)),
        new AgileIterations(),
        new ZeroClosedWaterfall(),
        new JustStartedWaterfall(),
    ];

    // The site configuration: five demo Projects. The order is the display order the brief agreed; the last Project
    // has no period so the list page must sort it last.
    private const string Site = """
        {
          "title": "プロジェクト進捗",
          "demo": true,
          "projects": [
            {
              "url": "https://github.com/users/demo/projects/1",
              "name": "第2027.04版",
              "stateField": "Status",
              "groupField": "工程",
              "periodStart": "2026-10-13",
              "periodEnd": "2027-04-30"
            },
            {
              "url": "https://github.com/users/demo/projects/2",
              "name": "第2027.10版",
              "stateField": "Status",
              "groupField": "工程",
              "periodStart": "2026-10-13",
              "periodEnd": "2027-10-29"
            },
            {
              "url": "https://github.com/users/demo/projects/3",
              "name": "改善バックログ",
              "stateField": "Status",
              "groupField": "Iteration",
              "periodStart": "2026-04-06",
              "periodEnd": "2026-10-25"
            },
            {
              "url": "https://github.com/users/demo/projects/4",
              "name": "第2027.01版",
              "stateField": "Status",
              "groupField": "工程",
              "periodStart": "2026-08-03",
              "periodEnd": "2026-12-25"
            },
            {
              "url": "https://github.com/users/demo/projects/5",
              "name": "新規プロジェクト",
              "stateField": "Status",
              "groupField": "工程"
            }
          ]
        }
        """;

    // One Project's recorded days, in order. A generator rebuilds an item line only when its recorded values change,
    // so the year-long scenario does not pay for 1,000 items on every quiet day.
    private abstract class DemoProject
    {
        protected sealed record Record(DateOnly Date, JsonObject Project, IReadOnlyDictionary<string, string> Items);

        internal abstract int Number { get; }
        protected abstract IEnumerable<Record> Days();

        internal void Write(string rawRoot)
        {
            var lines = new Dictionary<string, string>(StringComparer.Ordinal);
            var elements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var record in Days())
            {
                foreach (var removed in lines.Keys.Where(id => !record.Items.ContainsKey(id)).ToArray())
                {
                    lines.Remove(removed);
                    elements.Remove(removed);
                }
                foreach (var (id, line) in record.Items)
                {
                    if (lines.TryGetValue(id, out var previous) && previous == line) continue;
                    lines[id] = line;
                    elements[id] = JsonDocument.Parse(line).RootElement.Clone();
                }
                var snapshot = RawRecord.Build(
                    JsonDocument.Parse(record.Project.ToJsonString()).RootElement.Clone(),
                    elements.Values, ReadAt(record.Date), record.Date, TimeZoneId, elements.Count);
                RawStore.Write(rawRoot, snapshot);
            }
        }

        internal RawSnapshot Snapshot(DateOnly day)
        {
            foreach (var record in Days())
                if (record.Date == day)
                    return RawRecord.Build(
                        JsonDocument.Parse(record.Project.ToJsonString()).RootElement.Clone(),
                        record.Items.Values.Select(line => JsonDocument.Parse(line).RootElement.Clone()),
                        ReadAt(day), day, TimeZoneId, record.Items.Count);
            throw new ArgumentException($"プロジェクト {Number} は {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} を記録していません。");
        }

        // The recorder runs early in the morning; the demo records the end of its day at 21:00 JST, which is also the
        // time zone the close dates are expressed in, so a day's close is visible in that day's record.
        private static DateTimeOffset ReadAt(DateOnly day) => new(day.Year, day.Month, day.Day, 21, 0, 0, TimeSpan.FromHours(9));
    }

    // The tracked evaluation plans are the source of the waterfall shapes. The demo relies on their exact counts,
    // so a plan that no longer matches fails immediately instead of writing a different dataset silently.
    private sealed record Plan(string Title, DateOnly ProjectStart, DateOnly ProjectEnd, string[] Phases, PlanRow[] Rows)
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        internal static Plan Read(string versionPlanJson)
        {
            var plan = JsonSerializer.Deserialize<Plan>(versionPlanJson, Json)
                ?? throw new ArgumentException("版計画を読み取れませんでした。");
            if (plan.Phases is not { Length: 9 } || !plan.Phases.SequenceEqual(DemoData.Phases))
                throw new ArgumentException("版計画の工程がデモの前提と違います。");
            var parents = plan.Rows.Count(row => row.Parent is null);
            if (parents != 40 || plan.Rows.Count(row => row.Parent is not null) != 1000)
                throw new ArgumentException("版計画は 40 の要求と 1,000 のタスクを前提にしています。");
            return plan;
        }
    }

    private sealed record PlanRow(string Key, string Title, string? Parent, string? Phase, decimal? Estimate, string? Person,
        string[] Predecessors, DateOnly? Start);

    private sealed record ParentPlan(int Index, string Title, string Repository, int Number, string[] Assignees);
    private sealed record SteadyTask(int Index, string Title, string Phase, int ParentIndex, string? Person, decimal Estimate,
        int CloseIndex, int Number);
    private sealed record BatchTask(int Index, string Title, string Phase, int ParentIndex, string? Person, int Number);
    private sealed record AgileIssue(int Index, int Iteration, string Repository, int Number, string Title, string[] Assignees);

    // Scenario 1: waterfall with steady completion. The 1,000 tasks close in plan order, spread evenly over the 144
    // recorded working days; each one walks Backlog -> Ready -> In progress -> In review -> Done before its close.
    private sealed class SteadyWaterfall : DemoProject
    {
        private readonly Plan plan;
        internal SteadyWaterfall(Plan plan) => this.plan = plan;
        internal override int Number => 1;

        protected override IEnumerable<Record> Days()
        {
            var days = WorkingDays(plan.ProjectStart, plan.ProjectEnd).ToArray();
            var parentRows = plan.Rows.Where(row => row.Parent is null).ToArray();
            var taskRows = plan.Rows.Where(row => row.Parent is not null).ToArray();
            var parentIndexes = parentRows.Select((row, index) => (row.Key, Index: index))
                .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.Ordinal);
            var parents = parentRows.Select((row, index) => new ParentPlan(
                index, row.Title, Repositories[index % Repositories.Length], index + 1,
                [People[index % People.Length]])).ToArray();
            var tasks = taskRows.Select((row, index) => new SteadyTask(
                index, row.Title, PhaseOf(row), parentIndexes[row.Parent!], row.Person, EstimateOf(row),
                (int)((long)index * days.Length / taskRows.Length), parents.Length + index + 1)).ToArray();
            var parentClose = Enumerable.Repeat(-1, parents.Length).ToArray();
            foreach (var task in tasks) parentClose[task.ParentIndex] = Math.Max(parentClose[task.ParentIndex], task.CloseIndex);

            var project = WaterfallProject(Number, plan.Title, withNumbers: true, withBuiltIns: true);
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            var lastStatus = Enumerable.Repeat(-1, tasks.Length).ToArray();
            var lastActual = new decimal[tasks.Length];
            var mergeDay = days.Length / 2;
            var pullRequestCloseDay = days.Length - 10;

            for (var d = 0; d < days.Length; d++)
            {
                for (var t = 0; t < tasks.Length; t++)
                {
                    var task = tasks[t];
                    var status = SteadyStatus(d, task.CloseIndex);
                    var (actual, remaining) = SteadyProgress(task.Estimate, d, task.CloseIndex);
                    if (lastStatus[t] == status && lastActual[t] == actual) continue;
                    lastStatus[t] = status;
                    lastActual[t] = actual;
                    var start = Math.Max(0, task.CloseIndex - 4);
                    items[ItemId(Number, "task", t)] = SteadyTaskItem(Number, task, parents[task.ParentIndex],
                        days[start], days[task.CloseIndex], status, actual, remaining, d >= task.CloseIndex, days[task.CloseIndex]);
                }
                for (var p = 0; p < parents.Length; p++)
                {
                    if (d != 0 && d != parentClose[p]) continue;
                    var closed = d >= parentClose[p];
                    items[ItemId(Number, "parent", p)] = WaterfallParentItem(Number, parents[p],
                        closed ? WaterfallDone : WaterfallBacklog, closed, days[parentClose[p]]);
                }
                if (d == 0)
                {
                    items[ItemId(Number, "pr", 1)] = PullRequestItem(Number, 1, 1041, "注文 API のエラー処理", WaterfallInReview, "OPEN", null, null);
                    items[ItemId(Number, "pr", 2)] = PullRequestItem(Number, 2, 1042, "受注画面の入力チェック", WaterfallInReview, "OPEN", null, null);
                    items[ItemId(Number, "pr", 3)] = PullRequestItem(Number, 3, 1043, "在庫バッチの実行時間短縮", WaterfallInProgress, "OPEN", null, null);
                    items[ItemId(Number, "draft", 1)] = DraftItem(Number, 1, "ログイン画面の文言整理", WaterfallBacklog);
                    items[ItemId(Number, "redacted", 1)] = RedactedItem(Number, 1);
                }
                if (d == mergeDay)
                    items[ItemId(Number, "pr", 2)] = PullRequestItem(Number, 2, 1042, "受注画面の入力チェック", WaterfallDone, "MERGED", days[mergeDay], days[mergeDay]);
                if (d == pullRequestCloseDay)
                    items[ItemId(Number, "pr", 3)] = PullRequestItem(Number, 3, 1043, "在庫バッチの実行時間短縮", WaterfallDone, "CLOSED", days[pullRequestCloseDay], null);
                yield return new Record(days[d], project, new Dictionary<string, string>(items, StringComparer.Ordinal));
            }
        }
    }

    // Scenario 2: waterfall with a final-week batch. Nothing closes for 55 weeks; in the last week 720 tasks close,
    // most of them in one batch, and 12 stay in Status "In progress" although they are closed (the owner's decision B
    // keeps the chart on the recorded Status).
    private sealed class FinalWeekWaterfall : DemoProject
    {
        // Tasks close in plan order: the first four days of the last week close a small part, the last day the batch.
        private static readonly int[] ClosesBeforeDay = [100, 160, 200, 220, 720];
        private readonly Plan plan;
        internal FinalWeekWaterfall(Plan plan) => this.plan = plan;
        internal override int Number => 2;

        protected override IEnumerable<Record> Days()
        {
            var days = WorkingDays(plan.ProjectStart, plan.ProjectEnd).ToArray();
            var parentRows = plan.Rows.Where(row => row.Parent is null).ToArray();
            var taskRows = plan.Rows.Where(row => row.Parent is not null).ToArray();
            var parentIndexes = parentRows.Select((row, index) => (row.Key, Index: index))
                .ToDictionary(pair => pair.Key, pair => pair.Index, StringComparer.Ordinal);
            var parents = parentRows.Select((row, index) => new ParentPlan(
                index, row.Title, Repositories[index % Repositories.Length], index + 1,
                [People[index % People.Length]])).ToArray();
            var tasks = taskRows.Select((row, index) => new BatchTask(
                index, row.Title, PhaseOf(row), parentIndexes[row.Parent!], row.Person, parents.Length + index + 1)).ToArray();
            var firstBatchDay = days.Length - ClosesBeforeDay.Length;
            var closeDays = tasks.Select(task => BatchCloseDay(task.Index, firstBatchDay)).ToArray();

            var project = WaterfallProject(Number, plan.Title, withNumbers: false, withBuiltIns: false);
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            var lastStatus = Enumerable.Repeat(-1, tasks.Length).ToArray();
            var lastClosed = new bool[tasks.Length];

            for (var p = 0; p < parents.Length; p++)
                items[ItemId(Number, "parent", p)] = WaterfallParentItem(Number, parents[p], WaterfallBacklog, closed: false, days[0]);

            for (var d = 0; d < days.Length; d++)
            {
                for (var t = 0; t < tasks.Length; t++)
                {
                    var task = tasks[t];
                    var closed = closeDays[t] >= 0 && d >= closeDays[t];
                    var status = closed ? BatchClosedStatus(task.Index) : BatchOpenStatus(task.Index);
                    if (lastStatus[t] == status && lastClosed[t] == closed) continue;
                    lastStatus[t] = status;
                    lastClosed[t] = closed;
                    items[ItemId(Number, "task", t)] = BatchTaskItem(Number, task, parents[task.ParentIndex], status, closed, days[Math.Max(0, closeDays[t])]);
                }
                yield return new Record(days[d], project, new Dictionary<string, string>(items, StringComparer.Ordinal));
            }
        }

        private static int BatchCloseDay(int taskIndex, int firstBatchDay)
        {
            for (var batch = 0; batch < ClosesBeforeDay.Length; batch++)
                if (taskIndex < ClosesBeforeDay[batch]) return firstBatchDay + batch;
            return -1;
        }

        // Before the final week only a small handful of tasks are away from Backlog.
        private static int BatchOpenStatus(int taskIndex) => taskIndex switch
        {
            >= 500 and < 530 => WaterfallReady,
            >= 530 and < 550 => WaterfallInProgress,
            _ => WaterfallBacklog,
        };

        private static int BatchClosedStatus(int taskIndex) => taskIndex is >= 530 and < 542 ? WaterfallInProgress : WaterfallDone;
    }

    // Scenario 3: agile with iteration-end batches. 84 flat Issues (a leaf Issue is a task, so all of them are
    // tasks); each iteration closes five on its last working day, and the current iteration is one record old.
    private sealed class AgileIterations : DemoProject
    {
        private const int IssueCount = 84;
        private const int TasksPerIteration = 5;
        private const int CurrentIteration = 15;
        private static readonly DateOnly LastRecord = new(2026, 10, 23);
        internal override int Number => 3;

        protected override IEnumerable<Record> Days()
        {
            var days = WorkingDays(FirstIterationStart, LastRecord).ToArray();
            var issues = Enumerable.Range(0, IssueCount).Select(index => new AgileIssue(
                index, Math.Min(index / TasksPerIteration + 1, CurrentIteration), Repositories[index % Repositories.Length], index + 1,
                $"S{index + 1:D2} {AgileTitles[index % AgileTitles.Length]}", [People[index % People.Length]])).ToArray();
            var lastStatus = Enumerable.Repeat(-1, issues.Length).ToArray();
            var lastClosed = new bool[issues.Length];
            var items = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var d = 0; d < days.Length; d++)
            {
                var day = days[d];
                for (var i = 0; i < issues.Length; i++)
                {
                    var issue = issues[i];
                    var (status, closed) = AgileState(issue, day);
                    if (lastStatus[i] == status && lastClosed[i] == closed) continue;
                    lastStatus[i] = status;
                    lastClosed[i] = closed;
                    items[ItemId(Number, "issue", i)] = AgileIssueItem(Number, issue, status, closed);
                }
                var offset = (day.DayNumber - FirstIterationStart.DayNumber) / IterationDuration;
                var project = ProjectDefinition(Number, "改善バックログ", [
                    SingleSelectField(StatusFieldId, "Status", AgileStatuses),
                    IterationField(offset + 1, offset),
                ]);
                yield return new Record(day, project, new Dictionary<string, string>(items, StringComparer.Ordinal));
            }
        }

        private static (int Status, bool Closed) AgileState(AgileIssue issue, DateOnly day)
        {
            var start = IterationStart(issue.Iteration);
            if (day < start) return (AgileTodo, false);
            // Iterations start on Monday and only working days are recorded, so the working-day index inside the
            // iteration is the elapsed calendar days minus the weekends.
            var elapsed = day.DayNumber - start.DayNumber;
            var workingDay = elapsed / 7 * 5 + elapsed % 7;
            if (issue.Iteration < CurrentIteration && workingDay >= 9) return (AgileDone, true);
            if (workingDay >= 7 + issue.Index % 2) return (AgileReview, false);
            if (workingDay >= 2 + issue.Index % 5) return (AgileDoing, false);
            return (AgileTodo, false);
        }
    }

    // Scenario 4: a waterfall Project that never closes anything, with two isolated missing days and one whole
    // missing week, so the site has to draw gaps and zero-completion bars.
    private sealed class ZeroClosedWaterfall : DemoProject
    {
        private const int ParentCount = 8;
        private const int TasksPerParent = 6;
        private static readonly DateOnly Start = new(2026, 8, 3);
        private static readonly DateOnly End = new(2026, 10, 23);
        private static readonly HashSet<DateOnly> Skipped =
        [
            new(2026, 9, 1), new(2026, 9, 2),
            new(2026, 9, 14), new(2026, 9, 15), new(2026, 9, 16), new(2026, 9, 17), new(2026, 9, 18),
        ];
        internal override int Number => 4;

        protected override IEnumerable<Record> Days()
        {
            var days = WorkingDays(Start, End).Where(day => !Skipped.Contains(day)).ToArray();
            var project = ProjectDefinition(Number, "第2027.01版", [
                SingleSelectField(StatusFieldId, "Status", WaterfallStatuses),
                SingleSelectField(PhaseFieldId, "工程", PhaseOptions),
                ProjectField(EstimateFieldId, "見積", "NUMBER"),
            ]);
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var p = 0; p < ParentCount; p++)
            {
                var repository = Repositories[p % Repositories.Length];
                items[ItemId(Number, "parent", p)] = SimpleParentItem(Number, p, repository, p + 1, RequirementTitle(p),
                    WaterfallBacklog, closed: false, null, JanuaryAddedAt, JanuaryCreatedAt);
                for (var k = 0; k < TasksPerParent; k++)
                {
                    var index = p * TasksPerParent + k;
                    var phase = Phases[index % Phases.Length];
                    items[ItemId(Number, "task", index)] = SimpleTaskItem(Number, index, repository, ParentCount + index + 1,
                        TaskTitle(p, phase, k), 3 + index % 7 * 2, (repository, p + 1), WaterfallStatuses[WaterfallBacklog],
                        phase, closed: false, null, JanuaryAddedAt, JanuaryCreatedAt);
                }
            }
            foreach (var day in days) yield return new Record(day, project, items);
        }
    }

    // Scenario 5: a Project that has just started. Four tasks are closed, four have no Status value at all, and
    // there is only two weeks of history.
    private sealed class JustStartedWaterfall : DemoProject
    {
        private const int ParentCount = 2;
        private const int TasksPerParent = 5;
        private const int TaskCount = ParentCount * TasksPerParent;
        private static readonly DateOnly Start = new(2026, 10, 13);
        private static readonly DateOnly End = new(2026, 10, 23);
        // The first four tasks close on recorded day index 2..5.
        private static readonly int[] CloseDays = [2, 3, 4, 5];
        internal override int Number => 5;

        protected override IEnumerable<Record> Days()
        {
            var days = WorkingDays(Start, End).ToArray();
            var project = ProjectDefinition(Number, "新規プロジェクト", [
                SingleSelectField(StatusFieldId, "Status", WaterfallStatuses),
                SingleSelectField(PhaseFieldId, "工程", PhaseOptions),
            ]);
            var items = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var p = 0; p < ParentCount; p++)
                items[ItemId(Number, "parent", p)] = SimpleParentItem(Number, p, Repositories[p % Repositories.Length], p + 1,
                    RequirementTitle(p), WaterfallBacklog, closed: false, null, NewProjectAddedAt, NewProjectCreatedAt);
            var lastStatus = Enumerable.Repeat(-1, TaskCount).ToArray();
            var lastClosed = new bool[TaskCount];

            for (var d = 0; d < days.Length; d++)
            {
                for (var t = 0; t < TaskCount; t++)
                {
                    var (status, closed) = StartedState(t, d);
                    if (lastStatus[t] == status && lastClosed[t] == closed) continue;
                    lastStatus[t] = status ?? -1;
                    lastClosed[t] = closed;
                    var p = t / TasksPerParent;
                    var phase = Phases[t % Phases.Length];
                    items[ItemId(Number, "task", t)] = SimpleTaskItem(Number, t, Repositories[p % Repositories.Length],
                        ParentCount + t + 1, TaskTitle(p, phase, t % TasksPerParent), null, (Repositories[p % Repositories.Length], p + 1),
                        status is { } chosen ? WaterfallStatuses[chosen] : null, phase, closed,
                        closed ? days[CloseDays[t]] : null, NewProjectAddedAt, NewProjectCreatedAt);
                }
                yield return new Record(days[d], project, new Dictionary<string, string>(items, StringComparer.Ordinal));
            }
        }

        // Returns a Status option index, or null for a task that has no Status value at all.
        private static (int? Status, bool Closed) StartedState(int taskIndex, int day)
        {
            if (taskIndex < CloseDays.Length) return day >= CloseDays[taskIndex] ? (WaterfallDone, true) : (WaterfallInProgress, false);
            return taskIndex switch
            {
                8 => (WaterfallInProgress, false),
                9 => (WaterfallBacklog, false),
                _ => (null, false),
            };
        }
    }

    private static IEnumerable<DateOnly> WorkingDays(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) yield return day;
    }

    private static string PhaseOf(PlanRow row)
        => row.Phase ?? throw new InvalidOperationException("デモのタスクには工程が必要です: " + row.Key);

    private static decimal EstimateOf(PlanRow row)
        => row.Estimate ?? throw new InvalidOperationException("デモのタスクには見積が必要です: " + row.Key);

    private static string PersonLogin(string? identity)
        => identity is { Length: > 1 } && identity[0] == 'U'
            && int.TryParse(identity.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number >= 1 && number <= People.Length
            ? People[number - 1]
            : People[0];

    private static (string Id, string Name) PhaseOption(string phase) => ("P_" + phase, phase);

    private static DateOnly IterationStart(int iteration) => FirstIterationStart.AddDays(IterationDuration * (iteration - 1));

    private static string IterationTitle(int iteration) => "Iteration " + iteration.ToString(CultureInfo.InvariantCulture);

    private static string IterationId(int iteration) => "IT_" + iteration.ToString(CultureInfo.InvariantCulture);

    private static string RequirementTitle(int parentIndex)
        => $"R{parentIndex + 1:D2} {RequirementTitles[parentIndex % RequirementTitles.Length]}";

    private static string TaskTitle(int parentIndex, string phase, int within)
        => $"R{parentIndex + 1:D2} {phase}-{within + 1:D3} {WorkNames[within % WorkNames.Length]}";

    private static string ItemId(int project, string kind, int index)
        => "PVTI_" + project.ToString(CultureInfo.InvariantCulture) + "_" + kind + "_" + index.ToString(CultureInfo.InvariantCulture);

    private static string Url(int projectNumber) => ProjectUrlPrefix + projectNumber.ToString(CultureInfo.InvariantCulture);

    private static string DateText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Close times are 18:00 JST so the site's close date (the record's time zone) is the recorded day.
    private static string ClosedStamp(DateOnly day) => DateText(day) + "T09:00:00Z";

    // The shape GitHub's GraphQL returns for one Project, which is what RawRecord.Build consumes.
    private static JsonObject ProjectDefinition(int number, string title, IEnumerable<JsonObject> fields)
    {
        var definitions = fields.ToArray();
        var nodes = new JsonArray();
        foreach (var field in definitions) nodes.Add(field);
        return new JsonObject
        {
            ["__typename"] = "ProjectV2",
            ["id"] = "PVT_demo_" + number.ToString(CultureInfo.InvariantCulture),
            ["number"] = number,
            ["title"] = title,
            ["url"] = Url(number),
            ["closed"] = false,
            ["owner"] = new JsonObject { ["__typename"] = "User", ["login"] = OwnerLogin },
            ["fields"] = new JsonObject { ["totalCount"] = definitions.Length, ["nodes"] = nodes },
        };
    }

    private static JsonObject ProjectField(string id, string name, string dataType) => new()
    {
        ["__typename"] = "ProjectV2Field",
        ["id"] = id,
        ["name"] = name,
        ["dataType"] = dataType,
        ["isIssueField"] = false,
        ["issueField"] = null,
    };

    private static JsonObject SingleSelectField(string id, string name, IReadOnlyList<(string Id, string Name)> options)
    {
        var nodes = new JsonArray();
        foreach (var option in options) nodes.Add(new JsonObject { ["id"] = option.Id, ["name"] = option.Name });
        return new JsonObject
        {
            ["__typename"] = "ProjectV2SingleSelectField",
            ["id"] = id,
            ["name"] = name,
            ["dataType"] = "SINGLE_SELECT",
            ["isIssueField"] = false,
            ["options"] = nodes,
            ["issueField"] = null,
        };
    }

    private static JsonObject IterationField(int current, int completed)
    {
        var iterations = new JsonArray();
        for (var iteration = 1; iteration <= completed; iteration++) iterations.Add(IterationDefinition(iteration));
        return new JsonObject
        {
            ["__typename"] = "ProjectV2IterationField",
            ["id"] = IterationFieldId,
            ["name"] = "Iteration",
            ["dataType"] = "ITERATION",
            ["isIssueField"] = false,
            ["configuration"] = new JsonObject
            {
                ["duration"] = IterationDuration,
                ["startDay"] = 1,
                ["iterations"] = new JsonArray(IterationDefinition(current)),
                ["completedIterations"] = iterations,
            },
        };
    }

    private static JsonObject IterationDefinition(int iteration) => new()
    {
        ["id"] = IterationId(iteration),
        ["title"] = IterationTitle(iteration),
        ["startDate"] = DateText(IterationStart(iteration)),
        ["duration"] = IterationDuration,
    };

    private static JsonObject FieldRef(string id, string name) => new() { ["id"] = id, ["name"] = name };

    private static JsonObject SingleSelectValue(string fieldId, string fieldName, (string Id, string Name) option) => new()
    {
        ["__typename"] = "ProjectV2ItemFieldSingleSelectValue",
        ["name"] = option.Name,
        ["optionId"] = option.Id,
        ["field"] = FieldRef(fieldId, fieldName),
    };

    private static JsonObject NumberValue(string fieldId, string fieldName, decimal number) => new()
    {
        ["__typename"] = "ProjectV2ItemFieldNumberValue",
        ["number"] = number,
        ["field"] = FieldRef(fieldId, fieldName),
    };

    private static JsonObject DateValue(string fieldId, string fieldName, DateOnly day) => new()
    {
        ["__typename"] = "ProjectV2ItemFieldDateValue",
        ["date"] = DateText(day),
        ["field"] = FieldRef(fieldId, fieldName),
    };

    private static JsonObject IterationValue(int iteration) => new()
    {
        ["__typename"] = "ProjectV2ItemFieldIterationValue",
        ["title"] = IterationTitle(iteration),
        ["iterationId"] = IterationId(iteration),
        ["startDate"] = DateText(IterationStart(iteration)),
        ["duration"] = IterationDuration,
        ["field"] = FieldRef(IterationFieldId, "Iteration"),
    };

    private static JsonObject LoginsConnection(IReadOnlyList<string> logins)
    {
        var nodes = new JsonArray();
        foreach (var login in logins) nodes.Add(new JsonObject { ["login"] = login });
        return new JsonObject { ["totalCount"] = logins.Count, ["nodes"] = nodes };
    }

    // The GraphQL shape of one Project item, which is what RawRecord.Build consumes.
    private static string Item(string id, string type, string addedAt, JsonObject? content, params JsonObject[] values)
    {
        var nodes = new JsonArray();
        foreach (var value in values) nodes.Add(value);
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["isArchived"] = false,
            ["createdAt"] = addedAt,
            ["content"] = content,
            ["fieldValues"] = new JsonObject { ["totalCount"] = values.Length, ["nodes"] = nodes },
        }.ToJsonString();
    }

    private static JsonObject IssueContent(string repository, int number, string title, string state, string? closedAt,
        string? issueType, (string Repository, int Number)? parent, IReadOnlyList<string> assignees, string createdAt) => new()
    {
        ["__typename"] = "Issue",
        ["id"] = "I_" + repository.Replace('/', '_') + "_" + number.ToString(CultureInfo.InvariantCulture),
        ["number"] = number,
        ["title"] = title,
        ["url"] = "https://github.com/" + repository + "/issues/" + number.ToString(CultureInfo.InvariantCulture),
        ["state"] = state,
        ["stateReason"] = state == "CLOSED" ? "COMPLETED" : null,
        ["createdAt"] = createdAt,
        ["closedAt"] = closedAt,
        ["repository"] = new JsonObject { ["nameWithOwner"] = repository },
        ["assignees"] = LoginsConnection(assignees),
        ["labels"] = new JsonObject { ["totalCount"] = 0, ["nodes"] = new JsonArray() },
        ["milestone"] = null,
        ["issueType"] = issueType is null ? null : new JsonObject { ["name"] = issueType },
        ["parent"] = parent is { } reference
            ? new JsonObject
            {
                ["number"] = reference.Number,
                ["repository"] = new JsonObject { ["nameWithOwner"] = reference.Repository },
            }
            : null,
        ["blockedBy"] = new JsonObject { ["totalCount"] = 0, ["nodes"] = new JsonArray() },
    };

    private static JsonObject PullRequestContent(string repository, int number, string title, string state, string? closedAt,
        string? mergedAt, IReadOnlyList<string> assignees) => new()
    {
        ["__typename"] = "PullRequest",
        ["id"] = "PR_" + repository.Replace('/', '_') + "_" + number.ToString(CultureInfo.InvariantCulture),
        ["number"] = number,
        ["title"] = title,
        ["url"] = "https://github.com/" + repository + "/pull/" + number.ToString(CultureInfo.InvariantCulture),
        ["state"] = state,
        ["createdAt"] = WaterfallCreatedAt,
        ["closedAt"] = closedAt,
        ["repository"] = new JsonObject { ["nameWithOwner"] = repository },
        ["assignees"] = LoginsConnection(assignees),
        ["labels"] = new JsonObject { ["totalCount"] = 0, ["nodes"] = new JsonArray() },
        ["milestone"] = null,
        ["mergedAt"] = mergedAt,
    };

    private static JsonObject DraftContent(string title) => new()
    {
        ["__typename"] = "DraftIssue",
        ["id"] = "DI_demo_1",
        ["title"] = title,
        ["createdAt"] = WaterfallCreatedAt,
    };

    private static string PullRequestItem(int project, int index, int number, string title, int status, string state,
        DateOnly? closedDay, DateOnly? mergedDay)
    {
        var repository = Repositories[index % Repositories.Length];
        var content = PullRequestContent(repository, number, title, state,
            closedDay is { } closed ? ClosedStamp(closed) : null,
            mergedDay is { } merged ? ClosedStamp(merged) : null,
            [People[index % People.Length]]);
        return Item(ItemId(project, "pr", index), "PULL_REQUEST", WaterfallAddedAt, content,
            SingleSelectValue(StatusFieldId, "Status", WaterfallStatuses[status]));
    }

    private static string DraftItem(int project, int index, string title, int status)
        => Item(ItemId(project, "draft", index), "DRAFT_ISSUE", WaterfallAddedAt, DraftContent(title),
            SingleSelectValue(StatusFieldId, "Status", WaterfallStatuses[status]));

    private static string RedactedItem(int project, int index)
        => Item(ItemId(project, "redacted", index), "REDACTED", WaterfallAddedAt, content: null);

    private static JsonObject WaterfallProject(int number, string title, bool withNumbers, bool withBuiltIns)
    {
        var fields = new List<JsonObject>();
        if (withBuiltIns)
        {
            fields.Add(ProjectField("F_TITLE", "Title", "TITLE"));
            fields.Add(ProjectField("F_ASSIGNEES", "Assignees", "ASSIGNEES"));
        }
        fields.Add(SingleSelectField(StatusFieldId, "Status", WaterfallStatuses));
        fields.Add(SingleSelectField(PhaseFieldId, "工程", PhaseOptions));
        if (withNumbers)
        {
            fields.Add(ProjectField(EstimateFieldId, "見積", "NUMBER"));
            fields.Add(ProjectField(ActualFieldId, "実績", "NUMBER"));
            fields.Add(ProjectField(RemainingFieldId, "残", "NUMBER"));
            fields.Add(ProjectField(StartDateFieldId, "開始日", "DATE"));
            fields.Add(ProjectField(EndDateFieldId, "終了日", "DATE"));
        }
        return ProjectDefinition(number, title, fields);
    }

    private static int SteadyStatus(int day, int closeIndex)
    {
        if (day >= closeIndex) return WaterfallDone;
        if (day == closeIndex - 1) return WaterfallInReview;
        if (day >= closeIndex - 3) return WaterfallInProgress;
        if (day >= closeIndex - 5) return WaterfallReady;
        return WaterfallBacklog;
    }

    private static (decimal Actual, decimal Remaining) SteadyProgress(decimal estimate, int day, int closeIndex)
    {
        var start = Math.Max(0, closeIndex - 4);
        var length = closeIndex - start + 1;
        if (day < start) return (0, estimate);
        if (day >= closeIndex) return (estimate, 0);
        var actual = decimal.Floor(estimate * (day - start + 1) / length);
        return (actual, estimate - actual);
    }

    private static string SteadyTaskItem(int project, SteadyTask task, ParentPlan parent, DateOnly start, DateOnly end,
        int status, decimal actual, decimal remaining, bool closed, DateOnly closeDay)
    {
        var content = IssueContent(parent.Repository, task.Number, task.Title, closed ? "CLOSED" : "OPEN",
            closed ? ClosedStamp(closeDay) : null, "Task", (parent.Repository, parent.Number), [PersonLogin(task.Person)],
            WaterfallCreatedAt);
        return Item(ItemId(project, "task", task.Index), "ISSUE", WaterfallAddedAt, content,
            SingleSelectValue(StatusFieldId, "Status", WaterfallStatuses[status]),
            SingleSelectValue(PhaseFieldId, "工程", PhaseOption(task.Phase)),
            NumberValue(EstimateFieldId, "見積", task.Estimate),
            NumberValue(ActualFieldId, "実績", actual),
            NumberValue(RemainingFieldId, "残", remaining),
            DateValue(StartDateFieldId, "開始日", start),
            DateValue(EndDateFieldId, "終了日", end));
    }

    private static string BatchTaskItem(int project, BatchTask task, ParentPlan parent, int status, bool closed, DateOnly closeDay)
    {
        var content = IssueContent(parent.Repository, task.Number, task.Title, closed ? "CLOSED" : "OPEN",
            closed ? ClosedStamp(closeDay) : null, "Task", (parent.Repository, parent.Number), [PersonLogin(task.Person)],
            WaterfallCreatedAt);
        return Item(ItemId(project, "task", task.Index), "ISSUE", WaterfallAddedAt, content,
            SingleSelectValue(StatusFieldId, "Status", WaterfallStatuses[status]),
            SingleSelectValue(PhaseFieldId, "工程", PhaseOption(task.Phase)));
    }

    private static string WaterfallParentItem(int project, ParentPlan parent, int status, bool closed, DateOnly closeDay)
    {
        var content = IssueContent(parent.Repository, parent.Number, parent.Title, closed ? "CLOSED" : "OPEN",
            closed ? ClosedStamp(closeDay) : null, "Requirement", null, parent.Assignees, WaterfallCreatedAt);
        return Item(ItemId(project, "parent", parent.Index), "ISSUE", WaterfallAddedAt, content,
            SingleSelectValue(StatusFieldId, "Status", WaterfallStatuses[status]));
    }

    private static string AgileIssueItem(int project, AgileIssue issue, int status, bool closed)
    {
        var content = IssueContent(issue.Repository, issue.Number, issue.Title, closed ? "CLOSED" : "OPEN",
            closed ? ClosedStamp(IterationStart(issue.Iteration).AddDays(11)) : null, "Task", null, issue.Assignees, AgileCreatedAt);
        return Item(ItemId(project, "issue", issue.Index), "ISSUE", AgileAddedAt, content,
            SingleSelectValue(StatusFieldId, "Status", AgileStatuses[status]),
            IterationValue(issue.Iteration));
    }

    private static string SimpleParentItem(int project, int index, string repository, int number, string title, int status,
        bool closed, DateOnly? closeDay, string addedAt, string createdAt)
    {
        var content = IssueContent(repository, number, title, closed ? "CLOSED" : "OPEN",
            closed ? ClosedStamp(closeDay!.Value) : null, "Requirement", null, [People[index % People.Length]], createdAt);
        return Item(ItemId(project, "parent", index), "ISSUE", addedAt, content,
            SingleSelectValue(StatusFieldId, "Status", WaterfallStatuses[status]));
    }

    private static string SimpleTaskItem(int project, int index, string repository, int number, string title, decimal? estimate,
        (string Repository, int Number) parent, (string Id, string Name)? status, string phase, bool closed, DateOnly? closeDay,
        string addedAt, string createdAt)
    {
        var content = IssueContent(repository, number, title, closed ? "CLOSED" : "OPEN",
            closed ? ClosedStamp(closeDay!.Value) : null, "Task", parent, [People[index % People.Length]], createdAt);
        var values = new List<JsonObject>();
        if (status is { } option) values.Add(SingleSelectValue(StatusFieldId, "Status", option));
        values.Add(SingleSelectValue(PhaseFieldId, "工程", PhaseOption(phase)));
        if (estimate is { } estimateValue) values.Add(NumberValue(EstimateFieldId, "見積", estimateValue));
        return Item(ItemId(project, "task", index), "ISSUE", addedAt, content, values.ToArray());
    }
}
