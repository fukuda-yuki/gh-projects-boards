using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    [Test]
    public async Task ColumnWidthSettingClearsRejectsAndResetsWithoutChangingOtherSettings()
    {
        await Open();
        var session = workspace.Session!;
        await OpenColumnSettings();
        await Ui.Ready<TextBox>("PlanColumnWidthTitle");
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanColumnWidthTitle");
            Assert.That(input.Header, Is.Null);
            Assert.That(input.PlaceholderText, Is.EqualTo("200"));
            Assert.That(AutomationProperties.GetName(input), Is.EqualTo("タスク名 の幅"));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "PlanColumnWidthIndicator"), Is.False);
            var list = Ui.Find<ListView>("PlanColumnList"); var last = Ui.Find<TextBlock>("PlanColumnModeTitle");
            Assert.That(last.TransformToVisual(list).TransformPoint(new(last.ActualWidth, 0)).X, Is.LessThanOrEqualTo(list.ActualWidth), "The row's last cell stays inside the settings table.");
        });
        await CommitText("PlanColumnWidthTitle", "320");
        await Ui.Until(() => PlanSheetColumns.All(session.Document).Single(c => c.Key == "Title").Width == 320);
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        await CommitText("PlanColumnWidthTitle", "801");
        await Ui.Until(() => AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanColumnWidthTitle")).Contains("40〜800"));
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        await CommitText("PlanColumnWidthTitle", "");
        await Ui.Until(() => PlanSheetColumns.All(session.Document).Single(c => c.Key == "Title").Width is null);
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width == 200);
        await OpenColumnSettings();
        // The page stays loaded while hidden; wait until the reopened settings can take focus.
        await Ui.Until(() => Ui.Find<TextBox>("PlanColumnWidthTitle").Focus(FocusState.Programmatic));
        await CommitText("PlanColumnWidthTitle", "320");
        await Ui.Until(() => PlanSheetColumns.All(session.Document).Single(c => c.Key == "Title").Width == 320);
        var beforeReset = session.UndoCount;
        await Ui.Run(() => Ui.Click("PlanColumnReset"));
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.IsEmpty);
        Assert.That(session.UndoCount, Is.EqualTo(beforeReset + 1));
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width == 200);
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width == 320);
    }

    [Test]
    public async Task DeletedExtraEditIsVisibleAndDiscardableWithoutBlockingOtherChanges()
    {
        await Open();
        var session = workspace.Session!;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var baseline = session.Document.Baseline;
        baseline = baseline with { Columns = baseline.Columns.Add(new("DeletedNote", "Review notes", "TEXT")),
            Rows = baseline.Rows.Select(r => PlanValues.Set(r, "Project:DeletedNote", "\"original\"")).ToImmutableArray() };
        await session.AcceptRefresh(new(baseline, System.Collections.Immutable.ImmutableDictionary<string,string>.Empty, [], 0, 0), today, refresh: false);
        var identity = session.Document.State.Rows[0].Identity;
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(identity, "Project:DeletedNote", "keep this draft")]), today);
        var deleted = baseline with { Columns = baseline.Columns.RemoveAt(baseline.Columns.Length - 1),
            Rows = baseline.Rows.Select(r => r with { ExtraValues = r.ExtraValues.Remove("Project:DeletedNote") }).ToImmutableArray() };
        await session.AcceptRefresh(new(deleted, System.Collections.Immutable.ImmutableDictionary<string,string>.Empty, [], 0, 0), today, refresh: false);
        // The case is about the deleted-extra line not blocking *other* changes, so there must be one:
        // confirmation is disabled only when no writable change remains (docs/spec.md), and without this
        // title change the deleted-field line was the entire review and 発行する correctly stayed disabled.
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new(identity, PlanField.Title, "別の変更")]), today);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        var id = "PlanDiscardDeleted" + identity + "_Project:DeletedNote";
        await Ui.Ready<Button>(id);
        await Ui.Run(() => {
            var text = string.Join(" ", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("Review notes").And.Contain("keep this draft").And.Contain("GitHub で削除されたフィールド"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
            Ui.Click(id);
        });
        await Ui.Until(() => session.Document.Sync.Conflicts.IsEmpty);
        Assert.That(session.Document.State.Rows.All(r => !r.ExtraValues.ContainsKey("Project:DeletedNote")), Is.True);
    }

    // #319: 表示列 left the overflow. The title-bar gear opens 設定 and its section list reaches 列, which is
    // the only configuration route now.
    private async Task OpenColumnSettings()
    {
        await Settings();
        await Ui.Run(() => view.ShowSettingsSection("columns"));
    }
    // A project field's row can sit below the visible part of the settings page, as the last row already does in
    // the compact-table case, so bring its input into view before driving it.
    private async Task BringColumnIntoView(string id)
    {
        await Ui.Run(() => Ui.Find<TextBox>(id).StartBringIntoView(new() { AnimationDesired = false }));
        await Ui.Idle();
    }

    [Test]
    public async Task SettingsGearOpensTheCompactColumnsSectionAndItsAccessibleCommands()
    {
        await Open();
        await OpenColumnSettings();
        // The section list realizes its pane and content over the first layout passes after the page appears.
        await Ui.Ready<Button>("PlanSettingsBack");
        await Ui.Ready<TextBlock>("PlanColumnSettings");
        await Ui.Until(() => {
            var title = Ui.Find<TextBlock>("PlanColumnSettings");
            var viewport = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            var y = title.TransformToVisual(viewport).TransformPoint(new()).Y;
            return y >= 0 && y + title.ActualHeight <= viewport.ViewportHeight;
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanColumnSettings").Text,Is.EqualTo("列"));
            Assert.That(Ui.Tree(Ui.Root).OfType<TextBlock>().Any(t => AutomationProperties.GetAutomationId(t) is "PlanColumnIntro" or "PlanColumnFootnote"), Is.False);
            Assert.That(ToolTipService.GetToolTip(Ui.Find<TextBlock>("PlanColumnModeRepository")), Is.EqualTo("リポジトリを変えると Issue の移管になるため、読み取りのみです。"));
            // #319 D1: an app column shows its standard name as plain text, with no box and no placeholder.
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "PlanColumnNameTitle"), Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanColumnNameTitle").Text, Is.EqualTo("タスク名"));
            var width = Ui.Find<TextBox>("PlanColumnWidthTitle");
            Assert.That(width.Header, Is.Null);
            Assert.That(AutomationProperties.GetName(width), Is.EqualTo("タスク名 の幅"));
            Assert.That(AutomationProperties.GetName(Ui.Find<CheckBox>("PlanColumnEstimate")),Is.EqualTo("見積 h を表示"));
            Assert.That(Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith("PlanColumnUp") || AutomationProperties.GetAutomationId(b).StartsWith("PlanColumnDown")), Is.False, "Reordering has one way: drag or Alt+arrow.");
            Assert.That(Ui.Find<TextBlock>("PlanColumnTypeEstimate").Text,Is.EqualTo("数値"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnModeIndicator").Text,Is.EqualTo("計算"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnModeRepository").Text,Is.EqualTo("読み取りのみ"));
        });
        double backY = 0;
        await Ui.Run(() => {
            var back = Ui.Find<Button>("PlanSettingsBack");
            Assert.That(back.IsLoaded && back.IsEnabled, Is.True);
            Assert.That(back.Content, Is.EqualTo("計画に戻る"));
            backY = back.TransformToVisual(view).TransformPoint(new()).Y;
            var viewport = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            Assert.That(backY, Is.GreaterThanOrEqualTo(0));
            Assert.That(back.TransformToVisual(view).TransformPoint(new(back.ActualWidth, 0)).X, Is.LessThanOrEqualTo(viewport.TransformToVisual(view).TransformPoint(new()).X),
                "計画に戻る sits in the section list beside the scrolling content.");
            Assert.That(((NavigationViewItem)((NavigationView)Ui.Find<FrameworkElement>("PlanSettingsSections")).SelectedItem).Tag, Is.EqualTo("columns"), "The list marks the opened section.");
            Ui.Find<TextBox>("PlanColumnWidthRepository").Focus(FocusState.Keyboard);
            Ui.Find<TextBox>("PlanColumnWidthRepository").StartBringIntoView(new() { AnimationDesired = false });
        });
        // The compact table's last row is reachable inside the scrolling page. Whether that takes a scroll
        // depends on the window height, so assert reachability - and that the fixed 戻る button stays put.
        await Ui.Until(() => {
            var input = Ui.Find<TextBox>("PlanColumnWidthRepository");
            var viewport = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            var top = input.TransformToVisual(viewport).TransformPoint(new()).Y;
            return top >= 0 && top + input.ActualHeight <= viewport.ViewportHeight;
        }, () => "the last column row is not inside the settings viewport");
        await Ui.Run(() => {
            var back = Ui.Find<Button>("PlanSettingsBack");
            Assert.That(back.TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(backY).Within(1));
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
    }
    [Test]
    public async Task ColumnSettingsNavigateRenameHideResetAndUndoRenderedHeaders()
    {
        FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { ExtraFields = [new("X-notes", "Notes", "TEXT")] });
        await Open();
        var session=workspace.Session!;
        var undo=session.UndoCount;
        await OpenColumnSettings();
        await Ui.Ready<TextBox>("PlanColumnNameProject:X-notes");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<CheckBox>().Any(c => AutomationProperties.GetAutomationId(c) is "PlanColumnId" or "PlanColumnTitle"),Is.False);
            Assert.That(Ui.Find<TextBox>("PlanColumnNameProject:X-notes").Text,Is.EqualTo("Notes"));
        });
        // #319 D1: only a Project field changes its header name; the app's own columns keep theirs.
        await BringColumnIntoView("PlanColumnNameProject:X-notes");
        await CommitText("PlanColumnNameProject:X-notes","メモ");
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Any(c=>c.Key=="Project:X-notes" && c.DisplayName=="メモ"));
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanColumnProject:X-notes")));
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Any(c=>c.Key=="Project:X-notes" && c.Visible));
        Assert.That(session.UndoCount,Is.EqualTo(undo+2));
        // Reordering has one way in this page: drag the handle or Alt+↑/↓ on the focused row (native case), so
        // there is no up/down button to press here; the toggle below drives the re-rendered table.
        await Ui.Ready<CheckBox>("PlanColumnEstimate");
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanColumnEstimate")));
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Any(c=>c.Key==PlanField.Estimate && !c.Visible));
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
        // Returning re-renders the header after the plan is loaded again.
        await Ui.Until(() => !Ui.Tree(view).OfType<TextBlock>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanHeaderEstimate"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanHeaderProject:X-notes").Text,Is.EqualTo("メモ"));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Text,Is.EqualTo("タスク名"), "The app's own columns keep their standard name.");
            Assert.That(Ui.Tree(view).OfType<TextBlock>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanHeaderEstimate"),Is.False);
        });
        await OpenColumnSettings();
        await Ui.Ready<Button>("PlanColumnReset");
        await Ui.Run(()=>Ui.Click("PlanColumnReset"));
        await Ui.Until(()=>session.Document.State.Settings.SheetColumns.IsEmpty);
        await Ui.Run(()=>Ui.Click("PlanUndo"));
        await Ui.Until(()=>!session.Document.State.Settings.SheetColumns.IsEmpty);
        Assert.That(GhProjectsBoards.Tests.FakePlanEditor.Load(root).MutationBatches,Is.Zero);
    }

    [Test]
    public async Task InvalidSheetInputRefusesColumnSettingsNavigation()
    {
        await Open();
        var original = "";
        await Ui.Run(() => {
            var input = Ui.Find<PlanSheetCell>("PlanCell1_Remaining");
            Assert.That(input.Focus(FocusState.Programmatic), Is.True);
            input.BeginEditing();
            original = input.Text;
            Assert.That(original, Is.EqualTo(workspace.Session!.Document.State.Rows[0].Remaining?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? ""));
        });
        await CommitText("PlanCell1_Remaining","invalid");
        var before = PlanJson.Text(workspace.Session!.Document);
        var undo = workspace.Session.UndoCount;
        // #319: the only columns route starts at the title-bar gear, which validates the pending cell first.
        await Ui.Run(() => Ui.Click("PlanShowSettings"));
        await Ui.Idle();
        await Ui.Run(()=> {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text,Is.EqualTo("invalid"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").IsLoaded,Is.True);
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanColumnNameTitle" && t.IsLoaded && t.Visibility==Visibility.Visible),Is.False);
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        // Invalid input also refuses window close; cleanup must follow a real correction.
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.False));
        await CommitText("PlanCell1_Remaining", original);
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
    }

    [Test]
    public async Task ProjectFieldNameBoxShowsTheCurrentNameAndEmptyOrTheGitHubNameRestoresIt()
    {
        FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { ExtraFields = [new("X-notes", "Notes", "TEXT")] });
        await Open();
        var session = workspace.Session!;
        await OpenColumnSettings();
        await Ui.Ready<TextBox>("PlanColumnNameProject:X-notes");
        await Ui.Until(() => Ui.Find<TextBox>("PlanColumnNameProject:X-notes").Text == "Notes");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanColumnNameProject:X-notes").PlaceholderText, Is.EqualTo("Notes"),
                "The GitHub name is the placeholder, seen only while the box is empty.");
        });
        await BringColumnIntoView("PlanColumnNameProject:X-notes");
        await CommitText("PlanColumnNameProject:X-notes", "メモ");
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Any(c => c.Key == "Project:X-notes" && c.DisplayName == "メモ"));
        await Ui.Until(() => Ui.Find<TextBox>("PlanColumnNameProject:X-notes").Text == "メモ");
        // Typing the GitHub name, or clearing the box, restores the default.
        await CommitText("PlanColumnNameProject:X-notes", "Notes");
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Single(c => c.Key == "Project:X-notes").DisplayName is null);
        await CommitText("PlanColumnNameProject:X-notes", "");
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Single(c => c.Key == "Project:X-notes").DisplayName is null);
        await Ui.Until(() => Ui.Find<TextBox>("PlanColumnNameProject:X-notes").Text == "Notes");
    }

    [Test, Category("PlanSheetNative")]
    public async Task AltArrowMovesTheFocusedColumnRowAndKeepsFocusOnIt()
    {
        await Open();
        var session = workspace.Session!;
        var undo = session.UndoCount;
        await OpenColumnSettings();
        await Ui.Ready<TextBox>("PlanColumnWidthTitle");
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanColumnWidthTitle").Focus(FocusState.Keyboard), Is.True));
        string[] Order() => PlanSheetColumns.All(session.Document).Select(c => c.Key).ToArray();
        var before = Order();
        var index = Array.IndexOf(before, PlanField.Title);
        await SheetNativeInput.Press(Windows.System.VirtualKey.Up, Windows.System.VirtualKey.Menu);
        await Ui.Until(() => Array.IndexOf(Order(), PlanField.Title) == index - 1);
        Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Idle();
        // Focus stays on the moved row, so a second Alt+↓ moves the same column back.
        await SheetNativeInput.Press(Windows.System.VirtualKey.Down, Windows.System.VirtualKey.Menu);
        await Ui.Until(() => Array.IndexOf(Order(), PlanField.Title) == index);
        Assert.That(Order(), Is.EqualTo(before));
        Assert.That(GhProjectsBoards.Tests.FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    // #319 D1/D2: app columns show their standard name as text and only a Project field keeps a name box; the
    // 種類 row follows the Project's repositories and a renamed field's header names its GitHub field.
    [Test]
    public async Task ColumnSettingsShowAppColumnNamesAsTextAndOnlyProjectFieldsWithANameBox()
    {
        FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { ExtraFields = [new("X-priority", "Priority", "TEXT")] });
        await Open(); await OpenColumnSettings();
        await Ui.Run(() => {
            var nameBoxes = Ui.Tree(view).OfType<TextBox>()
                .Select(input => AutomationProperties.GetAutomationId(input))
                .Where(id => id.StartsWith("PlanColumnName", StringComparison.Ordinal)).ToArray();
            Assert.That(nameBoxes, Is.EqualTo(new[] { "PlanColumnNameProject:X-priority" }),
                "Only a GitHub Project field has a display-name box.");
            Assert.That(Ui.Find<TextBlock>("PlanColumnNameTitle").Text, Is.EqualTo("タスク名"));
            var box = Ui.Find<TextBox>("PlanColumnNameProject:X-priority");
            Assert.That(box.Header, Is.Null);
            Assert.That(box.Text, Is.EqualTo("Priority"));
            Assert.That(box.PlaceholderText, Is.EqualTo("Priority"), "The GitHub name is the placeholder and the empty value restores it.");
            Assert.That(AutomationProperties.GetName(box), Is.EqualTo("Priority の表示名"));
            // The 種類 row is listed for a personal-repository Project, but hidden by default.
            Assert.That(Ui.Find<TextBlock>("PlanColumnSourceIssueType").Text, Is.EqualTo("—"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnTypeIssueType").Text, Is.EqualTo("単一選択"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnModeIssueType").Text, Is.EqualTo("編集できる"));
            Assert.That(Ui.Find<TextBox>("PlanColumnWidthIssueType").PlaceholderText, Is.EqualTo("64"), "The 種類 default width is 64 effective pixels.");
            Assert.That(Ui.Find<CheckBox>("PlanColumnIssueType").IsChecked, Is.False);
        });
        // A renamed Project field names its GitHub field in the header, as 「優先度（GitHub: Priority）」.
        await BringColumnIntoView("PlanColumnNameProject:X-priority");
        await CommitText("PlanColumnNameProject:X-priority", "優先度");
        await Ui.Until(() => workspace.Session!.Document.State.Settings.SheetColumns.Any(c => c.Key == "Project:X-priority" && c.DisplayName == "優先度"));
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanColumnProject:X-priority")));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.SheetColumns.Any(c => c.Key == "Project:X-priority" && c.Visible));
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Ready<TextBlock>("PlanHeaderProject:X-priority");
        await Ui.Run(() => {
            var header = Ui.Find<TextBlock>("PlanHeaderProject:X-priority");
            Assert.That(header.Text, Is.EqualTo("優先度"));
            Assert.That(AutomationProperties.GetHelpText(header), Is.EqualTo("優先度（GitHub: Priority）"));
            Assert.That(ToolTipService.GetToolTip(header), Is.EqualTo("優先度（GitHub: Priority）"));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Text, Is.EqualTo("タスク名"), "An app column is never renamed.");
        });
    }

    // #319 D2: a Project whose repositories can carry Issue types shows 種類 by default.
    [Test]
    public async Task IssueTypeIsShownByDefaultWhenTheProjectsRepositoriesCanCarryIssueTypes()
    {
        await Open();
        var session = workspace.Session!;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var baseline = session.Document.Baseline;
        baseline = baseline with {
            IssueTypes = [new("IT-1", "Task"), new("IT-2", "Bug"), new("IT-3", "Feature")],
            IssueTypeRepositories = ["acme/repo"],
            Rows = baseline.Rows.Select(row => row with { IssueType = "Task" }).ToImmutableArray() };
        await session.AcceptRefresh(new(baseline, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, [], 0, 0), today, refresh: false);
        await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh());
        await Ui.Ready<TextBlock>("PlanHeaderIssueType");
        await Ui.Ready<TextBox>("PlanCell1_IssueType");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanHeaderIssueType").Text, Is.EqualTo("種類"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_IssueType").Text, Is.EqualTo("Task"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_IssueType").IsReadOnly, Is.False);
            // 種類 joins the fifteen default columns at its own 64-pixel width.
            var shown = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(shown.VisibleColumns.Length, Is.EqualTo(15));
            Assert.That(shown.VisibleColumns.Single(column => column.Field == PlanField.IssueType).Width, Is.EqualTo(64));
        });
        await OpenColumnSettings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<CheckBox>("PlanColumnIssueType").IsChecked, Is.True, "An organization Project shows 種類 by default.");
            Assert.That(Ui.Find<TextBlock>("PlanColumnSourceIssueType").Text, Is.EqualTo("—"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnTypeIssueType").Text, Is.EqualTo("単一選択"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}

internal sealed partial class PlanSheetHostedTests
{
    // A header TextBlock reports its text extent as ActualWidth; RenderSize is the arranged column width.
    [TestCase("Title", 320d, 200d), TestCase("Id", 100d, 76d)]
    public async Task HeaderRangeValueResizesAlignedRowsAndUndoRestoresWidth(string key, double width, double original)
    {
        await Ui.Ready<FrameworkElement>("PlanHeaderGrip" + key);
        var undo = session.UndoCount;
        await Ui.Run(() => {
            Assert.That(Ui.Tree(sheet).OfType<FrameworkElement>().Any(e => AutomationProperties.GetAutomationId(e) == "PlanHeaderGripIndicator"), Is.False);
            var grip = Ui.Find<FrameworkElement>("PlanHeaderGrip" + key);
            Assert.That(((Control)grip).IsTabStop, Is.False);
            Assert.That(AutomationProperties.GetName(grip), Is.EqualTo((key == "Title" ? "タスク名" : "ID") + " の幅"));
            var range = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(grip).GetPattern(PatternInterface.RangeValue);
            Assert.That(range.Minimum, Is.EqualTo(40)); Assert.That(range.Maximum, Is.EqualTo(800));
            Assert.That(range.Value, Is.EqualTo(original)); range.SetValue(width);
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeader" + key).RenderSize.Width == width && session.UndoCount == undo + 1);
        await Ui.Run(() => {
            // Every later column starts where its header starts, which also proves a widened ID column.
            foreach (var row in sheet.Realized.Where(r => r.Identity.Length > 0 && r.IsLoaded)) {
                var number = sheet.PlanIds[row.Identity];
                foreach (var column in sheet.VisibleColumns.Where(c => c.Field is not null)) {
                    var header = Ui.Find<TextBlock>("PlanHeader" + column.Field);
                    var cell = Ui.Find<FrameworkElement>("PlanCell" + number + "_" + column.Field);
                    Assert.That(cell.TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(header.TransformToVisual(sheet).TransformPoint(new()).X).Within(2), column.Field);
                }
                if (key == "Title") Assert.That(Ui.Find<FrameworkElement>("PlanCell" + number + "_Title").RenderSize.Width, Is.EqualTo(width).Within(4));
                Assert.That(row.ColumnDefinitions[0].ActualWidth, Is.EqualTo(sheet.SheetViewport).Within(1));
            }
        });
        await Ui.Run(async () => await sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeader" + key).RenderSize.Width == original && session.UndoCount == undo);
    }

    [Test]
    public async Task InvalidPendingInputRejectsHeaderWidthWithoutAnUndoStep()
    {
        await Edit(1, "Remaining", "invalid");
        var undo = session.UndoCount;
        await Ui.Run(() => ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
            Ui.Find<FrameworkElement>("PlanHeaderGripTitle")).GetPattern(PatternInterface.RangeValue)).SetValue(320));
        await Ui.Run(async () => await sheet.Run(() => Task.CompletedTask));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Width, Is.EqualTo(200));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("invalid"));
        });
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Edit(1, "Remaining", "9");
    }

    [TestCase(false), TestCase(true)]
    public async Task LockedSettingsRejectHeaderWidthAndReportTheReason(bool publishPending)
    {
        await Ui.Ready<FrameworkElement>("PlanHeaderGripTitle");
        var undo = session.UndoCount;
        if (publishPending) Assert.That((await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), []) })).Succeeded, Is.True);
        using (publishPending ? null : session.BeginRemoteOperation()) {
            await Ui.Run(() => ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
                Ui.Find<FrameworkElement>("PlanHeaderGripTitle")).GetPattern(PatternInterface.RangeValue)).SetValue(320));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Contains(publishPending ? "発行結果を確認" : "更新または発行が進行中"));
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Width, Is.EqualTo(200)));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            Assert.That(session.Document.State.Settings.SheetColumns, Is.Empty);
        }
        if (publishPending) await session.SaveSync(session.Document.Sync with { Publish = null });
    }

    [Test, Category("PlanSheetNative")]
    public async Task RejectedHeaderDragRestoresEveryHeaderAndGripToTheRowColumns()
    {
        await Select(1, "Title");
        await Ui.Ready<PlanSheetCell>("PlanCell2_Title");
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        var start = await SheetNativeInput.PointFor("PlanHeaderGripTitle");
        double scale = 1;
        await Ui.Run(() => scale = sheet.XamlRoot.RasterizationScale);
        using (session.BeginRemoteOperation()) {
            await SheetNativeInput.Drag(start, new(start.X + 64 * scale, start.Y), async () => {
                await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width > 200);
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
                Assert.That(session.UndoCount, Is.EqualTo(undo));
            });
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Contains("更新または発行が進行中"));
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                // The ID number sizes to its text, so the ID column is covered by the next columns' positions and its grip.
                foreach (var column in sheet.VisibleColumns.Where(c => c.Field is not null || c.Indicator)) {
                    var key = column.Indicator ? "Indicator" : column.Field!;
                    var header = Ui.Find<TextBlock>("PlanHeader" + key);
                    var cell = Ui.Find<FrameworkElement>(column.Indicator ? "PlanIndicator2" : "PlanCell2_" + key);
                    var left = cell.TransformToVisual(sheet).TransformPoint(new()).X;
                    Assert.That(header.RenderSize.Width, Is.EqualTo(cell.RenderSize.Width).Within(1), key);
                    Assert.That(header.TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(left).Within(1), key);
                    if (!column.Indicator) {
                        var grip = Ui.Find<FrameworkElement>("PlanHeaderGrip" + key);
                        Assert.That(grip.TransformToVisual(sheet).TransformPoint(new()).X + grip.ActualWidth / 2,
                            Is.EqualTo(left + cell.RenderSize.Width).Within(1), key);
                    }
                }
            });
            Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
        }
    }

    [Test, Category("PlanSheetNative")]
    public async Task HeaderGripDragPreviewsThenCommitsAndDoubleClickFitsDisplayedCells()
    {
        var rows = ImmutableArray.Create(new PlanRow("I1", "Parent", "acme/repo"),
            new PlanRow("I2", "A child title that should fit without being cut off", "acme/repo") { Parent = "I1" });
        await MountPresentation(rows, rows);
        var undo = session.UndoCount;
        var start = await SheetNativeInput.PointFor("PlanHeaderGripTitle");
        double scale = 1;
        await Ui.Run(() => scale = sheet.XamlRoot.RasterizationScale);
        await SheetNativeInput.Drag(start, new(start.X + 64 * scale, start.Y), async () => {
            await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width > 200);
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            Assert.That(session.Document.State.Settings.SheetColumns, Is.Empty);
        });
        await Ui.Until(() => session.UndoCount == undo + 1 && Math.Abs(Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width - 264) <= 1);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width, Is.EqualTo(264).Within(1)));
        var point = await SheetNativeInput.PointFor("PlanHeaderGripTitle");
        SheetNativeInput.Move(point);
        SheetNativeInput.Button(true); SheetNativeInput.Button(false);
        SheetNativeInput.Button(true); SheetNativeInput.Button(false);
        // The accepted width is a whole effective pixel, but XAML snaps the rendered header to the device
        // pixel grid: at a fractional RasterizationScale (for example 125 %) 317 effective pixels render as
        // 316.8, a subpixel difference and not a clipped column. Read the accepted fit only after the second
        // commit (the drag width is still on screen until the fit's refresh) and allow it one effective
        // pixel; the content range gets the same allowance.
        await Ui.Until(() => session.UndoCount == undo + 2);
        var fitted = PlanSheetColumns.All(session.Document).Single(c => c.Key == "Title").Width ?? 0;
        await Ui.Until(() => Math.Abs(Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width - fitted) <= 1);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width,
            Is.EqualTo(fitted).Within(1)));
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell2_Title");
            var measure = new TextBlock { Text = rows[1].Title, FontSize = cell.FontSize, FontFamily = cell.FontFamily, FontWeight = cell.FontWeight };
            measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").RenderSize.Width,
                Is.InRange(Math.Ceiling(measure.DesiredSize.Width + 39), Math.Ceiling(measure.DesiredSize.Width + 45)));
        });
    }

    [TestCase("TEXT", "\"held\""), TestCase("NUMBER", "8"), TestCase("DATE", "\"2026-10-14\""), TestCase("SINGLE_SELECT", "\"High\"")]
    public async Task UnenteredLocalExtraCopiesBlankAndDoesNotClearPasteDestination(string type, string held)
    {
        var source = PlanRow.New("New", "acme/repo");
        var target = PlanValues.Set(new("I2", "Existing", "acme/repo"), "Project:X", held);
        await MountPresentation([source, target], [target], columns: [new("X", "Extra", type) { Options = ["High"] }]);
        await SetColumnVisible("Project:X", true);
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        await Select(1, "Project:X");
        await Ui.Run(async () => {
            Assert.That(sheet.Display(source.Identity, "Project:X"), Is.Empty);
            await sheet.KeyboardCommand(Windows.System.VirtualKey.C);
        });
        Assert.That(clipboard.Text, Is.Empty);
        using var metadata = System.Text.Json.JsonDocument.Parse(clipboard.Metadata!);
        Assert.That(metadata.RootElement.GetProperty("values")[0][0].GetString(), Is.EqualTo("null"));
        await Select(2, "Project:X");
        await Ui.Run(async () => await sheet.KeyboardCommand(Windows.System.VirtualKey.V));
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [TestCase("Repository", false), TestCase("Project:I", false), TestCase("Project:I", true)]
    public async Task ReadOnlyColumnCellsAndRectanglesReachTheClipboard(string field, bool rectangle)
    {
        var rows = System.Collections.Immutable.ImmutableArray.Create(
            PlanValues.Set(new PlanRow("I1", "First", "acme/repo"), "Project:I", "\"Sprint 2\""),
            PlanValues.Set(new PlanRow("I2", "Second", "acme/other"), "Project:I", "\"Sprint 3\""));
        await MountPresentation(rows, rows, columns: [new("I", "Sprint", "ITERATION")]);
        await SetColumnVisible("Project:I", true); await SetColumnVisible("Repository", true);
        await Ui.Run(async () => {
            await session.Execute(new ReplacePlanSettings(PlanSheetColumns.Move(session.Document, "Project:I", 0)), Today);
            await session.Execute(new ReplacePlanSettings(PlanSheetColumns.Move(session.Document, "Repository", 1)), Today);
            sheet.Refresh();
        });
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        await Select(1, field);
        if (rectangle) await Select(2, "Repository", true);
        await Ui.Run(async () => await sheet.KeyboardCommand(Windows.System.VirtualKey.C));
        Assert.That(clipboard.Text, Is.EqualTo(rectangle ? "Sprint 2\tacme/repo\nSprint 3\tacme/other" : field == "Repository" ? "acme/repo" : "Sprint 2"));
        using var metadata = System.Text.Json.JsonDocument.Parse(clipboard.Metadata!);
        Assert.That(metadata.RootElement.GetProperty("values").GetArrayLength(), Is.EqualTo(rectangle ? 2 : 1));
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [Test, Category("PlanSheetNative")]
    public async Task ExtraDateF2EnterKeepsTheOriginalValueWithoutAnUndoStep()
    {
        var row = new PlanRow("I1","Task","acme/repo") {
            ExtraValues = System.Collections.Immutable.ImmutableDictionary<string,PlanExtraValue>.Empty
                .Add("Project:D",new(GhProjectsBoards.Core.Projects.ValueAvailability.Present,"\"2026-10-14\"")) };
        await MountPresentation([row],[row],columns:[new("D","Review date","DATE")]);
        await SetColumnVisible("Project:D",true);
        await Select(1,"Project:D");
        await SheetNativeInput.Click("PlanCell1_Project:D");
        var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount;
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Project:D").Text == "2026-10-14");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Enter);
        await Ui.Idle();
        Assert.That(PlanJson.Text(session.Document),Is.EqualTo(before));
        Assert.That(session.UndoCount,Is.EqualTo(undo));
    }
    [Test]
    public async Task ExtraTextUsesNativeEditorPasteClearUndoAndConfiguredOrder()
    {
        var empty=System.Collections.Immutable.ImmutableDictionary<string,PlanExtraValue>.Empty.Add("Project:X",new(GhProjectsBoards.Core.Projects.ValueAvailability.Empty,"null"));
        var rows=System.Collections.Immutable.ImmutableArray.Create(new PlanRow("I1","Task","acme/repo") {ExtraValues=empty},new PlanRow("I2","Other","acme/repo") {ExtraValues=empty});
        await MountPresentation(rows,rows,columns:[new("X","Notes","TEXT")]);
        await SetColumnVisible("Project:X",true);
        await Ui.Run(async ()=> {
            await session.Execute(new ReplacePlanSettings(PlanSheetColumns.Move(session.Document,"Project:X",3)),Today); sheet.Refresh();
        });
        await Ui.Ready<TextBox>("PlanCell1_Project:X");
        await Edit(1,"Project:X","メモ");
        await Ui.Until(()=>PlanExtraEditing.Value(session.Document.State.Rows[0],"Project:X") as string == "メモ");
        await Select(1,"Project:X");
        await Ui.Run(async ()=>await sheet.KeyboardCommand(Windows.System.VirtualKey.C));
        await Select(2,"Project:X");
        await Ui.Run(async ()=>await sheet.KeyboardCommand(Windows.System.VirtualKey.V));
        await Ui.Until(()=>Ui.Find<TextBox>("PlanCell2_Project:X").Text=="メモ", PasteState);
        await Ui.Run(async ()=>await sheet.KeyboardCommand(Windows.System.VirtualKey.Delete));
        await Ui.Until(()=>Ui.Find<TextBox>("PlanCell2_Project:X").Text=="", PasteState);
        await Ui.Run(async ()=>await sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Until(()=>Ui.Find<TextBox>("PlanCell2_Project:X").Text=="メモ");
        await Ui.Run(()=>Assert.That(Ui.Find<TextBlock>("PlanHeaderProject:X").Text,Is.EqualTo("Notes")));
        string PasteState() => $"cells=[{Ui.Find<TextBox>("PlanCell1_Project:X").Text}|{Ui.Find<TextBox>("PlanCell2_Project:X").Text}], "
            + $"selected1={sheet.IsSelected("I1","Project:X")}, selected2={sheet.IsSelected("I2","Project:X")}, "
            + $"error={Ui.Find<TextBlock>("PlanSheetError").Text}, focused={(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(sheet.XamlRoot) as FrameworkElement)?.Name}/"
            + $"{AutomationProperties.GetAutomationId(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(sheet.XamlRoot) as FrameworkElement ?? sheet)}, {sheet.WorkDescription}";
    }
}
