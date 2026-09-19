using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private static StackPanel PlanningSection(StackPanel parent, string title)
    {
        var panel = new StackPanel { Spacing = 10 };
        var expander = new Expander { Header = title, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(expander, "PlanSection-" + title); parent.Children.Add(expander);
        return panel;
    }
    private Func<(PlanningPerson[] People, PlanningCalendar Calendar)> PlanningSettings(StackPanel parent, ProjectPlanning plan)
    {
        var peoplePanel = PlanningSection(parent, "担当者・配賦");
        var people = plan.People.Concat(registration.Snapshot.Issues.Values.SelectMany(i => i.Native?.Assignees ?? [])
            .Select(a => new PlanningPerson(a.Id.NodeId, a.Login))).DistinctBy(p => p.Id).ToArray();
        var inputs = new List<(PlanningPerson Person, CheckBox Selected, TextBox Weight)>();
        foreach (var person in people)
        {
            var check = new CheckBox { Content = person.Name, IsChecked = plan.People.Any(p => p.Id == person.Id) };
            AutomationProperties.SetAutomationId(check, "PlanPerson-" + person.Id); peoplePanel.Children.Add(check);
            var weight = PlanningText(peoplePanel, "このProjectの配賦（0–100%）", "PlanWeight-" + person.Id, person.WeightPercent.ToString(CultureInfo.InvariantCulture));
            inputs.Add((person, check, weight));
        }
        if (people.Length == 0) peoplePanel.Children.Add(new TextBlock { Text = "担当者はGitHubの担当者を再取得すると選べます。担当なしは共通・暫定計画です。", TextWrapping = TextWrapping.Wrap });
        var calendarPanel = PlanningSection(parent, "カレンダー・祝日");
        var ignore = new CheckBox { Content = "祝日を考慮しない", IsChecked = plan.Calendar.HolidaysNotConsidered };
        AutomationProperties.SetAutomationId(ignore, "PlanIgnoreHolidays"); calendarPanel.Children.Add(ignore);
        var preset = PlanningContract.BundledHolidays();
        var oldDates = plan.Calendar.Holidays.Dates.ToDictionary(d => d.Date, d => d.Name);
        var newDates = preset.Dates.ToDictionary(d => d.Date, d => d.Name);
        var differences = oldDates.Keys.Union(newDates.Keys).Where(d => oldDates.GetValueOrDefault(d) != newDates.GetValueOrDefault(d)).Order().ToArray();
        var adopt = new CheckBox { Content = $"同梱祝日を採用（{preset.FirstYear}–{preset.LastYear}）" };
        AutomationProperties.SetAutomationId(adopt, "PlanAdoptHolidays"); calendarPanel.Children.Add(adopt);
        calendarPanel.Children.Add(new TextBlock { Text = differences.Length == 0 ? "採用済み祝日との差分なし。個別例外・Manual・実績は保持します。"
            : "変更日: " + string.Join("、", differences.Select(d => d.ToString("yyyy-MM-dd"))), TextWrapping = TextWrapping.Wrap });
        calendarPanel.Children.Add(new TextBlock { Text = "日付例外：空の時間帯は休日。担当者別 → Project共通 → 通常週・祝日の順で採用します。", TextWrapping = TextWrapping.Wrap });
        var exceptions = new List<(StackPanel Row, TextBox Day, ComboBox Person, TextBox Intervals)>();
        var exceptionList = new StackPanel { Spacing = 12 }; calendarPanel.Children.Add(exceptionList);
        void AddException(CalendarException? existing)
        {
            var row = new StackPanel { Spacing = 6 }; exceptionList.Children.Add(row);
            var day = PlanningText(row, "日付 yyyy-MM-dd", "PlanExceptionDay-" + exceptions.Count, existing?.Date.ToString("yyyy-MM-dd"));
            var owner = new ComboBox { Header = "対象", HorizontalAlignment = HorizontalAlignment.Stretch };
            owner.Items.Add(new ComboBoxItem { Content = "Project共通", Tag = "" });
            foreach (var person in people) owner.Items.Add(new ComboBoxItem { Content = person.Name, Tag = person.Id });
            if (existing?.PersonId is { } retained && !people.Any(p => p.Id == retained)) owner.Items.Add(new ComboBoxItem { Content = retained, Tag = retained });
            owner.SelectedItem = owner.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == (existing?.PersonId ?? "")); row.Children.Add(owner);
            var periods = PlanningText(row, "時間帯（例 09:00-13:00,14:00-18:00）", "PlanExceptionIntervals-" + exceptions.Count,
                existing is null ? "" : string.Join(",", existing.Intervals.Select(i => $"{i.StartMinute / 60:00}:{i.StartMinute % 60:00}-{i.EndMinute / 60:00}:{i.EndMinute % 60:00}")));
            var remove = new Button { Content = "例外を削除" }; row.Children.Add(remove);
            remove.Click += (_, _) => { exceptionList.Children.Remove(row); exceptions.RemoveAll(e => e.Row == row); };
            exceptions.Add((row, day, owner, periods));
        }
        foreach (var exception in plan.Calendar.Exceptions) AddException(exception);
        var add = new Button { Content = "日付例外を追加" }; AutomationProperties.SetAutomationId(add, "PlanAddException");
        add.Click += (_, _) => AddException(null); calendarPanel.Children.Add(add);
        return () =>
        {
            int Minute(string value)
            {
                if (value == "24:00") return 1440;
                if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    throw new InvalidOperationException("例外の時間帯は HH:mm-HH:mm で入力してください。");
                return time.Hour * 60 + time.Minute;
            }
            var selected = inputs.Where(i => i.Selected.IsChecked == true).Select(i => i.Person with {
                WeightPercent = decimal.TryParse(i.Weight.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var weight)
                    && weight is >= 0 and <= 100 ? weight : throw new InvalidOperationException("配賦は0–100%で入力してください。") }).ToArray();
            var dates = exceptions.Select(e => {
                if (!DateOnly.TryParseExact(e.Day.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new InvalidOperationException("例外の日付は yyyy-MM-dd で入力してください。");
                var intervals = e.Intervals.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => {
                    var pair = s.Split('-'); if (pair.Length != 2) throw new InvalidOperationException("時間帯は開始-終了の組で入力してください。");
                    return new WorkingInterval(Minute(pair[0]), Minute(pair[1])); }).ToArray();
                return new CalendarException(date, (string)((ComboBoxItem)e.Person.SelectedItem).Tag is { Length: > 0 } id ? id : null, intervals);
            }).ToArray();
            return (selected, plan.Calendar with { Revision = Guid.NewGuid().ToString("N"), Holidays = adopt.IsChecked == true ? preset : plan.Calendar.Holidays,
                HolidaysNotConsidered = ignore.IsChecked == true, Exceptions = dates });
        };
    }
}
