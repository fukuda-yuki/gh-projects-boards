using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    private async Task BeginBodyEdit()
    {
        await Open(); await DetailFallback();
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        await DetailReady(); await Ui.Ready<Button>("PlanDetailEdit");
        await Ui.Run(() => Ui.Click("PlanDetailEdit"));
        await Ui.Ready<TextBox>("PlanDetailEditor");
    }
    private void RemoteBody(string body) => File.WriteAllText(Path.Combine(root, "detail-bodies.json"),
        JsonSerializer.Serialize(new Dictionary<string, string> { ["I1"] = body }));
    private JsonElement[] BodyWrites() => DetailCalls().Where(c => c.GetProperty("mutation").GetBoolean()).ToArray();
    private async Task PublishBodyDraft(string draft)
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanDetailEditor").Text = draft);
        await Ui.Until(() => DetailElement<Button>("PlanDetailPublishBody") is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Click("PlanDetailPublishBody"));
    }
    private async Task<bool> StopDetailTest()
    {
        var stop = view.StopAsync();
        while (!stop.IsCompleted) {
            if (Ui.Dialog("PlanDetailDiscard") is { IsLoaded: true } dialog && Ui.Tree(dialog).OfType<Button>().Any(b => b.Name == "PrimaryButton" && b.IsLoaded && b.IsEnabled))
                Ui.DialogButton("PlanDetailDiscard", "PrimaryButton");
            await Task.WhenAny(stop, Task.Delay(20));
        }
        return await stop;
    }

    [Test, Category("IssueDetail")]
    public async Task SelectionLineToggleSharesStateAndPreservesPendingCellInputIncludingLocalRows()
    {
        await Open(); await DetailFallback();
        await Ui.Run(() => {
            SelectDetail(""); Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").Visibility, Is.EqualTo(Visibility.Collapsed));
            SelectDetail("I1");
            var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "pending";
            var toggle = Ui.Find<ToggleButton>("PlanSheetDetail");
            Assert.That(toggle.Visibility, Is.EqualTo(Visibility.Visible));
            toggle.Focus(FocusState.Programmatic);
            ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(DetailSheet.Pending[("I1", PlanField.Title)].Text, Is.EqualTo("pending"));
            ToggleDetail(); Assert.That(toggle.IsChecked, Is.False);
            DetailSheet.CancelEdit("I1", PlanField.Title);
        });
        var row = PlanRow.New("Local", "acme/repo");
        await workspace.Session!.Execute(new InsertPlanRows([row]), readDay);
        await Ui.Run(() => {
            DetailSheet.Refresh(); SelectDetail(row.Identity);
            Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").Visibility, Is.EqualTo(Visibility.Visible));
        });
        Assert.That(BodyWrites(), Is.Empty);
    }

    // #265: an open side pane takes about 380 px from the plan; the Gantt keeps 640 and the sheet scrolls instead,
    // and closing the pane returns the ordinary split.
    [Test, Category("IssueDetail")]
    public async Task OpenDetailPaneKeepsTheGanttMinimumAndTheSheetScrollsInstead()
    {
        await Open(); await DetailFallback();
        double sheetBefore = 0;
        // The default host window follows the display scale; at 125 % it cannot hold sheet, 640 px Gantt and pane.
        // 1280 is narrow enough for the bar to overflow and wide enough for the Gantt minimum.
        await Ui.Run(() => ResizeWorkspace(1280, 800));
        await Ui.Until(() => view.ActualWidth >= 1270);
        await Ui.Run(() => { sheetBefore = DetailSheet.SheetViewport; SelectDetail("I1"); ToggleDetail(); });
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").ActualWidth > 0 && DetailSheet.ChartViewport >= PlanSheetView.SidePaneChartMinimum
            && Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth > 0,
            () => $"pane={Ui.Find<Border>("PlanDetailPane").ActualWidth} chart={DetailSheet.ChartViewport} sheet={DetailSheet.SheetViewport} scrollable={Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth}");
        await Ui.Run(() => {
            Assert.That(DetailSheet.ChartViewport, Is.GreaterThanOrEqualTo(PlanSheetView.SidePaneChartMinimum));
            Assert.That(DetailSheet.SheetViewport, Is.LessThan(sheetBefore));
            Assert.That(Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth, Is.GreaterThan(0), "The sheet scrolls horizontally instead.");
            // The bar's overflow moves commands out of the realized visual tree, so read their state from the
            // CommandBar's own command lists rather than by finding the button.
            Assert.That(Ui.CommandButton("PlanSheetCollapseAll").IsInOverflow, Is.True, "The narrowed bar overflows from its end.");
            Assert.That(Ui.CommandButton("PlanSheetGoToDate").IsInOverflow, Is.False, "選択タスクの日程へ移動 overflows last.");
            Assert.That(Ui.Find<Button>("PlanGanttGoToStatusDate").ActualWidth, Is.GreaterThan(0));
            ToggleDetail();
        });
        await Ui.Until(() => Math.Abs(DetailSheet.SheetViewport - sheetBefore) < .5);
        Assert.That(BodyWrites(), Is.Empty);
    }

    [Test, Category("IssueDetail")]
    public async Task BodyPublishChangesOnlyBodyAndPreservesPlanHistoryAndPendingInput()
    {
        await BeginBodyEdit();
        var before = workspace.Session!.Document;
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("**Body 設計**"));
            var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "pending";
            Ui.Find<TextBox>("PlanDetailEditor").Focus(FocusState.Programmatic);
        });
        await PublishBodyDraft("New\rbody");
        await Ui.Until(() => DetailElement<TextBlock>("PlanDetailPublished") is { Visibility: Visibility.Visible } text && text.Text.StartsWith("本文を発行しました"));
        await Ui.Until(() => Ui.Tree(Ui.Find<ScrollViewer>("PlanDetailMarkdown")).OfType<TextBlock>().Any(t => t.Text.Contains("New\nbody")));
        await Ui.Run(() => {
            Assert.That(workspace.Session.Document, Is.SameAs(before));
            Assert.That(workspace.Session.UndoCount, Is.Zero);
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 0 タスク"));
            Assert.That(DetailSheet.Pending[("I1", PlanField.Title)].Text, Is.EqualTo("pending"));
            Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<Button>("PlanDetailEdit")));
            DetailSheet.CancelEdit("I1", PlanField.Title);
        });
        var input = BodyWrites().Single().GetProperty("bodyInput");
        Assert.That(input.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "id", "body" }));
        Assert.That(input.GetProperty("id").GetString(), Is.EqualTo("I1"));
        Assert.That(input.GetProperty("body").GetString(), Is.EqualTo("New\nbody\n\n<!-- ghpb-plan:fixture -->"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("IssueDetail")]
    public async Task BodyLineEndingOnlyEditCannotPublishAndCancelsWithoutPrompt()
    {
        RemoteBody("first\nsecond"); await Open(); await DetailFallback();
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        await Ui.Until(() => DetailElement<Button>("PlanDetailEdit")?.IsEnabled == true);
        await Ui.Run(() => {
            Ui.Click("PlanDetailEdit"); Ui.Find<TextBox>("PlanDetailEditor").Text = "first\rsecond";
            Assert.That(Ui.Find<Button>("PlanDetailPublishBody").IsEnabled, Is.False);
            Ui.Click("PlanDetailCancelEdit");
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).Any(p => p.Child is ContentDialog), Is.False);
        });
    }

    [Test, Category("IssueDetail")]
    public async Task EditingPinsIssueUntilCancelShowsCurrentSelection()
    {
        SeedDetailRows(); await BeginBodyEdit();
        await Ui.Run(() => {
            SelectDetail("I2");
            Assert.That(Ui.Find<TextBlock>("PlanDetailTitle").Text, Is.EqualTo("設計"));
            Assert.That(Ui.Find<TextBlock>("PlanDetailPinnedNote").Visibility, Is.EqualTo(Visibility.Visible));
            Ui.Click("PlanDetailCancelEdit");
        });
        await DetailReady("実装");
    }

    [TestCase(false), TestCase(true), Category("IssueDetail")]
    public async Task BodyConflictKeepsDraftUntilOverwriteOrRevert(bool overwrite)
    {
        await BeginBodyEdit(); RemoteBody("remote changed");
        await PublishBodyDraft("my draft");
        await Ui.Until(() => DetailElement<InfoBar>("PlanDetailConflict")?.IsOpen == true);
        Assert.That(BodyWrites(), Is.Empty);
        await Ui.Until(() => DetailElement<Button>(overwrite ? "PlanDetailOverwrite" : "PlanDetailRevert") is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("PlanDetailConflict").Severity, Is.EqualTo(InfoBarSeverity.Warning));
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("my draft"));
            Ui.Click(overwrite ? "PlanDetailOverwrite" : "PlanDetailRevert");
        });
        await Ui.Until(() => DetailElement<TextBox>("PlanDetailEditor")?.Visibility == Visibility.Collapsed);
        await Ui.Until(() => Ui.Tree(Ui.Find<ScrollViewer>("PlanDetailMarkdown")).OfType<TextBlock>().Any(t => t.Text.Contains(overwrite ? "my draft" : "remote changed")));
        Assert.That(BodyWrites().Length, Is.EqualTo(overwrite ? 1 : 0));
    }

    [Test, Category("IssueDetail")]
    public async Task BodyFailureKeepsDraftAndExplicitRetrySucceeds()
    {
        await BeginBodyEdit(); DetailScenario("body-write");
        await PublishBodyDraft("retry draft");
        await Ui.Until(() => DetailElement<InfoBar>("PlanDetailPublishFailure")?.IsOpen == true);
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("PlanDetailPublishFailure").Severity, Is.EqualTo(InfoBarSeverity.Error));
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("retry draft"));
        });
        DetailScenario(); await Ui.Run(() => Ui.Click("PlanDetailPublishRetry"));
        await Ui.Until(() => DetailElement<TextBlock>("PlanDetailPublished")?.Visibility == Visibility.Visible);
        Assert.That(BodyWrites().Length, Is.EqualTo(2));
    }

    [TestCase("cancel"), TestCase("pane"), TestCase("sheet"), TestCase("assist"), TestCase("project"), TestCase("connection"), TestCase("stop"), Category("IssueDetail")]
    public async Task DiscardReturnPreservesDraftAndCancelsTrigger(string trigger)
    {
        await BeginBodyEdit(); var session = workspace.Session; Task<bool>? stop = null;
        if (trigger is "project" or "connection") {
            await Ui.Run(() => Ui.Click("PlanProjectPicker"));
            await Ui.Ready<Button>(trigger == "project" ? "PlanChooseProject" : "PlanConnection");
        }
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanDetailEditor").Text = "keep draft";
            switch (trigger) {
                case "cancel": Ui.Click("PlanDetailCancelEdit"); break;
                case "pane": ToggleDetail(); break;
                case "sheet": Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked = false; break;
                case "assist": ToggleAssist(); break;
                case "project": Ui.Click("PlanChooseProject"); break;
                case "connection": Ui.Click("PlanConnection"); break;
                case "stop": stop = view.StopAsync(); break;
            }
        });
        await Ui.Ready<ContentDialog>("PlanDetailDiscard");
        await Ui.Run(() => {
            var dialog = Ui.Find<ContentDialog>("PlanDetailDiscard");
            Assert.That(dialog.Title, Is.EqualTo("本文の変更を破棄しますか？"));
            Assert.That(dialog.DefaultButton, Is.EqualTo(ContentDialogButton.Close));
            Ui.DialogButton("PlanDetailDiscard", "CloseButton");
        });
        if (stop is not null) Assert.That(await stop, Is.False);
        await Ui.Idle();
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(view.XamlRoot), Ui.Find<TextBox>("PlanDetailEditor")));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("keep draft"));
            Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.True);
            Assert.That(Ui.Find<ToggleButton>("PlanAssist").IsChecked, Is.False);
            Assert.That(workspace.Session, Is.SameAs(session));
            Ui.Find<TextBox>("PlanDetailEditor").Text = "**Body 設計**"; Ui.Click("PlanDetailCancelEdit");
        });
        await Ui.Until(() => Ui.Find<TextBox>("PlanDetailEditor").Visibility == Visibility.Collapsed);
    }

    [Test, Category("IssueDetail")]
    public async Task BodyUpdatePermissionExplainsDisabledEdit()
    {
        await Open(); await DetailFallback(); DetailScenario("readonly");
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); }); await DetailReady();
        await Ui.Run(() => {
            var button = Ui.Find<Button>("PlanDetailEdit");
            Assert.That(button.IsEnabled, Is.False); Assert.That(button.AllowFocusWhenDisabled, Is.True);
            Assert.That(AutomationProperties.GetHelpText(button), Is.EqualTo("この Issue の本文を編集する権限がありません"));
        });
    }

    [Test, Category("IssueDetail")]
    public async Task RemoteRefreshDisablesBodyPublishAndKeepsDraft()
    {
        await BeginBodyEdit();
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"holdOperation":"api"}""");
        await Ui.Run(() => { Ui.Find<TextBox>("PlanDetailEditor").Text = "waiting draft"; Ui.Click("PlanRefresh"); });
        await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
        try {
            await Ui.Run(() => {
                Assert.That(Ui.Find<Button>("PlanDetailPublishBody").IsEnabled, Is.False);
                Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBox>("PlanDetailEditor")).IsOffscreen(), Is.False);
                Assert.That(Ui.Find<TextBox>("PlanDetailEditor").IsReadOnly, Is.True);
                Assert.That(Ui.Find<TextBlock>("PlanDetailReference").Text, Is.EqualTo("acme/repo#1"));
            });
        }
        finally { File.WriteAllText(Path.Combine(root, "release-gh"), ""); }
        await Ui.Until(() => Ui.Find<Button>("PlanDetailPublishBody").IsEnabled);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("waiting draft"));
            Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBox>("PlanDetailEditor")).IsOffscreen(), Is.False);
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").IsReadOnly, Is.False);
            Ui.Find<TextBox>("PlanDetailEditor").Text = "**Body 設計**"; Ui.Click("PlanDetailCancelEdit");
        });
    }

    [TestCase(null), TestCase("body-mismatch"), TestCase("body-refresh"), Category("IssueDetail")]
    public async Task UncertainBodyWriteRetryRecognizesPublishedBodyWithoutAnotherMutation(string? fault)
    {
        await BeginBodyEdit();
        if (fault is not null) DetailScenario(fault);
        else RemoteBody("completed\n\n<!-- ghpb-plan:fixture -->");
        await PublishBodyDraft("completed");
        if (fault is not null) {
            await Ui.Until(() => DetailElement<InfoBar>("PlanDetailPublishFailure")?.IsOpen == true);
            // The retry action is part of the failure bar; wait for the bar's control before clicking it.
            await Ui.Ready<Button>("PlanDetailPublishRetry");
            DetailScenario(); await Ui.Run(() => Ui.Click("PlanDetailPublishRetry"));
        }
        await Ui.Until(() => DetailElement<TextBlock>("PlanDetailPublished")?.Visibility == Visibility.Visible);
        Assert.That(BodyWrites().Length, Is.EqualTo(fault is not null ? 1 : 0));
    }

    [Test, Category("IssueDetail")]
    public async Task DiscardPrimaryClosesPaneAndSummaryAndSettingsKeepHiddenDraft()
    {
        await BeginBodyEdit();
        await Ui.Run(() => { Ui.Find<TextBox>("PlanDetailEditor").Text = "hidden draft"; Ui.Click("PlanShowSummary"); });
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Collapsed);
        await Ui.Run(() => Ui.Click("PlanShowTasks"));
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Visible);
        await Settings(); await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Visible);
        await Ui.Run(async () => {
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("hidden draft"));
            await RenderedEvidence.Capture(view, "issue-body-editing"); ToggleDetail();
        });
        await Ui.DialogReady("PlanDetailDiscard");
        await Ui.Run(() => Ui.DialogButton("PlanDetailDiscard", "PrimaryButton"));
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Collapsed);
        await Ui.Run(() => { ToggleDetail(); Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Visibility, Is.EqualTo(Visibility.Collapsed)); });
        Assert.That(BodyWrites(), Is.Empty);
    }

    [Test, Category("IssueDetail")]
    public async Task AppCloseWaitsForBodyPublishAndDoesNotCancelWrite()
    {
        await BeginBodyEdit();
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"holdQuery":"PlanIssueBodyPublish"}""");
        await PublishBodyDraft("closing body");
        await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
        Task<bool>? stop = null;
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").IsReadOnly, Is.True);
            Assert.That(Ui.Find<Button>("PlanDetailCancelEdit").IsEnabled, Is.False);
            stop = view.StopAsync(); Assert.That(stop.IsCompleted, Is.False);
        });
        File.WriteAllText(Path.Combine(root, "release-gh"), "");
        Assert.That(await stop!, Is.True);
        Assert.That(BodyWrites().Length, Is.EqualTo(1));
        Assert.That(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "detail-bodies.json")))!["I1"],
            Is.EqualTo("closing body\n\n<!-- ghpb-plan:fixture -->"));
    }

    [Test, Category("IssueDetail")]
    public async Task RegisteredProjectSwitchAsksBeforeDiscardAndReturnKeepsProject()
    {
        await Open(); await PickerCommand("PlanChooseProject");
        await ProjectChooserTestActions.SelectOwner("acme");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2"); await Ui.Idle(); await DetailFallback();
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); }); await DetailReady();
        await Ui.Run(() => Ui.Click("PlanDetailEdit")); await Ui.Ready<TextBox>("PlanDetailEditor");
        await Ui.Run(() => Ui.Find<TextBox>("PlanDetailEditor").Text = "project draft");
        var session = workspace.Session;
        foreach (var discard in new[] { false, true }) {
            await OpenProjectPicker(); await WaitForProjectPickerItems("P2");
            await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == "P1"));
            await Ui.DialogReady("PlanDetailDiscard");
            await Ui.Run(() => Ui.DialogButton("PlanDetailDiscard", discard ? "PrimaryButton" : "CloseButton"));
            await Ui.Idle();
            if (!discard) await Ui.Run(() => {
                Assert.That(workspace.Session, Is.SameAs(session));
                Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("project draft"));
            });
        }
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Visibility, Is.EqualTo(Visibility.Collapsed)));
        Assert.That(BodyWrites(), Is.Empty);
    }

    [Test, Category("IssueDetail")]
    public async Task ExistingDialogPreventsSecondDiscardDialogAndKeepsDraft()
    {
        await BeginBodyEdit(); await Ui.Run(() => Ui.Find<TextBox>("PlanDetailEditor").Text = "modal draft");
        await OpenResetDialog();
        await Ui.Run(() => {
            ToggleDetail();
            Assert.That(Ui.Dialog("PlanDetailDiscard"), Is.Null);
            Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.True);
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("modal draft"));
            Ui.DialogButton("PlanResetConfirm", "CloseButton");
        });
        await Ui.Idle();
    }

    [Test, Category("IssueDetail")]
    public async Task RemoteRefreshDoesNotCancelRunningBodyPublish()
    {
        await BeginBodyEdit();
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true,"holdQuery":"PlanIssueBodyPublish"}""");
        await PublishBodyDraft("in flight");
        await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => !Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        File.WriteAllText(Path.Combine(root, "release-gh"), "");
        await Ui.Until(() => Ui.Find<TextBox>("PlanDetailEditor").Visibility == Visibility.Collapsed);
        await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        Assert.That(BodyWrites().Length, Is.EqualTo(1));
        Assert.That(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "detail-bodies.json")))!["I1"],
            Is.EqualTo("in flight\n\n<!-- ghpb-plan:fixture -->"));
    }

    [Test, Category("IssueDetail"), Category("PlanSheetNative")]
    public async Task BodyEditorNativeUndoRedoAndEscapeStayOutsidePlanHistory()
    {
        await BeginBodyEdit(); await SheetNativeInput.ActivateWindow();
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Local plan title")]), readDay);
        await Ui.Run(() => DetailSheet.Refresh());
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(1));
        var before = workspace.Session!.Document;
        await Ui.Run(() => { var editor = Ui.Find<TextBox>("PlanDetailEditor"); editor.Focus(FocusState.Programmatic); editor.SelectAll(); });
        await SheetNativeInput.Press(VirtualKey.Back);
        await Ui.Until(() => Ui.Find<TextBox>("PlanDetailEditor").Text.Length == 0);
        await SheetNativeInput.Press(VirtualKey.Z, [VirtualKey.Control]);
        await Ui.Until(() => Ui.Find<TextBox>("PlanDetailEditor").Text == "**Body 設計**");
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(1));
        await SheetNativeInput.Press(VirtualKey.Y, [VirtualKey.Control]);
        await Ui.Until(() => Ui.Find<TextBox>("PlanDetailEditor").Text.Length == 0);
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.DialogReady("PlanDetailDiscard");
        await Ui.Run(() => {
            Assert.That(workspace.Session.Document, Is.SameAs(before)); Assert.That(workspace.Session.UndoCount, Is.EqualTo(1));
            Ui.DialogButton("PlanDetailDiscard", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Find<TextBox>("PlanDetailEditor").Visibility == Visibility.Collapsed);
    }

    [Test, Category("IssueDetail"), Category("PlanSheetNative")]
    public async Task BodyEditorCtrlEnterPublishesAndCtrlIHonorsDiscardReturn()
    {
        await BeginBodyEdit(); await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => { var editor = Ui.Find<TextBox>("PlanDetailEditor"); editor.Focus(FocusState.Programmatic); editor.Text = "shortcut body"; });
        await Ui.Until(() => Ui.Find<Button>("PlanDetailPublishBody").IsEnabled);
        await SheetNativeInput.Press(VirtualKey.Enter, [VirtualKey.Control]);
        await Ui.Until(() => Ui.Find<TextBlock>("PlanDetailPublished").Visibility == Visibility.Visible);
        await Ui.Until(() => Ui.Find<Button>("PlanDetailEdit").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanDetailEdit"));
        await Ui.Ready<TextBox>("PlanDetailEditor");
        await Ui.Run(() => { var editor = Ui.Find<TextBox>("PlanDetailEditor"); editor.Focus(FocusState.Programmatic); editor.Text = "keep shortcut draft"; });
        await SheetNativeInput.Press(VirtualKey.I, [VirtualKey.Control]);
        await Ui.DialogReady("PlanDetailDiscard");
        await Ui.Run(() => Ui.DialogButton("PlanDetailDiscard", "CloseButton")); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanDetailEditor").Text, Is.EqualTo("keep shortcut draft"));
            Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.True);
            Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<TextBox>("PlanDetailEditor")));
        });
        Assert.That(BodyWrites().Length, Is.EqualTo(1));
    }

    private static void ToggleDetail()
    {
        // #275: the selection line's 詳細 is the pane's only button.
        var toggle = Ui.Find<ToggleButton>("PlanSheetDetail");
        Assert.That(toggle.IsLoaded && toggle.IsEnabled, Is.True);
        ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
    }
    private PlanSheetView DetailSheet => Ui.Tree(view).OfType<PlanSheetView>().Single();
    private void SelectDetail(string identity) => DetailSheet.Select(identity, PlanField.Title, false);
    private void DetailScenario(string? fault = null, bool hold = false, int? comments = null) =>
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, detailFault = fault, detailCommentCount = comments, holdQuery = hold ? "PlanIssueDetail" : "unused"
        }));
    private string? detailEnvironmentFolder;
    private JsonElement[] DetailCalls()
    {
        using var stream = new FileStream(Path.Combine(root, "calls.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n');
        return lines.Take(lines.Length - 1).Select(line => {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.Clone();
        }).ToArray();
    }
    private string[] DetailReads() => DetailCalls().Where(c => c.TryGetProperty("detailId", out var id) && id.ValueKind == JsonValueKind.String)
        .Select(c => c.GetProperty("detailId").GetString()!).ToArray();
    private async Task DetailFallback()
    {
        detailEnvironmentFolder = null;
        await Ui.Run(() => view.DetailEnvironmentEdge = folder => {
            detailEnvironmentFolder = folder;
            throw new InvalidOperationException("Unavailable fixture runtime");
        });
    }
    private void SeedDetailRows() => FakePlanEditor.Save(root, new([
        new(new("I1", "設計", "acme/repo"), "", true),
        new(new("I2", "実装", "acme/repo") { Parent = "I1" }, "", true),
        new(new("I3", "確認", "acme/repo"), "", true)
    ], 4));
    private T? DetailElement<T>(string id) where T : FrameworkElement =>
        Ui.Tree(view).OfType<T>().FirstOrDefault(element => element.IsLoaded && AutomationProperties.GetAutomationId(element) == id);
    private Task DetailReady(string title = "設計") => Ui.Until(() =>
        DetailElement<TextBlock>("PlanDetailTitle")?.Text == title &&
        DetailElement<ScrollViewer>("PlanDetailMarkdown") is { Visibility: Visibility.Visible } markdown &&
        Ui.Tree(markdown).OfType<TextBlock>().Any(t => t.Text == "**Body " + title + "**"));
    private async Task<string> DetailWebText(string expected)
    {
        await Ui.Until(() => DetailElement<WebView2>("PlanDetailBody") is { CoreWebView2: not null, Visibility: Visibility.Visible });
        var text = "";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true) {
            await Ui.Run(async () => text = JsonSerializer.Deserialize<string>(
                await Ui.Find<WebView2>("PlanDetailBody").CoreWebView2.ExecuteScriptAsync("document.body.innerText"))!);
            if (text.Contains(expected)) return text;
            if (DateTime.UtcNow >= deadline) Assert.Fail("The Issue document did not render: " + expected);
            await Task.Delay(20);
        }
    }

    [TestCase(false), TestCase(true), Category("IssueDetail")]
    public async Task DetailHeaderGroupsIdentityAndStatusAsSecondaryMetadata(bool closed)
    {
        var title = string.Join(" ", Enumerable.Repeat("R24 設計レビューと担当者の作業計画を確認する", 4));
        var fake = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, fake with { Issues = [fake.Issues[0] with { Row = fake.Issues[0].Row with { Title = title } }] });
        await Open(title); await DetailFallback();
        // The header state comes from the Issue read; a closed plan row would change what the open itself shows.
        if (closed) DetailScenario("closed");
        await Ui.Run(() => { ResizeWorkspace(1920, 1032); SelectDetail("I1"); ToggleDetail(); });
        await DetailReady(title);
        await Ui.Until(() => Ui.Find<TextBlock>("PlanDetailUpdated").ActualWidth > 0 && Ui.Find<TextBlock>("PlanDetailTitle").ActualHeight > 20);
        await Ui.Run(async () => {
            var id = Ui.Find<TextBlock>("PlanDetailId"); var reference = Ui.Find<TextBlock>("PlanDetailReference");
            var heading = Ui.Find<TextBlock>("PlanDetailTitle"); var state = Ui.Find<TextBlock>("PlanDetailState");
            var updated = Ui.Find<TextBlock>("PlanDetailUpdated"); var open = Ui.Find<Button>("PlanDetailOpen");
            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(view)
                .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            // owner/repo#number already names a published task (#263), so the line carries nothing else.
            Assert.That(id.Text, Is.Empty); Assert.That(reference.Text, Is.EqualTo("acme/repo#1"));
            var separator = Ui.Tree(view).OfType<TextBlock>().Single(t => t.Text == "·" && t.Parent == id.Parent);
            Assert.That(separator.Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(state.Text, Is.EqualTo(closed ? "クローズ" : "オープン"));
            foreach (var text in new[] { id, reference, state, updated }) {
                Assert.That(text.FontSize, Is.EqualTo(12));
                Assert.That(((SolidColorBrush)text.Foreground).Color,
                    Is.EqualTo(((SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]).Color));
                Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(text).GetName(), Is.EqualTo(text.Text));
            }
            Assert.That(heading.FontWeight, Is.EqualTo(Microsoft.UI.Text.FontWeights.SemiBold));
            Assert.That(heading.TextWrapping, Is.EqualTo(TextWrapping.Wrap)); Assert.That(heading.MaxLines, Is.EqualTo(3));
            Assert.That(Bounds(reference).Top, Is.EqualTo(Bounds(id).Top).Within(1));
            Assert.That(Bounds(reference).Left, Is.GreaterThanOrEqualTo(Bounds(id).Right));
            Assert.That(Bounds(updated).Top, Is.EqualTo(Bounds(state).Top).Within(1));
            Assert.That(Bounds(updated).Left, Is.GreaterThan(Bounds(state).Right));
            Assert.That(Bounds(heading).Top, Is.GreaterThanOrEqualTo(Bounds(id).Bottom));
            Assert.That(Bounds(state).Top, Is.GreaterThanOrEqualTo(Bounds(heading).Bottom));
            Assert.That(Bounds(open).Top, Is.GreaterThanOrEqualTo(Bounds(state).Bottom));
            await RenderedEvidence.Capture(view, "issue-detail-header-" + (closed ? "closed" : "open"));
        });
    }

    [TestCase(1, "コメント 1 件", true), TestCase(5, "コメント 5 件", true), TestCase(6, "コメント 6 件（最新 5 件）", true)]
    [TestCase(1, "コメント 1 件", false), TestCase(5, "コメント 5 件", false), TestCase(6, "コメント 6 件（最新 5 件）", false), Category("IssueDetail")]
    public async Task DetailCommentHeadingExplainsTheDisplayedCount(int count, string expected, bool fallback)
    {
        await Open(); if (fallback) await DetailFallback(); DetailScenario(comments: count);
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        if (fallback) {
            await DetailReady();
            await Ui.Until(() => Ui.Tree(Ui.Find<ScrollViewer>("PlanDetailMarkdown")).OfType<TextBlock>().Any(text => text.Text == expected));
        } else {
            await DetailWebText("Body 設計");
            await Ui.Run(async () => Assert.That(JsonSerializer.Deserialize<string>(await Ui.Find<WebView2>("PlanDetailBody")
                .CoreWebView2.ExecuteScriptAsync("document.querySelector('h2').innerText")), Is.EqualTo(expected)));
        }
    }

    [Test, Category("IssueDetail")]
    public async Task DetailAvailabilitySessionStateAndAssistShareOneDock()
    {
        await Ui.Run(() => Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Collapsed)));
        await Open(); await DetailFallback();
        await Ui.Run(() => {
            SelectDetail("I1");
            Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.False);
            ToggleDetail();
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Visible));
            ToggleAssist();
            Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.False);
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Collapsed));
            ToggleDetail();
            Assert.That(Ui.Find<ToggleButton>("PlanAssist").IsChecked, Is.False);
            Assert.That(Ui.Find<Border>("PlanAssistPane").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("PlanShowSummary");
        });
        await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowSummary").IsSelected);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Visible);
        await Settings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Collapsed));
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Visible);
    }

    [Test, Category("IssueDetail")]
    public async Task DetailEmptyAndLocalRowsSendNoReadAndSelectionNeverOpensPane()
    {
        await Open(); await DetailFallback();
        var row = PlanRow.New("Local task", "acme/repo");
        await workspace.Session!.Execute(new InsertPlanRows([row]), readDay);
        await Ui.Run(() => {
            DetailSheet.Refresh(); SelectDetail("I1");
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Collapsed));
            ToggleDetail(); SelectDetail("");
        });
        await Ui.Until(() => DetailElement<TextBlock>("PlanDetailTitle") is not null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanDetailEmpty").Text, Is.EqualTo("行を選ぶと、その Issue の本文とコメントを表示します"));
            SelectDetail(row.Identity);
            Assert.That(Ui.Find<TextBlock>("PlanDetailTitle").Text, Is.EqualTo("Local task"));
            Assert.That(Ui.Find<TextBlock>("PlanDetailLocal").Text, Is.EqualTo("GitHub にまだ作成されていないタスクです。発行すると本文を表示できます"));
        });
        await Task.Delay(700);
        await Ui.Run(() => {
            var title = Ui.Find<TextBlock>("PlanDetailTitle"); var open = Ui.Find<Button>("PlanDetailOpen");
            Assert.That(Ui.Find<TextBlock>("PlanDetailId").Text, Is.EqualTo("新1"));
            Assert.That(Ui.Find<TextBlock>("PlanDetailReference").Text, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("PlanDetailState").Text, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("PlanDetailUpdated").Text, Is.Empty);
            var gap = open.TransformToVisual(view).TransformPoint(new()).Y - title.TransformToVisual(view).TransformPoint(new()).Y - title.ActualHeight;
            Assert.That(gap, Is.InRange(0, 8), "Local tasks have no empty status line above their actions.");
        });
        Assert.That(DetailReads(), Is.Empty);
        Assert.That(detailEnvironmentFolder, Is.Null, "No renderer is created for a row without remote content.");
        await Ui.Run(() => {
            ToggleDetail(); SelectDetail("I1");
            Assert.That(Ui.Find<Border>("PlanDetailPane").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    [Test, Category("IssueDetail")]
    public async Task DetailDebouncesSelectionCachesReloadsAndRefreshClears()
    {
        SeedDetailRows(); await Open(); await DetailFallback();
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); SelectDetail("I2"); SelectDetail("I3"); });
        await DetailReady("確認");
        Assert.That(DetailReads(), Is.EqualTo(new[] { "I3" }));
        await Ui.Run(() => SelectDetail("I2")); await DetailReady("実装");
        await Ui.Run(() => { SelectDetail("I3"); SelectDetail("I2"); SelectDetail("I3"); }); await DetailReady("確認");
        Assert.That(DetailReads(), Is.EqualTo(new[] { "I3", "I2" }));
        await Ui.Run(() => Ui.Click("PlanDetailReload"));
        await Ui.Until(() => DetailReads().Length == 3); await DetailReady("確認");
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        await Ui.Until(() => DetailReads().Length == 4); await DetailReady("確認");
        Assert.That(DetailReads().Last(), Is.EqualTo("I3"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase("reset"), TestCase("reopen"), TestCase("switch"), TestCase("reconnect"), Category("IssueDetail")]
    public async Task DetailCacheDoesNotSurviveProjectOrConnectionReload(string action)
    {
        await Open(); await DetailFallback();
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); }); await DetailReady();
        Assert.That(DetailReads().Length, Is.EqualTo(1));
        if (action == "reset") {
            await OpenResetDialog();
            await Ui.Run(() => Ui.DialogButton("PlanResetConfirm", "PrimaryButton"));
            await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshResult").IsOpen && Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        } else if (action == "reconnect") {
            await PickerCommand("PlanConnection");
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected);
        } else {
            await PickerCommand("PlanChooseProject");
            if (action == "switch") await ProjectChooserTestActions.SelectOwner("acme");
            await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
            await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected);
        }
        if (action == "switch") {
            await Ui.Until(() => workspace.Session?.Document.Project.NodeId == "P2" &&
                DetailSheet.Session == workspace.Session && DetailElement<TextBlock>("PlanDetailEmpty")?.Visibility == Visibility.Visible);
            await Ui.Run(() => {
                Assert.That(DetailSheet.IsSelectedRow(""), Is.True, "A newly opened Project starts on the input row.");
                Assert.That(DetailElement<TextBlock>("PlanDetailTitle")?.Text, Is.Empty);
                Assert.That(DetailReads(), Is.EqualTo(new[] { "I1" }), "No Issue read is sent until a row is selected.");
                // Both fixture Projects contain I1; its second read proves the shared node's cache was cleared.
                SelectDetail("I1");
            });
        }
        await DetailReady();
        Assert.That(DetailReads(), Is.EqualTo(new[] { "I1", "I1" }));
        Assert.That(DetailCalls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test, Category("IssueDetail")]
    public async Task RefreshCancelsHeldDetailReadBeforeTakingConnectionGate()
    {
        await Open(); await DetailFallback(); DetailScenario(hold: true);
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        var marker = Path.Combine(root, "held-gh.pid");
        int pid = 0;
        await Ui.Until(() => { try { return File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out pid); } catch (IOException) { return false; } });
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        DetailScenario();
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        try {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled);
        }
        finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await DetailReady();
        Assert.That(DetailCalls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test, Category("IssueDetail")]
    public async Task DetailLoadingIsDelayedAndObsoleteReadCannotReplaceNewSelection()
    {
        SeedDetailRows(); await Open(); await DetailFallback(); DetailScenario(hold: true);
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); Assert.That(Ui.Find<TextBlock>("PlanDetailLoading").Visibility, Is.EqualTo(Visibility.Collapsed)); });
        await Ui.Until(() => DetailReads().Length == 1);
        await Ui.Until(() => DetailElement<TextBlock>("PlanDetailLoading")?.Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<SplitButton>("PlanRefresh").IsEnabled, Is.True);
            SelectDetail("I2");
            Assert.That(Ui.Find<TextBlock>("PlanDetailLoading").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        DetailScenario();
        await DetailReady("実装");
        Assert.That(DetailReads(), Is.EqualTo(new[] { "I1", "I2" }));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanDetailTitle").Text, Is.EqualTo("実装")));
    }

    [Test, Category("IssueDetail")]
    public async Task DetailFailureRetryAndFallbackKeepPlanningAvailable()
    {
        await Open(); await DetailFallback(); DetailScenario("missing");
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        await Ui.Until(() => DetailElement<InfoBar>("PlanDetailFailure")?.IsOpen == true &&
            DetailElement<TextBlock>("PlanDetailFailureText") is not null && DetailElement<Button>("PlanDetailRetry")?.IsEnabled == true);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanDetailFailureText").Text, Is.EqualTo("Issue が見つからないか、アクセスできません"));
            Assert.That(Ui.Find<Button>("PlanDetailRetry").IsEnabled, Is.True);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsEnabled, Is.True);
        });
        DetailScenario();
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanDetailRetry").Focus(FocusState.Programmatic), Is.True);
            Ui.Click("PlanDetailRetry");
            Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<Button>("PlanDetailReload")));
        });
        await DetailReady();
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("PlanDetailFailure").IsOpen, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanDetailFallbackNote").Text, Does.Contain("Markdown のまま"));
            Assert.That(detailEnvironmentFolder, Is.EqualTo(Path.Combine(root, "PlanningEditor", "v1", "webview2")));
            var texts = Ui.Tree(Ui.Find<ScrollViewer>("PlanDetailMarkdown")).OfType<TextBlock>().ToArray();
            Assert.That(texts.Any(t => t.Text == "Comment 5" && t.IsTextSelectionEnabled), Is.True);
            Assert.That(texts.Any(t => t.Text == "コメント 7 件（最新 5 件）"), Is.True);
            Assert.That(texts.Any(t => t.Text == "Comment 7") && !texts.Any(t => t.Text == "Comment 2"), Is.True, "The newest five comments are shown.");
            Assert.That(Ui.Tree(Ui.Find<ScrollViewer>("PlanDetailMarkdown")).OfType<HyperlinkButton>(), Is.Empty);
            Assert.That(texts.Any(t => t.Text.StartsWith("ghost · ")), Is.True);
            Assert.That(texts.Where(t => t.IsTextSelectionEnabled).All(t => t.TextWrapping == TextWrapping.Wrap), Is.True);
        });
    }

    [TestCase(false), TestCase(true, Category = "PlanSheetNative"), Category("IssueDetail")]
    public async Task DetailActionsPreservePendingInputDocumentHistoryAndUnpublishedCount(bool keyboard)
    {
        await Open(); await DetailFallback();
        var session = workspace.Session!;
        var before = session.Document;
        var writes = Directory.GetFiles(Path.Combine(root, "PlanningEditor"), "*.json", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.GetLastWriteTimeUtc);
        var external = new List<Uri>();
        await Ui.Run(() => {
            view.DetailExternalOpen = uri => { external.Add(uri); return Task.CompletedTask; };
            SelectDetail("I1"); ToggleDetail();
        });
        await DetailReady();
        if (keyboard) await PrepareDetailKeyboard();
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title");
            cell.Focus(FocusState.Programmatic); cell.Text = "Still pending";
            Assert.That(Ui.Find<Button>("PlanDetailReload").Focus(FocusState.Programmatic), Is.True);
            if (!keyboard) Ui.Click("PlanDetailReload");
        });
        if (keyboard) await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Run(AssertPendingDetailReloadFocus);
        await Ui.Until(() => DetailReads().Length == 2); await DetailReady();
        await Ui.Run(AssertPendingDetailReloadFocus);
        await Ui.Run(() => {
            Ui.Click("PlanDetailOpen");
            ToggleDetail(); ToggleDetail();
            Assert.That(DetailSheet.Pending[("I1", PlanField.Title)].Text, Is.EqualTo("Still pending"));
            Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 0 タスク"));
        });
        Assert.That(session.Document, Is.SameAs(before));
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(session.RedoCount, Is.Zero);
        Assert.That(external.Single().AbsoluteUri, Is.EqualTo("https://github.com/acme/repo/issues/1"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        Assert.That(DetailCalls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
        foreach (var (path, modified) in writes) Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(modified));
        await Ui.Run(() => DetailSheet.CancelEdit("I1", PlanField.Title));

        void AssertPendingDetailReloadFocus()
        {
            Assert.That(DetailSheet.Pending.GetValueOrDefault(("I1", PlanField.Title))?.Text, Is.EqualTo("Still pending"));
            Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<Button>("PlanDetailReload")));
            Assert.That(Ui.Find<Button>("PlanDetailReload").IsEnabled, Is.True);
            Assert.That(Ui.Find<Button>("PlanDetailOpen").IsEnabled, Is.True);
            Assert.That(session.Document, Is.SameAs(before));
        }
    }

    [Test, Category("IssueDetail")]
    public async Task DetailWebViewRendersAndLinksOpenExternallyWithoutNavigatingDocument()
    {
        SeedDetailRows(); await Open();
        var external = new List<Uri>();
        await Ui.Run(() => {
            view.DetailExternalOpen = uri => { external.Add(uri); return Task.CompletedTask; };
            var create = view.DetailEnvironmentEdge;
            view.DetailEnvironmentEdge = folder => { detailEnvironmentFolder = folder; return create(folder); };
            SelectDetail("I1"); ToggleDetail();
        });
        var text = await DetailWebText("Body 設計");
        await Ui.Run(async () => {
            var web = Ui.Find<WebView2>("PlanDetailBody");
            var cell = Ui.Find<TextBox>("PlanCell1_Title");
            cell.Focus(FocusState.Programmatic); cell.Text = "Pending while reading";
            Assert.That(web.Focus(FocusState.Programmatic), Is.True);
            Assert.That(web.CoreWebView2.Settings.IsScriptEnabled, Is.False);
            Assert.That(web.CoreWebView2.Settings.AreDevToolsEnabled, Is.False);
            Assert.That(web.CoreWebView2.Settings.IsWebMessageEnabled, Is.False);
            Assert.That(web.CoreWebView2.Settings.AreHostObjectsAllowed, Is.False);
            Assert.That(web.CoreWebView2.Settings.IsStatusBarEnabled, Is.False);
            Assert.That(detailEnvironmentFolder, Is.EqualTo(Path.Combine(root, "PlanningEditor", "v1", "webview2")));
            Assert.That(JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('meta[http-equiv]').content")),
                Is.EqualTo("default-src 'none'; img-src https: data:; style-src 'unsafe-inline'"));
            Assert.That(text, Does.Contain("コメント 7 件（最新 5 件）").And.Contain("Comment 7").And.Contain("ghost").And.Not.Contain("すべてのコメント"));
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-link').click()");
        });
        await Ui.Until(() => external.Count == 1);
        await Ui.Run(async () => {
            var web = Ui.Find<WebView2>("PlanDetailBody");
            Assert.That(JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.body.innerText")), Is.EqualTo(text));
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-new').click()");
        });
        await Ui.Until(() => external.Count == 2);
        await Ui.Run(async () => await Ui.Find<WebView2>("PlanDetailBody").CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-relative').click()"));
        await Ui.Until(() => external.Count == 3);
        Assert.That(external[2].AbsoluteUri, Is.EqualTo("https://github.com/acme/repo/issues/2"));
        await Ui.Run(async () => await Ui.Find<WebView2>("PlanDetailBody").CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-fragment').click()"));
        await Ui.Until(() => external.Count == 4);
        Assert.That(external[3].AbsoluteUri, Is.EqualTo("https://github.com/acme/repo/issues/1#section"));
        await Ui.Run(async () => {
            var web = Ui.Find<WebView2>("PlanDetailBody");
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('fixture-other').click()");
            Assert.That(JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.body.innerText")), Is.EqualTo(text));
            await RenderedEvidence.Capture(view, "issue-detail-rendered");
        });
        Assert.That(external.All(u => u.Scheme == "https"), Is.True);
        Assert.That(external.Count, Is.EqualTo(4));
        await Ui.Run(() => {
            Assert.That(DetailSheet.Pending[("I1", PlanField.Title)].Text, Is.EqualTo("Pending while reading"));
            Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.EqualTo("設計"));
            Assert.That(workspace.Session.UndoCount, Is.Zero);
            DetailSheet.CancelEdit("I1", PlanField.Title);
            SelectDetail("I2");
            Assert.That(Ui.Find<WebView2>("PlanDetailBody").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        Assert.That(await DetailWebText("Body 実装"), Does.Not.Contain("Body 設計"));
        Assert.That(DetailCalls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [Test, Category("IssueDetail")]
    public async Task DetailFailedNavigationFallsBackAndReloadCanRenderAgain()
    {
        await Open();
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        await DetailWebText("Body 設計");
        void CancelNavigation(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;
        await Ui.Run(() => {
            Ui.Find<WebView2>("PlanDetailBody").CoreWebView2.NavigationStarting += CancelNavigation;
            Ui.Click("PlanDetailReload");
        });
        await DetailReady();
        await Ui.Run(() => {
            var web = Ui.Find<WebView2>("PlanDetailBody");
            Assert.That(web.Visibility, Is.EqualTo(Visibility.Collapsed));
            web.CoreWebView2.NavigationStarting -= CancelNavigation;
            Ui.Click("PlanDetailReload");
        });
        Assert.That(await DetailWebText("Body 設計"), Does.Contain("Comment 5"));
        await Ui.Run(() => Assert.That(Ui.Find<ScrollViewer>("PlanDetailMarkdown").Visibility, Is.EqualTo(Visibility.Collapsed)));
        Assert.That(DetailCalls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
    }

    [TestCase("empty", true), TestCase("marker", true), TestCase("empty", false), TestCase("marker", false), Category("IssueDetail")]
    public async Task DetailDistinguishesEmptyBodyFromNoComments(string scenario, bool fallback)
    {
        await Open();
        if (fallback) await DetailFallback();
        DetailScenario(scenario);
        await Ui.Run(() => { SelectDetail("I1"); ToggleDetail(); });
        if (fallback) {
            await Ui.Until(() => DetailElement<ScrollViewer>("PlanDetailMarkdown") is { Visibility: Visibility.Visible } markdown &&
                Ui.Tree(markdown).OfType<TextBlock>().Any(t => t.Text == "本文はありません"));
            await Ui.Run(() => {
                var texts = Ui.Tree(Ui.Find<ScrollViewer>("PlanDetailMarkdown")).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Assert.That(texts, Does.Contain("本文はありません").And.Contain("コメント").And.Contain("コメントはありません"));
                Assert.That(string.Join('\n', texts), Does.Not.Contain("ghpb-plan").And.Not.Contain("fixture marker"));
            });
        } else {
            Assert.That(await DetailWebText("本文はありません"), Does.Contain("コメントはありません").And.Not.Contain("ghpb-plan"));
            await Ui.Run(async () => Assert.That(JsonSerializer.Deserialize<string>(await Ui.Find<WebView2>("PlanDetailBody")
                .CoreWebView2.ExecuteScriptAsync("document.querySelector('h2').innerText")), Is.EqualTo("コメント")));
        }
        await Ui.Until(() => DetailElement<TextBlock>("PlanDetailState")?.Text == "オープン");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanDetailState").Text, Is.EqualTo("オープン"));
            Assert.That(Ui.Find<TextBlock>("PlanDetailUpdated").Text, Is.EqualTo("最終更新 " + DateTimeOffset.Parse("2026-10-15T08:30:00Z")
                .ToLocalTime().ToString("M/d H:mm", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"))));
        });
    }

    [TestCase(false), TestCase(true), Category("IssueDetail"), Category("PlanSheetNative")]
    public async Task DetailKeyboardCyclesThreeRegionsAndCtrlIRespectsEditing(bool reverse)
    {
        await Open(); await DetailFallback();
        await PrepareDetailKeyboard();
        await SheetNativeInput.Press(VirtualKey.I, [VirtualKey.Control]);
        await DetailReady();
        await Ui.Run(() => {
            Assert.That(DetailSheet.HasActiveCellEdit, Is.False, "Ctrl+I must not start an edit or IME composition.");
            Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<PlanSheetCell>("PlanCell1_Title")));
            Assert.That(workspace.Session!.UndoCount, Is.Zero);
        });
        foreach (var id in reverse ? new[] { "PlanDetailOpen", "PlanShowTasks", "PlanCell1_Title" }
                                   : new[] { "PlanShowTasks", "PlanDetailOpen", "PlanCell1_Title" }) {
            await SheetNativeInput.Press(VirtualKey.F6, reverse ? [VirtualKey.Shift] : []);
            await Ui.Run(() => Assert.That(FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<FrameworkElement>(id))));
        }
        await SheetNativeInput.Press(VirtualKey.F2, []);
        await SheetNativeInput.Press(VirtualKey.I, [VirtualKey.Control]);
        await Ui.Run(() => Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.True));
        await SheetNativeInput.Press(VirtualKey.Escape, []);
        await SheetNativeInput.Press(VirtualKey.I, [VirtualKey.Control]);
        await Ui.Run(() => Assert.That(Ui.Find<ToggleButton>("PlanSheetDetail").IsChecked, Is.False));
    }

    [TestCase(false), TestCase(true), Category("IssueDetail"), Category("PlanSheetNative")]
    public async Task DetailBodyTabReturnsToHeaderForRegionShortcutsAndClose(bool reverse)
    {
        await Open(); await PrepareDetailKeyboard();
        var before = workspace.Session!.Document;
        await Ui.Run(ToggleDetail);
        await DetailWebText("Body 設計");

        await LeaveBody();
        await SheetNativeInput.Press(VirtualKey.F6, reverse ? [VirtualKey.Shift] : []);
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(view.XamlRoot),
            Ui.Find<FrameworkElement>(reverse ? "PlanShowTasks" : "PlanCell1_Title")));
        await LeaveBody();
        await SheetNativeInput.Press(VirtualKey.I, [VirtualKey.Control]);
        await Ui.Until(() => Ui.Find<Border>("PlanDetailPane").Visibility == Visibility.Collapsed &&
            ReferenceEquals(FocusManager.GetFocusedElement(view.XamlRoot), Ui.Find<PlanSheetCell>("PlanCell1_Title")));
        await Ui.Run(() => {
            Assert.That(workspace.Session.Document, Is.SameAs(before));
            Assert.That(workspace.Session.UndoCount, Is.Zero);
            Assert.That(DetailSheet.Pending, Is.Empty);
        });
        Assert.That(DetailCalls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);

        async Task LeaveBody()
        {
            await Ui.Run(async () => {
                var web = Ui.Find<WebView2>("PlanDetailBody");
                Assert.That(web.Focus(FocusState.Programmatic), Is.True);
                Assert.That(web.CoreWebView2.Settings.IsScriptEnabled, Is.False);
                // Only position focus at a document boundary; the key itself uses native input.
                await web.CoreWebView2.ExecuteScriptAsync(reverse
                    ? "document.querySelector('a[href]').focus()"
                    : "Array.from(document.querySelectorAll('a[href]')).at(-1).focus()");
            });
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true) {
                var focused = false;
                await Ui.Run(async () => focused = await Ui.Find<WebView2>("PlanDetailBody").CoreWebView2
                    .ExecuteScriptAsync("document.hasFocus() && document.activeElement.tagName === 'A'") == "true");
                if (focused) break;
                if (DateTime.UtcNow >= deadline) Assert.Fail("The rendered Issue body did not receive native focus.");
                await Task.Delay(20);
            }
            // Browser key-up need not bubble to the XAML root. Wait for the resulting native focus instead.
            if (reverse) SheetNativeInput.Key(VirtualKey.Shift, true);
            try { SheetNativeInput.Key(VirtualKey.Tab, true); SheetNativeInput.Key(VirtualKey.Tab, false); }
            finally { if (reverse) SheetNativeInput.Key(VirtualKey.Shift, false); }
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(view.XamlRoot),
                Ui.Find<Button>(reverse ? "PlanDetailEdit" : "PlanDetailOpen")));
        }
    }

    private async Task PrepareDetailKeyboard()
    {
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => { SelectDetail("I1"); DetailSheet.FocusSelected(); });
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(view.XamlRoot), Ui.Find<PlanSheetCell>("PlanCell1_Title")));
        // A prior native editor can leave an IME composition in the host's input context.
        // These shortcut cases require selection mode; F6 during composition belongs to the IME.
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => !Ui.Find<PlanSheetCell>("PlanCell1_Title").Composing);
        // The IME can consume the first Escape; the next one exits the cell editor itself.
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => !DetailSheet.HasActiveCellEdit);
        await Ui.Run(() => Assert.That(DetailSheet.Pending, Is.Empty));
    }
}
