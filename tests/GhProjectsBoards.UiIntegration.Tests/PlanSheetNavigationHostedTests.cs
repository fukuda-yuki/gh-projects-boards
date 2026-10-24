using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

// #228: the plan sheet's grid edge keys and F5 ジャンプ at version scale. The routine cases drive the same
// view entry points the key handler calls against real controls, selection, focus and scrolling; the native
// cases press the chords themselves. Both run on the hosted desktop.
[TestFixture, NonParallelizable, Category("PlanSheet")]
internal sealed class PlanSheetNavigationHostedTests
{
    private const int TaskCount = 1000;
    // The unfiltered sheet shows every document row plus the empty new-task row; folding I997 hides its
    // three children, and the title filter "Task 99" matches eleven task titles.
    private const int AllRows = TaskCount + 2, FilteredRows = 12, FoldedRows = 999;
    private static readonly DateOnly Today = new(2026, 10, 5);
    private string root = null!;
    private string localIdentity = null!;
    private PlanSession session = null!;
    private PlanSheetView sheet = null!;
    private Grid sheetHost = null!;
    private PlanClipboardContent clipboard = new("", null);

    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-nav-" + Guid.NewGuid().ToString("N"));
        var rows = NavigationRows(out localIdentity);
        var links = rows.Where(row => row.Identity.StartsWith("I", StringComparison.Ordinal)).ToImmutableDictionary(
            row => row.Identity, row => new PlanIssueLink("acme/repo#" + row.Identity[1..], "https://github.com/acme/repo/issues/" + row.Identity[1..]));
        session = await PlanSession.CreateAsync(new(root), new(
            new(new("github.com", 1), "P1"),
            new(rows.Where(row => !row.Identity.StartsWith("local:", StringComparison.Ordinal)).ToImmutableArray(), []),
            new(rows, new() { StatusDate = Today, DefaultRepository = "acme/repo", ProjectEnd = new(2027, 6, 30) }))
        { Sync = new() { IssueLinks = links } }, Today);
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Run(() => {
            sheetHost = new Grid(); sheetHost.RowDefinitions.Add(new() { Height = GridLength.Auto }); sheetHost.RowDefinitions.Add(new());
            sheetHost.Children.Add(sheet.statusDate); sheetHost.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
    }

    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            try { await Ui.Idle(); }
            finally
            {
                try { await Ui.Unmount(sheetHost, check: false); await Ui.Idle(); }
                finally { await session.FlushAsync(); Directory.Delete(root, true); }
            }
        }
        finally { Ui.EndTest(); }
    }

    private static ImmutableArray<PlanRow> NavigationRows(out string localIdentity)
    {
        var local = PlanRow.New("Local task", "acme/repo") with { End = Today };
        localIdentity = local.Identity;
        var rows = Enumerable.Range(1, TaskCount).Select(i => new PlanRow("I" + i, "Task " + i, "acme/repo") {
            End = Today,
            // The last three tasks are children of I997, so folding changes the last visible task row.
            Parent = i >= 998 ? "I997" : null,
        }).ToList();
        rows.Insert(900, local);
        return [.. rows];
    }

    private string LastField => sheet.VisibleColumns.Where(column => column.Field is not null).Select(column => column.Field!).Last();
    private string CellId(string identity, string field) => $"PlanCell{sheet.PlanIds[identity]}_{field}";
    private ScrollViewer Viewport() => Ui.Tree(sheet.List).OfType<ScrollViewer>().Single(scroll => scroll.Content is ItemsPresenter);
    private bool Focused(string identity, string field) =>
        Ui.Tree(sheet).OfType<Control>().Any(control => AutomationProperties.GetAutomationId(control) == CellId(identity, field)
            && control.FocusState != FocusState.Unfocused);

    private async Task SelectCell(string identity, string field)
    {
        await Ui.Run(() => sheet.SelectAndFocus(identity, field, false));
        await Ui.Until(() => sheet.IsSelectedRow(identity) && sheet.IsSelected(identity, field) && Focused(identity, field));
    }

    private async Task PressEdge(VirtualKey key, bool control = false, bool extend = false)
    {
        await Ui.Run(() => { sheet.HandleEdgeKey(key, control, extend); });
    }

    private Task AssertFocused(string identity, string field) => Ui.Until(() => Focused(identity, field));

    private Task AssertRowInView(string identity) => Ui.Until(() => {
        if (sheet.List.ContainerFromItem(identity) is not ListViewItem item || !item.IsLoaded) return false;
        var viewport = Viewport();
        var y = item.TransformToVisual(viewport).TransformPoint(new()).Y;
        return y >= -1 && y + sheet.RowHeight <= viewport.ViewportHeight + 1;
    });

    private async Task OpenGoTo()
    {
        await Ui.Run(() => sheet.OpenGoTo());
        await Ui.Ready<TextBox>("PlanGoToInput");
        await Ui.Until(() => Ui.Find<TextBox>("PlanGoToInput").FocusState != FocusState.Unfocused);
    }

    private async Task SubmitGoTo(string name)
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanGoToInput").Text = name);
        await Ui.Run(() => Ui.Click("PlanGoToConfirm"));
    }

    // The prohibited side effects: no plan edit, Undo entry, unpublished change or save. The state record is
    // replaced only by a command, so an unchanged reference is an unchanged document.
    private sealed record PlanSnapshot(int Undo, PlanState State, int Unpublished);
    private PlanSnapshot CapturePlan() => new(session.UndoCount, session.Document.State, session.Changes(Today).TaskCount);
    private void AssertPlanUnchanged(PlanSnapshot before)
    {
        Assert.That(session.UndoCount, Is.EqualTo(before.Undo), "Navigation must not add Undo history.");
        Assert.That(ReferenceEquals(session.Document.State, before.State), Is.True, "Navigation must not edit the plan.");
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(before.Unpublished), "Navigation must not change unpublished work.");
    }

    // The focused cell's own bring-into-view arrives frames after focus; a row jump must stay where it put
    // the viewport, and the selection must stay on the row it named.
    private static async Task Settle()
    {
        await SheetNativeInput.Rendered(); await SheetNativeInput.Rendered(); await Ui.LowPriorityIdle();
    }

    [Test]
    public async Task EdgeKeysMoveToTheFirstAndLastVisibleRowAndFieldColumn()
    {
        var before = CapturePlan();
        await SelectCell("I500", PlanField.Title);
        // Ctrl+↓: the last visible task row in the same column.
        await PressEdge(VirtualKey.Down, control: true);
        Assert.That(sheet.IsSelectedRow("I1000"), Is.True);
        Assert.That(sheet.IsSelected("I1000", PlanField.Title), Is.True);
        await AssertFocused("I1000", PlanField.Title);
        await AssertRowInView("I1000");
        // Ctrl+↑: back to the first visible row.
        await PressEdge(VirtualKey.Up, control: true);
        Assert.That(sheet.IsSelectedRow("I1"), Is.True);
        Assert.That(sheet.IsSelected("I1", PlanField.Title), Is.True);
        await AssertFocused("I1", PlanField.Title);
        // Ctrl+End: the last visible task row and the last visible field column.
        await PressEdge(VirtualKey.End, control: true);
        Assert.That(sheet.IsSelectedRow("I1000"), Is.True);
        Assert.That(sheet.IsSelected("I1000", LastField), Is.True);
        await AssertFocused("I1000", LastField);
        await Ui.Until(() => Math.Abs(Viewport().VerticalOffset - Viewport().ScrollableHeight) < 1);
        // Ctrl+Home: the first visible row and the first field column.
        await PressEdge(VirtualKey.Home, control: true);
        Assert.That(sheet.IsSelectedRow("I1"), Is.True);
        Assert.That(sheet.IsSelected("I1", PlanField.Title), Is.True);
        await AssertFocused("I1", PlanField.Title);
        await Ui.Until(() => Viewport().VerticalOffset == 0);
        // Home / End and Ctrl+← / Ctrl+→ keep the row and move along the field columns.
        await SelectCell("I500", LastField);
        await PressEdge(VirtualKey.Home);
        Assert.That(sheet.IsSelectedRow("I500"), Is.True);
        Assert.That(sheet.IsSelected("I500", PlanField.Title), Is.True);
        await AssertFocused("I500", PlanField.Title);
        await PressEdge(VirtualKey.End);
        Assert.That(sheet.IsSelected("I500", LastField), Is.True);
        await AssertFocused("I500", LastField);
        await PressEdge(VirtualKey.Left, control: true);
        Assert.That(sheet.IsSelected("I500", PlanField.Title), Is.True);
        await AssertFocused("I500", PlanField.Title);
        await PressEdge(VirtualKey.Right, control: true);
        Assert.That(sheet.IsSelected("I500", LastField), Is.True);
        await AssertFocused("I500", LastField);
        AssertPlanUnchanged(before);
    }

    [Test]
    public async Task ShiftEdgeKeysExtendTheSelectionFromItsAnchor()
    {
        await SelectCell("I500", PlanField.Title);
        await PressEdge(VirtualKey.Down, control: true, extend: true);
        Assert.That(sheet.IsSelectedRow("I1000"), Is.True);
        // The anchor stays where the extension started and the range spans only the column it moved in.
        Assert.That(sheet.IsSelected("I500", PlanField.Title), Is.True);
        Assert.That(sheet.IsSelected("I900", PlanField.Title), Is.True);
        Assert.That(sheet.IsSelected("I900", LastField), Is.False);
        Assert.That(sheet.IsSelected("I499", PlanField.Title), Is.False);
        await AssertFocused("I1000", PlanField.Title);
        // Shift+End at the range end widens the rectangle to the last field column.
        await PressEdge(VirtualKey.End, extend: true);
        Assert.That(sheet.IsSelected("I700", PlanField.Title), Is.True);
        Assert.That(sheet.IsSelected("I700", LastField), Is.True);
        Assert.That(sheet.IsSelected("I499", PlanField.Title), Is.False);
        // Shift+Ctrl+Home extends back to the first row and column from the same anchor.
        await PressEdge(VirtualKey.Home, control: true, extend: true);
        Assert.That(sheet.IsSelected("I1", PlanField.Title), Is.True);
        Assert.That(sheet.IsSelected("I500", PlanField.Title), Is.True);
        Assert.That(sheet.IsSelected("I1000", PlanField.Title), Is.False);
    }

    [Test]
    public async Task ShiftCtrlUpAndDownKeepAWholeRowSelectionWhole()
    {
        await Ui.Run(() => sheet.SelectWholeRow("I500", false));
        await Ui.Until(() => sheet.IsSelectedRow("I500") && sheet.IsSelected("I500", PlanField.Estimate));
        await PressEdge(VirtualKey.Down, control: true, extend: true);
        Assert.That(sheet.IsSelectedRow("I1000"), Is.True);
        // A whole-row selection stays whole while it extends vertically, as Shift+PageDown already does.
        Assert.That(sheet.IsSelected("I500", PlanField.Estimate), Is.True);
        Assert.That(sheet.IsSelected("I700", PlanField.Estimate), Is.True);
        Assert.That(sheet.IsSelected("I700", PlanField.Title), Is.True);
        Assert.That(sheet.IsSelected("I499", PlanField.Title), Is.False);
        await AssertFocused("I1000", PlanField.Title);
        await PressEdge(VirtualKey.Up, control: true, extend: true);
        Assert.That(sheet.IsSelected("I500", PlanField.Estimate), Is.True);
        Assert.That(sheet.IsSelected("I500", PlanField.Title), Is.True);
    }

    [Test]
    public async Task FilterAndFoldLimitTheEdgeKeysToVisibleRows()
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 99");
        await Ui.Until(() => sheet.RowIds.Count == FilteredRows);
        await SelectCell("I99", PlanField.Title);
        await PressEdge(VirtualKey.End, control: true);
        Assert.That(sheet.IsSelectedRow("I999"), Is.True);
        Assert.That(sheet.IsSelected("I999", LastField), Is.True);
        await AssertFocused("I999", LastField);
        await PressEdge(VirtualKey.Home, control: true);
        Assert.That(sheet.IsSelectedRow("I99"), Is.True);
        await PressEdge(VirtualKey.Down, control: true);
        Assert.That(sheet.IsSelectedRow("I999"), Is.True);
        // Clearing the filter returns the whole version, and folding hides the last three tasks.
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "");
        await Ui.Until(() => sheet.RowIds.Count == AllRows);
        await Ui.Run(() => sheet.SetFold("I997", true));
        await Ui.Until(() => sheet.IsFolded("I997") && sheet.RowIds.Count == FoldedRows);
        await SelectCell("I500", PlanField.Title);
        await PressEdge(VirtualKey.Down, control: true);
        Assert.That(sheet.IsSelectedRow("I997"), Is.True);
        Assert.That(sheet.IsSelected("I997", PlanField.Title), Is.True);
        await PressEdge(VirtualKey.End, control: true);
        Assert.That(sheet.IsSelectedRow("I997"), Is.True);
        Assert.That(sheet.IsSelected("I997", LastField), Is.True);
        await AssertFocused("I997", LastField);
    }

    [Test]
    public async Task GoToByIssueNameSelectsTheTitleCellAndKeepsFocus()
    {
        var before = CapturePlan();
        await SelectCell("I500", PlanField.Title);
        await OpenGoTo();
        await Ui.Run(() => Assert.That(AutomationProperties.GetName(Ui.Find<TextBox>("PlanGoToInput")), Is.EqualTo("ジャンプ先のタスク名")));
        await SubmitGoTo("#750");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        Assert.That(sheet.IsSelectedRow("I750"), Is.True);
        Assert.That(sheet.IsSelected("I750", PlanField.Title), Is.True);
        await AssertFocused("I750", PlanField.Title);
        await AssertRowInView("I750");
        // repo#N is the same task-name grammar the 先行 cell accepts.
        await OpenGoTo();
        await SubmitGoTo("acme/repo#250");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        Assert.That(sheet.IsSelectedRow("I250"), Is.True);
        Assert.That(sheet.IsSelected("I250", PlanField.Title), Is.True);
        await AssertFocused("I250", PlanField.Title);
        AssertPlanUnchanged(before);
    }

    [Test]
    public async Task GoToByLocalNameSelectsTheTitleCell()
    {
        await SelectCell("I500", PlanField.Title);
        await OpenGoTo();
        await SubmitGoTo("新1");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        Assert.That(sheet.IsSelectedRow(localIdentity), Is.True);
        Assert.That(sheet.IsSelected(localIdentity, PlanField.Title), Is.True);
        await AssertFocused(localIdentity, PlanField.Title);
        await AssertRowInView(localIdentity);
    }

    [Test]
    public async Task UnknownGoToNameShowsTheReasonAndChangesNothing()
    {
        var before = CapturePlan();
        await SelectCell("I500", PlanField.Title);
        await OpenGoTo();
        await SubmitGoTo("#1001");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanGoToError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanGoToError").Text, Does.Contain("この版にありません"));
            // The input stays open for correction, and the selection has not moved.
            Assert.That(Ui.Popup<TextBox>("PlanGoToInput"), Is.Not.Null);
            Assert.That(sheet.IsSelectedRow("I500"), Is.True);
        });
        await Ui.Run(() => Ui.Click("PlanGoToCancel"));
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        Assert.That(sheet.IsSelectedRow("I500"), Is.True);
        AssertPlanUnchanged(before);
    }

    [Test]
    public async Task GoToRevealsATaskHiddenByTheTitleFilter()
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 99");
        await Ui.Until(() => sheet.RowIds.Count == FilteredRows);
        Assert.That(sheet.RowIds.Contains("I500"), Is.False);
        await OpenGoTo();
        await SubmitGoTo("#500");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        // Go To names the task the PMO asked for, so the accepted filter is cleared rather than refusing.
        await Ui.Until(() => Ui.Find<TextBox>("PlanSheetFilter").Text.Length == 0 && sheet.RowIds.Count == AllRows);
        Assert.That(sheet.IsSelectedRow("I500"), Is.True);
        Assert.That(sheet.IsSelected("I500", PlanField.Title), Is.True);
        await AssertFocused("I500", PlanField.Title);
    }

    [Test]
    public async Task GoToRevealsATaskHiddenByFolding()
    {
        await Ui.Run(() => sheet.SetFold("I997", true));
        await Ui.Until(() => sheet.IsFolded("I997") && sheet.RowIds.Count == FoldedRows);
        Assert.That(sheet.RowIds.Contains("I1000"), Is.False);
        await SelectCell("I500", PlanField.Title);
        await OpenGoTo();
        await SubmitGoTo("#1000");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        await Ui.Until(() => !sheet.IsFolded("I997") && sheet.RowIds.Contains("I1000"));
        Assert.That(sheet.IsSelectedRow("I1000"), Is.True);
        Assert.That(sheet.IsSelected("I1000", PlanField.Title), Is.True);
        await AssertFocused("I1000", PlanField.Title);
        await AssertRowInView("I1000");
    }

    [Test, Category("PlanSheetNative")]
    public async Task NativeCtrlHomeAndCtrlEndLandAndStayAtVersionScale()
    {
        var before = CapturePlan();
        await SelectCell("I900", PlanField.Title);
        await SheetNativeInput.ActivateWindow();
        // Ctrl+Home from row 900: the far jump must land on row 1 and stay there, not snap back when a
        // stale row container's bring-into-view arrives after the move (#256).
        await SheetNativeInput.Press(VirtualKey.Home, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelectedRow("I1") && Focused("I1", PlanField.Title));
        await Settle();
        await Ui.Run(() => {
            Assert.That(sheet.IsSelectedRow("I1"), Is.True, "A late bring-into-view must not move the selection.");
            Assert.That(Focused("I1", PlanField.Title), Is.True);
            Assert.That(Viewport().VerticalOffset, Is.Zero);
        });
        // Ctrl+End to the last visible task row, row 1000.
        await SheetNativeInput.Press(VirtualKey.End, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelectedRow("I1000") && Focused("I1000", LastField));
        await Settle();
        await Ui.Run(() => {
            Assert.That(sheet.IsSelectedRow("I1000"), Is.True);
            Assert.That(Focused("I1000", LastField), Is.True);
            Assert.That(Viewport().VerticalOffset, Is.EqualTo(Viewport().ScrollableHeight).Within(1));
        });
        // Back to row 1 again, and it still holds after the focus settles.
        await SheetNativeInput.Press(VirtualKey.Home, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelectedRow("I1") && Focused("I1", PlanField.Title));
        await Settle();
        await Ui.Run(() => {
            Assert.That(sheet.IsSelectedRow("I1"), Is.True);
            Assert.That(Focused("I1", PlanField.Title), Is.True);
            Assert.That(Viewport().VerticalOffset, Is.Zero);
        });
        AssertPlanUnchanged(before);
    }

    [Test, Category("PlanSheetNative")]
    public async Task NativeGridEdgeChordsMoveTheSelectionWithoutEditing()
    {
        var before = CapturePlan();
        await SelectCell("I500", LastField);
        await SheetNativeInput.ActivateWindow();
        await SheetNativeInput.Press(VirtualKey.Home);
        await Ui.Until(() => sheet.IsSelectedRow("I500") && sheet.IsSelected("I500", PlanField.Title));
        await SheetNativeInput.Press(VirtualKey.End);
        await Ui.Until(() => sheet.IsSelected("I500", LastField));
        await SheetNativeInput.Press(VirtualKey.Left, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelected("I500", PlanField.Title));
        await SheetNativeInput.Press(VirtualKey.Right, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelected("I500", LastField));
        await SheetNativeInput.Press(VirtualKey.Down, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelectedRow("I1000") && sheet.IsSelected("I1000", LastField));
        await SheetNativeInput.Press(VirtualKey.Up, VirtualKey.Control);
        await Ui.Until(() => sheet.IsSelectedRow("I1") && sheet.IsSelected("I1", LastField));
        await SheetNativeInput.Press(VirtualKey.Home, VirtualKey.Shift);
        await Ui.Until(() => sheet.IsSelected("I1", PlanField.Title));
        await AssertFocused("I1", PlanField.Title);
        // Ctrl+← on a parent row's title keeps its fold meaning; Shift+Ctrl+→ extends instead.
        await SelectCell("I997", PlanField.Title);
        await SheetNativeInput.Press(VirtualKey.Left, VirtualKey.Control);
        await Ui.Until(() => sheet.IsFolded("I997"));
        await Ui.Run(() => Assert.That(sheet.IsSelectedRow("I997"), Is.True));
        await SheetNativeInput.Press(VirtualKey.Right, VirtualKey.Control, VirtualKey.Shift);
        await Ui.Until(() => sheet.IsSelected("I997", LastField));
        await Ui.Run(() => Assert.That(sheet.IsFolded("I997"), Is.True, "Shift+Ctrl+→ extends the selection instead of folding."));
        AssertPlanUnchanged(before);
    }

    [Test, Category("PlanSheetNative")]
    public async Task NativeF5OpensGoToWithKeyboardFocusAndEnterMovesToTheNamedTask()
    {
        var before = CapturePlan();
        await SelectCell("I500", PlanField.Title);
        await SheetNativeInput.ActivateWindow();
        // The F5 handler opens ジャンプ before the key-up is routed, so the key-up lands in the popup:
        // send the chord without waiting for a sheet-root key-up.
        SheetNativeInput.Key(VirtualKey.F5, true); SheetNativeInput.Key(VirtualKey.F5, false);
        await Ui.Ready<TextBox>("PlanGoToInput");
        await Ui.Until(() => Ui.Find<TextBox>("PlanGoToInput").FocusState != FocusState.Unfocused);
        await Ui.Run(() => Assert.That(AutomationProperties.GetName(Ui.Find<TextBox>("PlanGoToInput")), Is.EqualTo("ジャンプ先のタスク名")));
        // Escape closes the input and returns focus to the selected cell.
        SheetNativeInput.Key(VirtualKey.Escape, true); SheetNativeInput.Key(VirtualKey.Escape, false);
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        await Ui.Until(() => Focused("I500", PlanField.Title));
        // F5, a name and Enter move to that task's title cell.
        SheetNativeInput.Key(VirtualKey.F5, true); SheetNativeInput.Key(VirtualKey.F5, false);
        await Ui.Ready<TextBox>("PlanGoToInput");
        await Ui.Until(() => Ui.Find<TextBox>("PlanGoToInput").FocusState != FocusState.Unfocused);
        await Ui.Run(() => Ui.Find<TextBox>("PlanGoToInput").Text = "#750");
        SheetNativeInput.Key(VirtualKey.Enter, true); SheetNativeInput.Key(VirtualKey.Enter, false);
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        await Ui.Until(() => sheet.IsSelectedRow("I750") && Focused("I750", PlanField.Title));
        // An unknown name keeps the input open with its reason and changes nothing.
        SheetNativeInput.Key(VirtualKey.F5, true); SheetNativeInput.Key(VirtualKey.F5, false);
        await Ui.Ready<TextBox>("PlanGoToInput");
        await Ui.Run(() => Ui.Find<TextBox>("PlanGoToInput").Text = "#1001");
        SheetNativeInput.Key(VirtualKey.Enter, true); SheetNativeInput.Key(VirtualKey.Enter, false);
        await Ui.Until(() => Ui.Find<TextBlock>("PlanGoToError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanGoToError").Text, Does.Contain("この版にありません"));
            Assert.That(sheet.IsSelectedRow("I750"), Is.True);
        });
        SheetNativeInput.Key(VirtualKey.Escape, true); SheetNativeInput.Key(VirtualKey.Escape, false);
        await Ui.Until(() => Ui.Popup<TextBox>("PlanGoToInput") is null);
        await Ui.Until(() => Focused("I750", PlanField.Title));
        AssertPlanUnchanged(before);
    }

    [Test, Category("PlanSheetNative")]
    public async Task NativeHomeAndEndKeepTheCaretWhileEditingACell()
    {
        var before = CapturePlan();
        await SelectCell("I500", PlanField.Title);
        await SheetNativeInput.ActivateWindow();
        await SheetNativeInput.Press(VirtualKey.F2);
        await Ui.Until(() => Ui.Find<PlanSheetCell>(CellId("I500", PlanField.Title)).Editing);
        await SheetNativeInput.Press(VirtualKey.End);
        await Ui.Run(() => Assert.That(Ui.Find<PlanSheetCell>(CellId("I500", PlanField.Title)).SelectionStart, Is.EqualTo("Task 500".Length)));
        await SheetNativeInput.Press(VirtualKey.Home);
        await Ui.Run(() => Assert.That(Ui.Find<PlanSheetCell>(CellId("I500", PlanField.Title)).SelectionStart, Is.Zero));
        await SheetNativeInput.Press(VirtualKey.Home, VirtualKey.Control);
        await Ui.Run(() => {
            Assert.That(Ui.Find<PlanSheetCell>(CellId("I500", PlanField.Title)).SelectionStart, Is.Zero);
            // The text box keeps Home/End while it edits; the selection does not jump to row 1.
            Assert.That(sheet.IsSelectedRow("I500"), Is.True);
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => !Ui.Find<PlanSheetCell>(CellId("I500", PlanField.Title)).Editing);
        AssertPlanUnchanged(before);
    }
}
