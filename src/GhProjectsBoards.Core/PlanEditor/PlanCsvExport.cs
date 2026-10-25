using System.Globalization;
using System.Text;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanCsvExport
{
    internal static byte[] Create(PlanDocument document, DateOnly today)
    {
        var columns = PlanSheetColumns.All(document);
        var visible = columns.Where(c => c.Visible && c.Key != "Id").ToArray();
        var names = columns.ToDictionary(c => c.Key, c => PlanSheetColumns.ReviewName(c.Key, c));
        var schedule = PlanOperations.Schedule(document, today);
        var scheduled = schedule.ToDictionary(t => t.Input.Identity);
        var changes = PlanOperations.Changes(document, today, schedule);
        var taskNames = PlanTaskNames.Of(document);
        var people = document.State.Settings.People.ToDictionary(p => p.Identity, p => p.Name);
        var published = document.Baseline.Rows.ToDictionary(r => r.Identity, r => r.End);
        var calendar = new PlanCalendar { ImportedHolidays = document.State.Settings.ImportedHolidays?.ToPreset(), CompanyDaysOff = document.State.Settings.CompanyDaysOff.ToHashSet() };
        var lateness = PlanLateness.Classify(schedule, calendar, document.State.Settings.StatusDate ?? today);
        var text = new StringBuilder("\uFEFF");
        var rowsById = document.State.Rows.ToDictionary(r => r.Identity);
        WriteRow([Text("ID"), .. visible.Select(c => Text(c.Name)), Text("要求事項"), Text("リポジトリ"), Text("Issue 番号"), Text("URL"), Text("未発行")]);
        foreach (var row in PlanOperations.EffectiveRows(document))
        {
            var task = scheduled[row.Identity];
            var link = row.Identity.StartsWith("local:", StringComparison.Ordinal) ? null : document.Sync.IssueLinks.GetValueOrDefault(row.Identity);
            var number = link is not null && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
                && long.TryParse(uri.Segments.LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var issue) && issue > 0
                ? issue.ToString(CultureInfo.InvariantCulture) : "";
            var marker = !changes.Fields.TryGetValue(row.Identity, out var fields) ? "" : fields.Contains(PlanField.NewTask) ? "新規"
                : string.Join(", ", fields.Select(f => names.GetValueOrDefault(f) ?? PlanSheetColumns.ReviewName(f)));
            WriteRow([Text(taskNames[row.Identity]), .. visible.Select(c => Cell(c.Key)),
                Text(Requirement(row)), Text(row.Repository), number, Text(link?.Url ?? ""), Text(marker)]);

            string Cell(string field)
            {
                if (PlanSheetColumns.IsExtra(field))
                {
                    if (PlanExtraEditing.Read(row, field).Availability is not (ValueAvailability.Present or ValueAvailability.Empty)) return Text("取得できません");
                    return PlanExtraEditing.Value(row, field) is decimal numeric ? Number(numeric) : Text(PlanExtraEditing.Value(row, field)?.ToString() ?? "");
                }
                return field switch {
                    "Indicator" => Text(PlanSheetText.Mark(taskNames, row, task, lateness.Tasks.GetValueOrDefault(row.Identity),
                        PlanSheetText.RemoteProblem(document.Sync, row.Identity),
                        PlanMarkVisibility.From(document.State.Settings)).Text),
                    "ForecastEnd" => Text(PlanSheetText.ForecastEnd(task, lateness.Tasks.GetValueOrDefault(row.Identity))),
                    PlanField.Estimate => Number(task.Estimate), PlanField.Remaining => Number(task.Remaining), PlanField.Actual => Number(task.Actual),
                    PlanField.Start => Date(task.Start.Value), PlanField.End => Date(task.End.Value),
                    PlanField.Assignees => Text(string.Join(", ", row.Assignees.Select(Assignee))),
                    PlanField.Predecessors => Text(taskNames.List(row.Predecessors)),
                    PlanField.Labels => Text(string.Join(", ", row.Labels)),
                    PlanField.Phase => Text(document.State.Settings.Columns.Any(c => c.Role == field) ? row.Phase ?? "" : ""),
                    _ => Text(PlanOperations.Value(row, field)?.ToString() ?? "")
                };
            }
        }
        return Encoding.UTF8.GetBytes(text.ToString());

        void WriteRow(IEnumerable<string> cells) => text.AppendJoin(',', cells).Append("\r\n");
        string Requirement(PlanRow row)
        {
            if (row.Parent is null) return "";
            var top = row;
            for (var depth = 0; depth < rowsById.Count && top.Parent is not null && rowsById.TryGetValue(top.Parent, out var parent); depth++) top = parent;
            return top.Title;
        }
        string Assignee(string id) => !string.IsNullOrWhiteSpace(people.GetValueOrDefault(id)) ? people[id]
            : !string.IsNullOrWhiteSpace(document.Sync.PeopleNames.GetValueOrDefault(id)) ? document.Sync.PeopleNames[id] : "担当者（未確認）";
    }

    private static string Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "";
    private static string Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    private static string Text(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
