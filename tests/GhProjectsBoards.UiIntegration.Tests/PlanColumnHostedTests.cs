using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    private async Task OpenColumnSettingsThroughOverflow()
    {
        await Ui.Run(() => Ui.Click("PlanSheetMore"));
        await Ui.Ready<MenuFlyoutItem>("PlanSheetColumns");
        await Ui.Run(() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(
            Ui.Popup<MenuFlyoutItem>("PlanSheetColumns")!).GetPattern(PatternInterface.Invoke)).Invoke());
    }

    [Test]
    public async Task OverflowOpensTheCompactColumnsSectionAndItsAccessibleCommands()
    {
        await Open();
        await OpenColumnSettingsThroughOverflow();
        await Ui.Ready<TextBlock>("PlanColumnSettings");
        await Ui.Until(() => {
            var title = Ui.Find<TextBlock>("PlanColumnSettings");
            var viewport = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            var y = title.TransformToVisual(viewport).TransformPoint(new()).Y;
            return y >= 0 && y + title.ActualHeight <= viewport.ViewportHeight;
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanColumnSettings").Text,Is.EqualTo("計画シートの列"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnIntro").Text,Is.EqualTo("表示する列、順序（ドラッグ）、表示名を選びます。このプロジェクトのフィールドはすべて選べます。編集できる列は、計画シートで編集して発行します。"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnFootnote").Text,Is.EqualTo("見出しにマウスを置くと GitHub のフィールド名を表示します。リポジトリは移すと Issue の移管になるため、読み取りのみです。"));
            var input = Ui.Find<TextBox>("PlanColumnNameTitle");
            Assert.That(input.Header,Is.Null);
            Assert.That(input.PlaceholderText,Is.EqualTo("タスク名"));
            Assert.That(AutomationProperties.GetName(input),Is.EqualTo("タスク名 の表示名"));
            Assert.That(AutomationProperties.GetName(Ui.Find<CheckBox>("PlanColumnEstimate")),Is.EqualTo("見積 h を表示"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanColumnUpTitle")),Is.EqualTo("タスク名 を上へ"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanColumnDownTitle")),Is.EqualTo("タスク名 を下へ"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnTypeEstimate").Text,Is.EqualTo("数値"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnModeIndicator").Text,Is.EqualTo("計算"));
            Assert.That(Ui.Find<TextBlock>("PlanColumnModeRepository").Text,Is.EqualTo("読み取りのみ"));
        });
        double before = 0;
        double backY = 0;
        await Ui.Run(() => {
            var back = Ui.Find<Button>("PlanSettingsBack");
            Assert.That(back.IsLoaded && back.IsEnabled, Is.True);
            backY = back.TransformToVisual(view).TransformPoint(new()).Y;
            var viewport = Ui.Find<ScrollViewer>("PlanSettingsScroll");
            Assert.That(backY, Is.GreaterThanOrEqualTo(0));
            Assert.That(backY + back.ActualHeight, Is.LessThanOrEqualTo(viewport.TransformToVisual(view).TransformPoint(new()).Y));
            before = Ui.Find<ScrollViewer>("PlanSettingsScroll").VerticalOffset;
            Ui.Find<TextBox>("PlanColumnNameRepository").Focus(FocusState.Keyboard);
            Ui.Find<TextBox>("PlanColumnNameRepository").StartBringIntoView(new() { AnimationDesired = false });
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSettingsScroll").VerticalOffset > before);
        await Ui.Run(() => {
            var back = Ui.Find<Button>("PlanSettingsBack");
            Assert.That(back.TransformToVisual(view).TransformPoint(new()).Y, Is.EqualTo(backY).Within(1));
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
    }
    [Test]
    public async Task ColumnSettingsNavigateRenameMoveHideResetAndUndoRenderedHeaders()
    {
        await Open();
        var session=workspace.Session!;
        var undo=session.UndoCount;
        await OpenColumnSettingsThroughOverflow();
        await Ui.Ready<TextBox>("PlanColumnNameTitle");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<CheckBox>().Any(c => AutomationProperties.GetAutomationId(c) is "PlanColumnId" or "PlanColumnTitle"),Is.False);
            Assert.That(Ui.Find<TextBox>("PlanColumnNameTitle").PlaceholderText,Is.EqualTo("タスク名"));
        });
        await CommitText("PlanColumnNameTitle","作業名");
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Any(c=>c.Key==PlanField.Title && c.DisplayName=="作業名"));
        Assert.That(session.UndoCount,Is.EqualTo(undo+1));
        await Ui.Run(() => Ui.Click("PlanColumnUpTitle"));
        await Ui.Until(() => session.Document.State.Settings.SheetColumns[1].Key==PlanField.Title);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("PlanColumnEstimate")));
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.Any(c=>c.Key==PlanField.Estimate && !c.Visible));
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Text,Is.EqualTo("作業名"));
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBlock>("PlanHeaderTitle")),Does.Contain("Title"));
            Assert.That(Ui.Tree(view).OfType<TextBlock>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanHeaderEstimate"),Is.False);
            var title=Ui.Find<TextBox>("PlanCell1_Title");
            Assert.That(title.TransformToVisual(view).TransformPoint(new()).X,Is.LessThan(Ui.Find<FontIcon>("PlanIndicator1").TransformToVisual(view).TransformPoint(new()).X));
        });
        await OpenColumnSettingsThroughOverflow();
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
        await CommitText("PlanCell1_Remaining","invalid");
        var before = PlanJson.Text(workspace.Session!.Document);
        var undo = workspace.Session.UndoCount;
        await OpenColumnSettingsThroughOverflow();
        await Ui.Idle();
        await Ui.Run(()=> {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text,Is.EqualTo("invalid"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").IsLoaded,Is.True);
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanColumnNameTitle" && t.IsLoaded && t.Visibility==Visibility.Visible),Is.False);
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
    }
}

internal sealed partial class PlanSheetHostedTests
{
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
        await Ui.Until(()=>Ui.Find<TextBox>("PlanCell2_Project:X").Text=="メモ");
        await Ui.Run(async ()=>await sheet.KeyboardCommand(Windows.System.VirtualKey.Delete));
        await Ui.Until(()=>Ui.Find<TextBox>("PlanCell2_Project:X").Text=="");
        await Ui.Run(async ()=>await sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Until(()=>Ui.Find<TextBox>("PlanCell2_Project:X").Text=="メモ");
        await Ui.Run(()=>Assert.That(Ui.Find<TextBlock>("PlanHeaderProject:X").Text,Is.EqualTo("Notes")));
    }
}
