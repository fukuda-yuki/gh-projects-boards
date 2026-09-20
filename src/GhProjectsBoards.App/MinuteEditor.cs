using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace GhProjectsBoards.App;

internal sealed class MinuteEditor : StackPanel
{
    private readonly TextBox text;
    private readonly CalendarDatePicker date;
    private readonly TimePicker time;
    private bool updating;
    internal event Action? Edited;
    internal string Text => text.Text;
    internal TextBox Input => text;
    internal MinuteEditor(string label, string id, string initial)
    {
        Spacing = 4;
        text = new() { Header = label, Text = initial, PlaceholderText = "yyyy-MM-dd HH:mm" };
        date = new() { PlaceholderText = "日付", Width = 164 };
        time = new() { ClockIdentifier = "24HourClock", MinuteIncrement = 1 };
        AutomationProperties.SetAutomationId(text, id); AutomationProperties.SetAutomationId(date, id + "-Date");
        AutomationProperties.SetAutomationId(time, id + "-Time");
        AutomationProperties.SetName(date, label + "の日付"); AutomationProperties.SetName(time, label + "の時刻");
        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pickers.Children.Add(date); pickers.Children.Add(time); Children.Add(text); Children.Add(pickers);
        void ReadText()
        {
            if (updating) return;
            updating = true;
            try
            {
                if (DateOnly.TryParseExact(text.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    date.Date = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9));
                    time.SelectedTime = null;
                }
                else
                {
                    var value = PlanningContract.ParseMinute(text.Text);
                    date.Date = value is { } d ? new DateTimeOffset(d.Date, TimeSpan.FromHours(9)) : null;
                    time.SelectedTime = value?.TimeOfDay;
                }
            }
            catch (InvalidOperationException)
            {
                // Unfinished text remains visible; stale picker values must not
                // turn a later component choice into a different exact minute.
                date.Date = null; time.SelectedTime = null;
            }
            finally { updating = false; }
        }
        void ReadPickers(bool dateChanged)
        {
            if (updating) return;
            updating = true;
            try
            {
                if (date.Date is { } d)
                    text.Text = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        + (time.SelectedTime is { } t ? $" {t.Hours:00}:{t.Minutes:00}" : "");
                else if (dateChanged)
                {
                    time.SelectedTime = null;
                    text.Text = "";
                }
            }
            finally { updating = false; }
        }
        ReadText();
        text.TextChanging += (_, _) => { ReadText(); Edited?.Invoke(); };
        date.DateChanged += (_, _) => ReadPickers(dateChanged: true);
        time.SelectedTimeChanged += (_, _) => ReadPickers(dateChanged: false);
    }
}
