using System.Globalization;

namespace GhProjectsBoards.Core.Reports;

// #271: the numbers one Project's pages present. They are recomputed from the raw days only; nothing is stored.
internal sealed record WeeklyBar(DateOnly Monday, DateOnly? Day, int Total, IReadOnlyList<int> Counts);

internal sealed record GroupRow(string Name, int Tasks, int Closed);

internal sealed record ProjectProgress(
    SiteProjectConfig Config,
    RawProject Project,
    DateOnly LatestDay,
    int RecordedDays,
    IReadOnlyList<RawItem> LatestItems,
    IReadOnlySet<string> LatestTaskIds,
    int TaskCount,
    int ClosedCount,
    IReadOnlyList<string> States,
    IReadOnlyList<WeeklyBar> Weeks,
    IReadOnlyList<GroupRow> Groups);

internal static class SiteAnalysis
{
    internal const string UnsetState = "未設定";

    internal static ProjectProgress Compute(SiteProjectConfig config, string dataRoot)
    {
        var days = RawDay.Days(dataRoot, config.Owner, config.Number);
        if (days.Count == 0)
            throw new InvalidOperationException(
                $"プロジェクト {config.Name} の記録がありません: {Path.Combine(dataRoot, config.Owner, config.Number.ToString(CultureInfo.InvariantCulture))}");
        var latestDay = days.Max;
        var project = RawDay.ReadProject(dataRoot, config.Owner, config.Number, latestDay);
        var state = Field(project, config, config.StateField, "単一選択", "SINGLE_SELECT");
        var group = Field(project, config, config.GroupField, "単一選択またはイテレーション", "SINGLE_SELECT", "ITERATION");
        var stateNames = state.Options.Select(o => o.Name).ToArray();
        var groupLabels = group.DataType == "ITERATION"
            ? Iterations(group).Select(i => (Id: (string?)i.Id, i.Title)).ToArray()
            : group.Options.Select(o => (Id: (string?)o.Id, o.Name)).ToArray();

        // One bar per Monday-Sunday week from the first recorded week to the latest recorded day. A week's bar is the
        // last recorded day in it; a week without any record stays an empty slot instead of carrying values forward.
        var recorded = days.ToArray();
        var next = 0;
        var weekCounts = new List<(DateOnly Monday, DateOnly? Day, int[] States, int Unset, int Total)>();
        for (var monday = Monday(recorded[0]); monday <= Monday(latestDay); monday = monday.AddDays(7))
        {
            DateOnly? last = null;
            while (next < recorded.Length && Monday(recorded[next]) == monday) last = recorded[next++];
            if (last is null)
            {
                weekCounts.Add((monday, null, new int[stateNames.Length], 0, 0));
                continue;
            }
            var items = RawDay.ReadItems(dataRoot, config.Owner, config.Number, last.Value);
            var tasks = RawDay.TaskIds(items);
            var counts = new int[stateNames.Length];
            var unset = 0;
            foreach (var item in items)
            {
                if (!tasks.Contains(item.Id)) continue;
                var index = StateIndex(item, state, stateNames);
                if (index < 0) unset++;
                else counts[index]++;
            }
            weekCounts.Add((monday, last, counts, unset, tasks.Count));
        }
        var includeUnset = weekCounts.Any(w => w.Unset > 0);
        var states = stateNames.Concat(includeUnset ? new[] { UnsetState } : []).ToArray();
        var weeks = weekCounts.Select(w =>
        {
            var counts = includeUnset ? w.States.Append(w.Unset).ToArray() : w.States;
            return new WeeklyBar(w.Monday, w.Day, w.Total, counts);
        }).ToArray();

        var latestItems = RawDay.ReadItems(dataRoot, config.Owner, config.Number, latestDay);
        var latestTasks = RawDay.TaskIds(latestItems);
        var closed = latestItems.Count(i => latestTasks.Contains(i.Id) && i.Content?.State == "CLOSED");
        return new ProjectProgress(
            config, project, latestDay, days.Count, latestItems, latestTasks, latestTasks.Count, closed,
            states, weeks, GroupRows(latestItems, latestTasks, group, groupLabels));
    }

    private static RawField Field(RawProject project, SiteProjectConfig config, string name, string description, params string[] types)
    {
        var field = project.Fields.FirstOrDefault(f => f.Name == name);
        if (field is null) throw new InvalidOperationException($"プロジェクト {config.Name} の列 {name} が見つかりません。");
        if (!types.Contains(field.DataType, StringComparer.Ordinal))
            throw new InvalidOperationException($"プロジェクト {config.Name} の列 {name} は{description}ではありません（{field.DataType}）。");
        return field;
    }

    private static IReadOnlyList<RawIteration> Iterations(RawField field)
        => field.Iterations.Concat(field.CompletedIterations)
            .GroupBy(i => i.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(i => i.StartDate).ThenBy(i => i.Title, StringComparer.Ordinal).ToArray();

    private static int StateIndex(RawItem item, RawField field, string[] names)
    {
        var id = RawDay.OptionId(item, field.Name);
        if (id is { } optionId)
            for (var n = 0; n < field.Options.Count; n++)
                if (field.Options[n].Id == optionId) return n;
        var name = RawDay.OptionName(item, field.Name);
        return name is null ? -1 : Array.IndexOf(names, name);
    }

    private static IReadOnlyList<GroupRow> GroupRows(
        IReadOnlyList<RawItem> items, IReadOnlySet<string> tasks, RawField field,
        IReadOnlyList<(string? Id, string Name)> labels)
    {
        var taskCounts = new int[labels.Count];
        var closedCounts = new int[labels.Count];
        var unset = 0;
        var unsetClosed = 0;
        foreach (var item in items)
        {
            if (!tasks.Contains(item.Id)) continue;
            var index = GroupIndex(item, field, labels);
            if (index < 0)
            {
                unset++;
                if (item.Content?.State == "CLOSED") unsetClosed++;
                continue;
            }
            taskCounts[index]++;
            if (item.Content?.State == "CLOSED") closedCounts[index]++;
        }
        var rows = new List<GroupRow>();
        for (var n = 0; n < labels.Count; n++) rows.Add(new GroupRow(labels[n].Name, taskCounts[n], closedCounts[n]));
        if (unset > 0) rows.Add(new GroupRow(UnsetState, unset, unsetClosed));
        return rows;
    }

    private static int GroupIndex(RawItem item, RawField field, IReadOnlyList<(string? Id, string Name)> labels)
    {
        string? id = null, name = null;
        if (field.DataType == "ITERATION")
        {
            id = RawDay.IterationId(item, field.Name);
            name = RawDay.IterationTitle(item, field.Name);
        }
        else
        {
            id = RawDay.OptionId(item, field.Name);
            name = RawDay.OptionName(item, field.Name);
        }
        if (id is { } value)
            for (var n = 0; n < labels.Count; n++)
                if (labels[n].Id == value) return n;
        if (name is { } text)
            for (var n = 0; n < labels.Count; n++)
                if (labels[n].Name == text) return n;
        return -1;
    }

    internal static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    internal static int Percent(int closed, int total)
        => total == 0 ? 0 : (int)Math.Round(closed * 100m / total, MidpointRounding.AwayFromZero);

    internal static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Period(SiteProjectConfig config)
        => config.PeriodStart is { } start && config.PeriodEnd is { } end ? Date(start) + " – " + Date(end) : "—";

    internal static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    internal static string DayOfWeek(DateOnly day) => day.ToString("ddd", CultureInfo.GetCultureInfo("ja-JP"));

    internal static bool Merged(RawItem item) => item.Content?.MergedAt is not null;
}
