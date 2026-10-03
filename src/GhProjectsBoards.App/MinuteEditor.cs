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
    private readonly StackPanel pickers;
    private readonly HyperlinkButton changeInput;
    private bool updating;
    internal event Action? Edited;
    internal string Text => text.Text;
    internal TextBox Input => text;
    internal Control DefaultInput => date;
    internal void RevealInput()
    {
        text.Visibility = Visibility.Visible; pickers.Visibility = Visibility.Collapsed;
        changeInput.Content = "日付と時刻を選ぶ";
    }
    internal MinuteEditor(string label, string id, string initial)
    {
        Spacing = 4;
        Children.Add(new TextBlock { Text = label });
        text = new() { Text = initial, PlaceholderText = "yyyy-MM-dd HH:mm", Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(text, label);
        date = new() { PlaceholderText = "日付", Width = 164 };
        time = new() { ClockIdentifier = "24HourClock", MinuteIncrement = 1 };
        AutomationProperties.SetAutomationId(text, id); AutomationProperties.SetAutomationId(date, id + "-Date");
        AutomationProperties.SetAutomationId(time, id + "-Time");
        AutomationProperties.SetName(date, label + "の日付"); AutomationProperties.SetName(time, label + "の時刻");
        pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pickers.Children.Add(date); pickers.Children.Add(time); Children.Add(pickers); Children.Add(text);
        changeInput = new HyperlinkButton { Content = "文字で入力", Padding = new(0), MinHeight = 24 };
        AutomationProperties.SetAutomationId(changeInput, id + "-Direct");
        Children.Add(changeInput);
        void Direct(bool show)
        {
            text.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            pickers.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            changeInput.Content = show ? "日付と時刻を選ぶ" : "文字で入力";
        }
        changeInput.Click += (_, _) => {
            if (text.Visibility != Visibility.Visible) { Direct(true); text.Focus(FocusState.Programmatic); return; }
            try { PlanningContract.ParseMinute(text.Text); text.Description = null; Direct(false); }
            catch (InvalidOperationException) { text.Description = "入力を確認：yyyy-MM-dd HH:mmで入力してください。"; text.Focus(FocusState.Programmatic); }
        };
        text.GotFocus += (_, _) => Direct(true);
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
                    Direct(true);
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
                Direct(true);
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
