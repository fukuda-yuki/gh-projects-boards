using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Polygon = Microsoft.UI.Xaml.Shapes.Polygon;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;
[TestFixture, NonParallelizable, Category("PlanWorkspace")]
internal sealed partial class PlanWorkspaceHostedTests
{
    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true), Category("PlanSheetNative")]
    public async Task SheetF6CyclesCommandRowAndSelectedCell(bool reverse, bool summary)
    {
        await Open();
        if (summary) {
            await workspace.Session!.Execute(new InsertPlanRows([PlanRow.New("Child", "acme/repo") with { Parent = "I1" }], Kind: PlanOperationKind.CsvImport), readDay);
            await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh()); await Ui.Idle();
        }
        var cellId = summary ? "PlanCell1_Estimate" : "PlanCell1_Title";
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => { var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single(); sheet.Select("I1", summary ? PlanField.Estimate : PlanField.Title, false); sheet.FocusSelected(); });
        await Ui.Until(() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(view.XamlRoot) == Ui.Find<Control>(cellId));
        await SheetNativeInput.Press(Windows.System.VirtualKey.F6, reverse ? [Windows.System.VirtualKey.Shift] : []);
        await Ui.Run(() => Assert.That(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(view.XamlRoot),
            Is.SameAs(Ui.Find<SelectorBarItem>("PlanShowTasks"))));
        await SheetNativeInput.Press(Windows.System.VirtualKey.F6, reverse ? [Windows.System.VirtualKey.Shift] : []);
        await Ui.Run(() => Assert.That(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(view.XamlRoot),
            Is.SameAs(Ui.Find<Control>(cellId))));
    }

    [Test, Category("CommandAvailability")]
    public async Task FreshProjectDisablesBothHistoryCommands()
    {
        await Open();
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.False);
            Assert.That(Ui.Find<Button>("PlanRedo").IsEnabled, Is.False);
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true), Category("CommandAvailability")]
    public async Task HistoryCommandsFollowAcceptedEditsAndSheetUndoRedo(bool settingsEdit)
    {
        await Open();
        if (settingsEdit) await Settings();
        await CommitText(settingsEdit ? "PlanAllowanceU1" : "PlanCell1_Title", settingsEdit ? "80" : "Changed title");
        // The focus-loss commit completes after the dispatcher turn that moved focus.
        await Ui.Until(() => Ui.Find<Button>("PlanUndo").IsEnabled);
        await AssertHistory(true, false);
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        await AssertHistory(false, true);
        await Ui.Run(() => Ui.Click("PlanRedo")); await Ui.Idle();
        await AssertHistory(true, false);
        if (settingsEdit) { await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle(); }
        await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Idle();
        await AssertHistory(false, true);
        await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().KeyboardCommand(Windows.System.VirtualKey.Y));
        await Ui.Idle();
        await AssertHistory(true, false);
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        await AssertHistory(false, true);
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled); await Ui.Idle();
        // Refresh rebases the local history onto the new read, so the undone edit stays redoable.
        Assert.That(workspace.Session!.RedoCount, Is.EqualTo(1));
        await AssertHistory(false, true);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);

        Task AssertHistory(bool undo, bool redo) => Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.EqualTo(undo));
            Assert.That(Ui.Find<Button>("PlanRedo").IsEnabled, Is.EqualTo(redo));
        });
    }

    private static void OpenRefreshMenu()
    {
        var split = Ui.Find<SplitButton>("PlanRefresh");
        ((Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider)Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(split)
            .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)).Expand();
    }
    private async Task OpenResetDialog()
    {
        await Ui.Run(OpenRefreshMenu);
        await Ui.Until(() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
            .SelectMany(p => Ui.Tree(p.Child)).OfType<MenuFlyoutItem>().Any(i => AutomationProperties.GetAutomationId(i) == "PlanReset"));
        await Ui.Run(() => {
            var item = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                .SelectMany(p => Ui.Tree(p.Child)).OfType<MenuFlyoutItem>().Single(i => AutomationProperties.GetAutomationId(i) == "PlanReset");
            Assert.That(item.IsEnabled, Is.True);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(item)
                .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        });
        await Ui.DialogReady("PlanResetConfirm");
    }

    [TestCase(false), TestCase(true), Category("PlanReadDate")]
    public async Task SettingsRecalculationNoticeFollowsTheSettingsPathAndUndoClosesItWithoutRedoNotice(bool reset)
    {
        readDay = PlanClock.Today;
        FakePlanEditor.Save(root, new([
            new(new("I1", "設計", "acme/repo"), "", true),
            // A dated task whose first working day becomes a company day off: its end moves a day later. A
            // settings change reschedules only tasks that have dates (#236).
            new(new("I2", "Dated task", "acme/repo") { Parent = "I1", Remaining = 16, Start = readDay, End = readDay.AddDays(1) }, "", true),
            new(new("I3", "Unchanged task", "acme/repo"), "", true)
        ], 4));
        await Open();
        if (reset) {
            await OpenResetDialog();
            // A later edit reschedules only the edited task and its successors (#236), not the whole plan.
            await Ui.Run(() => Assert.That(Ui.DialogText("PlanResetConfirm"), Does.Not.Contain("全体を再計算")));
            await Ui.Run(() => Ui.DialogButton("PlanResetConfirm", "PrimaryButton"));
            await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
            await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").Message, Does.Contain("日程は GitHub の値のままです。")));
        }
        // #236 part B: only a settings change that reschedules shows 日程を再計算しました and its filter; a
        // cell edit no longer opens this bar, so the case drives the settings path it now owns. A calendar
        // change is one of the three reschedule triggers (PlanOperations.Apply) and needs no assignee.
        await Settings();
        await AddDay("PlanCompanyDaysOff", readDay);
        // Core accepts the settings change at once; the workspace shows the notice when that save completes.
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult") is { IsOpen: true, Title: "日程を再計算しました" });
        await Ui.Run(() => {
            var bar = Ui.Find<InfoBar>("PlanRefreshResult");
            Assert.That(bar.IsOpen, Is.True);
            Assert.That(bar.Title, Is.EqualTo("日程を再計算しました"));
            Assert.That(bar.Message, Does.StartWith("日程が変わったタスク ").And.EndsWith("。発行すると GitHub に反映します。"));
        });
        await Ui.Ready<Button>("PlanRefreshChangedFilter");
        await Ui.Run(() => Ui.Click("PlanRefreshChangedFilter"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count < 4);
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "issue165-recalculation-" + reset));
        await Ui.Run(() => {
            // PlanTasks holds row identities, not titles: the moved row is I2 in this fixture.
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Does.Contain("I2"));
            Ui.Click("PlanUndo");
        });
        await Ui.Idle();
        // Undo restores the date; the filter is a view, not an edit, so it stays until the PMO clears it.
        Assert.That(workspace.Session!.Document.State.Rows.Single(r => r.Identity == "I2").End, Is.EqualTo(readDay.AddDays(1)));
        // #236 part B: the recalculation notice is not closed by Undo - it is replaced when the next
        // operation starts (PlanWorkspaceBusy closes it before running one). The subject survives as the
        // notice not outliving its recalculation: a refresh replaces it, and Redo never brings it back.
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled && Ui.Find<InfoBar>("PlanRefreshResult").IsOpen);
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").Title, Does.StartWith("最新の情報に更新しました（"));
            Ui.Click("PlanRedo");
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").Title, Does.Not.StartWith("日程を再計算しました")));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReset")]
    public async Task ResetConfirmationCancelBusyResultAndUndoUseTheRealView()
    {
        readDay = PlanClock.Today;
        var dateText = readDay.ToString("M/d (ddd)", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));
        await Open();
        await Ui.Run(() => Assert.That(Ui.Find<SplitButton>("PlanRefresh").Flyout.AreOpenCloseAnimationsEnabled, Is.False));
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local edit")]), readDay);
        await workspace.Session.Execute(new InsertPlanRows([PlanRow.New("New task", "acme/repo")]), readDay);
        var before = PlanJson.Text(workspace.Session.Document);
        await OpenResetDialog();
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanResetConfirm")!;
            Assert.That(dialog.Title, Is.EqualTo("GitHub の状態に戻しますか？"));
            Assert.That(dialog.DefaultButton, Is.EqualTo(ContentDialogButton.Close));
            Assert.That(dialog.PrimaryButtonText, Is.EqualTo("GitHub の状態に戻す"));
            Assert.That(dialog.CloseButtonText, Is.EqualTo("キャンセル"));
            var text = Ui.DialogText("PlanResetConfirm");
            Assert.That(text, Does.Contain("破棄する未発行のタスク 2"));
            Assert.That(text, Does.Contain("入力した変更 2（新しく追加した 1 タスクを含む。これらは作られません）"));
            Assert.That(text, Does.Contain(workspace.Selected!.Title + " の未発行の変更をすべて破棄します。"));
            // The 基準日 is already today, so the reset changes nothing worth a line; settings stay as they are.
            Assert.That(text, Does.Not.Contain("基準日は").And.Not.Contain("稼働率"));
            Assert.That(text, Does.Contain("「元に戻す」で取り消せます。"));
            Ui.DialogButton("PlanResetConfirm", "CloseButton");
        });
        await Ui.Idle();
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, holdQuery = "ProjectFields" }));
        await OpenResetDialog();
        await Ui.Run(() => Ui.DialogButton("PlanResetConfirm", "PrimaryButton"));
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("PlanBusyHeading").Text, Is.EqualTo("GitHub の状態に戻しています"));
                Assert.That(Ui.Find<ContentControl>("PlanBusyPanel").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(Ui.Find<SplitButton>("PlanRefresh").IsEnabled, Is.False);
                var reset = ((MenuFlyout)Ui.Find<SplitButton>("PlanRefresh").Flyout).Items.OfType<MenuFlyoutItem>().Single();
                Assert.That(reset.IsEnabled, Is.False);
                Assert.That(AutomationProperties.GetHelpText(reset), Is.EqualTo("更新または発行が進行中です。"));
            });
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 0 タスク"));
            var bar = Ui.Find<InfoBar>("PlanRefreshResult");
            Assert.That(bar.IsOpen, Is.True);
            Assert.That(bar.Title, Does.StartWith("GitHub の状態に戻しました（"));
            Assert.That(bar.Message, Is.EqualTo("基準日を " + dateText + " にしました。日程は GitHub の値のままです。"));
            Assert.That(bar.ActionButton, Is.Null);
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.True);
            Assert.That(Ui.Find<Button>("PlanRedo").IsEnabled, Is.False);
        });
        await OpenResetDialog();
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("PlanResetConfirm"), Does.Contain("未発行の変更はありません。"));
            Ui.DialogButton("PlanResetConfirm", "CloseButton");
        });
        await Ui.Idle();
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 2 タスク")));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true), Category("PlanReset")]
    public async Task ResetMenuExplainsUnreconciledPublication(bool unverified)
    {
        await Open();
        await workspace.Session!.SaveSync(workspace.Session.Document.Sync with {
            Publish = new(Guid.NewGuid().ToString("N"), []), Unverified = unverified ? ["I1"] : []
        });
        await Ui.Run(OpenRefreshMenu);
        await Ui.Until(() => VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).SelectMany(p => Ui.Tree(p.Child)).OfType<MenuFlyoutItem>().Any());
        await Ui.Run(() => {
            var item = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).SelectMany(p => Ui.Tree(p.Child)).OfType<MenuFlyoutItem>()
                .Single(i => AutomationProperties.GetAutomationId(i) == "PlanReset");
            Assert.That(item.IsEnabled, Is.False);
            Assert.That(AutomationProperties.GetHelpText(item), Is.EqualTo("発行の結果を確認するまで使えません"));
            Assert.That(ToolTipService.GetToolTip(item), Is.EqualTo("発行の結果を確認するまで使えません"));
            Ui.Find<SplitButton>("PlanRefresh").Flyout.Hide();
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false, true), TestCase(true, true), TestCase(false, false), Category("PlanReadDate")]
    public async Task RefreshResultNamesTheReadTimeWithoutDateChangesAndItsFilterCanBeCleared(bool closeBar, bool advanceDate)
    {
        readDay = new(2026, 10, 6);
        FakePlanEditor.Save(root, new([
            new(new("I1", "設計", "acme/repo"), "", true),
            new(new("I2", "Moving task", "acme/repo") { Parent = "I1", Remaining = 16, Start = new(2026, 10, 5), End = new(2026, 10, 6) }, "", true),
            new(new("I3", "Unchanged task", "acme/repo"), "", true)
        ], 4));
        await Open();
        if (advanceDate) readDay = new(2026, 10, 7);
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        var filtered = false;
        string? offeredId = null, offeredLabel = null;
        await Ui.Run(() => {
            var bar = Ui.Find<InfoBar>("PlanRefreshResult");
            Assert.That(bar.Severity, Is.EqualTo(InfoBarSeverity.Informational));
            Assert.That(bar.IsClosable, Is.True);
            Assert.That(bar.Title, Does.StartWith("最新の情報に更新しました（"));
            // #236 part B: a refresh reads and moves no date. The status bar already counts the lateness, so the
            // bar repeats neither counts nor a no-change sentence; it offers only the one useful filter.
            Assert.That(bar.Message, Is.Empty);
            Assert.That(bar.Content, Is.Null);
            // The bar's lateness filter is an identity set over this sheet. Its id and label are read here,
            // on the UI thread; the rendered button is found and invoked below, waiting for it to realise.
            if (bar.ActionButton is Button button) {
                var id = AutomationProperties.GetAutomationId(button);
                var label = button.Content as string ?? "";
                offeredId = id; offeredLabel = label;
                // The bar never offers a zero count: the 期限超過 fallback is absent when nothing is
                // overdue, and a count filter always names a nonzero set (docs/spec.md, #236 part B).
                if (id != "PlanRefreshCauseFilter")
                    Assert.That(int.TryParse(label.Split(' ')[^1].Replace(",", ""), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var offeredCount) && offeredCount > 0,
                        Is.True, "A count filter in the bar must name a nonzero set: " + label);
                filtered = true;
            }
        });
        if (filtered) {
            // The InfoBar holds the logical action button; the tree materialises its own instance, and its
            // id is not necessarily carried over. Wait for the rendered button (the search covers the whole
            // window, not just the view subtree) and invoke it, which is what a user does.
            Button? realized = null;
            await Ui.Until(() => {
                realized = Ui.Tree(Ui.Root).OfType<Button>().FirstOrDefault(b =>
                    (b.Content as string == offeredLabel || AutomationProperties.GetName(b) == offeredLabel) && b.IsLoaded);
                return realized is not null;
            }, () => "the bar's filter must be rendered: [" + offeredLabel + "] id=" + offeredId
                + "; buttons=[" + string.Join(" | ", Ui.Tree(Ui.Root).OfType<Button>().Select(b => AutomationProperties.GetName(b) + "/" + b.Content as string)) + "]");
            await Ui.Run(() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(realized!)
                .GetPattern(PatternInterface.Invoke)).Invoke());
            await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count < 4);
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "issue130-refresh-filter-" + closeBar + "-" + advanceDate));
        }
        await Ui.Run(() => {
            if (closeBar) Ui.Find<InfoBar>("PlanRefreshResult").IsOpen = false;
            else Ui.Find<TextBox>("PlanSheetFilter").Text = "";
            // Closing the bar clears the identity filter; clearing the text restores the unfiltered plan.
            if (!closeBar) Ui.Tree(view).OfType<PlanSheetView>().Single().ClearIdentityFilter();
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 4);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("PlanRefresh");
        });
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Run(() => {
            var bar = Ui.Find<InfoBar>("PlanRefreshResult");
            Assert.That(bar.Title, Does.StartWith("最新の情報に更新しました（"));
            Assert.That(bar.Message, Is.Empty);
            Assert.That(bar.Content, Is.Null, "A refresh with no added Issue shows no added-Issue filter.");
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReadDate")]
    public async Task RefreshNamesIssuesAddedOnGitHubPlacesTheSubIssueAndShowsThePartialTotal()
    {
        readDay = new(2026, 10, 6);
        FakePlanEditor.Save(root, new([
            new(new("I1", "R01 受注データの外部連携", "acme/repo"), "", true),
            new(new("I2", "R01 PG-001 製造", "acme/repo") { Parent = "I1", Estimate = 16 }, "", true),
            new(new("I3", "R02 出荷データの外部連携", "acme/repo"), "", true),
            new(new("I6", "R02 SA-001 現行業務の調査", "acme/repo") { Parent = "I3", Estimate = 8 }, "", true)
        ], 7));
        await Open("R01 受注データの外部連携");
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { NextId = 7, Issues = remote.Issues
            .Add(new(new("I4", "R01 PG-005 不具合修正", "acme/repo") { Parent = "I1" }, "", true))
            .Add(new(new("I5", "顧客からの問い合わせ対応", "acme/repo"), "", true)) });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Ready<Button>("PlanRefreshAddedFilter");
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", "I4", "I3", "I6", "I5" }));
            Assert.That(AutomationProperties.GetName(Ui.Find<FrameworkElement>("PlanIndicator3")), Does.Contain("未発行の変更あり"));
            Assert.That(workspace.Session.Changes(readDay).Fields.Where(p => p.Value.Contains(PlanField.Order)).Select(p => p.Key), Is.EqualTo(new[] { "I4" }));
            var links = workspace.Session.Document.Sync.IssueLinks;
            string Number(string id) => links[id].Caption[links[id].Caption.LastIndexOf('#')..];
            Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").Message, Does.EndWith(
                $"GitHub で追加された Issue 2: {Number("I4")} R01 PG-005 不具合修正、{Number("I5")} 顧客からの問い合わせ対応"));
            var total = Ui.Find<FrameworkElement>("PlanCell1_Estimate");
            Assert.That(AutomationProperties.GetName(total), Does.Contain("16h（見積のないタスク 1 件を除く）"));
            Assert.That(ToolTipService.GetToolTip(total), Is.EqualTo("見積工数 · 16h（見積のないタスク 1 件を除く）"));
            // The known sum reads first; the left-out count follows as a number-only caution badge, and the
            // sentence that explains it lives in the tooltip and the accessible name.
            var cell = (PlanSummaryCell)total;
            Assert.That(cell.Text.Text, Is.EqualTo("16h"));
            Assert.That(cell.Missing.Text, Is.EqualTo("1"));
            Assert.That(cell.Missing.FontSize, Is.EqualTo(11));
            Assert.That(cell.MissingTag.TransformToVisual(cell).TransformPoint(default).X,
                Is.GreaterThan(cell.Text.TransformToVisual(cell).TransformPoint(default).X), "The badge follows the sum.");
            Assert.That(cell.ActualWidth, Is.EqualTo(72).Within(1), "見積 h keeps its compact default width.");
            Assert.That(cell.Text.IsTextTrimmed, Is.False, "The sum stays readable beside its badge at the default width.");
            Assert.That(AutomationProperties.GetName(Ui.Find<FrameworkElement>("PlanIndicator1")), Does.Contain("見積のないタスク 1"));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "issue245-added-issues"));
        await Ui.Run(() => Ui.Click("PlanRefreshAddedFilter"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 3);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I4", "I5", "" }));
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Text, Is.EqualTo("追加された Issue"));
            Ui.Find<InfoBar>("PlanRefreshResult").IsOpen = false;
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 7);
        // A reset never lists added Issues, so its bar offers no filter for them either.
        await OpenResetDialog();
        await Ui.Run(() => Ui.DialogButton("PlanResetConfirm", "PrimaryButton"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Tree(Ui.Find<InfoBar>("PlanRefreshResult")).OfType<Button>().Select(AutomationProperties.GetAutomationId),
            Does.Not.Contain("PlanRefreshAddedFilter")));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReadDate")]
    public async Task RefreshBarCountsTheLatenessOfTheReadNotOfThePreviousStatusDate()
    {
        // The weekly flow: last week's 基準日 is still set when the PMO refreshes, and the refresh moves it to
        // the read day. The bar's counts must be the ones the refreshed sheet shows, not last week's.
        readDay = new(2026, 10, 6);
        FakePlanEditor.Save(root, new([
            new(new("I1", "設計", "acme/repo"), "", true),
            new(new("I2", "In progress", "acme/repo") { Parent = "I1", Actual = 8, Remaining = 8, Start = new(2026, 10, 5), End = new(2026, 10, 6) }, "", true)
        ], 3));
        await Open();
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = new DateTimeOffset(new DateTime(2026, 10, 20)));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.StatusDate == new DateOnly(2026, 10, 20));
        await Ui.Ready<Button>("PlanStatusOverdueFilter");
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Settings.StatusDate, Is.EqualTo(new DateOnly(2026, 10, 6)), "The read day becomes the 基準日.");
            Assert.That(Ui.Tree(Ui.Root).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanStatusOverdueFilter"), Is.False,
                "On the read day the task is not overdue.");
            Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").Message, Does.Not.Contain("期限超過"),
                "The bar reports the refreshed sheet's lateness, not the previous 基準日's.");
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReadDate")]
    public async Task RefreshResultKeepsThePmoStatusDateAndReportsNoMovedDate()
    {
        readDay = new(2026, 10, 6);
        await Open();
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = new DateTimeOffset(new DateTime(2026, 10, 7)));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.StatusDate == new DateOnly(2026, 10, 7));
        await Ui.Idle();
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Run(() => {
            var bar = Ui.Find<InfoBar>("PlanRefreshResult");
            // #236 part B: the status date a PMO chose is not "moved back" by a refresh, and the bar no
            // longer reports a 基準日 change at all — it reports that no date moved (docs/spec.md).
            Assert.That(bar.Title, Does.StartWith("最新の情報に更新しました（"));
            Assert.That(bar.Message, Is.Empty);
            // The read day still becomes the 基準日 of the read document.
            Assert.That(Ui.Find<CalendarDatePicker>("PlanStatusDate").Date!.Value.Date, Is.EqualTo(new DateTime(2026, 10, 6)));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanReadDate")]
    public async Task ClearingStatusDateRestoresAcceptedValueWithoutOperation()
    {
        readDay = new(2026, 10, 6);
        await Open();
        var before = workspace.Session!.Document;
        var undo = workspace.Session.UndoCount;
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = null);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<CalendarDatePicker>("PlanStatusDate").Date!.Value.Date,
            Is.EqualTo(new DateTime(2026, 10, 6))));
        Assert.That(workspace.Session.Document, Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
    }

    [Test]
    public async Task RemoteRefreshCancelsUnacceptedFilterAndKeepsTextRowsAndCountTogether()
    {
        await OpenWithAcceptedFilter();
        var before = workspace.Session!.Document.State;
        var undo = workspace.Session.UndoCount;
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdQuery = "ProjectFields"
        }));
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanSheetFilter").Text = "実装";
            Ui.Click("PlanRefresh");
        });
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Task.Delay(350);
            await Ui.Run(AssertAcceptedFilter);
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(AssertAcceptedFilter);
        Assert.That(workspace.Session.Document.State.Rows.Select(row => (row.Identity, row.Title)),
            Is.EqualTo(before.Rows.Select(row => (row.Identity, row.Title))));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase("PlanShowSummary"), TestCase("PlanShowSettings")]
    public async Task LeavingPlanCancelsUnacceptedFilterWithoutUnloadingTheSheet(string command)
    {
        await OpenWithAcceptedFilter();
        var before = workspace.Session!.Document.State;
        var undo = workspace.Session.UndoCount;
        PlanSheetView localSheet = null!;
        await Ui.Run(() => {
            localSheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Ui.Find<TextBox>("PlanSheetFilter").Text = "実装";
            Ui.Click(command);
        });
        await Ui.Idle();
        await Task.Delay(350);
        await Ui.Run(() => {
            Assert.That(localSheet.IsLoaded, Is.True, "Changing the visible surface keeps the sheet loaded.");
            Assert.That(((FrameworkElement)localSheet.Parent).Visibility, Is.EqualTo(Visibility.Collapsed));
            AssertAcceptedFilter();
            Ui.Click("PlanShowTasks");
        });
        await Ui.Idle();
        await Ui.Run(AssertAcceptedFilter);
        Assert.That(workspace.Session.Document.State, Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    private async Task OpenWithAcceptedFilter()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { NextId = 4, Issues = [state.Issues[0],
            new(new("I2", "設計レビュー", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true),
            new(new("I3", "実装", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)] });
        await Open();
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "設計");
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 3);
        await Ui.Run(AssertAcceptedFilter);
    }

    private static void AssertAcceptedFilter()
    {
        Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Text, Is.EqualTo("設計"));
        Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("2 件"));
        Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I1", "I2", "" }));
    }

    [Test]
    public async Task ProjectAndReviewListsHaveNoTransitions()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await Ui.Ready<ListView>("AvailableProjects");
        await Ui.Run(() => {
            PlanSheetHostedTests.AssertQuietList(Ui.Find<ListView>("AvailableProjects"));
            Assert.That(Ui.Find<Button>("PlanProjectPicker").Flyout.AreOpenCloseAnimationsEnabled, Is.False);
        });
        await OpenProjectPicker();
        await Ui.Run(() => {
            PlanSheetHostedTests.AssertQuietList(Ui.Popup<ListView>("RegisteredProjects")!);
            Ui.Find<Button>("PlanProjectPicker").Flyout.Hide();
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Ui.Click("PlanPublish");
        });
        await Ui.Idle();
        await Ui.Run(() => PlanSheetHostedTests.AssertQuietList(Ui.Find<ListView>("PlanPublishLines")));
    }

    [TestCase(1280, 800), TestCase(1920, 1032), TestCase(1000, 720), TestCase(900, 720)]
    [Category("PlanSheetReview")]
    public async Task DefaultDividerFitsColumnsOrKeepsMinimumGanttAtWorkspaceClientSize(int width, int height)
    {
        FakePlanEditor.Save(root, new(Enumerable.Range(1, 40).Select(i => new PlanFakeIssue(
            new("I" + i, i == 1 ? "設計" : "Planning task " + i, "acme/repo")
                { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)).ToImmutableArray(), 41));
        await Open();
        await Ui.Run(() => ResizeWorkspace(width, height));
        await Ui.Until(() => Math.Abs(Ui.Root.ActualWidth - width) < 1 && Math.Abs(Ui.Root.ActualHeight - height) < 1);
        await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            var horizontal = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            var headers = new[] { "Id", "Indicator", "Title", "Assignees", "Estimate", "Remaining", "Actual", "Start", "End", "ForecastEnd", "Predecessors" }
                .Select(name => Ui.Find<TextBlock>("PlanHeader" + name)).ToArray();
            var rowBounds = Ui.Tree(sheet.List).OfType<TextBox>()
                .Where(cell => AutomationProperties.GetAutomationId(cell).EndsWith("_Title", StringComparison.Ordinal))
                .Select(cell => cell.TransformToVisual(sheet.List).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight))).ToArray();
            var fullRows = rowBounds.Count(bounds => bounds.Top >= 0 && bounds.Bottom <= sheet.List.ActualHeight);
            var headerBounds = headers.Select(header => header.TransformToVisual(sheet)
                .TransformBounds(new Rect(0, 0, header.ActualWidth, header.ActualHeight))).ToArray();
            var columnsRight = headerBounds.Max(bounds => bounds.Right);
            Console.WriteLine($"Default divider: client={Ui.Root.ActualWidth}x{Ui.Root.ActualHeight}, scale={Ui.Root.XamlRoot.RasterizationScale}, sheetControl={sheet.ActualWidth}, sheetViewport={horizontal.ViewportWidth}, sheetExtent={horizontal.ExtentWidth}, sheetOffset={horizontal.HorizontalOffset}, Gantt={chart.ViewportWidth}, weeks={chart.ViewportWidth / (7 * sheet.DayWidth):F2}, rowViewport={sheet.List.ActualHeight}, fullRows={fullRows}");
            Assert.That(horizontal.HorizontalOffset, Is.Zero);
            Assert.That(chart.ViewportWidth, Is.GreaterThanOrEqualTo(320));
            foreach (var bounds in headerBounds) Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0));
            // #236 part B's 見込終了 column moved the fit threshold: the divider fits the columns when the
            // client can hold them plus the 320 px Gantt minimum, and otherwise the sheet scrolls while the
            // Gantt keeps that minimum (PlanSheetView picks SheetWidth + 6 unless the clamp bites).
            if (columnsRight <= horizontal.ViewportWidth + 0.5) {
                Assert.That(horizontal.ScrollableWidth, Is.Zero, "Fitted columns must not scroll.");
                Assert.That(columnsRight, Is.LessThanOrEqualTo(horizontal.ViewportWidth));
            } else {
                Assert.That(horizontal.ExtentWidth, Is.GreaterThan(horizontal.ViewportWidth), "Unfitted columns must scroll.");
                Assert.That(chart.ViewportWidth, Is.GreaterThanOrEqualTo(320), "The sheet may not take the Gantt's minimum.");
            }
            if (width == 1920) {
                // With 見積 h / 残 h at 72 px and 見込終了 at 108 px the sheet takes its 993 px of columns plus the
                // 6 px clearance, leaving 1920 - 42 (card) - 20 (right reservation) - 993 = 865.
                Assert.That(chart.ViewportWidth, Is.EqualTo(865).Within(2), "Default-column budget, allowing pixel rounding.");
                Assert.That(fullRows, Is.EqualTo(29).Within(2));
            }
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, $"default-divider-{width}x{height}"));
    }

    [TestCase(1000), TestCase(1440), Category("ShellChrome")]
    public async Task TitleBarUsesGanttGlyphAndPlacesGearBesideNativeCaptions(int width)
    {
        await Open();
        await Ui.Run(() => {
            Ui.Window.ExtendsContentIntoTitleBar = true;
            Ui.Window.SetTitleBar(view.WorkspaceTitleBar);
            ResizeWorkspace(width, 720);
        });
        try {
            await Ui.Until(() => Math.Abs(Ui.Root.ActualWidth - width) < 1 && Math.Abs(view.ActualWidth - width) < 1);
            await Ui.Run(() => {
                var bar = view.WorkspaceTitleBar;
                var gear = Ui.Find<Button>("PlanShowSettings");
                var bounds = gear.TransformToVisual(Ui.Root).TransformBounds(new Rect(0, 0, gear.ActualWidth, gear.ActualHeight));
                var captionLeft = Ui.Root.ActualWidth - Ui.Window.AppWindow.TitleBar.RightInset / Ui.Root.XamlRoot.RasterizationScale;
                Console.WriteLine($"Title bar geometry: width={Ui.Root.ActualWidth}, scale={Ui.Root.XamlRoot.RasterizationScale}, rightInset={Ui.Window.AppWindow.TitleBar.RightInset}, gearRight={bounds.Right}, gap={captionLeft - bounds.Right}");
                Assert.That(captionLeft - bounds.Right, Is.InRange(0d, 8d));
                Assert.That(bar.IconSource, Is.Null);
                Assert.That(bar.LeftHeader, Is.TypeOf<PathIcon>());
                var geometry = ((PathIcon)bar.LeftHeader).Data as Microsoft.UI.Xaml.Media.PathGeometry;
                Assert.That(geometry, Is.Not.Null);
                Assert.That(geometry!.Figures.Select(f => f.StartPoint), Is.EqualTo(new[] { new Point(1, 2), new Point(6, 7), new Point(3, 12) }));
                Assert.That(geometry.Figures.All(f => f.IsClosed), Is.True);
            });
        } finally { await Ui.Run(() => { Ui.Window.SetTitleBar(null); Ui.Window.ExtendsContentIntoTitleBar = false; }); }
    }

    [Test, Category("ShellChrome")]
    public async Task CommandRowUsesSubtleHistoryAndSeparatesTheThreeGroups()
    {
        await Open();
        await Ui.Run(() => {
            var undo = Ui.Find<Button>("PlanUndo"); var redo = Ui.Find<Button>("PlanRedo");
            Assert.That(undo.Style, Is.SameAs(Application.Current.Resources["SubtleButtonStyle"]));
            Assert.That(redo.Style, Is.SameAs(undo.Style));
            var group = (Panel)undo.Parent;
            var dividers = group.Children.OfType<Border>().Where(b => b.Width == 1 && b.Height == 20).ToArray();
            Assert.That(dividers, Has.Length.EqualTo(2));
            var date = Ui.Find<CalendarDatePicker>("PlanStatusDate");
            double X(FrameworkElement e) => e.TransformToVisual(group).TransformPoint(new Point()).X;
            Assert.That(X(dividers[0]), Is.GreaterThan(X(date) + date.ActualWidth - 1).And.LessThan(X(undo)));
            Assert.That(X(dividers[1]), Is.GreaterThan(X(redo) + redo.ActualWidth - 1).And.LessThan(X(Ui.Find<TextBlock>("PlanUnpublished"))));
            var commands = Ui.Find<CommandBar>("PlanSheetCommands").SecondaryCommands;
            Assert.That(commands.Select(c => c is AppBarButton b ? b.Label : "|"),
                Is.EqualTo(new[] { "上へ移動", "下へ移動", "階層順に並べ替え", "|", "コピー", "貼り付け", "下へコピー", "クリア", "|",
                    "行を削除", "完了にする", "中止にする", "この版から外す", "未完了に戻す", "|", "CSV から追加…", "CSV に書き出し…", "表示列" }));
            Assert.That(commands.OfType<AppBarButton>().Take(2).Select(b => b.KeyboardAcceleratorTextOverride), Is.EqualTo(new[] { "Alt+Shift+↑", "Alt+Shift+↓" }));
            Assert.That(commands.OfType<AppBarButton>().Skip(3).Take(4).Select(b => b.KeyboardAcceleratorTextOverride),
                Is.EqualTo(new[] { "Ctrl+C", "Ctrl+V", "Ctrl+D", "Delete" }));
        });
    }

    [Test, Category("ShellChrome")]
    public async Task GanttLegendOnlyAppearsOnPlanWhileProjectCountsRemain()
    {
        await Open();
        foreach (var command in new[] { "PlanShowTasks", "PlanShowSummary", "PlanShowSettings", "PlanShowTasks", "PlanPublish" }) {
            await Ui.Run(() => Ui.Click(command)); await Ui.Idle();
            await Ui.Run(() => {
                Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility,
                    Is.EqualTo(command == "PlanShowTasks" ? Visibility.Visible : Visibility.Collapsed));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 0 · タスク 1"));
            });
        }
        foreach (var command in new[] { "PlanChooseProject", "PlanConnection" }) {
            await PickerCommand(command);
            await Ui.Run(() => {
                Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility, Is.EqualTo(Visibility.Collapsed));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 0 · タスク 1"));
            });
        }
    }

    [TestCase(false, false, "")]
    [TestCase(true, false, "期限超過 1 · 遅れの原因 1")]
    [TestCase(false, true, "予定より遅れ 1 · 遅れの原因 1")]
    [TestCase(true, true, "期限超過 1 · 予定より遅れ 1 · 遅れの原因 2")]
    [Category("ShellChrome")]
    public async Task StatusCountsOmitZeroLatenessParts(bool overdue, bool later, string expectedMarks)
    {
        var date = new DateOnly(2026, 10, 5);
        var state = FakePlanEditor.Load(root);
        var row = state.Issues[0].Row with { Start = date, End = date.AddDays(2) };
        // 遅れの原因 (#236 part B) is an overdue task or a task whose own remaining work pushes its forecast
        // past its planned end, so the overdue row and the later row each contribute one cause.
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = row with { Start = overdue ? date.AddDays(-3) : date, End = overdue ? date.AddDays(-3) : date.AddDays(2) } },
            new(row with { Identity = "I2", Title = "後続" }, "", true),
            new(row with { Identity = "I3", Title = "見込み超過", Start = date, End = date.AddDays(2), Estimate = 40, Remaining = 16 }, "", true)] });
        await Open();
        await Ui.Run(async () => {
            var session = workspace.Session!;
            await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = date }), date);
            // Pulling I3's end onto the 基準日 leaves its 16h forecast on the next day: a later task that is
            // not overdue, so the two lateness levels are exercised independently.
            if (later) await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I3", PlanField.End, date)]), date);
            Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh();
            // #236 part B keeps the requirement and task counts in the status text and moves the lateness
            // counts beside it as identity-set filter buttons, so a zero part is absent rather than "0".
            Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 0 · タスク 3"));
            var marks = Ui.Tree(Ui.Find<StackPanel>("PlanStatusMarks")).OfType<Button>()
                .Select(b => b.Content as string ?? "").Where(t => t.Length > 0).ToArray();
            Assert.That(string.Join(" · ", marks), Is.EqualTo(expectedMarks));
        });
    }

    [Test, Category("ShellChrome")]
    public async Task StatusCountFilterKeepsAncestorRequirementRowsDimmedForContext()
    {
        var date = new DateOnly(2026, 10, 5);
        readDay = date;
        var requirement = new PlanRow("I1", "要求事項", "acme/repo");
        var overdue = new PlanRow("I2", "遅れ", "acme/repo") { Parent = "I1", Start = date.AddDays(-3), End = date.AddDays(-3), Estimate = 8, Remaining = 8 };
        var onPlan = new PlanRow("I3", "予定どおり", "acme/repo") { Parent = "I1", Start = date.AddDays(3), End = date.AddDays(3), Estimate = 8, Remaining = 8 };
        var outside = new PlanRow("I4", "別の要求", "acme/repo") { Start = date.AddDays(3), End = date.AddDays(3), Estimate = 8, Remaining = 8 };
        FakePlanEditor.Save(root, new([new(requirement, "", true), new(overdue, "", true), new(onPlan, "", true), new(outside, "", true)], 5));
        await Open("要求事項");
        await Ui.Ready<Button>("PlanStatusOverdueFilter");
        await Ui.Run(() => Ui.Click("PlanStatusOverdueFilter"));
        await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            // The matched row and its ancestor requirement row stay; unrelated rows leave the view.
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I1", "I2", "" }));
            // XAML stores Opacity as a float, so compare against the float value the product sets.
            Assert.That(sheet.Realized.Single(row => row.Identity == "I1").Opacity, Is.EqualTo(0.55f),
                "A count filter keeps the ancestor requirement row for context and dims it.");
            Assert.That(sheet.Realized.Single(row => row.Identity == "I2").Opacity, Is.EqualTo(1),
                "The matched task keeps its ordinary weight.");
            sheet.ClearIdentityFilter();
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 5);
        await Ui.Run(() => Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().Realized.Single(row => row.Identity == "I1").Opacity, Is.EqualTo(1),
            "Clearing the filter restores the ancestor row."));
    }


    [Test, Category("ShellChrome")]
    public async Task WorkspaceUndoAndRedoDismissTheRowCommandReport()
    {
        // #231 C2: the report describes the operation; a history change removes it, so it cannot claim a
        // state the Undo took back. Redo must not resurrect the stale sentence.
        readDay = new DateOnly(2026, 10, 5);
        FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)], 2));
        await Open();
        // Read the visual tree on the XAML dispatcher thread like every other direct tree walk.
        PlanSheetView sheet = null!;
        await Ui.Run(() => sheet = Ui.Tree(view).OfType<PlanSheetView>().Single());
        await Ui.Run(() => { sheet.Select("I1", PlanField.Title, false); sheet.Refresh(); });
        await Ui.Run(() => sheet.ShowRowMenu("I1", PlanField.Title, sheet.Realized.Single(row => row.Identity == "I1")));
        await Ui.Ready<MenuFlyoutItem>("PlanRowComplete");
        await Ui.Run(() => {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Popup<MenuFlyoutItem>("PlanRowComplete")!);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        });
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Single().Closed);
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("完了にする（未発行）"));
            Ui.Click("PlanUndo");
        });
        await Ui.Until(() => !workspace.Session!.Document.State.Rows.Single().Closed);
        // The undo button clears the report after the undo completes, so let the command finish before reading it.
        await Ui.Idle();
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length == 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty, "undo dismisses the stale report");
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("PlanRedo");
        });
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Single().Closed);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty, "redo does not resurrect the report"));
    }

    [Test, Category("ShellChrome")]
    public async Task ProjectPickerChecksOnlyOpenProjectAndSeparatesIconCommands()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Idle();
        await OpenProjectPicker();
        await WaitForProjectPickerItems("P2");
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("RegisteredProjects");
            var titleXs = new List<double>();
            for (var i = 0; i < list.Items.Count; i++) {
                var choice = (ProjectChoice)list.Items[i];
                var item = (ListViewItem)list.ContainerFromIndex(i);
                var check = Ui.Tree(item).OfType<FontIcon>().Single(f => f.Glyph == "\uE73E");
                Assert.That(check.Opacity, Is.EqualTo(choice.Id == workspace.Selected!.Id ? 1d : 0d));
                var texts = Ui.Tree(item).OfType<TextBlock>().ToArray();
                Assert.That(texts.Any(t => t.Text == $"{choice.OwnerLogin} · プロジェクト {choice.Number}"), Is.True, string.Join(" | ", texts.Select(t => t.Text)));
                titleXs.Add(texts.Single(t => t.Text == choice.Title).TransformToVisual(list).TransformPoint(new Point()).X);
            }
            Assert.That(titleXs.Distinct().Count(), Is.EqualTo(1));
            var content = (StackPanel)((Flyout)Ui.Find<Button>("PlanProjectPicker").Flyout).Content;
            Assert.That(content.Children[1], Is.TypeOf<Border>());
            Assert.That(((Border)content.Children[1]).Height, Is.EqualTo(1));
            foreach (var id in new[] { "PlanChooseProject", "PlanConnection" })
                Assert.That(Ui.Tree(Ui.Find<Button>(id)).OfType<FontIcon>().Any(), Is.True);
        });
    }

    [Test, Category("ShellChrome")]
    public async Task ProjectPickerChecksCurrentProjectAfterTwoSwitchesAndReopening()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Idle();
        await OpenProjectPicker();
        await WaitForProjectPickerItems("P2");
        foreach (var project in new[] { "P1", "P2" }) {
            await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == project));
            await Ui.Until(() => workspace.Selected?.Id.NodeId == project);
            await OpenProjectPicker();
            await WaitForProjectPickerItems(project);
            await Ui.Run(() => Ui.Find<Button>("PlanProjectPicker").Flyout.Hide());
            await OpenProjectPicker();
            await WaitForProjectPickerItems(project);
        }
    }

    private async Task WaitForProjectPickerItems(string currentProject)
    {
        await Ui.Until(() => {
            var list = Ui.Find<ListView>("RegisteredProjects");
            if (list.Items.Count != 2 || workspace.Selected?.Id.NodeId != currentProject) return false;
            var checkedProjects = new List<string>();
            for (var i = 0; i < list.Items.Count; i++) {
                if (list.ContainerFromIndex(i) is not ListViewItem { IsLoaded: true } item) return false;
                var choice = (ProjectChoice)list.Items[i];
                var texts = Ui.Tree(item).OfType<TextBlock>().ToArray();
                if (!texts.Any(t => t.Text == choice.Title) ||
                    !texts.Any(t => t.Text == $"{choice.OwnerLogin} · プロジェクト {choice.Number}")) return false;
                var check = Ui.Tree(item).OfType<FontIcon>().SingleOrDefault(f => f.Glyph == "\uE73E");
                if (check is null || (check.Opacity != 0 && check.Opacity != 1)) return false;
                if (check.Opacity == 1) checkedProjects.Add(choice.Id.NodeId);
            }
            return checkedProjects.SequenceEqual(new[] { currentProject });
        });
    }

    [TestCase(false), TestCase(true), Category("PlanSheetPhase2")]
    public async Task ShellStatusDateResolvesTheActiveViewsInputAndCreatesOneSettingsUndo(bool people)
    {
        await Open();
        if (people) { await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU1"); }
        var before = workspace.Session!.Document.State.Settings.StatusDate;
        var undo = workspace.Session.UndoCount;
        var inputId = people ? "PlanAllowanceU1" : "PlanCell1_Remaining";
        await Ui.Run(() => {
            var picker = Ui.Find<CalendarDatePicker>("PlanStatusDate");
            Assert.That(Ui.Tree(Ui.Tree(view).OfType<PlanSheetView>().Single()).Contains(picker), Is.False);
            var input = Ui.Find<TextBox>(inputId); input.Focus(FocusState.Programmatic); input.Text = "invalid";
            picker.Date = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        });
        await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.StatusDate, Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>(inputId).Text, Is.EqualTo("invalid"));
            Assert.That(Ui.Find<CalendarDatePicker>("PlanStatusDate").Date!.Value.Date,
                Is.EqualTo((before ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue)));
            Ui.Find<TextBox>(inputId).Text = people ? "80" : "8";
            Ui.Find<CalendarDatePicker>("PlanStatusDate").Focus(FocusState.Programmatic);
        });
        await Ui.Idle();
        if (people) { await Ui.Run(() => Ui.Click("PlanShowTasks")); await Ui.Idle(); }
        // The rejected picker value can be applied by the pending-input flush, so the second assignment must
        // target a date that is not already in force to be a real change, and Undo must restore whatever was
        // accepted before it rather than the session's original date.
        var accepted = workspace.Session.Document.State.Settings.StatusDate;
        var target = accepted == new DateOnly(2026, 10, 20) ? new DateOnly(2026, 10, 21) : new DateOnly(2026, 10, 20);
        undo = workspace.Session.UndoCount;
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = new DateTimeOffset(target.ToDateTime(TimeOnly.MinValue)));
        await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.StatusDate, Is.EqualTo(target));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.StatusDate, Is.EqualTo(accepted));
        await Ui.Run(() => {
            var localSheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(localSheet.Pending, Is.Empty, string.Join(";", localSheet.Pending.Select(p => $"{p.Key}: {p.Value.Text} original={p.Value.OriginalText}")));
        });
    }

    [Test, Category("WorkspaceShell")]
    public async Task RemoteWorkDisablesViewSwitchesUntilCompletion()
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdQuery = "ProjectFields"
        }));
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Run(() => {
                Assert.That(Ui.Find<SelectorBarItem>("PlanShowSummary").IsEnabled, Is.False);
                Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsEnabled, Is.False);
            });
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.True);
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowSummary").IsSelected, Is.False);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.True);
        });
    }

    [Test, Category("WorkspaceShell")]
    public async Task RejectedViewSelectionReturnsToTheActualPlanningTab()
    {
        await Open();
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanCell1_Remaining"); input.Focus(FocusState.Programmatic); input.Text = "invalid";
            Ui.Click("PlanShowSummary");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.True);
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowSummary").IsSelected, Is.False);
            var input = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(input.Text, Is.EqualTo("invalid"));
            Assert.That(input.Focus(FocusState.Programmatic), Is.True);
            input.Text = "8";
        });
    }

    [TestCase(false), TestCase(true), Category("WorkspaceShell")]
    public async Task SettingsBackRestoresTheSelectedPlanningView(bool summary)
    {
        await Open();
        if (summary) { await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<TextBlock>("SummaryStatusDate"); }
        await Settings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.False);
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowSummary").IsSelected, Is.False);
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Ready<FrameworkElement>(summary ? "SummaryStatusDate" : "PlanCell1_Title");
        await Ui.Run(() => Assert.That(Ui.Find<SelectorBarItem>(summary ? "PlanShowSummary" : "PlanShowTasks").IsSelected, Is.True));
    }

    [TestCase(false), TestCase(true), Category("WorkspaceShell")]
    public async Task WorkspaceHistoryCommitsThenReversesAndReappliesOneOperation(bool people)
    {
        await Open();
        await CommitText("PlanCell1_Title", "履歴のある設計");
        await Ui.Until(() => Ui.Find<Button>("PlanUndo").IsEnabled);
        if (people) { await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU1"); }
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>(people ? "PlanAllowanceU1" : "PlanCell1_Title");
            input.Focus(FocusState.Programmatic); input.Text = people ? "80" : "変更した設計";
            Ui.Click("PlanUndo");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("履歴のある設計"));
            if (people) Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.Null);
            Ui.Click("PlanRedo");
        });
        await Ui.Idle();
        await Ui.Ready<TextBox>(people ? "PlanAllowanceU1" : "PlanCell1_Title");
        await Ui.Run(() => {
            if (people) Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").Text, Is.EqualTo("80"));
            else Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Text, Is.EqualTo("変更した設計"));
        });
    }

    [TestCase("PlanUndo"), TestCase("PlanRedo"), Category("WorkspaceShell")]
    public async Task WorkspaceHistoryRejectsInvalidSheetInput(string command)
    {
        await Open();
        await Ui.Run(() => { var title = Ui.Find<TextBox>("PlanCell1_Title"); title.Focus(FocusState.Programmatic); title.Text = "履歴"; Ui.Click("PlanShowSettings"); });
        await Ui.Idle();
        if (command == "PlanRedo") { await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle(); }
        await Ui.Run(() => Ui.Click("PlanShowTasks")); await Ui.Idle();
        var accepted = PlanJson.Text(workspace.Session!.Document.State);
        var undo = workspace.Session.UndoCount; var redo = workspace.Session.RedoCount;
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanCell1_Remaining"); input.Focus(FocusState.Programmatic); input.Text = "invalid";
            Ui.Click(command);
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(PlanJson.Text(workspace.Session!.Document.State), Is.EqualTo(accepted));
            Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo)); Assert.That(workspace.Session.RedoCount, Is.EqualTo(redo));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("invalid"));
            Ui.Find<TextBox>("PlanCell1_Remaining").Text = "8";
        });
    }

    [Test, Category("WorkspaceShell")]
    public async Task StatusCountsIncludeRequirementsAndTasksAndWorkspaceOverridesDarkHost()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0],
            new(new("I2", "子", "acme/repo") { Parent = "I1" }, "", true),
            new(new("I3", "独立", "acme/repo"), "", true)] });
        await Ui.Run(() => { Ui.Root.RequestedTheme = ElementTheme.Dark; ResizeWorkspace(1280, 720); });
        try {
            await Open();
            await Ui.Run(() => {
                Assert.That(view.ActualTheme, Is.EqualTo(ElementTheme.Light));
                var caption = Ui.Window.AppWindow.TitleBar;
                Assert.That(caption.ButtonForegroundColor, Is.EqualTo(Microsoft.UI.Colors.Black));
                Assert.That(caption.ButtonHoverForegroundColor, Is.EqualTo(Microsoft.UI.Colors.Black));
                Assert.That(caption.ButtonInactiveForegroundColor, Is.EqualTo(Windows.UI.Color.FromArgb(102, 0, 0, 0)));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 1 · タスク 2"));
            });
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "workspace-shell-light"));
            await Ui.Run(async () => await RenderedEvidence.Capture((FrameworkElement)Ui.Find<SplitButton>("PlanRefresh").Parent, "workspace-command-row"));
        } finally { await Ui.Run(() => Ui.Root.RequestedTheme = ElementTheme.Default); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DivergentNativeChildOrderAloneIsNotReviewedOrCountedAsUnpublished(bool reversedNativeOrder)
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = state.Issues[0].Row with { Remaining = null, Estimate = null, Assignees = [] } },
            new(new("I2", "子A", "acme/repo") { Parent = "I1" }, "", true),
            new(new("I3", "子B", "acme/repo") { Parent = "I1" }, "", true)] });
        await Open();
        // GitHub's native sub-issue order differs from the Project item order the plan displays (Issue #167).
        await workspace.Session!.SaveSync(workspace.Session.Document.Sync with {
            NativeOrders = reversedNativeOrder ? ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", ["I3", "I2"]) : ImmutableDictionary<string, ImmutableArray<string>>.Empty });
        await Ui.Ready<TextBox>("PlanCell2_Title");
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell2_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "子Aの変更"; Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("子A → 子Aの変更")));
        await Ui.Run(() => {
            Assert.That(workspace.Session.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount, Is.EqualTo(1), "Only the edited task is unpublished.");
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 1 タスク"));
            var lines = Ui.Tree(Ui.Find<ListView>("PlanPublishLines")).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.That(lines.Count(t => t.Contains("子タスクの順序")), Is.Zero, "A GitHub-side child-order difference is not a plan change.");
        });
    }

    [TestCase("VerificationMismatch", "プロジェクトへの追加を確認できません")]
    [TestCase("NotDispatched", "まだGitHubへ送信されていません")]
    public async Task NewIssueReviewUsesProposedValuesAndReadableMembershipFailure(string reason, string text)
    {
        await Open();
        var parent = PlanRow.New("CSV group", "acme/repo");
        var row = PlanRow.New("CSV task", "acme/repo") with { Estimate = 16, Parent = parent.Identity };
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new InsertPlanRows([parent, row], Kind: PlanOperationKind.CsvImport), DateOnly.FromDateTime(DateTime.Today));
            await workspace.Session.SaveSync(workspace.Session.Document.Sync with { Failures = [new(row.Identity, PlanField.NewTask, reason)] });
            Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh();
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            // The sheet also contains CSV task; its text does not prove this review group is realized.
            list.ScrollIntoView(list.Items.Cast<PlanPublishReviewGroup>().Single(g => g.Identity == row.Identity), ScrollIntoViewAlignment.Leading);
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroup" + row.Identity);
        await Ui.Run(async () => {
            var lines = Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroup" + row.Identity)).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.That(string.Join(" ", lines), Does.Not.Contain("未入力 →").And.Not.Contain(reason).And.Contain(text));
            Assert.That(lines.Any(t => t.Contains("見積工数") && t.Contains("16")), Is.True);
            if (reason == "NotDispatched") Assert.That(string.Join(" ", lines), Does.Contain("未送信").And.Not.Contain("発行失敗"));
            if (reason == "NotDispatched") await RenderedEvidence.Capture(view, "publish-pending-reason");
            Ui.Click("PlanPublishClose");
        });
        await Ui.Ready<PlanSheetCell>("PlanCell3_Title");
        await Ui.Run(() => {
            var help = Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<PlanSheetCell>("PlanCell3_Title"));
            Assert.That(help, Does.Contain(text).And.Not.Contain(reason));
            if (reason == "NotDispatched") Assert.That(help, Does.Contain("未送信").And.Not.Contain("発行失敗"));
        });
    }
    private static string FakeExecutable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GHPB_UI_FAKE_GH_PATH") is { Length: > 0 } configured) return configured;
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "GhProjectsBoards.sln"))) folder = folder.Parent;
            return Path.Combine(folder?.FullName ?? throw new InvalidOperationException("Repository root required."),
                "tests", "GhProjectsBoards.Tests", "bin", "Release", "net10.0-windows", "GhProjectsBoards.Tests.exe");
        }
    }
    private DateOnly readDay;
    private string root = null!;
    private PlanWorkspace workspace = null!;
    private PlanWorkspaceView view = null!;
    private TaskCompletionSource? pickerClosed;
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        readDay = PlanClock.Today;
        root = Path.Combine(Path.GetTempPath(), "ghpb-workspace-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)], 2) { Drafts = 2, PullRequests = 1, Redacted = 1, HiddenItems = 1 });
        workspace = new(new(root), () => readDay);
        await Ui.Run(() => view = new(workspace, (_, host) => new(FakeExecutable, host,
            new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }))));
        if (TestContext.CurrentContext.Test.MethodName == nameof(TitleBarUsesGanttGlyphAndPlacesGearBesideNativeCaptions))
            await Ui.Run(() => { Ui.Window.ExtendsContentIntoTitleBar = true; Ui.Window.SetTitleBar(view.WorkspaceTitleBar); });
        await Ui.Mount(view);
        await Ui.Run(() => {
            var flyout = Ui.Find<Button>("PlanProjectPicker").Flyout;
            flyout.Closing += (_, _) => { if (flyout.IsOpen && (pickerClosed is null || pickerClosed.Task.IsCompleted)) pickerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously); };
            flyout.Closed += (_, _) => pickerClosed?.TrySetResult();
        });
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
        var stopped = false; var problem = "";
        try
        {
            await Ui.Run(async () => { stopped = await StopDetailTest(); problem = Ui.Find<TextBlock>("PlanError", view).Text; });
        }
        finally
        {
            try { await Ui.Unmount(view, check: false); await Ui.Run(() => { Ui.Window.SetTitleBar(null); Ui.Window.ExtendsContentIntoTitleBar = false; }); await Ui.Idle(); await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1400, 1000))); }
            finally
            {
                try { await workspace.Flush(); }
                catch when (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Passed)
                { await workspace.RetrySave(); }
                finally {
                    // WebView2 closes its browser asynchronously after the view unloads; native profile
                    // files can remain locked briefly even though all managed work has finished.
                    var deletion = Stopwatch.StartNew();
                    while (Directory.Exists(root)) {
                        try { Directory.Delete(root, true); break; }
                        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && deletion.Elapsed < TimeSpan.FromSeconds(15)) {
                            await Task.Delay(100);
                        }
                    }
                }
            }
        }
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed) Assert.That(stopped, Is.True, problem);
        else TestContext.Out.WriteLine("Close after failed test: " + problem);
            }
        finally { Ui.EndTest(); }
    }

    [Test]
    public async Task CsvPickerValidatesWholeFileThenImportsAndUndoesOneOperation()
    {
        await Open();
        // An earlier edit is an ordinary unpublished change; measure the CSV's additional changes from it.
        await CommitText("PlanCell1_Title", "設計の変更");
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Title == "設計の変更");
        var unpublishedBeforeCsv = workspace.Session!.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount;
        var undoBeforeCsv = workspace.Session.UndoCount;
        var path = Path.Combine(root, "tasks.csv");
        await File.WriteAllTextAsync(path, "キー,タイトル,見積,担当者,先行タスク,親\na,,8,,,\nb,確認,4,,missing,\n");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "設計");
        await Ui.Run(() => { view.PickFile = purpose => { Assert.That(purpose, Is.EqualTo("csv")); return Task.FromResult<string?>(path); }; });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.DialogReady("PlanCsvErrors");
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("PlanCsvErrors"), Does.Contain("2行: タイトル").And.Contain("3行: 参照"));
            Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
        });
        await File.WriteAllTextAsync(path, "キー,タイトル,見積,リポジトリ\na,準備,8,unknown/repo\nb,確認,4,unknown/repo\n");
        await Ui.Run(() => Ui.DialogButton("PlanCsvErrors", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("PlanCsvErrors") is not null && Ui.DialogText("PlanCsvErrors").Contains("リポジトリ"));
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("PlanCsvErrors"), Does.Contain("2行:").And.Contain("3行:"));
            Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
        });
        await File.WriteAllTextAsync(path, "キー,タイトル,見積,担当者,先行タスク,親\np,準備,,,,\na,設計の追加,8,alice,,p\nb,確認,4,alice,a,p\n");
        await Ui.Run(() => Ui.DialogButton("PlanCsvErrors", "PrimaryButton"));
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Length == 4);
        await Ui.Run(async () => await Ui.Tree(view).OfType<PlanSheetView>().Single().FlushInput());
        await Ui.Ready<PlanSheetCell>("PlanCell3_Title");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo($"未発行 {unpublishedBeforeCsv + 3} タスク"));
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell3_Title").Text, Is.EqualTo("設計の追加"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.DialogReady("PlanCsvDuplicate");
        await Ui.Run(() => Ui.DialogButton("PlanCsvDuplicate", "CloseButton"));
        await Ui.Until(() => Ui.Dialog("PlanCsvDuplicate") is null);
        await Ui.Run(async () => await RenderedEvidence.Capture(Ui.Tree(view).OfType<PlanSheetView>().Single(), "csv-imported-plan"));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Length == 1);
        await Ui.Run(() => {
            Assert.That(workspace.Session!.UndoCount, Is.EqualTo(undoBeforeCsv));
            Assert.That(workspace.Session.Document.State.Rows[0].Title, Is.EqualTo("設計の変更"));
            Assert.That(workspace.Session.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount, Is.EqualTo(unpublishedBeforeCsv));
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == $"未発行 {unpublishedBeforeCsv} タスク");
        await Ui.Run(() => { view.PickFile = _ => Task.FromResult<string?>(null); });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.Run(async () => await Ui.Tree(view).OfType<PlanSheetView>().Single().FlushInput());
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
    }

    [Test]
    public async Task ClosingDismissesCsvErrorsWithoutImporting()
    {
        await Open();
        var path = Path.Combine(root, "invalid.csv");
        File.WriteAllText(path, "キー,タイトル,見積\na,,8\n");
        await Ui.Run(() => { view.PickFile = _ => Task.FromResult<string?>(path); });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.DialogReady("PlanCsvErrors");
        Task<bool> stop = null!;
        await Ui.Run(() => { stop = view.StopAsync(); });
        var completed = await Task.WhenAny(stop, Task.Delay(3000)) == stop;
        try { Assert.That(completed, Is.True, "Close must dismiss the CSV dialog without requiring another click."); }
        finally
        {
            await Ui.Run(() => Ui.Dialog("PlanCsvErrors")?.Hide());
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(await stop, Is.True);
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("WorkspaceShell")]
    public async Task SettingsAllowanceRetainsAcceptedValueAfterSaveRetry()
    {
        await Open(); await Settings(); await Ui.Ready<TextBox>("PlanAllowanceU1");
        var undo = workspace.Session!.UndoCount;
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanAllowanceU1"); cell.Focus(FocusState.Programmatic); cell.Text = "80"; Ui.Click("PlanSettingsBack"); });
            await Ui.Ready<Button>("PlanRetrySave");
        }
        await Ui.Run(() => Ui.Click("PlanRetrySave")); await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle(); await Settings();
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").Text, Is.EqualTo("80")));
    }

    [Test]
    public async Task SettingsAllowancesStayWithTheSelectedProject()
    {
        await Open(); await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU1");
        await Ui.Run(() => { var input = Ui.Find<TextBox>("PlanAllowanceU1"); input.Focus(FocusState.Programmatic); input.Text = "80"; });
        await PickerCommand("PlanChooseProject");
        await Ui.Until(() => workspace.Session!.Document.State.Settings.People.Any(p => p.Allowance == 80));
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Session!.Document.Project.NodeId == "P2");
        await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU1");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").Text, Is.Empty);
        });
        await SelectRegistered("P1");
        await Ui.Until(() => workspace.Session!.Document.Project.NodeId == "P1");
        await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU1");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").Text, Is.EqualTo("80")));
    }

    [Test]
    public async Task SettingsAllowanceEntryKeepsTheNextCellFocusedAcrossRecalculation()
    {
        AssignPeopleThroughFixture("U2");
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = [new("U1", "alice", 100, null), new("U2", "bob", 100, null)] }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU2");
        await Ui.Run(() => {
            var first = Ui.Find<TextBox>("PlanAllowanceU1"); first.Focus(FocusState.Programmatic); first.Text = "80";
            var second = Ui.Find<TextBox>("PlanAllowanceU2"); second.Focus(FocusState.Programmatic); second.Text = "7";
        });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[0].Allowance == 80);
        await Ui.Idle();
        await Ui.Ready<TextBox>("PlanAllowanceU2");
        Assert.That(workspace.Session.Document.State.Settings.People[1].Allowance, Is.Null, "Typing in the next cell must not be committed by the previous cell's focus loss.");
        await Ui.Run(() => Assert.That(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<TextBox>("PlanAllowanceU2"))));
        await Ui.Run(() => { Assert.That(Ui.Find<TextBox>("PlanAllowanceU2").Text, Is.EqualTo("7")); Ui.Find<TextBox>("PlanAllowanceU2").Text = "75"; Ui.Click("PlanSettingsBack"); });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[1].Allowance == 75);
    }

    [Test]
    public async Task SettingsQueuedEditsAndInvalidTextSurviveOtherCellsSaving()
    {
        AssignPeopleThroughFixture("U2", "U3");
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = [new("U1", "alice", 100, null), new("U2", "bob", 100, null), new("U3", "carol", 100, null)] }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU3");
        await Ui.Run(() => {
            foreach (var (id, text) in new[] { ("U1", "80"), ("U2", "75"), ("U3", "7") }) {
                var box = Ui.Find<TextBox>("PlanAllowance" + id); box.Focus(FocusState.Programmatic); box.Text = text;
            }
        });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[0].Allowance == 80);
        await Ui.Idle();
        await Ui.Ready<TextBox>("PlanAllowanceU3");
        await Ui.Run(() => {
            Assert.That(workspace.Session.Document.State.Settings.People[1].Allowance, Is.EqualTo(75), "The queued second edit must survive the first redraw.");
            Assert.That(Ui.Find<TextBox>("PlanAllowanceU3").Text, Is.EqualTo("7"));
            Assert.That(workspace.Session.Document.State.Settings.People[2].Allowance, Is.Null);
            var first = Ui.Find<TextBox>("PlanAllowanceU1"); first.Focus(FocusState.Programmatic); first.Text = "bad";
            var second = Ui.Find<TextBox>("PlanAllowanceU2"); second.Focus(FocusState.Programmatic); second.Text = "70";
            Ui.Find<TextBox>("PlanAllowanceU3").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[1].Allowance == 70);
        await Ui.Idle();
        await Ui.Ready<TextBox>("PlanAllowanceU1");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").Text, Is.EqualTo("bad"));
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => { Ui.Find<TextBox>("PlanAllowanceU1").Text = "85"; Ui.Click("PlanSettingsBack"); });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[0].Allowance == 85);
    }

    [Test]
    public async Task SettingsAllowancePersistsAndInvalidInputBlocksNavigationUntilCorrected()
    {
        await Open(); await Ui.Run(() => Ui.Click("PlanShowSettings"));
        await Ui.Ready<TextBox>("PlanAllowanceU1");
        var undo = workspace.Session!.UndoCount;
        await Ui.Run(() => { var input = Ui.Find<TextBox>("PlanAllowanceU1"); input.Focus(FocusState.Programmatic); input.Text = "-1"; Ui.Click("PlanShowTasks"); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => { Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").IsLoaded, Is.True); Ui.Find<TextBox>("PlanAllowanceU1").Text = "80"; Ui.Click("PlanShowTasks"); });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People.Any(p => p.Identity == "U1" && p.Allowance == 80));
        await workspace.Session.FlushAsync();
        var loaded = await PlanSession.OpenAsync(new(root), workspace.Session.Document.Project, DateOnly.FromDateTime(DateTime.Today));
        Assert.That(loaded.Session!.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.Document.State.Settings.People.SingleOrDefault(p => p.Identity == "U1")?.Allowance is null);
    }

    [Test]
    public async Task ReviewShowsNonConflictingNativeOrderAndUnretrievedRelationships()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = state.Issues[0].Row with { Parent = "I99" } },
            new(new("I2", "子A", "acme/repo") { Parent = "I1", Predecessors = ["I98"] }, "", true),
            new(new("I3", "子B", "acme/repo") { Parent = "I1" }, "", true)],
            SubOrders = state.SubOrders.SetItem("I1", ["I3", "I2"]), NextId = 4 });
        await Open();
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Parent, null), new("I2", PlanField.Predecessors, ImmutableArray<string>.Empty)]), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("親タスク")));
        await Ui.Run(() => {
            var text = string.Join("\n", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text));
            // The parent's native sub-issue order differs from the Project item order, but the PMO changed no child order (Issue #167).
            Assert.That(text, Does.Not.Contain("子タスクの順序"));
            Assert.That(text, Does.Contain("親タスク  計画外Issue（未取得） → 未入力"));
            Assert.That(text, Does.Contain("先行  計画外Issue（未取得） → 未入力"));
            Assert.That(Ui.Tree(view).OfType<HyperlinkButton>().Any(b => b.NavigateUri?.AbsoluteUri == "https://github.com/acme/repo/issues/1"), Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        });
    }

    [Test]
    public async Task PublishReviewRequiresExplicitConfirmationAndUndoStaysLocal()
    {
        await Open();
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "設計の変更"; Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 設計の変更")));
        await Ui.Run(() => {
            Assert.That(string.Join(" ", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("設計 → 設計の変更"));
            Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計"));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => workspace.Session!.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount == 0);
        await Ui.Until(() => Ui.Find<Button>("PlanPublish").IsEnabled && !Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計の変更"));
        await Ui.Run(() => { Ui.Click("PlanPublishClose"); Ui.Click("PlanUndo"); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 1 タスク");
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計の変更"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 1 タスク")));
    }

    [TestCase(false), TestCase(true), Category("OperationStates")]
    public async Task RemoteWorkShowsBlockingPanelAndLocksCellsUntilCompletion(bool publish)
    {
        await Open();
        if (publish) await CommitText("PlanCell1_Title", "発行予定");
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdQuery = publish ? "mutation PlanPublish(" : "ProjectFields"
        }));
        if (publish) { await Ui.Run(() => Ui.Click("PlanPublish")); await Ui.Idle(); }
        await Ui.Run(() => Ui.Click(publish ? "PlanPublishConfirm" : "PlanRefresh"));
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Run(() => {
                var panel = Ui.Find<ContentControl>("PlanBusyPanel");
                Assert.That(panel.Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(Ui.Find<ProgressRing>("PlanBusyRing").IsActive, Is.True);
                var heading = publish ? "発行しています" : "最新の情報を取得しています";
                Assert.That(Ui.Find<TextBlock>("PlanBusyHeading").Text, Is.EqualTo(heading));
                Assert.That(AutomationProperties.GetName(panel), Does.StartWith(heading));
                Assert.That(AutomationProperties.GetLiveSetting(panel), Is.EqualTo(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite));
                var button = Ui.Find<Control>(publish ? "PlanPublish" : "PlanRefresh");
                Assert.That(button.IsEnabled, Is.False);
                Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.False);
                Assert.That(Ui.Find<Button>("PlanRedo").IsEnabled, Is.False);
                Assert.That(AutomationProperties.GetName(button), Is.EqualTo(publish ? "発行…" : "最新の情報に更新"));
                Assert.That(Ui.Find<Button>("PlanBusyCancel").Visibility, Is.EqualTo(publish ? Visibility.Collapsed : Visibility.Visible));
                // The panel names the operation and its steps; what 中止 keeps lives on the button itself.
                var texts = Ui.Tree(panel).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Text).ToArray();
                Assert.That(texts, Has.None.Contains("終わるまで計画は操作できません"));
                if (publish) Assert.That(texts, Does.Contain("閉じても、次の発行で続きから再開します。"));
                else {
                    Assert.That(texts, Has.None.Contains("中止すると"));
                    Assert.That(ToolTipService.GetToolTip(Ui.Find<Button>("PlanBusyCancel")), Is.EqualTo("中止すると、操作の前の状態に戻ります。"));
                }
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.False);
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.True);
            });
            await Ui.Run(async () => await RenderedEvidence.Capture(view, publish ? "t6-publishing" : "t6-refreshing"));
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ContentControl>("PlanBusyPanel").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(AutomationProperties.GetName(Ui.Find<Control>(publish ? "PlanPublish" : "PlanRefresh")), Is.EqualTo(publish ? "発行…" : "最新の情報に更新"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.False);
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.EqualTo(publish));
            Assert.That(Ui.Find<Button>("PlanRedo").IsEnabled, Is.False);
        });
    }

    [TestCase(false), TestCase(true), Category("OperationStates")]
    public async Task CancellingOpenKeepsPreviousProjectAndPlanWithoutError(bool previousProject)
    {
        if (previousProject) await Open();
        else {
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1);
        }
        var previous = workspace.Session;
        var selected = workspace.Selected;
        if (previousProject) await PickerCommand("PlanChooseProject");
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, holdQuery = "ProjectFields" }));
        if (previousProject) await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        try {
            await Ui.Until(() => Ui.Find<ContentControl>("PlanBusyPanel").Visibility == Visibility.Visible);
            await Ui.Until(() => new[] { "PlanBusyPanel", "PlanBusyRing", "PlanBusyHeading", "PlanBusyCancel" }
                .All(id => Ui.Tree(view).OfType<FrameworkElement>().Any(element => AutomationProperties.GetAutomationId(element) == id &&
                    element.IsLoaded && element.ActualWidth > 0 && element.ActualHeight > 0)));
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "opening-before-cancel-" + previousProject));
            await Ui.Run(() => {
                var expected = previousProject ? "運用計画" : "開発計画";
                Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo(expected));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo(expected + " を読み込み中"));
                Assert.That(AutomationProperties.GetName(Ui.Find<ContentControl>("PlanBusyPanel")), Does.StartWith(expected + " を開いています"));
                if (previousProject) Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.False);
                var panel = Ui.Find<ContentControl>("PlanBusyPanel");
                var ring = Ui.Find<ProgressRing>("PlanBusyRing");
                var heading = Ui.Find<TextBlock>("PlanBusyHeading");
                var cancel = Ui.Find<Button>("PlanBusyCancel");
                Assert.That(ring.ActualWidth, Is.EqualTo(40));
                Assert.That(ring.ActualHeight, Is.EqualTo(40));
                Assert.That(heading.FontSize, Is.EqualTo(20));
                Assert.That(heading.FontWeight, Is.EqualTo(Microsoft.UI.Text.FontWeights.SemiBold));
                Assert.That(heading.TextAlignment, Is.EqualTo(TextAlignment.Center));
                var steps = Ui.Find<StackPanel>("PlanBusySteps");
                Assert.That(steps.ActualWidth, Is.LessThan(panel.ActualWidth - 48), "The step list is a centred block, not a full-width list.");
                foreach (var element in new FrameworkElement[] { ring, heading, steps, cancel }) {
                    // TextBlock.ActualWidth measures text, while ActualSize measures its arranged box.
                    var bounds = element.TransformToVisual(panel).TransformBounds(new Rect(0, 0, element.ActualSize.X, element.ActualSize.Y));
                    Assert.That(bounds.Left + bounds.Width / 2, Is.EqualTo(panel.ActualWidth / 2).Within(2),
                        $"{AutomationProperties.GetAutomationId(element)} bounds={bounds}; panel={panel.ActualWidth}x{panel.ActualHeight}");
                }
                Ui.Click("PlanBusyCancel");
            });
            await Ui.Until(() => Ui.Find<ContentControl>("PlanBusyPanel").Visibility == Visibility.Collapsed);
            Assert.That(workspace.Session, Is.SameAs(previous));
            Assert.That(workspace.Selected, Is.EqualTo(selected));
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
                Assert.That(Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen, Is.False);
                Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo(selected?.Title ?? "GitHub Projects"));
                if (!previousProject) Assert.That(Ui.Find<ListView>("AvailableProjects").ActualHeight, Is.GreaterThan(0));
            });
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
    }

    [TestCase(false), TestCase(true), Category("OperationStates")]
    public async Task CancellingUrlResolutionRestoresChooserAndBlocksAnotherOpen(bool previousProject)
    {
        if (previousProject) { await Open(); await PickerCommand("PlanChooseProject"); }
        else {
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1);
        }
        var previous = workspace.Session;
        var selected = workspace.Selected;
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, holdQuery = "RegistrationResolve" }));
        await Ui.Run(() => Ui.Tree(view).OfType<Expander>().Single(e => Equals(e.Header, "URLで開く")).IsExpanded = true);
        await Ui.Ready<Button>("PlanOpenUrl");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanProjectUrl").Text = "https://github.com/users/fixture-user/projects/3";
            Ui.Click("PlanOpenUrl");
        });
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")) && Ui.Find<ContentControl>("PlanBusyPanel").Visibility == Visibility.Visible);
            await Ui.Ready<ProgressRing>("PlanBusyRing");
            await Ui.Ready<Button>("PlanBusyCancel");
            await Ui.Run(() => {
                var panel = Ui.Find<ContentControl>("PlanBusyPanel");
                Assert.That(AutomationProperties.GetName(panel), Does.Contain("プロジェクトを確認"));
                Assert.That(Ui.Find<ProgressRing>("PlanBusyRing").IsActive, Is.True);
                Assert.That(Ui.Find<Button>("PlanBusyCancel").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(Ui.Find<Button>("PlanBusyCancel").IsEnabled, Is.True);
                Assert.That(Ui.Find<Button>("PlanOpenUrl").IsEnabled, Is.False);
                Assert.That(Ui.Find<TextBox>("PlanProjectUrl").IsEnabled, Is.False);
                Assert.That(Ui.Find<ListView>("AvailableProjects").IsEnabled, Is.False);
                Assert.That(Ui.Find<TextBox>("PlanProjectUrl").Focus(FocusState.Programmatic), Is.False);
                Ui.Click("PlanBusyCancel");
            });
            await Ui.Until(() => Ui.Find<ContentControl>("PlanBusyPanel").Visibility == Visibility.Collapsed);
            await Ui.Run(() => {
                Assert.That(workspace.Session, Is.SameAs(previous));
                Assert.That(workspace.Selected, Is.EqualTo(selected));
                Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
                Assert.That(Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen, Is.False);
                Assert.That(Ui.Find<Button>("PlanOpenUrl").IsEnabled, Is.True);
                Assert.That(Ui.Find<TextBox>("PlanProjectUrl").Focus(FocusState.Programmatic), Is.True);
            });
        }
        finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
    }

    [TestCase(false), TestCase(true), Category("OperationStates")]
    public async Task CalculationKeepsCancelVisibleButDisabled(bool refresh)
    {
        if (refresh) await Open();
        else {
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1);
        }
        var observed = false;
        var enabled = true;
        var visibility = Visibility.Collapsed;
        long callback = 0;
        await Ui.Run(() => {
            var panel = Ui.Find<ContentControl>("PlanBusyPanel");
            callback = panel.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, (_, _) => {
                if (!AutomationProperties.GetName(panel).Contains("日程を計算")) return;
                observed = true;
                var cancel = Ui.Find<Button>("PlanBusyCancel");
                enabled = cancel.IsEnabled;
                visibility = cancel.Visibility;
            });
            if (refresh) Ui.Click("PlanRefresh");
            else Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
        });
        try {
            await Ui.Until(() => observed);
            await Ui.Until(() => Ui.Find<ContentControl>("PlanBusyPanel").Visibility == Visibility.Collapsed);
            Assert.That(enabled, Is.False);
            Assert.That(visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(workspace.Session, Is.Not.Null);
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty));
        }
        finally {
            await Ui.Run(() => Ui.Find<ContentControl>("PlanBusyPanel").UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, callback));
        }
    }

    [Test, Category("OperationStates")]
    public async Task CachedProjectOpenDoesNotFlashTheDelayedPanel()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        var shown = false; long callback = 0;
        await Ui.Run(() => {
            var panel = Ui.Find<ContentControl>("PlanBusyPanel");
            callback = panel.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => shown |= panel.Visibility == Visibility.Visible);
            Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
        });
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").SelectedItem is null);
        await Task.Delay(350);
        await Ui.Run(() => {
            Ui.Find<ContentControl>("PlanBusyPanel").UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, callback);
            Assert.That(shown, Is.False);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.True);
        });
    }

    [Test, Category("OperationStates")]
    public async Task CancellingRefreshRetainsLocalPlanAndDoesNotShowFailure()
    {
        await Open();
        await CommitText("PlanCell1_Title", "ローカルの計画");
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Title == "ローカルの計画");
        await Ui.Idle();
        var session = workspace.Session!;
        var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount;
        var redo = session.RedoCount;
        var path = new PlanStore(root).FileFor(session.Document.Project);
        var savedBefore = File.ReadAllBytes(path);
        Assert.That(PlanJson.Read<PlanCheckpoint>(savedBefore).Document.State.Rows[0].Title, Is.EqualTo("ローカルの計画"));
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, holdQuery = "ProjectFields" }));
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Ready<Button>("PlanBusyCancel");
            await Ui.Run(() => Ui.Click("PlanBusyCancel"));
            await Ui.Until(() => Ui.Find<ContentControl>("PlanBusyPanel").Visibility == Visibility.Collapsed);
            await Ui.Idle();
            Assert.That(PlanJson.Text(workspace.Session!.Document), Is.EqualTo(before));
            Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(workspace.Session.RedoCount, Is.EqualTo(redo));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(savedBefore));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await Ui.Run(() => {
                Assert.That(Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen, Is.False);
                Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.False);
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Text, Is.EqualTo("ローカルの計画"));
            });
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
    }

    [Test, Category("OperationStates")]
    public async Task LocalUndoDoesNotShowRemoteProgress()
    {
        await Open();
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "変更"; Ui.Click("PlanPublish"); });
        await Ui.Idle();
        var shown = false;
        long callback = 0;
        await Ui.Run(() => {
            var progress = Ui.Find<ContentControl>("PlanBusyPanel");
            callback = progress.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => shown |= progress.Visibility == Visibility.Visible);
            Ui.Click("PlanUndo");
        });
        await Ui.Idle();
        await Ui.Run(() => Ui.Find<ContentControl>("PlanBusyPanel").UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, callback));
        Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.EqualTo("設計"));
        Assert.That(shown, Is.False);
    }

    [Test, Category("OperationStates")]
    public async Task SheetSaveFailureAppearsAboveWorkspaceAndKeepsOneRetry()
    {
        await Open();
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Ui.Run(() => {
                var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "保存待ち";
                Ui.Find<TextBox>("PlanCell1_Estimate").Focus(FocusState.Programmatic);
            });
            await Ui.Ready<Button>("PlanSheetRetrySave");
            await Ui.Run(() => {
                AssertFailure("PlanSheetSaveFailure", "保存できませんでした", "保存を再試行");
                Assert.That(Ui.Tree(view).OfType<InfoBar>().Count(b => b.IsOpen && b.Title == "保存できませんでした"), Is.EqualTo(1));
            });
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "t6-sheet-save-failure"));
        }
        await Ui.Run(() => Ui.Click("PlanSheetRetrySave")); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanSheetSaveFailure").IsOpen, Is.False));
    }

    private void AssertFailure(string id, string title, string retry)
    {
        var bar = Ui.Find<InfoBar>(id);
        Assert.That(bar.IsOpen, Is.True);
        Assert.That(bar.IsClosable, Is.False);
        Assert.That(bar.Severity, Is.EqualTo(InfoBarSeverity.Error));
        Assert.That(bar.Title, Is.EqualTo(title));
        Assert.That(bar.Message, Is.Not.Empty);
        Assert.That(((Button)bar.ActionButton).Content, Is.EqualTo(retry));
        var card = Ui.Find<Border>("PlanWorkCard");
        var bounds = bar.TransformToVisual(card).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
        Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
        Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(card.ActualHeight));
        var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
        if (sheet.Visibility == Visibility.Visible && sheet.ActualHeight > 0)
            Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(sheet.TransformToVisual(card).TransformPoint(new Point()).Y + 1));
        Assert.That(Ui.Find<TextBlock>("PlanError").Visibility, Is.EqualTo(Visibility.Collapsed));
        Assert.That(Ui.Find<ContentControl>("PlanBusyPanel").Visibility, Is.EqualTo(Visibility.Collapsed));
        Assert.That(AutomationProperties.GetName(Ui.Find<SplitButton>("PlanRefresh")), Is.EqualTo("最新の情報に更新"));
        Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanPublish")), Is.EqualTo("発行…"));
    }

    [Test]
    public async Task FailedSaveWhileClosingPublicationKeepsRetryAndEditingReachable()
    {
        await Open();
        await CommitText("PlanCell1_Title", "発行予定");
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, holdQuery = "mutation PlanPublish(" }));
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 発行予定")));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
        bool stopped = true;
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(async () => stopped = await view.StopAsync());
            Assert.That(stopped, Is.False);
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => {
                Assert.That(Ui.Find<Button>("PlanRetrySave").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(Ui.Find<SplitButton>("PlanRefresh").IsEnabled, Is.True);
                Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanPublish")), Is.EqualTo("発行…"));
                Assert.That(Ui.Find<ContentControl>("PlanBusyPanel").Visibility, Is.EqualTo(Visibility.Collapsed));
            });
        }
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanSaveFailure").IsOpen);
    }

    [Test]
    public async Task PublishSaveFailureOffersRetryBeforeAnyWrite()
    {
        await Open();
        await CommitText("PlanCell1_Title", "発行予定");
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 発行予定")));
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
            await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => AssertFailure("PlanSaveFailure", "保存できませんでした", "保存を再試行"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanSaveFailure").IsOpen && Ui.Find<TextBlock>("PlanError").Text.Length == 0);
    }

    [Test]
    public async Task PublishShowsHierarchyPredecessorAndOrderStagesAndRemoteResults()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = state.Issues.Add(new(new("I2", "前提作業", "acme/repo"), "", true)), NextId = 3 });
        await Open();
        var newRow = PlanRow.New("子タスク", "acme/repo") with { Parent = "I1", Predecessors = ["I2"] };
        await workspace.Session!.Execute(new InsertPlanRows([newRow], Kind: PlanOperationKind.CsvImport), DateOnly.FromDateTime(DateTime.Today));
        await workspace.Session.Execute(new MovePlanRows(["I2"], "I1"), DateOnly.FromDateTime(DateTime.Today));
        var stages = new List<string>(); long callback = 0;
        await Ui.Run(() => {
            var progress = Ui.Find<ContentControl>("PlanBusyPanel");
            callback = progress.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, (_, _) => stages.Add(AutomationProperties.GetName(progress)));
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("新規 Issue")));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "publish-review"));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" && Ui.Find<Button>("PlanPublish").IsEnabled && !Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Find<ContentControl>("PlanBusyPanel").UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, callback));
        var remote = FakePlanEditor.Load(root);
        Assert.That(remote.Issues.Single(i => i.Row.Title == "子タスク").Row.Parent, Is.EqualTo("I1"));
        Assert.That(remote.Issues.Single(i => i.Row.Title == "子タスク").Row.Predecessors, Is.EqualTo(new[] { "I2" }));
        Assert.That(remote.Issues[0].Row.Identity, Is.EqualTo("I2"));
        foreach (var stage in new[] { "親子関係", "先行タスク", "表示順" })
            Assert.That(stages.Any(s => s.Contains(stage)), Is.True, stage);
    }

    [TestCase("partial")]
    [TestCase("verificationfailure")]
    public async Task PublishFailureOrUnverifiedRowsRemainVisibleAndRetryReconciles(string fault)
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, planFault = fault }));
        // The synthetic fault rejects the second write of the first batch. Dates are plan values now (#236), so a
        // title alone is a single write; the remaining-work edit gives the row its second write in that batch.
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "発行予定";
            var remaining = Ui.Find<TextBox>("PlanCell1_Remaining"); remaining.Focus(FocusState.Programmatic); remaining.Text = "4";
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 発行予定")));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanPublishFailure").IsOpen && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Ready<Button>("PlanRetryPublish");
        await Ui.Run(() => {
            AssertFailure("PlanPublishFailure", "発行できませんでした", "再試行");
            Assert.That(string.Join(" ", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text)), Does.Contain(fault == "partial" ? "発行失敗" : "GitHub で未確認"));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => {
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Title")), Does.Contain(fault == "partial" ? "Synthetic failure" : "GitHub で未確認"));
        });
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        var writesBeforeRetry = FakePlanEditor.Load(root).MutationBatches;
        await Ui.Run(() => Ui.Click("PlanRetryPublish"));
        await Ui.Idle();
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(writesBeforeRetry));
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" && Ui.Find<Button>("PlanPublish").IsEnabled && !Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        Assert.That(workspace.Session!.Document.Sync.Failures, Is.Empty);
        Assert.That(workspace.Session.Document.Sync.Unverified, Is.Empty);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("発行予定"));
    }

    [Test]
    public async Task IncompleteRefreshKeepsLocalValuesAndCanBeRetried()
    {
        await Open();
        var before = workspace.Session!.Document;
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"planFault\":\"readfailure\"}");
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen);
        await Ui.Ready<Button>("PlanRetryRefresh");
        await Ui.Run(() => AssertFailure("PlanRefreshFailure", "最新の情報に更新できませんでした", "再試行"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "t6-refresh-failure"));
        Assert.That(workspace.Session.Document, Is.EqualTo(before));
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "更新済み" } }] });
        await Ui.Run(() => Ui.Click("PlanRetryRefresh"));
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").Text == "更新済み");
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen, Is.False));
    }

    [Test]
    public async Task NewIssuePublicationReplacesReviewWithVisibleStages()
    {
        await Open();
        var stages = new List<string>();
        var busySeen = false; var publishDisabled = false; var prematureVerification = false;
        long nameCallback = 0, visibilityCallback = 0, enabledCallback = 0;
        Button? publish = null;
        await Ui.Run(() => {
            var progress = Ui.Find<ContentControl>("PlanBusyPanel");
            // The panel is shown, renamed and hidden inside one publish, so polling its visibility races the
            // publication and can miss it entirely. Its stages, its visibility and the publish command's
            // availability are recorded from property changes, which observe the same facts.
            nameCallback = progress.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, (_, _) => {
                var name = AutomationProperties.GetName(progress); stages.Add(name);
                // The verification tick must not appear before its own stage is announced. The stage
                // vocabulary an ordinary publish announces is 最新情報の確認 / 新規 Issue / 検証: the
                // fields-and-assignees and later phases report only when they have a write, so keying this
                // on a fields stage would make the check vacuous.
                if (!name.Contains("検証") && Ui.Tree(progress).OfType<TextBlock>().Any(t => t.Text == "✓ 検証"))
                    prematureVerification = true;
            });
            visibilityCallback = progress.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => {
                if (progress.Visibility == Visibility.Visible) busySeen = true;
            });
            publish = Ui.Find<Button>("PlanPublish");
            enabledCallback = publish.RegisterPropertyChangedCallback(Control.IsEnabledProperty, (_, _) => {
                if (!publish.IsEnabled) publishDisabled = true;
            });
            // Only the title: PlanCell0 is the entry row, so a second value typed there would insert a second,
            // untitled row, which the publish holds back (#247) and leaves unpublished.
            var input = Ui.Find<TextBox>("PlanCell0_Title"); input.Focus(FocusState.Programmatic); input.Text = "新しい計画";
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("新規 Issue")),
            () => "review line 新規 Issue; stages=[" + string.Join(" | ", stages) + "]");
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(1));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => busySeen, () => "busy panel visible; stages=[" + string.Join(" | ", stages) + "]");
        Assert.That(publishDisabled, Is.True, "The publish command is disabled while the publication runs.");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" && Ui.Find<Button>("PlanPublish").IsEnabled && !Ui.Find<Button>("PlanPublishConfirm").IsEnabled,
            () => $"publication settled; unpublished=[{Ui.Find<TextBlock>("PlanUnpublished").Text}], publishEnabled={Ui.Find<Button>("PlanPublish").IsEnabled}, confirmEnabled={Ui.Find<Button>("PlanPublishConfirm").IsEnabled}, stages=[" + string.Join(" | ", stages) + "]");
        await Ui.Run(() => {
            var progress = Ui.Find<ContentControl>("PlanBusyPanel");
            progress.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, nameCallback);
            progress.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, visibilityCallback);
            publish!.UnregisterPropertyChangedCallback(Control.IsEnabledProperty, enabledCallback);
        });
        TestContext.Out.WriteLine("Announced stages: [" + string.Join(" | ", stages) + "]");
        Assert.That(prematureVerification, Is.False);
        Assert.That(FakePlanEditor.Load(root).Issues.Count(i => i.Row.Title == "新しい計画" && i.Added), Is.EqualTo(1));
        // The delivered publish of this plan announces 最新情報の確認, 新規 Issue and 検証; the
        // fields-and-assignees phase reports only when it has a write and this plan has none for it.
        foreach (var stage in new[] { "最新情報の確認", "新規 Issue", "検証" })
            Assert.That(stages.Any(s => s.Contains(stage)), Is.True, stage + " in [" + string.Join(" | ", stages) + "]");
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "publish-result"));
    }

    [Test]
    public async Task PublicationOfOneChangedRowLeavesNothingUnpublished()
    {
        // Experiment that separates two readings of the new-Issue case's unfinished publication: with no
        // drafts, pull requests, redacted or hidden items in the fixture, publishing one changed title must
        // leave 0 unpublished tasks. Green means that case's [1] is its fixture's excluded items being
        // counted; red means the publish's completion leaves a row behind, which would be a defect.
        FakePlanEditor.Save(root, new([
            new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)], 2));
        await Open();
        // Edit the baseline Issue through the cell the app commits, not by index: row 0 is a locally
        // inserted row in this fixture and a session command on I1 left no unpublished change, so the cell
        // carrying the baseline title is located by its text and committed the way that other case commits.
        var titleCell = "";
        await Ui.Run(() => {
            var cells = Ui.Tree(view).OfType<TextBox>()
                .Select(t => (Id: AutomationProperties.GetAutomationId(t), Text: t.Text))
                .Where(p => p.Id is not null && p.Id.StartsWith("PlanCell", StringComparison.Ordinal) && p.Id.EndsWith("_Title", StringComparison.Ordinal))
                .ToArray();
            TestContext.Out.WriteLine("Title cells: " + string.Join(" | ", cells.Select(c => c.Id + "=" + c.Text)));
            titleCell = cells.Single(c => c.Text == "設計").Id;
        });
        await CommitText(titleCell, "変更した計画");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text != "未発行 0 タスク",
            () => "the title edit must be an unpublished change; unpublished=[" + Ui.Find<TextBlock>("PlanUnpublished").Text + "]");
        // Decide defect or fixture before the publish: a row that carries a baseline Issue must be updated,
        // not created. The identity assertion is what makes that unambiguous.
        await Ui.Run(() => {
            var session = workspace.Session!;
            var baseline = session.Document.Baseline.Rows.Select(r => r.Identity).ToArray();
            var state = session.Document.State.Rows.Select(r => r.Identity).ToArray();
            TestContext.Out.WriteLine($"Before publish: state=[{string.Join(",", state)}], baseline=[{string.Join(",", baseline)}], planTasks=[{string.Join(",", Ui.Find<ListView>("PlanTasks").Items.Cast<string>())}]");
            var edited = session.Document.State.Rows.FirstOrDefault(r => r.Title == "変更した計画");
            Assert.That(edited?.Identity, Is.EqualTo("I1"),
                "Premise: the experiment must edit the baseline Issue, not a locally inserted row.");
        });
        var calls = Path.Combine(root, "plan-mutations.jsonl");
        var before = File.Exists(calls) ? File.ReadAllLines(calls).Length : 0;
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク",
            () => $"publication settled; unpublished=[{Ui.Find<TextBlock>("PlanUnpublished").Text}], publishEnabled={Ui.Find<Button>("PlanPublish").IsEnabled}, confirmEnabled={Ui.Find<Button>("PlanPublishConfirm").IsEnabled}");
        var writes = File.Exists(calls) ? File.ReadAllLines(calls).Skip(before).ToArray() : [];
        TestContext.Out.WriteLine($"Publish calls ({writes.Length}): " + string.Join(" ;; ",
            writes.Select(w => w.Length > 200 ? w[..200] : w)));
        Assert.That(writes.Any(w => w.Contains("updateIssue")), Is.True, "The publish must send the one change it was given.");
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("変更した計画"));
    }

    [TestCase(false), TestCase(true)]
    public async Task ParentConflictResolutionKeepsReviewAndPublishAvailabilityConsistent(bool initiallySummary)
    {
        var initial = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, initial with { Issues = [initial.Issues[0],
            new(new("I2", "Child", "acme/repo") { Parent = "I4" }, "", true),
            new(new("I3", "Other", "acme/repo"), "", true),
            new(new("I4", "Previous parent", "acme/repo"), "", true)], NextId = 5 });
        await Open();
        var session = workspace.Session!;
        var today = DateOnly.FromDateTime(DateTime.Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Assignees, new[] { "U2" }),
            new("I2", PlanField.Parent, initiallySummary ? "I1" : null)]), today);
        var rows = session.Document.Baseline.Rows.Select(r => r.Identity == "I1" ? r with { Assignees = ["U3"] }
            : r.Identity == "I2" ? r with { Parent = initiallySummary ? null : "I1" } : r).ToImmutableArray();
        await session.AcceptRefresh(new(session.Document.Baseline with { Rows = rows }, ImmutableDictionary<string, string>.Empty, [], 0, 0), today);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanResolveI2_Parent_True"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanPublishBlockedReason").Text, Does.Contain("競合"));
            Ui.Click("PlanResolveI2_Parent_True");
        });
        await Ui.Until(() => session.Document.Sync.Conflicts.Length == 1
            && !Ui.Find<Button>("PlanPublishConfirm").IsEnabled
            && Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanResolveI1_Assignees_True") == initiallySummary);
        await Ui.Run(() => {
            Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.Assignees));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False);
            if (!initiallySummary) Assert.That(session.Changes(today).TaskCount, Is.Zero);
            var reason = Ui.Find<TextBlock>("PlanPublishBlockedReason");
            // With nothing left to write, the empty review's own line is the visible reason.
            if (initiallySummary) Assert.That(reason.Text, Does.Contain("競合"));
            else Assert.That(Ui.Tree(Ui.Find<ListView>("PlanPublishLines")).OfType<TextBlock>().Any(t => t.Text == "未発行の変更はありません"), Is.True);
            Assert.That(reason.Visibility, Is.EqualTo(initiallySummary ? Visibility.Visible : Visibility.Collapsed));
            Assert.That(Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanResolveI1_Assignees_True"), Is.EqualTo(initiallySummary));
        });
        await Ui.Run(async () => {
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I3", PlanField.Title, "Independent edit")]), today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("PlanPublishLines")).OfType<TextBlock>().Any(text => text.Text.Contains("Other → Independent edit")));
        await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.EqualTo(!initiallySummary)));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RefreshMarksConflictsAndReviewResolvesWithoutWriting(bool useGitHub)
    {
        await Open();
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "ローカル"; Ui.Click("PlanShowSettings"); });
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Title == "ローカル");
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "GitHub変更", Actual = 1 } }] });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.Length == 1);
        await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowTasks").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanShowTasks"));
        await Ui.Until(() => AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Title")).Contains("競合"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.True));
        // Native GotFocus selects the cell asynchronously; Focus returning does not mean the reason is ready.
        await Ui.Until(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title");
            var selection = (ISelectionItemProvider)FrameworkElementAutomationPeer.CreatePeerForElement(cell).GetPattern(PatternInterface.SelectionItem);
            return cell.FocusState != FocusState.Unfocused && selection.IsSelected
                && Ui.Find<TextBlock>("PlanStartReason").Text.Contains("競合: GitHub では GitHub変更");
        });
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title");
            var frame = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(cell));
            var critical = ((SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"]).Color;
            Assert.That(((SolidColorBrush)frame.BorderBrush).Color, Is.EqualTo(critical));
            Assert.That(frame.BorderThickness.Left, Is.InRange(1d, 2d));
            Assert.That(((SolidColorBrush)frame.Background).Color, Is.Not.EqualTo(((SolidColorBrush)Application.Current.Resources["SheetChangedBrush"]).Color));
            var corner = Ui.Tree(frame).OfType<Polygon>().Single();
            Assert.That(corner.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(((SolidColorBrush)corner.Fill).Color, Is.EqualTo(critical));
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain("競合: GitHub では GitHub変更"));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "t6-conflict-cell-" + useGitHub));
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows[0].Actual, Is.EqualTo(1));
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<Button>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == $"PlanResolveI1_Title_{useGitHub}"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "publish-conflict-" + useGitHub));
        await Ui.Run(() => { Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False); Ui.Click($"PlanResolveI1_Title_{useGitHub}"); });
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.IsEmpty);
        Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.EqualTo(useGitHub ? "GitHub変更" : "ローカル"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PendingSettingsSaveDoesNotDropTheNextCommand(bool switchProject)
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2");
        await Ui.Idle();
        await Settings();
        var previous = workspace.Session!;
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanAllowanceU1");
            input.Focus(FocusState.Programmatic);
            input.Text = "80";
            RoutedEventHandler? nextCommand = null;
            nextCommand = async (_, _) => {
                input.LostFocus -= nextCommand;
                if (switchProject) {
                    Ui.Click("PlanProjectPicker");
                    await Ui.Ready<ListView>("RegisteredProjects");
                    Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == "P1");
                }
                else Ui.Click("PlanShowTasks");
            };
            input.LostFocus += nextCommand;
            Ui.Find<SelectorBarItem>("PlanShowTasks").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => previous.Document.State.Settings.People.Any(p => p.Identity == "U1" && p.Allowance == 80));
        await Ui.Until(() => view.IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(previous.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
            if (switchProject)
            {
                Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
                Assert.That(workspace.Selected.Id.NodeId, Is.EqualTo("P1"));
            }
            Assert.That(Ui.Find<ScrollViewer>("PlanSettingsScroll").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }
    [Test]
    public async Task RefreshWhileOnSettingsShowsNewPeopleAndColumnChoices()
    {
        await Open(); await Settings();
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with {
            Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Assignees = ["U2"] } }],
            ExtraFields = [new("X-points", "Story points", "NUMBER")] });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.People.Any(p => p.Identity == "U2"));
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<NumberBox>().Any(n => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(n) == "PlanRateU2"), Is.True);
            Assert.That(Ui.Find<ComboBox>("PlanMapEstimate").Items.Cast<PlanFieldChoice>().Any(c => c.Label == "Story points"), Is.True);
            Assert.That(Ui.Find<ScrollViewer>("PlanSettingsScroll").Visibility, Is.EqualTo(Visibility.Visible));
        });
    }
    [Test]
    public async Task ConnectListsOwnersAndOneClickOpensMappedTasks()
    {
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        string problem = "";
        await Ui.Run(() => problem = Ui.Find<TextBlock>("PlanError").Text);
        Assert.That(problem, Is.Empty);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanProjectOwners").Items.Cast<OwnerChoice>().Select(o => o.Login), Is.EqualTo(new[] { "fixture-user", "acme" }));
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Count, Is.EqualTo(1));
            Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
        });
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Until(() => Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo("開発計画"));
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>().Count(id => id.Length > 0), Is.EqualTo(1));
            Assert.That(workspace.Session!.Document.State.Settings.Columns.Length, Is.EqualTo(5));
            Assert.That(string.Join(" ", Ui.Tree(view).Select(t => t is TextBlock label ? label.Text : t is TextBox input ? input.Text : "")), Does.Contain("開始日").And.Contain("設計"));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "workspace"));
        await Settings();
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "settings"));
        await Ui.Run(() => { var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll"); scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSettingsScroll").VerticalOffset > 0);
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "settings-calendar-people"));
    }
    private void ResizeWorkspace(int width, int height)
    {
        var scale = Ui.Root.XamlRoot.RasterizationScale;
        Ui.Window.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)));
        view.Width = width; view.Height = height;
    }
    private async Task OpenProjectPicker()
    {
        await Ui.Idle();
        if (pickerClosed is { } closingPicker) await closingPicker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Ui.Run(() => Ui.Click("PlanProjectPicker"));
        try { await Ui.Ready<ListView>("RegisteredProjects"); }
        catch {
            await Ui.Diagnose("project picker", view);
            await Ui.Run(async () => { TestContext.Out.WriteLine("Picker error: " + Ui.Find<TextBlock>("PlanError").Text); await RenderedEvidence.Capture(view, "picker-failure"); });
            throw;
        }
    }
    private async Task PickerCommand(string id)
    {
        await OpenProjectPicker();
        await Ui.Run(() => Ui.Click(id));
        await Ui.Idle();
    }
    private async Task SelectRegistered(string project)
    {
        await OpenProjectPicker();
        await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == project));
    }
    private async Task Open(string firstTitle = "設計")
    {
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
            Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
        });
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Until(() => Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == firstTitle));
        await Ui.Idle();
    }
    private async Task Settings()
    {
        await Ui.Run(() => Ui.Click("PlanShowSettings"));
        await Ui.Ready<ComboBox>("PlanMapEstimate");
        await Ui.Idle();
    }
    // #253: the settings page lists only the people a row holds, so a case that seeds stored people for the
    // rate/allowance editors first assigns them through real Project Issues instead of relying on a stored list.
    private void AssignPeopleThroughFixture(params string[] identities)
    {
        var state = FakePlanEditor.Load(root);
        var issues = state.Issues;
        var next = state.NextId;
        foreach (var identity in identities)
            issues = issues.Add(new(new("I" + next++, "担当 " + identity, "acme/repo") { Assignees = [identity] }, "", true));
        FakePlanEditor.Save(root, state with { Issues = issues, NextId = next });
    }
    private async Task CommitText(string id, string value)
    {
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>(id);
            Assert.That(input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic), Is.True);
            input.Text = value;
        });
        await Ui.Run(() => Ui.Find<Button>("PlanShowSettings").Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
        await Ui.Idle();
    }
    // 期間 uses date pickers like the 基準日 (#251), so its cases set the picked date instead of typing ISO text.
    private async Task PickDate(string id, DateOnly? day)
    {
        await Ui.Run(() => Ui.Find<CalendarDatePicker>(id).Date = day is { } date
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)) : null);
        await Ui.Idle();
    }
    [Test]
    public async Task SwitchingPreservesEachProjectsUnpublishedWorkAndRestartsTheSelectedProject()
    {
        await Open();
        // Edit through the sheet: its cached unpublished count is what the badge shows after switching back.
        await CommitText("PlanCell1_Title", "未発行の設計");
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Single().Title == "未発行の設計");
        await PickerCommand("PlanChooseProject");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2");
        await Ui.Idle();
        await SelectRegistered("P1");
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1");
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 1 タスク"));
        });
        var restarted = new PlanWorkspace(new(root), () => DateOnly.FromDateTime(DateTime.Today));
        await restarted.Connect(new(FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
        Assert.That(restarted.Selected!.Id.NodeId, Is.EqualTo("P1"));
        Assert.That(restarted.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
    }
    [TestCase("rate", "2026-10-06")]
    [TestCase("company", "2026-10-06")]
    public async Task SettingChangesRecalculateAndOneUndoRestoresThePreviousDates(string setting, string expected)
    {
        // A settings change moves only a task that already has dates (#236 rule 5).
        var seeded = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, seeded with { Issues = seeded.Issues.Select(i => i with { Row = i.Row with { Start = new(2026, 10, 5), End = new(2026, 10, 5) } }).ToImmutableArray() });
        await Open();
        // A 基準日 change alone does not reschedule (#236 part B): only a calendar, Project-period or rate
        // change can move dates, so the setup that establishes the previous dates sets the Project period.
        await Ui.Run(async () => await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            StatusDate = new(2026, 10, 5), ProjectStart = new(2026, 10, 5) }), new(2026, 10, 5)));
        var history = workspace.Session!.UndoCount;
        await Settings();
        if (setting == "rate") await Ui.Run(() => Ui.Find<NumberBox>("PlanRateU1").Value = 50);
        else await AddDay("PlanCompanyDaysOff", new(2026, 10, 5));
        await Ui.Until(() => workspace.Session.UndoCount == history + 1);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session.Schedule(new(2026, 10, 5)).Single().End.Value!.Value.ToString("yyyy-MM-dd"), Is.EqualTo(expected));
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("PlanTasks")).OfType<TextBox>().Any(t => t.Text == PlanSheetView.DateText(DateOnly.Parse(expected))));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.UndoCount == history);
        Assert.That(workspace.Session.Schedule(new(2026, 10, 5)).Single().End.Value, Is.EqualTo(new DateOnly(2026, 10, 5)));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
    [Test]
    public async Task HolidayImportRecalculatesAndSettingsFileRoundTripsThroughRealControls()
    {
        // A settings change moves only a task that already has dates (#236 rule 5).
        var seeded = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, seeded with { Issues = seeded.Issues.Select(i => i with { Row = i.Row with { Start = new(2026, 10, 5), End = new(2026, 10, 5) } }).ToImmutableArray() });
        await Open();
        await Ui.Run(async () => await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = new(2026, 10, 5) }), new(2026, 10, 5)));
        var holidayPath = Path.Combine(root, "holidays.csv");
        var exported = Path.Combine(root, "settings.json");
        var preset = GhProjectsBoards.Core.Projects.PlanningContract.BundledHolidays();
        File.WriteAllText(holidayPath, "国民の祝日・休日月日,国民の祝日・休日名称" + Environment.NewLine +
            string.Join(Environment.NewLine, preset.Dates.Select(d => $"{d.Date:yyyy/M/d},{d.Name}").Append("2026/10/5,追加休日")));
        // Only the OS picker is substituted; file parsing, export, import and storage are real.
        await Ui.Run(() => view.PickFile = purpose => Task.FromResult<string?>(purpose == "holiday" ? holidayPath : exported));
        await Settings();
        await Ui.Run(() => Ui.Click("PlanHolidayImport"));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.ImportedHolidays is not null || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty));
        await Ui.Idle();
        Assert.That(workspace.Session!.Schedule(new(2026, 10, 5)).Single().End.Value, Is.EqualTo(new DateOnly(2026, 10, 6)));
        await Ui.Until(() => Ui.Find<Button>("PlanExportSettings").IsLoaded && Ui.Find<Button>("PlanExportSettings").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanExportSettings")); await Ui.Idle();
        Assert.That(File.Exists(exported), Is.True);
        var before = PlanJson.Text(workspace.Session.Document.State.Settings);
        await AddDay("PlanCompanyDaysOff", new(2026, 10, 6));
        await Ui.Run(() => Ui.Click("PlanImportSettings")); await Ui.Idle();
        Assert.That(PlanJson.Text(workspace.Session.Document.State.Settings), Is.EqualTo(before));
    }

    [Test]
    public async Task ClosingCommitsTheFocusedSettingsInputBeforeFlushing()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanAllowanceU1");
            input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            input.Text = "80";
        });
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        var loaded = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 5));
        Assert.That(loaded.Session!.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(loaded.Session.UndoCount, Is.EqualTo(1));
    }
    [Test]
    public async Task InvalidSettingsKeepTheCurrentProjectAndInputUntilCorrected()
    {
        await Open(); await Settings();
        await CommitText("PlanAllowanceU1", "invalid");
        // Error text precedes the low-priority focus/scroll recovery that can dismiss a newly opened picker.
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0
            && Ui.Popup<Border>("SettingsInputProblem") is { IsLoaded: true }
            && Ui.Find<TextBox>("PlanAllowanceU1").FocusState != FocusState.Unfocused);
        await SheetNativeInput.Rendered();
        await PickerCommand("PlanChooseProject");
        if (pickerClosed is { } closingPicker) await closingPicker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Ui.Until(() => Ui.Popup<Border>("SettingsInputProblem") is { IsLoaded: true }
            && Ui.Find<TextBox>("PlanAllowanceU1").FocusState != FocusState.Unfocused);
        await SheetNativeInput.Rendered();
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanAllowanceU1").Text, Is.EqualTo("invalid"));
            Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
            Assert.That(workspace.Session!.UndoCount, Is.Zero);
        });
        await CommitText("PlanAllowanceU1", "80");
        await Ui.Until(() => workspace.Session!.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance == 80);
        await Ui.Idle();
    }

    [Test]
    public async Task SettingsCountsDraftsAndPullRequestsWhileThePlanOnlyShowsIssues()
    {
        await Open(); await Settings();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanExcludedCounts").Text, Is.EqualTo("計画対象外: ドラフト 2 件・プルリクエスト 1 件・参照できない項目 2 件")));
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
    }

    [Test]
    public async Task SettingsListsOnlyTheExcludedKindsThatArePresent()
    {
        FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { Drafts = 2, PullRequests = 0, Redacted = 0, HiddenItems = 0 });
        await Open(); await Settings();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanExcludedCounts").Text, Is.EqualTo("計画対象外: ドラフト 2 件")));
        await Ui.Run(() => Ui.Click("PlanSettingsBack")); await Ui.Idle();
        FakePlanEditor.Save(root, FakePlanEditor.Load(root) with { Drafts = 0 });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled); await Ui.Idle(); await Settings();
        await Ui.Run(() => Assert.That(Ui.Tree(Ui.Root).OfType<TextBlock>().Any(t => AutomationProperties.GetAutomationId(t) == "PlanExcludedCounts"), Is.False));
    }

    [Test]
    public async Task SettingsLabelsPersonColumnsOnceAndKeepsEachControlNamed()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanPeopleHeaderRate").Text, Is.EqualTo("稼働率 %"));
            Assert.That(Ui.Find<TextBlock>("PlanPeopleHeaderAllowance").Text, Is.EqualTo("上限工数 h"));
            var rate = Ui.Find<NumberBox>("PlanRateU1");
            var allowance = Ui.Find<TextBox>("PlanAllowanceU1");
            Assert.That(rate.Header, Is.Null);
            Assert.That(allowance.Header, Is.Null);
            Assert.That(AutomationProperties.GetName(rate), Does.EndWith(" 稼働率 (%)"));
            Assert.That(AutomationProperties.GetName(allowance), Does.EndWith(" 上限工数 h"));
        });
    }

    [Test]
    public async Task SettingsMappingHeadersUseJapaneseConceptsAndKeepGitHubFieldNames()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            foreach (var (field, caption) in new[] {
                (PlanField.Estimate, "見積工数"), (PlanField.Remaining, "見込み残存工数"),
                (PlanField.Actual, "実績工数"), (PlanField.Start, "開始日"), (PlanField.End, "終了日"),
                (PlanField.Phase, "工程") }) {
                var combo = Ui.Find<ComboBox>("PlanMap" + field);
                Assert.That(combo.Header, Is.EqualTo(caption));
                Assert.That(AutomationProperties.GetName(combo), Is.EqualTo(caption));
            }
            var estimate = Ui.Find<ComboBox>("PlanMapEstimate");
            Assert.That(((PlanFieldChoice)estimate.SelectedItem).Label, Is.EqualTo("Estimate"));
            Assert.That(estimate.Items.Cast<PlanFieldChoice>().Select(c => c.Label), Does.Contain("Remaining"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task SettingsPeriodPicksDatesRejectsReversedEndAndUndoRestoresAcceptedEnd()
    {
        await Open(); await Settings();
        PlanDocument Plan() => workspace.Session!.Document;
        await PickDate("PlanProjectStart", new(2026, 10, 5));
        await Ui.Until(() => Plan().State.Settings.ProjectStart == new DateOnly(2026, 10, 5));
        var undo = workspace.Session!.UndoCount;
        await PickDate("PlanProjectEnd", new(2026, 10, 4));
        await Ui.Until(() => Ui.Popup<Border>("SettingsInputProblem") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(Plan().State.Settings.ProjectEnd, Is.Null, "A rejected pair must not change the accepted settings.");
            Assert.That(Ui.Find<CalendarDatePicker>("PlanProjectEnd").Date, Is.Null, "The picker returns to the accepted value.");
            Assert.That(((TextBlock)Ui.Popup<Border>("SettingsInputProblem")!.Child).Text, Does.Contain("終了日は開始日以降"));
        });
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        await PickDate("PlanProjectEnd", new(2027, 4, 30));
        await Ui.Until(() => Plan().State.Settings.ProjectEnd == new DateOnly(2027, 4, 30));
        await Ui.Until(() => Ui.Popup<Border>("SettingsInputProblem") is null);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        Assert.That(Plan().State.Settings.ProjectEnd, Is.Null);
    }

    [Test]
    public async Task SettingsPeriodPickersShowThePickedDateAndClearIt()
    {
        await Open(); await Settings();
        PlanDocument Plan() => workspace.Session!.Document;
        await Ui.Run(() => Assert.That(Ui.Find<CalendarDatePicker>("PlanProjectStart").DateFormat, Does.Contain("dayofweek.abbreviated")));
        await PickDate("PlanProjectStart", new(2026, 10, 13));
        await Ui.Until(() => Plan().State.Settings.ProjectStart == new DateOnly(2026, 10, 13));
        await Ui.Run(() => Assert.That(Ui.Tree(Ui.Find<CalendarDatePicker>("PlanProjectStart")).OfType<TextBlock>()
            .Any(t => t.Text.Contains("2026/10/13")), Is.True, "The picker shows the date like the 基準日."));
        await PickDate("PlanProjectEnd", new(2027, 4, 30));
        await Ui.Until(() => Plan().State.Settings.ProjectEnd == new DateOnly(2027, 4, 30));
        var undo = workspace.Session!.UndoCount;
        await Ui.Ready<Button>("PlanProjectEndClear");
        await Ui.Run(() => Ui.Click("PlanProjectEndClear"));
        await Ui.Until(() => Plan().State.Settings.ProjectEnd is null && Ui.Find<CalendarDatePicker>("PlanProjectEnd").Date is null);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task SettingsPeriodRejectionClearsWhenAValidDateIsPicked()
    {
        await Open(); await Settings();
        PlanDocument Plan() => workspace.Session!.Document;
        await PickDate("PlanProjectStart", new(2026, 10, 5));
        await PickDate("PlanProjectEnd", new(2027, 4, 30));
        await Ui.Until(() => Plan().State.Settings.ProjectEnd == new DateOnly(2027, 4, 30));
        var before = PlanJson.Text(workspace.Session!.Document); var undo = workspace.Session.UndoCount;
        await PickDate("PlanProjectEnd", new(2026, 10, 4));
        await Ui.Until(() => Ui.Popup<Border>("SettingsInputProblem") is { IsLoaded: true }
            && Ui.Find<TextBlock>("PlanError").Text.Contains("終了日は開始日以降"));
        // A calendar picker cannot keep the rejected text: it returns to the accepted date, without an edit.
        await Ui.Run(() => {
            Assert.That(Ui.Find<CalendarDatePicker>("PlanProjectEnd").Date, Is.EqualTo(
                new DateTimeOffset(new DateOnly(2027, 4, 30).ToDateTime(TimeOnly.MinValue))));
            Assert.That(Plan().State.Settings.ProjectEnd, Is.EqualTo(new DateOnly(2027, 4, 30)));
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        // A valid pick is one operation and clears the callout.
        await PickDate("PlanProjectEnd", new(2027, 5, 31));
        await Ui.Until(() => Plan().State.Settings.ProjectEnd == new DateOnly(2027, 5, 31));
        await Ui.Until(() => Ui.Popup<Border>("SettingsInputProblem") is null && Ui.Find<TextBlock>("PlanError").Text.Length == 0);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
    }

    [Test]
    public async Task SettingsPhaseUsesTheColumnMappingAndListsOptionsAndLeafCounts()
    {
        var fake = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, fake with { Phases = ["SA", "UI"], Issues = fake.Issues.Select(i => i with { Phase = "SA" }).ToImmutableArray() });
        await Open(); await Settings();
        await Ui.Run(() => {
            Assert.That(((PlanFieldChoice)Ui.Find<ComboBox>("PlanMapPhase").SelectedItem).Definition.Id, Is.EqualTo("F-Phase"));
            Assert.That(Ui.Find<TextBlock>("PlanPhaseOptions").Visibility, Is.EqualTo(Visibility.Collapsed), "No task lacks a phase, so there is nothing to say.");
            Assert.That(Ui.Find<TextBlock>("PlanPhaseCount_SA").Text, Is.EqualTo("1"));
            Assert.That(Ui.Find<TextBlock>("PlanPhaseCount_UI").Text, Is.EqualTo("0"));
            Assert.That(Ui.Find<ComboBox>("PlanPhaseMilestone_SA").Items.Cast<string>(), Is.EqualTo(new[] { "なし" }));
            Ui.Find<ComboBox>("PlanMapPhase").SelectedIndex = 0;
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanPhaseOptions").Text, Is.EqualTo("工程のフィールドが未設定です。")));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task FailedReconnectHidesPriorProjectDataAndOffersConnectionAgain()
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"state\":\"notLoggedIn\"}");
        await PickerCommand("PlanConnection");
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanProjectPicker").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"), Is.False);
            Assert.That(workspace.Registered, Is.Empty);
            Assert.That(Ui.Find<Button>("PlanConnect").IsEnabled, Is.True);
        });
    }

    [TestCase("mapping"), TestCase("rate")]
    public async Task RejectedSettingsRestoreTheAcceptedControlValue(string kind)
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            if (kind == "rate") Ui.Find<NumberBox>("PlanRateU1").Value = 0;
            else {
                var combo = Ui.Find<ComboBox>("PlanMapEstimate");
                combo.SelectedItem = combo.Items.Cast<PlanFieldChoice>().Single(c => c.Label == "Remaining");
            }
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session!.UndoCount, Is.Zero);
            if (kind == "rate") Assert.That(Ui.Find<NumberBox>("PlanRateU1").Value, Is.EqualTo(100));
            else Assert.That(((PlanFieldChoice)Ui.Find<ComboBox>("PlanMapEstimate").SelectedItem).Label, Is.EqualTo("Estimate"));
        });
    }

    [Test]
    public async Task ClosingCommitsTheFocusedNativeRateInput()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            var rate = Ui.Find<NumberBox>("PlanRateU1");
            var input = Ui.Tree(rate).OfType<TextBox>().Single();
            input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            input.Text = "50";
        });
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        var loaded = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 5));
        Assert.That(loaded.Session!.Document.State.Settings.People.Single(p => p.Identity == "U1").Rate, Is.EqualTo(50));
        Assert.That(loaded.Session.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ReplacedSettingsControlsCannotChangeAnotherProject()
    {
        await Open(); await Settings();
        NumberBox oldRate = null!;
        await Ui.Run(() => oldRate = Ui.Find<NumberBox>("PlanRateU1"));
        await PickerCommand("PlanChooseProject");
        await Ui.Ready<ListView>("AvailableProjects");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected!.Id.NodeId == "P2"); await Ui.Idle();
        await Ui.Run(() => oldRate.Value = 50); await Ui.Idle();
        Assert.That(workspace.Session!.UndoCount, Is.Zero);
        Assert.That(workspace.People.Single(p => p.Identity == "U1").Rate, Is.EqualTo(100));
    }

    private async Task AddDay(string id, DateOnly day)
    {
        await Ui.Run(() => {
            Ui.Find<CalendarDatePicker>(id + "Date").Date = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
            Ui.Click(id + "Add");
        });
        await Ui.Idle();
    }

    [TestCase(false), TestCase(true), Category("PlanWorkspaceReview")]
    public async Task SuccessfulChangeClearsPreviousSettingsSaveFailure(bool fromPeople)
    {
        await Open(); await Settings();
        var store = new PlanStore(root);
        var firstDay = new DateOnly(2026, 10, 7);
        var secondDay = new DateOnly(2026, 10, 8);
        using (var writer = new FileStream(store.FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await AddDay("PlanCompanyDaysOff", firstDay);
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => {
                var failure = Ui.Find<InfoBar>("PlanSaveFailure");
                Assert.That(failure.IsOpen, Is.True);
                Assert.That(failure.Title, Is.EqualTo("保存できませんでした"));
                Assert.That(((Button)failure.ActionButton).Visibility, Is.EqualTo(Visibility.Visible));
            });
        }
        if (fromPeople) {
            // The settings page is already open; rebuilding it would invalidate its controls.
            await Ui.Ready<TextBox>("PlanAllowanceU1"); await Ui.Ready<Button>("PlanSettingsBack");
            await Ui.Run(() => {
                var input = Ui.Find<TextBox>("PlanAllowanceU1"); input.Focus(FocusState.Programmatic); input.Text = "80";
                Ui.Click("PlanSettingsBack");
            });
            await Ui.Idle();
        } else await AddDay("PlanCompanyDaysOff", secondDay);
        var stored = await store.LoadAsync(workspace.Session.Document.Project);
        Assert.That(stored.Checkpoint!.Document.State.Settings.CompanyDaysOff, Is.EqualTo(fromPeople ? new[] { firstDay } : new[] { firstDay, secondDay }));
        if (fromPeople) Assert.That(stored.Checkpoint.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        await Ui.Run(() => {
            var failure = Ui.Find<InfoBar>("PlanSaveFailure");
            Assert.That(failure.IsOpen, Is.False, "A successful later save must remove the obsolete failure.");
            Assert.That(((Button)failure.ActionButton).Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task SaveRetryRecoversPendingSettingsAndAllowsRefreshAndNormalClose()
    {
        await Open(); await Settings();
        var path = new PlanStore(root).FileFor(workspace.Selected!.Id);
        try
        {
            using (var writer = new FileStream(path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await CommitText("PlanColumnNameTitle", "作業名");
                await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen);
                await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.False));
            }
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => Ui.Click("PlanRetrySave")); await Ui.Idle();
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty));
            var reopened = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 6));
            Assert.That(reopened.Session!.Document.State.Settings.SheetColumns.Single(c => c.Key == PlanField.Title).DisplayName, Is.EqualTo("作業名"));
            Assert.That(reopened.Session.UndoCount, Is.EqualTo(1));
            await Ui.Run(() => Ui.Click("PlanRefresh")); await Ui.Idle();
            await Ui.Run(async () => { Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty); Assert.That(await view.StopAsync(), Is.True); });
        }
        finally { await workspace.RetrySave(); }
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task ConnectingToAnEarlierVersionsPlanOffersReadingTheProjectAgainFromGitHub()
    {
        await Open();
        var choice = workspace.Selected!;
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        await Ui.Unmount(view, check: false);
        var store = new PlanStore(root);
        var path = store.FileFor(choice.Id);
        // A build from before #125/#127/#133 saved rows without these properties.
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        foreach (var name in new[] { "milestoneTitle", "labels", "extraValues", "phase", "closeDate" })
            node["document"]!["state"]!["rows"]![0]!.AsObject().Remove(name);
        var unreadable = node.ToJsonString();
        await File.WriteAllTextAsync(path, unreadable);

        workspace = new(store, () => readDay);
        await Ui.Run(() => view = new(workspace, (_, host) => new(FakeExecutable, host,
            new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }))));
        await Ui.Mount(view);
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanOpenFailure").IsOpen || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Ready<Button>("PlanReadAgain");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => {
            var failure = Ui.Find<InfoBar>("PlanOpenFailure");
            Assert.That(failure.Title, Is.EqualTo("計画を開けませんでした"));
            Assert.That(failure.Message, Does.Contain("「開発計画」").And.Contain("以前の版").And.Contain("未発行の変更は引き継がれません"));
            Assert.That(((Button)failure.ActionButton).Content, Is.EqualTo("GitHub から読み直す"));
            Assert.That(Ui.Find<InfoBar>("PlanSaveFailure").IsOpen, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
            var others = Ui.Find<ListView>("AvailableProjects");
            Assert.That(others.Items.Cast<ProjectChoice>().Single().Id.NodeId, Is.EqualTo("P2"));
            Assert.That(others.IsEnabled, Is.True); Assert.That(others.ActualHeight, Is.GreaterThan(0), "The other Projects stay reachable.");
        });
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(unreadable), "Showing the failure never changes the stored plan.");

        await Ui.Run(() => Ui.Click("PlanReadAgain"));
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Until(() => Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanOpenFailure").IsOpen, Is.False));
        Assert.That(workspace.Selected!.Id, Is.EqualTo(choice.Id));
        var kept = Directory.GetFiles(store.Root, Path.GetFileName(path) + ".unreadable-*").Where(f => !f.EndsWith(".bak", StringComparison.Ordinal));
        Assert.That(await File.ReadAllTextAsync(kept.Single()), Is.EqualTo(unreadable));
        Assert.That((await store.LoadAsync(choice.Id)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task FailedCatalogSaveKeepsSettingsAndSelectionOnTheOriginalProject()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2"); await Ui.Idle();
        await SelectRegistered("P1");
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1"); await Ui.Idle();
        await Settings();
        var original = workspace.Session;
        using (var lockedCatalog = new FileStream(Path.Combine(workspace.Root, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await SelectRegistered("P2");
            await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen); await Ui.Idle();
            Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
            await OpenProjectPicker();
            await Ui.Run(() => {
                Assert.That(((ProjectChoice)Ui.Find<ListView>("RegisteredProjects").SelectedItem).Id.NodeId, Is.EqualTo("P1"));
                Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo("開発計画"));
            });
            await Ui.Run(() => Ui.Find<Button>("PlanProjectPicker").Flyout.Hide());
            await AddDay("PlanCompanyDaysOff", new(2026, 10, 7));
            Assert.That(original!.Document.State.Settings.CompanyDaysOff, Is.EqualTo(new[] { new DateOnly(2026, 10, 7) }));
        }
        await SelectRegistered("P2");
        await Ui.Until(() => workspace.Selected!.Id.NodeId == "P2"); await Ui.Idle();
        Assert.That(workspace.Session!.Document.State.Settings.CompanyDaysOff, Is.Empty);
        Assert.That(workspace.Session.UndoCount, Is.Zero);
    }

    [TestCase("connect"), TestCase("open"), TestCase("refresh"), TestCase("csv"), Category("PlanWorkspaceReview")]
    public async Task ClosingCancelsTheOwnedGhProcessBeforeItResponds(string stage)
    {
        if (stage == "open") {
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => Ui.Find<ListView>("AvailableProjects").Items.Count == 1); await Ui.Idle();
        }
        else if (stage is "refresh" or "csv") await Open();
        if (stage == "csv")
        {
            var csvPath = Path.Combine(root, "tasks.csv");
            File.WriteAllText(csvPath, "キー,タイトル,見積\na,A,8\n");
            await Ui.Run(() => view.PickFile = _ => Task.FromResult<string?>(csvPath));
        }
        var before = workspace.Session is { } session ? PlanJson.Text(session.Document) : null;
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdOperation = stage == "connect" ? "auth" : "none",
            holdQuery = stage == "connect" ? "none" : stage == "csv" ? "PlanCsvRepository" : "ProjectFields"
        }));
        await Ui.Run(() => {
            if (stage == "open") Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
            else if (stage != "csv") Ui.Click(stage == "connect" ? "PlanConnect" : "PlanRefresh");
        });
        if (stage == "csv") await Ui.ClickCommand("PlanSheetCsv");
        var marker = Path.Combine(root, "held-gh.pid");
        int pid = 0;
        // The fake gh may still hold the marker open while writing its process id.
        await Ui.Until(() => { try { return File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out pid); } catch (IOException) { return false; } });
        using var process = Process.GetProcessById(pid);
        Task<bool> stop = null!;
        await Ui.Run(() => { stop = view.StopAsync(); });
        var completedBeforeResponse = await Task.WhenAny(stop, Task.Delay(3000)) == stop;
        try { Assert.That(completedBeforeResponse, Is.True, "Close must cancel its gh process rather than await its delayed response."); }
        finally {
            File.WriteAllText(Path.Combine(root, "release-gh"), "release");
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(await stop, Is.True);
        if (stage == "refresh") await Ui.Run(() => {
            Assert.That(AutomationProperties.GetName(Ui.Find<SplitButton>("PlanRefresh")), Is.EqualTo("最新の情報に更新"));
            Assert.That(Ui.Find<ContentControl>("PlanBusyPanel").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.False);
        });
        process.Refresh(); Assert.That(process.HasExited, Is.True);
        if (before is not null) Assert.That(PlanJson.Text(workspace.Session!.Document), Is.EqualTo(before));
        else Assert.That(workspace.Session, Is.Null);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task ManyRegisteredProjectsScrollWithoutDisplacingCommandsAndKeepTitlesReadable()
    {
        var scope = new ConnectionScope("github.com", 42);
        var choices = Enumerable.Range(1, 40).Select(i => new ProjectChoice(new(scope, "P" + i), new(scope, "O1"),
            "fixture-user", "User", i, "https://github.com/users/fixture-user/projects/" + i,
            i == 40 ? "codex-sandbox" : "計画 " + i)).ToImmutableArray();
        Directory.CreateDirectory(workspace.Root);
        File.WriteAllText(Path.Combine(workspace.Root, "workspace.json"), PlanJson.Text(new PlanWorkspaceCatalog(1, choices, null)));
        await Ui.Run(() => { view.Width = 900; view.Height = 620; Ui.Click("PlanConnect"); });
        await Ui.Until(() => workspace.Registered.Count == 40); await Ui.Idle();
        await Ui.Until(() => Ui.Find<ListView>("PlanRecentProjects").Items.Count == 5);
        await Ui.Run(() => Assert.That(Ui.Find<ListView>("PlanRecentProjects").Items.Cast<ProjectChoice>().Select(p => p.Number), Is.EqualTo(new[] { 40, 39, 38, 37, 36 })));
        await OpenProjectPicker();
        await Ui.Until(() => Ui.Find<ListView>("RegisteredProjects").ContainerFromIndex(0) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => {
            var open = Ui.Find<Button>("PlanChooseProject"); var connect = Ui.Find<Button>("PlanConnection");
            foreach (var command in new[] { open, connect }) {
                var bounds = command.TransformToVisual(view).TransformBounds(new Rect(0, 0, command.ActualWidth, command.ActualHeight));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(view.ActualHeight));
            }
            var list = Ui.Find<ListView>("RegisteredProjects");
            Assert.That(Ui.Tree((ListViewItem)list.ContainerFromIndex(0)).OfType<TextBlock>().Any(t => t.Text == "codex-sandbox"), Is.True,
                "The title must have its own presentation, without an appended owner consuming the title line.");
            list.ScrollIntoView(list.Items[^1]);
        });
        await Ui.Until(() => {
            var list = Ui.Find<ListView>("RegisteredProjects");
            if (list.ContainerFromIndex(39) is not ListViewItem { IsLoaded: true } last) return false;
            var bounds = last.TransformToVisual(list).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
            return bounds.Top >= 0 && bounds.Bottom <= list.ActualHeight + 1;
        });
        await Ui.Run(async () => {
            var flyout = (Flyout)Ui.Find<Button>("PlanProjectPicker").Flyout;
            var content = (FrameworkElement)flyout.Content;
            var popup = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).Single(p => Ui.Tree(p.Child).Contains(content));
            // Evidence awaits rendering; unrelated desktop focus must not dismiss its target mid-capture.
            var dismiss = popup.IsLightDismissEnabled; popup.IsLightDismissEnabled = false;
            try {
                Assert.That(flyout.IsOpen && content.IsLoaded, Is.True);
                var list = Ui.Find<ListView>("RegisteredProjects"); var last = (ListViewItem)list.ContainerFromIndex(39);
                var bounds = last.TransformToVisual(list).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(list.ActualHeight + 1));
                await RenderedEvidence.Capture(content, "many-projects");
            } finally { popup.IsLightDismissEnabled = dismiss; }
        });
    }

    [TestCase("PlanCompanyDaysOff"), Category("PlanWorkspaceReview")]
    public async Task CalendarDateListsAddOnceRemoveAndUndoWithoutTypedDateFormats(string id)
    {
        await Open(); await Settings();
        var day = new DateOnly(2026, 10, 7);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>(id + "Add").IsEnabled, Is.False);
            Assert.That(Ui.Tree(view).OfType<FrameworkElement>().Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e).StartsWith("PlanDaysOff") || e is TextBlock { Text: "個人休日" }), Is.False);
            Ui.Find<CalendarDatePicker>(id + "Date").Date = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        });
        Assert.That(workspace.Session!.UndoCount, Is.Zero, "Selection alone must not change the calendar.");
        await AddDay(id, day); await AddDay(id, day);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => {
            var dates = Ui.Find<ListView>(id + "Dates");
            Assert.That(dates.Items.Count, Is.EqualTo(1)); dates.SelectedIndex = 0;
            Ui.Click(id + "Remove");
        }); await Ui.Idle();
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(2));
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        var reopened = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 6));
        var settings = reopened.Session!.Document.State.Settings;
        Assert.That(settings.CompanyDaysOff, Is.EqualTo(new[] { day }));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    private async Task TypeIntoCell(string id, string value)
    {
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>(id);
            Assert.That(input.Focus(FocusState.Programmatic), Is.True);
            input.Text = value;
        });
    }
    private async Task CommitByFocusLoss()
    {
        await Ui.Run(() => Ui.Find<Button>("PlanShowSettings").Focus(FocusState.Programmatic));
        await Ui.Idle();
    }
    private static string[] RecordedHostedQueries(string root)
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Select(call => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString()! : "")
                .Where(query => query.Length > 0).ToArray()
            : [];
    }

    // #253 behavior 11/12: Settings lists only the people a row holds, and a typed login that is not yet a
    // Project person is verified on demand and registered with the assignment.
    [Test, Category("PlanWorkspaceReview")]
    public async Task SettingsListsOnlyAssignedPeopleAndAResolvedLoginJoinsThem()
    {
        await Open();
        await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU1");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<NumberBox>().Any(n => AutomationProperties.GetAutomationId(n) == "PlanRateU2"), Is.False,
                "A person no task holds is not in the settings list.");
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Idle();
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        // A name the sheet already knows is local: no lookup is sent for the ordinary edit.
        await TypeIntoCell("PlanCell1_Assignees", "person-U1");
        await CommitByFocusLoss();
        Assert.That(RecordedHostedQueries(root).Any(query => query.Contains("PlanResolveAssignee")), Is.False,
            "A locally known assignee needs no GitHub lookup.");
        await TypeIntoCell("PlanCell1_Assignees", "person-U2");
        await CommitByFocusLoss();
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Single().Assignees.Contains("U2"));
        Assert.That(RecordedHostedQueries(root).Count(query => query.Contains("PlanResolveAssignee")), Is.EqualTo(1),
            "One lookup for the one typed login the sheet did not know.");
        await Ui.Run(() => Ui.Click("PlanShowSettings")); await Ui.Ready<TextBox>("PlanAllowanceU2");
        await Ui.Run(() => {
            Assert.That(Ui.Find<NumberBox>("PlanRateU2").Value, Is.EqualTo(100), "The looked-up login is stored at 100% and listed.");
            Assert.That(workspace.Session!.UndoCount, Is.EqualTo(1));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    // #253 behavior 12 / the recorded in-flight presentation: the status line names the login, the typed text
    // stays in the editing cell, nothing is written until the lookup returns, and the commit is one Undo.
    [Test, Category("PlanWorkspaceReview")]
    public async Task InFlightAssigneeLookupNamesTheLoginWhileTheTypedTextStaysInTheCell()
    {
        await Open();
        var session = workspace.Session!;
        var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount;
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"holdQuery\":\"PlanResolveAssignee\"}");
        await TypeIntoCell("PlanCell1_Assignees", "late-user");
        await CommitByFocusLoss();
        try
        {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            // The lookup is a tracked operation while the fake holds the query, so Ui.Idle would wait on it;
            // wait for the in-flight status line instead.
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("担当者 late-user を確認しています…"));
                Assert.That(Ui.Find<TextBox>("PlanCell1_Assignees").Text, Is.EqualTo("late-user"), "The typed text stays in the editing cell while the lookup runs.");
                Assert.That(PlanJson.Text(workspace.Session!.Document), Is.EqualTo(before));
                Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
            });
        }
        finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Single().Assignees.Contains("U101"));
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty);
            Assert.That(workspace.Session!.UndoCount, Is.EqualTo(undo + 1), "The assignment and the registration are one Undo step.");
            Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U101").Rate, Is.EqualTo(100));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    // #253 behavior 13: a login GitHub cannot assign is refused at the cell, nothing is written, and correcting
    // the text clears the message.
    [Test, Category("PlanWorkspaceReview")]
    public async Task RefusedAssigneeLookupShowsTheLoginAtTheCellAndWritesNothing()
    {
        await Open();
        var session = workspace.Session!;
        var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount;
        await TypeIntoCell("PlanCell1_Assignees", "nobody");
        await CommitByFocusLoss();
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is { IsLoaded: true });
        await Ui.Run(() => {
            Assert.That(((TextBlock)Ui.Popup<Border>("SheetInputProblem")!.Child).Text, Is.EqualTo("担当者を確認できません: nobody"));
            Assert.That(PlanJson.Text(workspace.Session!.Document), Is.EqualTo(before), "A refused lookup writes nothing.");
            Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        // The refused commit already moved focus to the settings button, so re-enter the cell before correcting
        // the text; setting the text while focus stays away would not commit.
        await TypeIntoCell("PlanCell1_Assignees", "person-U1");
        await CommitByFocusLoss();
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is null);
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Single().Assignees, Is.EqualTo(new[] { "U1" }));
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty, "the refusal message does not outlive the correction");
        });
    }

    // #253: Escape (CancelEdit) while the lookup is in flight cancels it through its token: nothing is written
    // and the cell returns to its accepted value.
    [Test, Category("PlanWorkspaceReview")]
    public async Task EscapeDuringAnAssigneeLookupCancelsItWithoutWriting()
    {
        await Open();
        var session = workspace.Session!;
        var before = PlanJson.Text(session.Document);
        var undo = session.UndoCount;
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"holdQuery\":\"PlanResolveAssignee\"}");
        await TypeIntoCell("PlanCell1_Assignees", "late-user");
        await CommitByFocusLoss();
        try
        {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().CancelEdit("I1", PlanField.Assignees));
            await Ui.Idle();
        }
        finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(PlanJson.Text(workspace.Session!.Document), Is.EqualTo(before), "A cancelled lookup writes nothing.");
            Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty);
            Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().Pending, Is.Empty);
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

}

