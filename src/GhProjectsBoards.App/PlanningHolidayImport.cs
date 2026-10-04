using GhProjectsBoards.Core.Projects;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GhProjectsBoards.App;

internal sealed record HolidayImportFile(string Name, byte[] Bytes);

internal sealed partial class EditingGrid
{
    internal Func<Task<HolidayImportFile?>>? PickHolidayCsv { get; init; }

    private async Task<HolidayImportFile?> ReadHolidayCsvAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        picker.FileTypeFilter.Add(".csv");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;
        if (new FileInfo(file.Path).Length > HolidayCsvImport.MaximumBytes) throw new InvalidOperationException("CSVは1MB以下のファイルを選択してください。");
        return new(file.Name, await File.ReadAllBytesAsync(file.Path));
    }

    private Func<HolidayPreset?> CreateHolidayImport(StackPanel parent, HolidayPreset current, CheckBox bundled, Action edited)
    {
        HolidayPreset? candidate = null;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var choose = new Button { Content = "祝日CSVを選択…" };
        var source = new HyperlinkButton { Content = "内閣府の祝日", NavigateUri = new("https://www8.cao.go.jp/chosei/shukujitsu/gaiyou.html") };
        AutomationProperties.SetAutomationId(choose, "PlanImportHolidays");
        AutomationProperties.SetAutomationId(source, "PlanHolidaySource");
        actions.Children.Add(choose); actions.Children.Add(source); parent.Children.Add(actions);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(status, "HolidayImportStatus"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        parent.Children.Add(status);
        var adopt = new CheckBox { Content = "このCSVの祝日を採用", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(adopt, "PlanAdoptImportedHolidays"); parent.Children.Add(adopt);
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var disclosure = new Expander { Header = "変更日・出典", Content = details, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(disclosure, "HolidayImportDetails"); parent.Children.Add(disclosure);
        adopt.Checked += (_, _) => { bundled.IsChecked = false; edited(); };
        adopt.Unchecked += (_, _) => edited();
        bundled.Checked += (_, _) => adopt.IsChecked = false;
        choose.Click += async (_, _) => {
            var request = generation; var page = planningSettingsPage;
            choose.IsEnabled = false;
            try
            {
                var file = await (PickHolidayCsv?.Invoke() ?? ReadHolidayCsvAsync());
                if (file is null || !IsLoaded || !parent.IsLoaded || generation != request || planningSettingsPage != page) return;
                candidate = null; adopt.IsChecked = false; adopt.Visibility = disclosure.Visibility = Visibility.Collapsed;
                var imported = HolidayCsvImport.Parse(file.Bytes, file.Name, DateTimeOffset.UtcNow, current.FirstYear, current.LastYear);
                var oldDates = current.Dates.ToDictionary(date => date.Date, date => date.Name);
                var newDates = imported.Dates.ToDictionary(date => date.Date, date => date.Name);
                var changes = oldDates.Keys.Union(newDates.Keys).Where(day => oldDates.GetValueOrDefault(day) != newDates.GetValueOrDefault(day)).Order().ToArray();
                candidate = imported;
                status.Text = $"{file.Name} · {imported.FirstYear}–{imported.LastYear}年 · 変更{changes.Length}日";
                details.Text = (changes.Length == 0 ? "変更日なし" : string.Join("\n", changes.Select(day =>
                    $"{day:yyyy-MM-dd}: {oldDates.GetValueOrDefault(day) ?? "なし"} → {newDates.GetValueOrDefault(day) ?? "なし"}")))
                    + $"\n\nローカルCSVから取込。ファイルの発行元は未確認です。\n形式の参照元: {HolidayCsvImport.ReferenceUrl}"
                    + $"\n取込日時: {imported.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm zzz}\nSHA256: {imported.SourceSha256}";
                status.Visibility = adopt.Visibility = disclosure.Visibility = Visibility.Visible;
                edited();
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                if (IsLoaded && parent.IsLoaded && generation == request && planningSettingsPage == page)
                {
                    status.Text = error is InvalidOperationException ? error.Message : "CSVを開けませんでした。ファイルを選び直してください。";
                    status.Visibility = Visibility.Visible;
                }
            }
            finally { choose.IsEnabled = true; }
        };
        return () => adopt.IsChecked == true ? candidate : null;
    }
}
