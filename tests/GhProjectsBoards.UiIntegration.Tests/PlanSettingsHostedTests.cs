using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

// #251: the settings page itself — one section list grouped by scope beside a readable content column, and the
// settings the section owns (people table, calendar days off, AI provider naming, this-PC mark switches).
internal sealed partial class PlanWorkspaceHostedTests
{
    private static readonly (string Key, string Title, string Heading)[] Sections = [
        ("period", "期間", "PlanSettingsHeading_period"), ("records", "記録", "PlanSettingsHeading_records"),
        ("phases", "工程と目標日", "PlanSettingsHeading_phases"),
        ("people", "担当者", "PlanSettingsHeading_people"), ("calendar", "カレンダー", "PlanSettingsHeading_calendar"),
        ("columns", "列", "PlanColumnSettings"), ("fields", "GitHub のフィールド", "PlanSettingsHeading_fields"),
        ("issues", "新しい Issue", "PlanSettingsHeading_issues"), ("connection", "接続", "PlanSettingsHeading_connection"), ("display", "表示", "PlanSettingsHeading_display"),
        ("assist", "AI アシスト", "AssistSettingsHeading"), ("files", "書き出し・読み込み", "PlanSettingsHeading_files")];

    [Test]
    public async Task SettingsGhPathIsKeptForThisPcAndAMissingFileIsRefused()
    {
        // #275: gh.exe is chosen once per PC under 設定 › この PC › 接続, not typed on every connect.
        await Open(); await Settings();
        var missing = Path.Combine(root, "missing", "gh.exe");
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanSettingsGhPath");
            Assert.That(input.Text, Is.Empty, "Empty uses gh.exe from PATH.");
            input.Text = missing;
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Does.Contain("gh.exe が見つかりません"));
            Assert.That(Ui.Find<TextBox>("PlanSettingsGhPath").IsLoaded, Is.True, "A refused path keeps the settings page open.");
            Assert.That(workspace.GhPath, Is.Null);
            Ui.Find<TextBox>("PlanSettingsGhPath").Text = FakeExecutable;
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, () => Ui.Find<TextBlock>("PlanError").Text);
        Assert.That(workspace.GhPath, Is.EqualTo(FakeExecutable));
        Assert.That(new PlanConnectionSettingsStore(new PlanStore(root)).Load().GhPath, Is.EqualTo(FakeExecutable));
    }
    [Test]
    public async Task SettingsPageShowsTheTitleBackAndScopedSectionListBesideOneReadableContentColumn()
    {
        await Open(); await Settings();
        double listTop = 0, backTop = 0;
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSettingsTitle").Text, Is.EqualTo("設定"));
            var back = Ui.Find<Button>("PlanSettingsBack");
            Assert.That(back.Content, Is.EqualTo("計画に戻る"));
            var nav = Ui.Find<NavigationView>("PlanSettingsSections");
            var items = nav.MenuItems.OfType<NavigationViewItem>().ToArray();
            Assert.That(items.Select(i => (string)i.Tag), Is.EqualTo(Sections.Select(s => s.Key)));
            Assert.That(items.Select(i => (string)i.Content), Is.EqualTo(Sections.Select(s => s.Title)));
            var groups = nav.MenuItems.OfType<NavigationViewItemHeader>().Select(h => (string)h.Content).ToArray();
            Assert.That(groups, Has.Length.EqualTo(3));
            Assert.That(groups[0], Is.EqualTo("開発計画"), "The open Project's own settings come first.");
            Assert.That(groups[1], Is.EqualTo("この PC（すべてのプロジェクト）"));
            Assert.That(groups[2], Is.EqualTo("ファイル"));
            listTop = nav.TransformToVisual(view).TransformPoint(new()).Y;
            backTop = back.TransformToVisual(view).TransformPoint(new()).Y;
            var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            Assert.That(((FrameworkElement)scroll.Content).MaxWidth, Is.EqualTo(880), "The content column has a readable maximum width.");
            Assert.That(scroll.HorizontalScrollBarVisibility, Is.EqualTo(ScrollBarVisibility.Disabled));
        });
        await Ui.Run(() => { var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll"); scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSettingsScroll").VerticalOffset > 0);
        await Ui.Run(() => {
            var nav = Ui.Find<NavigationView>("PlanSettingsSections");
            Assert.That(nav.TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(listTop), "The list stays while the content scrolls.");
            Assert.That(Ui.Find<Button>("PlanSettingsBack").TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(backTop));
            Assert.That(Ui.Find<Button>("PlanSettingsBack").IsLoaded, Is.True);
        });
    }

    [Test]
    public async Task EverySectionIsAKeyboardFocusableListEntryThatScrollsItsHeadingIntoView()
    {
        await Open(); await Settings();
        double listTop = 0;
        await Ui.Run(() => listTop = Ui.Find<NavigationView>("PlanSettingsSections").TransformToVisual(view).TransformPoint(new()).Y);
        foreach (var (key, title, heading) in Sections) {
            await Ui.Run(() => {
                var item = Ui.Find<NavigationViewItem>("PlanSettingsSection_" + key);
                Assert.That(item.IsTabStop, Is.True, $"{title} is reachable with Tab.");
                Assert.That(AutomationProperties.GetName(item), Is.EqualTo(title));
                Assert.That(item.Focus(FocusState.Keyboard), Is.True, $"{title} takes keyboard focus.");
            });
            await Ui.Run(() => view.ShowSettingsSection(key));
            await Ui.Until(() => {
                var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll");
                var target = Ui.Find<TextBlock>(heading);
                var top = target.TransformToVisual(scroll).TransformPoint(new()).Y;
                var selected = ((NavigationViewItem)Ui.Find<NavigationView>("PlanSettingsSections").SelectedItem).Tag as string;
                return selected == key && target.IsLoaded && top >= -1 && top + target.ActualHeight <= scroll.ActualHeight + 1;
            });
            await Ui.Run(() => Assert.That(Ui.Find<NavigationView>("PlanSettingsSections").TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(listTop)));
        }
    }

    [Test]
    public async Task SettingsPeopleTableHasOneHeaderRowAndReadOnlyTaskAndRemainingCounts()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = state.Issues[0].Row with { Assignees = ["U1"], Remaining = 8 } },
            new(new("I2", "共有タスク", "acme/repo") { Assignees = ["U1"], Remaining = 4 }, "", true),
            new(new("I3", "残未入力", "acme/repo") { Assignees = ["U2"] }, "", true)] });
        await Open(); await Settings();
        await Ui.Run(() => {
            var table = Ui.Find<Grid>("PlanPeopleTable");
            var header = table.Children.OfType<TextBlock>().Where(t => Grid.GetRow(t) == 0).Select(t => t.Text).ToArray();
            Assert.That(header, Is.EqualTo(new[] { "担当者", "稼働率 %", "上限工数 h", "タスク", "残 h" }), "One header row names the table's columns.");
            Assert.That(Ui.Find<TextBlock>("PlanPeopleTasksU1").Text, Is.EqualTo("2"));
            Assert.That(Ui.Find<TextBlock>("PlanPeopleRemainingU1").Text, Is.EqualTo("12"));
            Assert.That(Ui.Find<TextBlock>("PlanPeopleTasksU2").Text, Is.EqualTo("1"));
            Assert.That(Ui.Find<TextBlock>("PlanPeopleRemainingU2").Text, Is.EqualTo("—"), "A missing 見込み残存工数 leaves the total unknown.");
            Assert.That(Ui.Tree(view).OfType<NumberBox>().Count(n => AutomationProperties.GetAutomationId(n) is "PlanRateU1" or "PlanRateU2"), Is.EqualTo(2));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero, "Read-only counts send nothing to GitHub.");
    }

    [Test]
    public async Task SettingsCalendarLabelsWorkingHoursAndAddsAndRemovesCompanyDaysOff()
    {
        var date = new DateOnly(2026, 10, 6);
        await Open(); await Settings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanWorkingHoursLabel").Text, Is.EqualTo("稼働時間"));
            Assert.That(Ui.Find<TextBlock>("PlanWorkingHours").Text, Does.Contain("09:00–13:00").And.Contain("14:00–18:00"));
        });
        await AddDay("PlanCompanyDaysOff", date);
        await Ui.Until(() => workspace.Session!.Document.State.Settings.CompanyDaysOff.Contains(date));
        await Ui.Ready<Button>("PlanCompanyDaysOffRemove20261006");
        var undo = workspace.Session!.UndoCount;
        await Ui.Run(() => Ui.Click("PlanCompanyDaysOffRemove20261006"));
        await Ui.Until(() => !workspace.Session!.Document.State.Settings.CompanyDaysOff.Contains(date));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1), "Removing a day off is one settings operation.");
        await Ui.Run(() => Assert.That(Ui.Find<ScrollViewer>("PlanSettingsScroll").IsEnabled, Is.True));
    }

    [Test]
    public async Task SettingsAssistProviderReadsAiNoSetsuzokusaki()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ComboBox>("AssistMode").Header, Is.EqualTo("AI の接続先"));
            // 接続先 alone names the GitHub host on the connection screen, so no settings control reuses it.
            Assert.That(Ui.Tree(view).OfType<ComboBox>().Any(c => c.Header as string == "接続先"), Is.False);
        });
    }

    [Test]
    public async Task SettingsDisplayHidesLatenessMarksOnSheetAndTheChoiceAppliesToEveryProjectOnThisPc()
    {
        var date = new DateOnly(2026, 10, 5);
        readDay = date;
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            new(new("I1", "要求事項", "acme/repo"), "", true),
            new(new("I2", "遅れ", "acme/repo") { Parent = "I1", Start = date.AddDays(-3), End = date.AddDays(-3), Remaining = 8 }, "", true)] });
        await Open("要求事項");
        await Ui.Run(async () => {
            var session = workspace.Session!;
            await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = date }), date);
            Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh();
        });
        // The mark and its counts exist before the switch is turned off.
        await Ui.Run(() => {
            Assert.That(AutomationProperties.GetName(Ui.Find<FontIcon>("PlanIndicator2")), Does.Contain("終了日"));
            // #260 D3: the 遅れ count lives on the attention strip, not in the status bar.
            Assert.That(AutomationProperties.GetName(Ui.Find<ToggleButton>("PlanAttentionLate")), Does.Contain("遅れ 1"));
        });
        await Settings();
        await Ui.Ready<CheckBox>("PlanShowOverdueMark");
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanShowOverdueMark")));
        await Ui.Until(() => !workspace.Display.Marks.Overdue);
        await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle();
        await Ui.Run(() => {
            var leaf = Ui.Find<FontIcon>("PlanIndicator2");
            Assert.That(AutomationProperties.GetName(leaf), Does.Not.Contain("終了日"), "A hidden mark leaves 状態 and its tooltip.");
            Assert.That(ToolTipService.GetToolTip(leaf), Does.Not.Contain("終了日"));
            var requirement = Ui.Find<FontIcon>("PlanIndicator1");
            Assert.That(AutomationProperties.GetName(requirement), Does.Not.Contain("配下"), "A hidden mark leaves the requirement row.");
            // #251 D2: the counts and their filters are lateness sets, not marks, so they keep finding the work.
            Assert.That(AutomationProperties.GetName(Ui.Find<ToggleButton>("PlanAttentionLate")), Does.Contain("遅れ 1"));
            Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().Marks.Overdue, Is.False);
        });
        // D1: the preference belongs to this PC, so another Project reads the same value.
        await PickerCommand("PlanChooseProject");
        await Ui.Ready<ListView>("AvailableProjects");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2"); await Ui.Idle();
        Assert.That(workspace.Display.Marks.Overdue, Is.False);
        // Restarting the app on the same isolated root keeps the choice; the Project's plan no longer holds it.
        await workspace.Flush();
        var restarted = new PlanWorkspace(new(root), () => readDay);
        await restarted.Connect(new(FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
        Assert.That(restarted.Display.Marks.Overdue, Is.False, "The display preference survives restart.");
    }

    // #298 item 3: the settings switch and the sheet's ガント toggle are one this-PC preference; either entry
    // point shows the other's choice, hiding changes no count or filter, and the choice survives restart.
    [Test, Category("DisplaySettings")]
    public async Task GanttVisibilityIsOneThisPcChoiceForBothEntryPointsAndSurvivesRestart()
    {
        var date = new DateOnly(2026, 10, 5);
        readDay = date;
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            new(new("I1", "要求事項", "acme/repo"), "", true),
            new(new("I2", "遅れ", "acme/repo") { Parent = "I1", Start = date.AddDays(-3), End = date.AddDays(-3), Remaining = 8 }, "", true)] });
        await Open("要求事項");
        await Ui.Run(async () => {
            var session = workspace.Session!;
            await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = date }), date);
            Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh();
        });
        var sheet = default(PlanSheetView)!;
        string attention = "", unpublished = "";
        await Ui.Run(() => {
            sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(Ui.Find<CheckBox>("PlanSheetShowGantt").IsChecked, Is.True);
            Assert.That(sheet.GanttVisible, Is.True);
            Assert.That(sheet.ChartViewport, Is.GreaterThan(0));
            Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility, Is.EqualTo(Visibility.Visible));
            attention = AttentionLabel("PlanAttentionLate");
            unpublished = Ui.Find<TextBlock>("PlanUnpublished").Text;
        });

        // The settings entry point hides the chart for every Project on this PC.
        await Settings();
        await Ui.Ready<CheckBox>("PlanSettingsShowGantt");
        await Ui.Run(() => {
            var box = Ui.Find<CheckBox>("PlanSettingsShowGantt");
            Assert.That(box.IsChecked, Is.True);
            Assert.That(AutomationProperties.GetName(box), Is.EqualTo("ガントを表示"));
            Ui.Toggle(box);
        });
        await Ui.Until(() => !workspace.Display.GanttVisible);
        await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle();
        await Ui.Until(() => sheet.ChartViewport == 0);
        await Ui.Run(() => {
            Assert.That(sheet.GanttVisible, Is.False);
            Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility, Is.EqualTo(Visibility.Collapsed), "The legend goes with the chart.");
            Assert.That(AttentionLabel("PlanAttentionLate"), Is.EqualTo(attention), "Hiding the Gantt changes no count.");
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo(unpublished));
            Assert.That(Ui.Find<CheckBox>("PlanSheetShowGantt").IsChecked, Is.False, "Both entry points show one stored choice.");
        });
        var stored = await new PlanDisplaySettingsStore(new PlanStore(root)).LoadAsync(workspace.Selected!.Id);
        Assert.That(stored.GanttVisible, Is.False, "The preference is stored in display.json.");

        // The sheet entry point shows it again and the settings switch follows.
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanSheetShowGantt")));
        await Ui.Until(() => workspace.Display.GanttVisible);
        await Ui.Until(() => sheet.ChartViewport > 0);
        await Ui.Run(() => Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility, Is.EqualTo(Visibility.Visible)));
        await Settings();
        await Ui.Run(() => Assert.That(Ui.Find<CheckBox>("PlanSettingsShowGantt").IsChecked, Is.True));
        await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle();

        // A hidden Gantt is kept for the next start of the app.
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanSheetShowGantt")));
        await Ui.Until(() => !workspace.Display.GanttVisible);
        await workspace.Flush();
        var restarted = new PlanWorkspace(new(root), () => readDay);
        await restarted.Connect(new(FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
        Assert.That(restarted.Display.GanttVisible, Is.False, "The Gantt visibility survives restart.");
    }
}

