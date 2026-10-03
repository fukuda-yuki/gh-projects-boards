using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class BulkEditingHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private TaskCompletionSource<string> clipboard = new();
    [SetUp]
    public async Task Setup()
    {
        project = EditingTests.Registration(count: 100);
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]); work.Open(project);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-bulk-ui-" + Guid.NewGuid())), work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true), readClipboard: () => clipboard.Task));
        await Ui.Mount(grid); await Ui.Ready<Button>("GridCell0_1");
    }
    [TearDown]
    public async Task Teardown()
    {
        string observed = "";
        await Ui.Run(() => observed = $"Final selection: {Ui.Find<TextBlock>("GridSelection").Text}; state: {Ui.Find<TextBlock>("DraftStatus").Text}; focus: {FocusManager.GetFocusedElement(Ui.Root.XamlRoot)?.GetType().Name}");
        TestContext.Out.WriteLine(observed);
        clipboard.TrySetResult("");
        await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle();
    }
    private static bool ChoicesOpen() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot)
        .Where(p => p.IsOpen).SelectMany(p => Ui.Tree(p.Child)).OfType<MenuFlyoutItem>()
        .Any(i => i.IsLoaded && AutomationProperties.GetAutomationId(i) == "ChoiceOption-done");
    private async Task ChangeSource() => await Ui.ChooseCell("GridCell0_1", "done");
    private Task AssertDifferences(int count) => Ui.Run(() => Assert.That(session.Workspace.DifferenceCount, Is.EqualTo(count)));

    [TestCase("GridRangePaste"), TestCase("GridRangeFillDown"), Category("RangeInputRepair")]
    public async Task NativeRangeMenuThenKeyboardUndoKeepsTheRangeAndUnrelatedPendingText(string command)
    {
        await ChangeSource();
        await AddUnrelatedPendingText();
        await SheetNativeInput.Click("GridCell0_1");
        await SheetNativeInput.Click("GridCell2_1", VirtualKey.Shift);
        await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("3行・3セル"));
        string before = "";
        await Ui.Run(() => before = WorkWithoutRevision());
        if (command == "GridRangePaste") clipboard.SetResult("Done");
        await SheetNativeInput.Click("GridRangeCommands");
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>(command) is { IsLoaded: true, IsEnabled: true });
        await FocusRangeCommandWithNativeKey(command);
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>(command) is null && session.Workspace.DifferenceCount == 3);
        string focusAfterMenu = "";
        await Ui.Run(() => focusAfterMenu = ElementName(FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject));
        TestContext.Out.WriteLine("Focus after native menu closure: " + focusAfterMenu);
        // The menu must close naturally. No test refocus is allowed before Undo.
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        try { await Ui.Until(() => session.Workspace.DifferenceCount == 1); }
        finally { await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "range-native-undo-" + command)); }
        await Ui.Run(() => {
            Assert.That(WorkWithoutRevision(), Is.EqualTo(before));
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・3セル"));
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBlock>("GridSelection")), Does.Contain("先頭 P1T1 / アクティブ P1T3"));
            AssertUnrelatedPendingText();
        });
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => session.Workspace.DifferenceCount == 0);
        await Ui.Run(() => { Assert.That(session.Workspace.Snapshot().History, Is.Empty); AssertUnrelatedPendingText(); });
    }

    [Test, Category("RangeInputRepair")]
    public async Task QuickFilterNativeUndoDoesNotUndoSheetWorkOrPendingText()
    {
        await ChangeSource(); await AddUnrelatedPendingText();
        await SheetNativeInput.Click("GridCell0_1"); await SheetNativeInput.Click("GridCell2_1", VirtualKey.Shift);
        string before = "";
        await Ui.Run(() => before = WorkWithoutRevision());
        await SheetNativeInput.Click("GridQuickTitleFilter");
        await SheetNativeInput.Press(VirtualKey.Number1);
        await Ui.Until(() => Ui.Find<TextBox>("GridQuickTitleFilter").Text == "1");
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => Ui.Find<TextBox>("GridQuickTitleFilter").Text == "");
        await Ui.Run(() => { Assert.That(WorkWithoutRevision(), Is.EqualTo(before)); AssertUnrelatedPendingText(); });
    }

    [TestCase(false), TestCase(true), Category("RangeInputRepair")]
    public async Task CancelledOrRejectedRangeMenuDoesNotInsertAnUndoOperation(bool reject)
    {
        await ChangeSource();
        if (reject) await Ui.Run(() => session.Workspace.SetBuffer(session.Workspace.Open(project)[1].Cells[1], "unfinished target"));
        await SheetNativeInput.Click("GridCell0_1"); await SheetNativeInput.Click("GridCell2_1", VirtualKey.Shift);
        string before = "";
        await Ui.Run(() => before = WorkWithoutRevision());
        await SheetNativeInput.Click("GridRangeCommands");
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("GridRangeFillDown") is { IsLoaded: true });
        if (reject)
        {
            await FocusRangeCommandWithNativeKey("GridRangeFillDown");
            await SheetNativeInput.Press(VirtualKey.Enter);
            await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("未確定入力"));
        }
        else await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("GridRangeFillDown") is null);
        await Ui.Run(() => Assert.That(WorkWithoutRevision(), Is.EqualTo(before)));
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => session.Workspace.DifferenceCount == 0);
        await Ui.Run(() => {
            Assert.That(session.Workspace.Snapshot().History, Is.Empty);
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[1].Cells[1]), Is.EqualTo(reject ? "unfinished target" : null));
            Assert.That(session.Workspace.Journal, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・3セル"));
        });
    }

    [Test, Category("RangeInputRepair")]
    public async Task NewlySelectedFillHandleAcquiresNativeDragAndCopiesOnlyOnRelease()
    {
        await ChangeSource(); await AddUnrelatedPendingText();
        await SheetNativeInput.Click("GridCell0_1");
        Button handle = null!;
        var directPress = false; var capturedOnPress = false; var captureLost = false;
        var observations = new List<string>();
        PointerEventHandler pressed = (_, args) => {
            directPress = true;
            capturedOnPress = handle.PointerCaptures?.Any(pointer => pointer.PointerId == args.Pointer.PointerId) == true;
            observations.Add($"Handle press: captured={capturedOnPress}; source={ElementPath(args.OriginalSource as DependencyObject)}");
        };
        PointerEventHandler lost = (_, _) => { captureLost = true; observations.Add("Handle pointer capture lost"); };
        PointerEventHandler rootPressed = (_, args) => observations.Add("Root press source: " + ElementPath(args.OriginalSource as DependencyObject));
        string before = "";
        await Ui.Run(() => {
            handle = Ui.Find<Button>("GridFillHandle0_1");
            observations.Add("Handle ancestry: " + ElementPath(handle));
            var center = handle.TransformToVisual(Ui.Root).TransformPoint(new(handle.ActualWidth / 2, handle.ActualHeight / 2));
            observations.Add("Hit ancestry: " + string.Join(" | ", VisualTreeHelper.FindElementsInHostCoordinates(center, Ui.Root).Take(5).Select(ElementPath)));
            handle.AddHandler(UIElement.PointerPressedEvent, pressed, true);
            handle.PointerCaptureLost += lost; Ui.Root.AddHandler(UIElement.PointerPressedEvent, rootPressed, true);
            before = WorkWithoutRevision();
        });
        try
        {
            await SheetNativeInput.Drag("GridFillHandle0_1", "GridCell9_1", async () => {
                await Ui.Run(() => {
                    observations.Add($"Held: directPress={directPress}; capturedOnPress={capturedOnPress}; captureLost={captureLost}; captures={handle.PointerCaptures?.Count ?? 0}; selection={Ui.Find<TextBlock>("GridSelection").Text}");
                    Assert.That(directPress && capturedOnPress, Is.True, "The visible handle must receive the native press and acquire its pointer.");
                    Assert.That(WorkWithoutRevision(), Is.EqualTo(before));
                });
                await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("10行へコピー予定"));
                await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "fill-held-preview"));
            });
            await Ui.Until(() => session.Workspace.DifferenceCount == 10);
            await Ui.Run(() => {
                Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Key.NodeId), Is.EquivalentTo(Enumerable.Range(1, 10).Select(i => "P1T" + i)));
                Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Change!.Value), Is.All.EqualTo("done"));
                AssertUnrelatedPendingText();
            });
            await Ui.ClickCommand("GridUndo");
            await Ui.Run(() => Assert.That(WorkWithoutRevision(), Is.EqualTo(before)));
        }
        finally
        {
            await Ui.Run(async () => {
                handle.RemoveHandler(UIElement.PointerPressedEvent, pressed); handle.PointerCaptureLost -= lost;
                Ui.Root.RemoveHandler(UIElement.PointerPressedEvent, rootPressed);
                observations.Add("Final handle ancestry: " + ElementPath(handle));
                await ApplyInformationEvidence.Capture(grid, "fill-native-final");
            });
            foreach (var observation in observations) TestContext.Out.WriteLine(observation);
        }
    }

    private Task AddUnrelatedPendingText() => Ui.Run(() => {
        var rows = session.Workspace.Open(project);
        session.Workspace.SetBuffer(rows[90].Cells[0], "unrelated pending title");
        session.Workspace.SetBuffer(rows[91].Cells[0], "second unrelated pending title");
    });
    private async Task FocusRangeCommandWithNativeKey(string command)
    {
        await Ui.Until(() => FocusManager.GetFocusedElement(grid.XamlRoot) is MenuFlyoutItem);
        var atCommand = false;
        await Ui.Run(() => atCommand = ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Popup<MenuFlyoutItem>(command)));
        if (!atCommand) await SheetNativeInput.Press(VirtualKey.Down);
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Popup<MenuFlyoutItem>(command)));
    }
    private void AssertUnrelatedPendingText()
    {
        var rows = session.Workspace.Open(project);
        Assert.That(session.Workspace.Buffer(rows[90].Cells[0]), Is.EqualTo("unrelated pending title"));
        Assert.That(session.Workspace.Buffer(rows[91].Cells[0]), Is.EqualTo("second unrelated pending title"));
        Assert.That(session.Workspace.Fields.Count(field => field.Buffer is not null), Is.EqualTo(2));
        Assert.That(session.Workspace.Journal, Is.Empty);
    }
    private string WorkWithoutRevision() => System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot() with { Revision = 0 });
    private static string ElementName(DependencyObject? element) => element is null ? "null" : element.GetType().Name + "#" + AutomationProperties.GetAutomationId(element);
    private static string ElementPath(DependencyObject? element)
    {
        var names = new List<string>();
        for (; element is not null; element = VisualTreeHelper.GetParent(element)) names.Add(ElementName(element));
        return string.Join(" > ", names);
    }

    [TestCase("GridRangePaste"), TestCase("GridRangeFillDown"), Category("RangeCommands")]
    public async Task NamedRangeMenuAtNarrowWidthKeepsTheRangeAndOneBulkUndo(string command)
    {
        await Ui.Run(() => grid.Width = 640);
        await ChangeSource();
        await SheetNativeInput.Click("GridCell0_1");
        await SheetNativeInput.Click("GridCell2_1", VirtualKey.Shift);
        await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("3行・3セル"));
        await Ui.Run(() => {
            var entry = Ui.Find<Button>("GridRangeCommands");
            var bounds = entry.TransformToVisual(grid).TransformBounds(new(0, 0, entry.ActualWidth, entry.ActualHeight));
            Assert.That(entry.Content, Is.EqualTo("範囲操作"));
            Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(entry).GetName(), Is.EqualTo("範囲操作"));
            Assert.That(entry.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(entry.ActualWidth, Is.GreaterThan(0));
            Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Right, Is.LessThanOrEqualTo(grid.ActualWidth));
            Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(entry).IsOffscreen(), Is.False);
        });
        if (command == "GridRangePaste") clipboard.SetResult("Done");
        await SheetNativeInput.Click("GridRangeCommands");
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>(command) is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(async () => {
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・3セル"));
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBlock>("GridSelection")), Does.Contain("先頭 P1T1 / アクティブ P1T3"));
            var expectedAction = command == "GridRangePaste" ? "選択範囲に貼り付け" : "先頭セルの値を下へコピー";
            Assert.That(Ui.Popup<MenuFlyoutItem>(command)!.Text, Is.EqualTo(expectedAction));
            Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Popup<MenuFlyoutItem>(command)!).GetName(), Is.EqualTo(expectedAction));
            await CaptureRangeMenu("range-menu-640-" + command);
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Popup<MenuFlyoutItem>(command)!).GetPattern(PatternInterface.Invoke)).Invoke();
        });
        await Ui.Until(() => session.Workspace.DifferenceCount == 3);
        await Ui.Run(() => {
            Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Key.NodeId), Is.EquivalentTo(new[] { "P1T1", "P1T2", "P1T3" }));
            Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Change!.Value), Is.All.EqualTo("done"));
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・3セル"));
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBlock>("GridSelection")), Does.Contain("先頭 P1T1 / アクティブ P1T3"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
        await Ui.Run(() => {
            Assert.That(session.Workspace.Fields.Single(f => f.Change is not null).Key.NodeId, Is.EqualTo("P1T1"));
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・3セル"));
        });
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(0);
    }

    [Test, Category("RangeCommands")]
    public async Task MultiColumnRangeExplainsFillDownWithoutChangingTheSelection()
    {
        await SheetNativeInput.Click("GridCell0_0"); await SheetNativeInput.Click("GridCell2_1", VirtualKey.Shift);
        await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("3行・6セル"));
        await SheetNativeInput.Click("GridRangeCommands");
        await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("GridRangeFillDown") is { IsLoaded: true });
        await Ui.Run(async () => {
            var fill = Ui.Popup<MenuFlyoutItem>("GridRangeFillDown")!;
            Assert.That(fill.IsEnabled, Is.False); Assert.That(fill.Text, Does.Contain("1列を選択"));
            Assert.That(Ui.Popup<MenuFlyoutItem>("GridRangePaste")!.IsEnabled, Is.True);
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・6セル"));
            Assert.That(session.Workspace.DifferenceCount, Is.Zero);
            await CaptureRangeMenu("range-menu-multiple-columns");
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
    }

    [Test, Category("RangeCommands")]
    public async Task SingleCellAndUnconfirmedNativeEditHideRangeCommandsEvenAfterToolbarFocus()
    {
        await SheetNativeInput.Click("GridCell0_0");
        await Ui.Run(() => Assert.That(Ui.Find<Button>("GridRangeCommands").Visibility, Is.EqualTo(Visibility.Collapsed)));
        await SheetNativeInput.Press(VirtualKey.Down, VirtualKey.Shift);
        await Ui.Until(() => Ui.Find<Button>("GridRangeCommands").Visibility == Visibility.Visible);
        await SheetNativeInput.Press(VirtualKey.F2);
        await Ui.Until(() => Ui.Find<Button>("GridRangeCommands").Visibility == Visibility.Collapsed);
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("GridCell1_0"); input.Text = "unfinished range input";
            Assert.That(Ui.Find<TextBox>("GridQuickTitleFilter").Focus(FocusState.Keyboard), Is.True);
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("GridRangeCommands").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("2行・2セル"));
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[1].Cells[0]), Is.EqualTo("unfinished range input"));
            Assert.That(session.Workspace.DifferenceCount, Is.Zero); Assert.That(session.Workspace.Journal, Is.Empty);
            FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBox>("GridCell1_0")).SetFocus();
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => Ui.Find<Button>("GridRangeCommands").Visibility == Visibility.Visible);
        await Ui.Run(() => Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[1].Cells[0]), Is.Null));
    }

    private async Task CaptureRangeMenu(string name)
    {
        await ApplyInformationEvidence.Capture(grid, name + "-sheet");
        var menu = VisualTreeHelper.GetOpenPopupsForXamlRoot(grid.XamlRoot)
            .Single(p => Ui.Tree(p.Child).OfType<MenuFlyoutItem>().Any(i => AutomationProperties.GetAutomationId(i) == "GridRangePaste"));
        await ApplyInformationEvidence.Capture((FrameworkElement)menu.Child, name + "-menu");
    }

    [Test]
    public async Task BodySelectsMenuKeysOpenAndFixedMarkerNeverMovesValueOrArrow()
    {
        await SheetNativeInput.Click("GridCell0_1");
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1");
        await Ui.Run(() => Assert.That(ChoicesOpen(), Is.False));
        foreach (var key in new[] { VirtualKey.F2, VirtualKey.F4, VirtualKey.Space })
        {
            TestContext.Out.WriteLine("Opening native choices through " + key);
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(Ui.Root.XamlRoot), Ui.Find<Button>("GridCell0_1")));
            await SheetNativeInput.Press(key); await Ui.Until(ChoicesOpen);
            await SheetNativeInput.Rendered();
            await SheetNativeInput.Press(VirtualKey.Escape); await Ui.Until(() => !ChoicesOpen());
            await SheetNativeInput.Rendered();
        }
        Windows.Foundation.Rect valueBefore = default, arrowBefore = default;
        await Ui.Run(() => {
            var value = Ui.Find<TextBlock>("GridCell0_1Value"); var arrow = Ui.Find<Button>("GridChoiceArrow0_1");
            valueBefore = value.TransformToVisual(grid).TransformBounds(new(0, 0, value.ActualWidth, value.ActualHeight));
            arrowBefore = arrow.TransformToVisual(grid).TransformBounds(new(0, 0, arrow.ActualWidth, arrow.ActualHeight));
        });
        await ChangeSource(); await AssertDifferences(1);
        await Ui.Run(() => {
            var value = Ui.Find<TextBlock>("GridCell0_1Value"); var arrow = Ui.Find<Button>("GridChoiceArrow0_1");
            Assert.That(value.TransformToVisual(grid).TransformBounds(new(0, 0, value.ActualWidth, value.ActualHeight)), Is.EqualTo(valueBefore));
            Assert.That(arrow.TransformToVisual(grid).TransformBounds(new(0, 0, arrow.ActualWidth, arrow.ActualHeight)), Is.EqualTo(arrowBefore));
            Assert.That(Ui.Find<TextBlock>("GridMarker0_1").Text, Is.EqualTo("◆"));
            Assert.That(Ui.Find<TextBlock>("GridMarker0_1").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Ui.Find<Button>("GridFillHandle0_1").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<Button>("GridCell0_1")), Does.Contain("変更あり"));
        });
    }

    [TestCase("fill"), TestCase("paste"), TestCase("down")]
    public async Task ActualRangeAndBulkCommandsKeepTwoIndependentUndoUnits(string route)
    {
        await ChangeSource();
        if (route == "fill")
            await SheetNativeInput.Drag("GridFillHandle0_1", "GridCell9_1", async () => {
                await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("10行へコピー予定"));
                await AssertDifferences(1);
            });
        else
        {
            await SheetNativeInput.Click("GridCell0_1");
            await SheetNativeInput.Click("GridCell9_1", VirtualKey.Shift);
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("10行・10セル"));
            if (route == "down") await SheetNativeInput.Press(VirtualKey.D, VirtualKey.Control);
            else { clipboard.SetResult("Done"); await Ui.ClickCommand("GridPaste"); }
        }
        await Ui.Until(() => session.Workspace.DifferenceCount == 10);
        await Ui.Run(() => Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Key.NodeId),
            Is.EquivalentTo(new[] { "P1T1", "P1T2", "P1T3", "P1T4", "P1T5", "P1T6", "P1T7", "P1T8", "P1T9", "P1T10" })));
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(0);
    }

    [Test, Category("SelectionGesture")]
    public async Task BodyRangeDragCanReturnToItsPressedCellWithoutChangingWork()
    {
        var before = System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot());
        var first = await SheetNativeInput.PointFor("GridCell0_1");
        var last = await SheetNativeInput.PointFor("GridCell9_1");
        await SheetNativeInput.Drag(first, last, async () => {
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("10行・10セル"));
            SheetNativeInput.Move(first);
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("1行・1セル"));
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("1行・1セル"));
            Assert.That(grid.SelectionIdentity, Is.EqualTo(("P1T1", session.Workspace.Open(project)[0].Cells[1].Key)));
            Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(Ui.Find<Button>("GridCell0_1")));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task BodyDragSelectsAndEscCancelsFillWithoutUndoingTheSource()
    {
        await SheetNativeInput.Drag("GridCell0_1", "GridCell9_1");
        await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("10行・10セル"));
        await AssertDifferences(0); await ChangeSource();
        await SheetNativeInput.Drag("GridFillHandle0_1", "GridCell9_1", async () => {
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("コピー予定"));
            await SheetNativeInput.Press(VirtualKey.Escape);
        });
        await AssertDifferences(1);
        await Ui.Run(() => Assert.That(session.Workspace.Snapshot().History, Has.Length.EqualTo(1)));
    }

    [Test]
    public async Task ShiftArrowsExtendTheSameColumnAcrossTheViewportWithoutLosingTheAnchor()
    {
        await ChangeSource(); await SheetNativeInput.Click("GridCell0_1");
        SheetNativeInput.Key(VirtualKey.Shift, true);
        try
        {
            for (var row = 1; row < 100; row++)
            {
                await SheetNativeInput.Press(VirtualKey.Down);
                var count = row + 1;
                await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains($"{count}行・{count}セル"));
            }
        }
        finally { SheetNativeInput.Key(VirtualKey.Shift, false); }
        await SheetNativeInput.Press(VirtualKey.D, VirtualKey.Control);
        await Ui.Until(() => session.Workspace.DifferenceCount == 100);
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
    }

    [Test]
    public async Task DelayedBroadcastCannotOverwriteLaterInputAndShowsAReason()
    {
        await SheetNativeInput.Click("GridCell0_1"); await SheetNativeInput.Click("GridCell9_1", VirtualKey.Shift);
        await Ui.ClickCommand("GridPaste");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell4_0").Text = "later pending");
        clipboard.SetResult("Done");
        await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("貼り付けを中止"));
        await AssertDifferences(0);
        await Ui.Run(() => Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I5")).Buffer, Is.EqualTo("later pending")));
    }

    [TestCase(0), TestCase(1)]
    public async Task RapidShiftArrowReleaseKeepsTheOriginalAnchorAfterFocusNotifications(int column)
    {
        await SheetNativeInput.Click($"GridCell0_{column}");
        SheetNativeInput.Key(VirtualKey.Shift, true);
        try
        {
            // Queue a native key burst while the UI dispatcher is occupied with
            // this short stimulus; late focus events must not reinterpret Shift.
            await Ui.Run(() => { for (var i = 0; i < 99; i++) { SheetNativeInput.Key(VirtualKey.Down, true); SheetNativeInput.Key(VirtualKey.Down, false); } });
        }
        finally { SheetNativeInput.Key(VirtualKey.Shift, false); }
        await Ui.Until(() => FocusManager.GetFocusedElement(Ui.Root.XamlRoot) is DependencyObject focused
            && AutomationProperties.GetAutomationId(focused) == $"GridCell99_{column}");
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("100行・100セル"));
            Assert.That(AutomationProperties.GetHelpText(Ui.Find<TextBlock>("GridSelection")), Does.Contain("先頭 P1T1 / アクティブ P1T100"));
            Assert.That(session.Workspace.DifferenceCount, Is.Zero);
        });
    }

    [Test]
    public async Task WideningARectangleRemovesItsFormerOuterEdgeFromInteriorCells()
    {
        await SheetNativeInput.Click("GridCell0_0");
        await SheetNativeInput.Click("GridCell9_1", VirtualKey.Shift);
        await Ui.Run(() => Assert.That(Outline("GridCell5_1").BorderThickness.Right, Is.EqualTo(1)));
        await SheetNativeInput.Press(VirtualKey.Right, VirtualKey.Shift);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("10行・30セル"));
            Assert.That(Outline("GridCell5_1").BorderThickness.Right, Is.Zero);
            Assert.That(Outline("GridCell5_2").BorderThickness.Right, Is.EqualTo(1));
            Assert.That(session.Workspace.DifferenceCount, Is.Zero);
        });
        static Border Outline(string id) => ((Panel)VisualTreeHelper.GetParent(Ui.Find<FrameworkElement>(id)))
            .Children.OfType<Border>().Single(border => !border.IsHitTestVisible);
    }

    [Test]
    public async Task NativeClipboardRetainsTheChosenOptionIdWhenTwoLabelsAreIdentical()
    {
        GhProjectsBoards.E2E.Tests.NativeClipboardScope saved = null!;
        await Ui.Run(() => saved = new());
        try
        {
            await Ui.Unmount(grid);
            await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
            await Ui.Mount(grid); await Ui.ChooseCell("GridCell0_1", "dup2");
            await SheetNativeInput.Click("GridCell0_1");
            await Ui.Run(() => GhProjectsBoards.E2E.Tests.NativeClipboardScope.WriteTestFormats("sentinel"));
            await Ui.ClickCommand("GridCopy");
            await Ui.Until(() => GhProjectsBoards.E2E.Tests.NativeClipboardScope.ReadText() != "sentinel");
            await SheetNativeInput.Click("GridCell9_1", VirtualKey.Shift);
            await Ui.ClickCommand("GridPaste");
            await Ui.Until(() => session.Workspace.DifferenceCount == 10);
            await Ui.Run(() => {
                Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Change!.Value), Is.All.EqualTo("dup2"));
                Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Key.NodeId),
                    Is.EquivalentTo(Enumerable.Range(1, 10).Select(i => "P1T" + i)));
            });
            await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
        }
        finally { await Ui.Run(() => saved.Dispose()); }
    }

    [Test]
    public async Task FillAtViewportEdgeScrollsToHundredthRowAndCommitsOnlyOnRelease()
    {
        await ChangeSource();
        var first = await SheetNativeInput.PointFor("GridFillHandle0_1");
        var edge = await SheetNativeInput.PointFor("ProjectItems", .45, .99);
        await SheetNativeInput.Drag(first, edge, async () => {
            try { await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("100行へコピー予定")); }
            finally {
                string observation = "";
                await Ui.Run(() => {
                    var scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                    observation = $"Held fill: {Ui.Find<TextBlock>("GridSelection").Text}; edge={edge}; offset={scroll.VerticalOffset}/{scroll.ScrollableHeight}; viewport={scroll.ViewportHeight}; actual={scroll.ActualHeight}; root={Ui.Root.ActualHeight}; scale={Ui.Root.XamlRoot.RasterizationScale}";
                });
                TestContext.Out.WriteLine(observation);
            }
            await AssertDifferences(1);
        });
        await Ui.Until(() => session.Workspace.DifferenceCount == 100);
        await Ui.Run(() => {
            Assert.That(Ui.Tree(Ui.Find<FrameworkElement>("GridCell99_1")).OfType<TextBlock>().Any(text => text.Text == "Done"), Is.True);
            Assert.That(session.Workspace.Fields.Where(f => f.Key.Kind == "Select" && f.Change is not null).Select(f => f.Key.NodeId),
                Is.EquivalentTo(Enumerable.Range(1, 100).Select(i => "P1T" + i)));
        });
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
    }

    [Test]
    public async Task UpwardFillUsesTheSourceBelowAndKeepsItsEarlierEditOnUndo()
    {
        await Ui.ChooseCell("GridCell9_1", "done");
        await SheetNativeInput.Drag("GridFillHandle9_1", "GridCell0_1", async () => {
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("10行へコピー予定"));
            await AssertDifferences(1);
        });
        await Ui.Until(() => session.Workspace.DifferenceCount == 10);
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
        await Ui.Run(() => Assert.That(session.Workspace.Fields.Single(f => f.Change is not null).Key.NodeId, Is.EqualTo("P1T10")));
    }

    [Test]
    public async Task ChangedRangeKeepsIdsAndMarkersAcrossScrollBoundaryResizeAndTheme()
    {
        await ChangeSource();
        await SheetNativeInput.Click("GridCell9_1", VirtualKey.Shift);
        await SheetNativeInput.Press(VirtualKey.D, VirtualKey.Control);
        await Ui.Until(() => session.Workspace.DifferenceCount == 10);
        await SheetNativeInput.Rendered();
        ElementTheme old = default; ScrollViewer scroll = null!;
        await Ui.Run(() => { old = Ui.Root.RequestedTheme; scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First(); });
        try
        {
            string scrollRequest = "";
            await Ui.Run(() => { scrollRequest = $"offset={scroll.VerticalOffset}; end={scroll.ScrollableHeight}; viewport={scroll.ViewportHeight}"; scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
            try { await Ui.Until(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1); }
            finally { await Ui.Run(() => scrollRequest += $" -> offset={scroll.VerticalOffset}; end={scroll.ScrollableHeight}; viewport={scroll.ViewportHeight}"); TestContext.Out.WriteLine(scrollRequest); }
            await Ui.Run(() => scroll.ChangeView(null, 0, null, true)); await Ui.Until(() => scroll.VerticalOffset < 1);
            var boundary = await SheetNativeInput.PointFor("GridColumnResize1");
            await SheetNativeInput.Drag(boundary, new(boundary.X + 60, boundary.Y));
            await Ui.Until(() => session.Workspace.Columns(project).Visible[1].Preference.Width > 144);
            await Ui.Run(() => Ui.Root.RequestedTheme = ElementTheme.Light);
            await Ui.Until(() => grid.ActualTheme == ElementTheme.Light); await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("10行・10セル"));
                Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T10"));
                foreach (var row in Enumerable.Range(0, 10)) Assert.That(Ui.Find<TextBlock>($"GridMarker{row}_1").Text, Is.EqualTo("◆"));
                Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Key.NodeId),
                    Is.EquivalentTo(Enumerable.Range(1, 10).Select(i => "P1T" + i)));
                Assert.That(session.Workspace.Snapshot().History, Has.Length.EqualTo(2));
            });
            await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
        }
        finally { await Ui.Run(() => Ui.Root.RequestedTheme = old); }
    }

    [Test]
    public async Task FilteredSortedFillThroughTheViewportTouchesOnlyTheHundredVisibleIds()
    {
        await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        project = EditingTests.Registration(count: 200);
        project = project with { Snapshot = project.Snapshot with { Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key,
            pair => pair.Value with { Title = new(ValueAvailability.Present, (pair.Value.Number % 2 == 1 ? "keep " : "excluded ") + pair.Value.Number.ToString("D3")) }) } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]); work.Open(project);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-filter-bulk-" + Guid.NewGuid())), work, 0);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridQuickTitleFilter");
        await Ui.Run(() => { Ui.Find<TextBox>("GridQuickTitleFilter").Text = "keep"; Ui.Click("GridQuickFilterApply"); });
        await Ui.Until(() => grid.DisplayedRowIds.Length == 100);
        await Ui.Ready<Button>("GridHeaderMenu0");
        await Ui.Run(() => Ui.Click("GridHeaderMenu0"));
        MenuFlyoutItem? sort = null;
        await Ui.Until(() => (sort = VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot).SelectMany(p => Ui.Tree(p.Child))
            .OfType<MenuFlyoutItem>().SingleOrDefault(i => AutomationProperties.GetAutomationId(i) == "HeaderSortDescending")) is { IsLoaded: true });
        await Ui.Run(() => ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(sort!).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke());
        await Ui.Until(() => grid.DisplayedRowIds.First() == "P1T199");
        await ChangeSource();
        await SheetNativeInput.Drag(await SheetNativeInput.PointFor("GridFillHandle0_1"), await SheetNativeInput.PointFor("ProjectItems", .45, .99),
            () => Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("100行へコピー予定")));
        await Ui.Until(() => session.Workspace.DifferenceCount == 100);
        await Ui.Run(() => Assert.That(session.Workspace.Fields.Where(f => f.Change is not null).Select(f => f.Key.NodeId),
            Is.EquivalentTo(Enumerable.Range(1, 200).Where(i => i % 2 == 1).Select(i => "P1T" + i))));
        await Ui.ClickCommand("GridUndo"); await AssertDifferences(1);
    }

    [Test]
    public async Task LostPointerCaptureCancelsFillAndLeavesSourceHistoryIntact()
    {
        await ChangeSource();
        await SheetNativeInput.Drag("GridFillHandle0_1", "GridCell9_1", async () => {
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("コピー予定"));
            // Exercise the framework's real capture-loss event, including its route.
            await Ui.Run(() => Ui.Find<Button>("GridFillHandle0_1").ReleasePointerCaptures());
            await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("フィルを取り消しました"));
        });
        await AssertDifferences(1);
        await Ui.Run(() => Assert.That(session.Workspace.Snapshot().History, Has.Length.EqualTo(1)));
    }

    [Test]
    public async Task BulkFailureShowsTheMiddleTargetAndPreservesPendingInputAndHistory()
    {
        await ChangeSource();
        await Ui.Run(() => session.Workspace.SetBuffer(session.Workspace.Open(project)[4].Cells[1], "unfinished"));
        await SheetNativeInput.Click("GridCell0_1"); await SheetNativeInput.Click("GridCell9_1", VirtualKey.Shift);
        await Ui.ClickCommand("GridFillDown");
        await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("P1T5"));
        await AssertDifferences(1);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain("未確定入力"));
            Assert.That(session.Workspace.Snapshot().History, Has.Length.EqualTo(1));
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[4].Cells[1]), Is.EqualTo("unfinished"));
        });
    }
}
