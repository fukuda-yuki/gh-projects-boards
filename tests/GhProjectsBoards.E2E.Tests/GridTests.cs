using System.Diagnostics;
using System.IO;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Tools;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;
using Application = FlaUI.Core.Application;

namespace GhProjectsBoards.E2E.Tests;

[TestFixture]
[Category("E2E")]
[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class GridTests
{
    [TestCase(true)]
    [TestCase(false)]
    [Category("RealIme")]
    public void JapaneseImeCompositionConfirmationAndCancellationKeepCellAndHistoryBoundaries(bool startWithF2)
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_REAL_IME") != "1") Assert.Ignore("Requires Microsoft Japanese IME in alphanumeric mode.");
        using var app = new GridAppDriver();
        app.SelectCell(2, 0);
        if (startWithF2)
        {
            Keyboard.Type(VirtualKeyShort.F2);
            GridAppDriver.Wait(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridCellEditor")) is not null);
        }
        try
        {
            Keyboard.TypeVirtualKeyCode(0x16); // VK_IME_ON; actual physical input, not Unicode insertion.
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            foreach (var key in new[] { VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H,
                VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O })
            {
                Keyboard.Type(key);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
                TestContext.Progress.WriteLine($"Physical {key}: {app.EditorText}");
            }
            GridAppDriver.Wait(() => app.EditorText == "にほんご");
            app.Capture("ime-physical-composition");
            Keyboard.Type(VirtualKeyShort.SPACE);
            GridAppDriver.Wait(() => app.EditorText == "日本語");
            app.Capture("ime-conversion");
            Keyboard.Type(VirtualKeyShort.RETURN);
            GridAppDriver.Wait(() => app.Element("GridCellEditor").Properties.HasKeyboardFocus.Value);
            Assert.That(app.EditorText, Is.EqualTo("日本語"));
            Assert.That(app.Value(2, 0), Is.EqualTo("試験データ 002"));
            Assert.That(app.Button("GridUndoButton").IsEnabled, Is.False);
            app.Capture("ime-confirmation-stays-in-cell");
            Keyboard.Type(VirtualKeyShort.RETURN);
            GridAppDriver.Wait(() => app.Value(2, 0) == "日本語");
            Assert.That(app.Cell(3, 0).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
            Assert.That(app.Button("GridUndoButton").Name, Does.Contain("1 操作"));
            app.SelectCell(2, 0);
            Keyboard.Type(VirtualKeyShort.F2);
            GridAppDriver.Wait(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridCellEditor")) is not null);
            foreach (var key in new[] { VirtualKeyShort.KEY_T, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_R, VirtualKeyShort.KEY_I })
            {
                Keyboard.Type(key);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            }
            GridAppDriver.Wait(() => app.EditorText == "とり");
            Keyboard.Type(VirtualKeyShort.SPACE);
            app.Capture("ime-cancel-before");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            GridAppDriver.Wait(() => app.Element("GridCellEditor").Properties.HasKeyboardFocus.Value);
            Assert.That(app.Button("GridUndoButton").Name, Does.Contain("1 操作"));
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            GridAppDriver.Wait(() => app.Value(2, 0) == "日本語");
            Assert.That(app.Button("GridUndoButton").Name, Does.Contain("1 操作"));
            app.Capture("ime-cancel-preserves-commit");
            app.Button("GridUndoButton").Invoke();
            GridAppDriver.Wait(() => app.Value(2, 0) == "試験データ 002");
            app.AssertNoGhCalls();
        }
        finally
        {
            // A failed assertion can leave composition active. Cancel only an existing
            // editor before restoring input mode, so cleanup cannot close the dialog via Esc.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridCellEditor")) is null) break;
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            }
            Keyboard.TypeVirtualKeyCode(0x1A); // Restore the required starting alphanumeric mode.
            FlaUI.Core.Input.Wait.UntilInputIsProcessed();
        }
        app.CloseNormally();
    }

    [Test]
    [Category("RealIme")]
    public void JapaneseImeReconversionSurvivesCellCommitAndOneUndo()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_REAL_IME") != "1") Assert.Ignore("Requires the installed Microsoft Japanese IME.");
        using var app = new GridAppDriver();
        app.EditText(1, 0, "日本語", VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Value(1, 0) == "日本語");
        app.SelectCell(1, 0);
        Keyboard.Type(VirtualKeyShort.F2);
        GridAppDriver.Wait(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridCellEditor")) is not null);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
        GridAppDriver.Wait(() => app.MenuItem("にほんご") is not null);
        app.Capture("ime-reconversion-menu");
        app.MenuItem("にほんご")!.Click();
        GridAppDriver.Wait(() => app.Element("GridCellEditor").Patterns.Text.Pattern.DocumentRange.GetText(-1) == "にほんご");
        app.Capture("ime-reconversion-selected");
        Keyboard.Type(VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Value(1, 0) == "にほんご");
        Assert.That(app.Cell(2, 0).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        Assert.That(app.Button("GridUndoButton").Name, Does.Contain("2 操作"));
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 0) == "日本語");
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void OrdinaryExecutableOpensAnOfflineHundredRowGrid()
    {
        using var app = new GridAppDriver();
        Assert.That(app.Text("GridRowCount"), Is.EqualTo("100 行"));
        Assert.That(app.Text("GridPrototypeNotice"), Does.Contain("試験データ").And.Contain("終了時破棄"));
        Assert.That(app.Element("PrototypeGrid"), Is.Not.Null);
        app.AssertNoGhCalls();
        app.Capture("grid-open");
        app.CloseNormally();
    }

    [Test]
    public void CellEditingKeyboardNavigationAndUndoUseCommittedOperations()
    {
        using var app = new GridAppDriver();
        app.EditText(1, 0, "日本語の編集", VirtualKeyShort.TAB);
        GridAppDriver.Wait(() => app.Value(1, 0) == "日本語の編集");
        Assert.That(app.Cell(1, 1).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        app.EditText(1, 2, "42.5", VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Value(1, 2) == "42.5");
        Assert.That(app.Cell(2, 2).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 2) == "0.1");
        Assert.That(app.Value(1, 0), Is.EqualTo("日本語の編集"));
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 0) == "試験データ 001");
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void RangePasteClearAndErrorRecoveryAreAtomicInTheOrdinaryGrid()
    {
        using var app = new GridAppDriver();
        app.SelectCell(1, 0);
        app.Paste("範囲1\tClosed\t0\t2028-02-29\t\r\n範囲2\t\t-2.5\t\tLow\r\n");
        GridAppDriver.Wait(() => app.Value(1, 0) == "範囲1");
        Assert.That(app.Value(1, 4), Is.EqualTo("High"));
        Assert.That(app.Value(2, 1), Is.EqualTo("Open"));
        Assert.That(app.Value(2, 4), Is.EqualTo("Low"));
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 0) == "試験データ 001");
        Assert.That(app.Value(2, 0), Is.EqualTo("試験データ 002"));
        app.SelectCell(1, 2);
        app.Paste("999\tinvalid\tLow");
        GridAppDriver.Wait(() => app.Text("GridStatus").Contains("適用していません"));
        Assert.That(app.Value(1, 2), Is.EqualTo("0.1"));
        Assert.That(app.Button("GridUndoButton").IsEnabled, Is.False);
        app.Paste("999\t2026-12-31\tLow");
        GridAppDriver.Wait(() => app.Value(1, 2) == "999");
        app.SelectCell(1, 2);
        app.Button("GridClearButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 2) == "");
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 2) == "999");
        app.Capture("grid-range");
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void NewRowCanBeEditedBeyondTheVirtualizedViewportAndUndone()
    {
        using var app = new GridAppDriver();
        app.Button("GridAddRowButton").Invoke();
        GridAppDriver.Wait(() => app.Text("GridRowCount") == "101 行");
        app.EditText(101, 0, "新規課題", VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Value(101, 0) == "新規課題");
        Assert.That(app.Value(101, 1), Is.EqualTo("Open"));
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(101, 0) == "");
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Text("GridRowCount") == "100 行");
        Assert.That(app.Value(100, 0), Is.EqualTo("試験データ 100"));
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void RectangularSelectionUsesKeyboardPasteClearAndUndoAtomically()
    {
        using var app = new GridAppDriver();
        app.SelectCell(1, 2);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.DOWN);
        GridAppDriver.Wait(() => app.Element("PrototypeGrid").Patterns.Selection.Pattern.Selection.Value.Length == 6);
        Assert.That(app.Element("PrototypeGrid").Patterns.Selection.Pattern.Selection.Value, Has.Length.EqualTo(6));
        System.Windows.Clipboard.SetText("21\t2026-10-10\tLow\n22\t2026-10-11\tHigh");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        GridAppDriver.Wait(() => app.Value(2, 2) == "22");
        Assert.That(app.Value(1, 4), Is.EqualTo("Low"));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        GridAppDriver.Wait(() => app.Value(1, 2) == "0.1");
        app.SelectCell(1, 2);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.DOWN);
        Keyboard.Type(VirtualKeyShort.DELETE);
        GridAppDriver.Wait(() => app.Value(1, 2) == "");
        Assert.That(app.Value(2, 4), Is.Empty);
        Assert.That(app.Button("GridUndoButton").Name, Does.Contain("1 操作"));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        GridAppDriver.Wait(() => app.Value(2, 4) == "Medium");
        app.SelectCell(1, 1);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.RIGHT);
        Keyboard.Type(VirtualKeyShort.DELETE);
        GridAppDriver.Wait(() => app.Text("GridStatus").Contains("適用していません"));
        Assert.That(app.Value(1, 1), Is.EqualTo("Open"));
        Assert.That(app.Value(1, 2), Is.EqualTo("0.1"));
        Assert.That(app.Button("GridUndoButton").IsEnabled, Is.False);
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void InvalidEditorCanBeCorrectedOrCancelledWithoutRollingBackCommittedCells()
    {
        using var app = new GridAppDriver();
        app.EditText(1, 0, "保持する編集", VirtualKeyShort.TAB);
        app.EditText(1, 2, "bad", VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Text("GridStatus").Contains("適用していません"));
        Assert.That(app.EditorText, Is.EqualTo("bad"));
        Assert.That(app.Element("GridCellEditor").Properties.HasKeyboardFocus.Value, Is.True);
        Assert.That(app.Value(1, 2), Is.EqualTo("0.1"));
        app.Capture("grid-invalid-editor");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("12.5");
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        GridAppDriver.Wait(() => app.Value(1, 2) == "12.5");
        Assert.That(app.Cell(1, 1).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        app.SelectCell(2, 0);
        Assert.That(app.Value(1, 0), Is.EqualTo("保持する編集"));
        Assert.That(app.Value(1, 2), Is.EqualTo("12.5"));
        app.SelectCell(1, 2);
        Keyboard.Type(VirtualKeyShort.F2);
        GridAppDriver.Wait(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridCellEditor")) is not null);
        Keyboard.Type(VirtualKeyShort.DELETE);
        GridAppDriver.Wait(() => app.EditorText == "");
        Assert.That(app.Button("GridUndoButton").Name, Does.Contain("2 操作"));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_Z);
        GridAppDriver.Wait(() => app.EditorText == "12.5");
        Assert.That(app.Button("GridUndoButton").Name, Does.Contain("2 操作"));
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        GridAppDriver.Wait(() => app.Value(1, 2) == "12.5");
        app.Button("GridUndoButton").Invoke();
        GridAppDriver.Wait(() => app.Value(1, 2) == "0.1");
        Assert.That(app.Value(1, 0), Is.EqualTo("保持する編集"));
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void ChoiceEditorsAndArrowKeysKeepFocusWithinTheExpectedCell()
    {
        using var app = new GridAppDriver();
        app.SelectCell(1, 1);
        Keyboard.Type(VirtualKeyShort.F2);
        GridAppDriver.Wait(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridChoiceEditor")) is not null);
        Keyboard.Type(VirtualKeyShort.DOWN);
        Keyboard.Type(VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Value(1, 1) == "Closed");
        Assert.That(app.Cell(2, 1).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        app.SelectCell(1, 4);
        Keyboard.Type(VirtualKeyShort.F2);
        GridAppDriver.Wait(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("GridChoiceEditor")) is not null);
        Keyboard.Type(VirtualKeyShort.END);
        Keyboard.Type(VirtualKeyShort.TAB);
        GridAppDriver.Wait(() => app.Value(1, 4) == "Low");
        Assert.That(app.Cell(2, 0).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        Keyboard.Type(VirtualKeyShort.RIGHT);
        Assert.That(app.Cell(2, 1).Patterns.SelectionItem.Pattern.IsSelected.Value, Is.True);
        app.EditText(2, 0, "discard", VirtualKeyShort.ESCAPE);
        Assert.That(app.Value(2, 0), Is.EqualTo("試験データ 002"));
        app.AssertNoGhCalls();
        app.CloseNormally();
    }

    [Test]
    public void TypingIntoASelectedCellRetainsTheFirstCharacter()
    {
        using var app = new GridAppDriver();
        app.SelectCell(2, 0);
        Keyboard.Type("abc");
        Keyboard.Type(VirtualKeyShort.RETURN);
        GridAppDriver.Wait(() => app.Value(2, 0) == "abc");
        app.CloseNormally();
    }
}

internal sealed class GridAppDriver : IDisposable
{
    private readonly DesktopDpiScope dpi;
    private readonly UIA3Automation automation;
    private readonly Process process;
    private readonly Application application;
    private readonly string fixtureDirectory;
    private readonly Window main;
    private readonly System.Windows.IDataObject? previousClipboard;
    public Window Window { get; private set; }
    public string Artifacts { get; }
    public double GridOpenMilliseconds { get; }

    public GridAppDriver()
    {
        if (Environment.GetEnvironmentVariable("GHPB_RUN_E2E") != "1") Assert.Ignore("Run scripts/Test-E2E.ps1 on an unlocked desktop.");
        dpi = new DesktopDpiScope();
        previousClipboard = System.Windows.Clipboard.GetDataObject();
        Artifacts = Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")!;
        fixtureDirectory = Path.Combine(Artifacts, "grid-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        File.WriteAllText(Path.Combine(fixtureDirectory, "scenario.json"), "{}");
        var executable = Environment.GetEnvironmentVariable("GHPB_E2E_APP_PATH")!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["GH_CONFIG_DIR"] = fixtureDirectory;
        process = Process.Start(start)!;
        automation = new UIA3Automation();
        application = Application.Attach(process.Id);
        main = application.GetMainWindow(automation, TimeSpan.FromSeconds(20))
            ?? throw new AssertionException("The ordinary executable did not open its main window.");
        Window = main;
        try
        {
            Element("ExecutablePath").AsTextBox().Text = Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!;
            Element("HostInput").AsTextBox().Text = "example.test";
            var started = Stopwatch.GetTimestamp();
            Button("OpenGridPrototypeButton").Invoke();
            Wait(() => main.ModalWindows.Any(window => window.AutomationId == "GridPrototypeWindow"));
            Window = main.ModalWindows.Single(window => window.AutomationId == "GridPrototypeWindow");
            Wait(() => Window.FindFirstDescendant(cf => cf.ByAutomationId("PrototypeGrid")) is not null);
            Wait(() => Text("GridRowCount") == "100 行" && Value(1, 0) == "試験データ 001");
            GridOpenMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch
        {
            Capture("grid-open-failure");
            Dispose();
            throw;
        }
    }

    public AutomationElement Element(string id) => Window.FindFirstDescendant(cf => cf.ByAutomationId(id))
        ?? throw new AssertionException($"Missing control: {id}");
    public Button Button(string id) => Element(id).AsButton();
    public AutomationElement? MenuItem(string name) => automation.GetDesktop().FindFirstDescendant(cf =>
        cf.ByProcessId(process.Id).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem)).And(cf.ByName(name)));
    public string Text(string id) => Element(id).Name;
    public string EditorText => Element("GridCellEditor").Patterns.Text.Pattern.DocumentRange.GetText(-1);
    // WPF substitutes a row/column description when an empty cell's Name is requested.
    // ValuePattern reports the actual cell content, including an explicit empty value.
    public string Value(int rowId, int column) => Cell(rowId, column).Patterns.Value.Pattern.Value.Value;
    public GridCell Cell(int rowId, int column)
    {
        var grid = Element("PrototypeGrid");
        var row = grid.Patterns.ItemContainer.Pattern.FindItemByProperty(null,
            automation.PropertyLibrary.Element.Name, $"行 {rowId:000}")
            ?? throw new AssertionException($"Missing row identity: {rowId}");
        if (row.Patterns.VirtualizedItem.IsSupported) row.Patterns.VirtualizedItem.Pattern.Realize();
        if (row.Patterns.ScrollItem.IsSupported) row.Patterns.ScrollItem.Pattern.ScrollIntoView();
        var realized = grid.FindFirstDescendant(cf => cf.ByAutomationId($"GridRow_{rowId}"))
            ?? throw new AssertionException($"Row {rowId} was not realized with its stable identity.");
        return realized.AsGridRow().Cells[column];
    }
    public void SelectCell(int rowId, int column) => Cell(rowId, column).Click();
    public void EditText(int rowId, int column, string text, VirtualKeyShort commit)
    {
        SelectCell(rowId, column);
        Keyboard.Type(VirtualKeyShort.F2);
        Wait(() => Window.FindFirstDescendant(cf => cf.ByAutomationId("GridCellEditor")) is not null);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        Keyboard.Type(commit);
    }
    public void Paste(string text)
    {
        System.Windows.Clipboard.SetText(text);
        Button("GridPasteButton").Invoke();
    }
    public void AssertNoGhCalls() => Assert.That(File.Exists(Path.Combine(fixtureDirectory, "calls.jsonl")), Is.False,
        "Grid operations must not invoke the selected gh boundary.");
    public static void Wait(Func<bool> condition) => Assert.That(
        Retry.WhileFalse(condition, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(40)).Result,
        Is.True, "The ordinary grid did not reach the expected UI state.");
    public void CloseNormally()
    {
        Window.Close();
        // IME helper windows may outlive the grid. Verify this window closes and then
        // require the original application process to exit normally as well.
        Wait(() => main.ModalWindows.All(window => window.AutomationId != "GridPrototypeWindow"));
        Wait(() => main.IsEnabled);
        main.Close();
        Wait(() => process.HasExited);
        Assert.That(process.ExitCode, Is.Zero);
    }
    public void Capture(string name)
    {
        try
        {
            var path = Path.Combine(Artifacts, $"{name}-{Guid.NewGuid():N}.png");
            using var capture = FlaUI.Core.Capturing.Capture.Element(Window);
            capture.ToFile(path);
            TestContext.AddTestAttachment(path);
        }
        catch (Exception ex) { TestContext.Progress.WriteLine($"Capture unavailable: {ex.GetType().Name}"); }
    }
    public void Dispose()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            TestContext.Progress.WriteLine(process.HasExited
                ? $"Owned application exited before cleanup: {process.ExitCode} (0x{process.ExitCode:X8})."
                : "Owned application was still running before failure cleanup.");
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed && !process.HasExited)
            Capture("grid-failure");
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        application.Dispose();
        process.Dispose();
        automation.Dispose();
        dpi.Dispose();
        if (previousClipboard is not null) System.Windows.Clipboard.SetDataObject(previousClipboard, true);
        else System.Windows.Clipboard.Clear();
    }
}
