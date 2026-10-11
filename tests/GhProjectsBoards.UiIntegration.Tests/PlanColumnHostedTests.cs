using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    [Test]
    public async Task ColumnSettingsNavigateRenameMoveHideResetAndUndoRenderedHeaders()
    {
        await Open();
        var session=workspace.Session!;
        var undo=session.UndoCount;
        await Ui.ClickCommand("PlanSheetColumns");
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
        await Ui.ClickCommand("PlanSheetColumns");
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
        await Ui.ClickCommand("PlanSheetColumns");
        await Ui.Idle();
        await Ui.Run(()=> {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text,Is.EqualTo("invalid"));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanColumnNameTitle" && t.IsLoaded && t.Visibility==Visibility.Visible),Is.False);
        });
    }
}

internal sealed partial class PlanSheetHostedTests
{
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
