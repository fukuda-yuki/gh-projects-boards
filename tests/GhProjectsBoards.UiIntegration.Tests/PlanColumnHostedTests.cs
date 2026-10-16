using System.Collections.Immutable;
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
    [Test]
    public async Task ColumnWidthSettingClearsRejectsAndResetsWithoutChangingOtherSettings()
    {
        await Open();
        var session = workspace.Session!;
        await OpenColumnSettingsThroughOverflow();
        await Ui.Ready<TextBox>("PlanColumnWidthTitle");
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanColumnWidthTitle");
            Assert.That(input.Header, Is.Null);
            Assert.That(input.PlaceholderText, Is.EqualTo("224"));
            Assert.That(AutomationProperties.GetName(input), Is.EqualTo("タスク名 の幅"));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => AutomationProperties.GetAutomationId(t) == "PlanColumnWidthIndicator"), Is.False);
            var list = Ui.Find<ListView>("PlanColumnList"); var down = Ui.Find<Button>("PlanColumnDownTitle");
            Assert.That(down.TransformToVisual(list).TransformPoint(new(down.ActualWidth, 0)).X, Is.LessThanOrEqualTo(list.ActualWidth), "The row's last command stays inside the settings table.");
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
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth == 224);
        await OpenColumnSettingsThroughOverflow();
        await Ui.Ready<TextBox>("PlanColumnWidthTitle");
        await CommitText("PlanColumnWidthTitle", "320");
        await Ui.Until(() => PlanSheetColumns.All(session.Document).Single(c => c.Key == "Title").Width == 320);
        var beforeReset = session.UndoCount;
        await Ui.Run(() => Ui.Click("PlanColumnReset"));
        await Ui.Until(() => session.Document.State.Settings.SheetColumns.IsEmpty);
        Assert.That(session.UndoCount, Is.EqualTo(beforeReset + 1));
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Ready<TextBlock>("PlanHeaderTitle");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth == 224);
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth == 320);
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
            Assert.That(Ui.Find<TextBlock>("PlanColumnIntro").Text,Is.EqualTo("表示する列、順序（ドラッグ）、表示名、幅を選びます。このプロジェクトのフィールドはすべて選べます。編集できる列は、計画シートで編集して発行します。"));
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
        // Returning re-renders the header after the plan is loaded again.
        await Ui.Until(() => !Ui.Tree(view).OfType<TextBlock>().Any(t=>AutomationProperties.GetAutomationId(t)=="PlanHeaderEstimate"));
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
        await OpenColumnSettingsThroughOverflow();
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
}

internal sealed partial class PlanSheetHostedTests
{
    [TestCase("Title", 320d, 224d), TestCase("Id", 100d, 52d)]
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
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeader" + key).ActualWidth == width && session.UndoCount == undo + 1);
        await Ui.Run(() => {
            var header = Ui.Find<TextBlock>("PlanHeader" + key);
            foreach (var row in sheet.Realized.Where(r => r.Identity.Length > 0 && r.IsLoaded)) {
                var number = sheet.PlanIds[row.Identity];
                var cell = key == "Id" ? Ui.Find<FrameworkElement>("PlanRowId" + number) : Ui.Find<FrameworkElement>("PlanCell" + number + "_Title");
                Assert.That(cell.ActualWidth, Is.EqualTo(width).Within(4));
                Assert.That(cell.TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(header.TransformToVisual(sheet).TransformPoint(new()).X).Within(2));
                Assert.That(row.ColumnDefinitions[0].ActualWidth, Is.EqualTo(sheet.SheetViewport).Within(1));
            }
        });
        await Ui.Run(async () => await sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanHeader" + key).ActualWidth == original && session.UndoCount == undo);
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
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Width, Is.EqualTo(224));
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
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Width, Is.EqualTo(224)));
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
                await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth > 224);
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
                Assert.That(session.UndoCount, Is.EqualTo(undo));
            });
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Contains("更新または発行が進行中"));
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                foreach (var column in sheet.VisibleColumns) {
                    var key = column.Indicator ? "Indicator" : column.Field ?? "Id";
                    var header = Ui.Find<TextBlock>("PlanHeader" + key);
                    var cell = Ui.Find<FrameworkElement>(column.Indicator ? "PlanIndicator2"
                        : column.Field is null ? "PlanRowId2" : "PlanCell2_" + key);
                    var left = cell.TransformToVisual(sheet).TransformPoint(new()).X;
                    Assert.That(header.ActualWidth, Is.EqualTo(cell.ActualWidth).Within(1), key);
                    Assert.That(header.TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(left).Within(1), key);
                    if (!column.Indicator) {
                        var grip = Ui.Find<FrameworkElement>("PlanHeaderGrip" + key);
                        Assert.That(grip.TransformToVisual(sheet).TransformPoint(new()).X + grip.ActualWidth / 2,
                            Is.EqualTo(left + cell.ActualWidth).Within(1), key);
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
            await Ui.Until(() => Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth > 224);
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            Assert.That(session.Document.State.Settings.SheetColumns, Is.Empty);
        });
        await Ui.Until(() => session.UndoCount == undo + 1 && Math.Abs(Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth - 288) <= 1);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth, Is.EqualTo(288).Within(1)));
        var point = await SheetNativeInput.PointFor("PlanHeaderGripTitle");
        SheetNativeInput.Move(point);
        SheetNativeInput.Button(true); SheetNativeInput.Button(false);
        SheetNativeInput.Button(true); SheetNativeInput.Button(false);
        await Ui.Until(() => session.UndoCount == undo + 2 && Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth
            == PlanSheetColumns.All(session.Document).Single(c => c.Key == "Title").Width);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell2_Title");
            var measure = new TextBlock { Text = rows[1].Title, FontSize = cell.FontSize, FontFamily = cell.FontFamily, FontWeight = cell.FontWeight };
            measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").ActualWidth, Is.InRange(Math.Ceiling(measure.DesiredSize.Width + 40), Math.Ceiling(measure.DesiredSize.Width + 44)));
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
