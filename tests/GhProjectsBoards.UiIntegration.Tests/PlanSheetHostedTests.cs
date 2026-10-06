using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Tests;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.E2E.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;
[TestFixture, NonParallelizable, Category("PlanSheet")]
internal sealed class PlanSheetHostedTests
{
    private string root = null!;
    private PlanSession session = null!;
    private PlanSheetView sheet = null!;
    private PlanClipboardContent clipboard = new("", null);
    private Func<Task<PlanClipboardContent>>? clipboardReader;
    private Action<PlanClipboardContent>? clipboardWriter;
    private string? previousMetrics;
    private string? metricsPath;
    private static readonly DateOnly Today = new(2026, 10, 5);
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        clipboardReader = null; clipboardWriter = null;
        root = Path.Combine(Path.GetTempPath(), "ghpb-sheet-" + Guid.NewGuid().ToString("N"));
        var performance = TestContext.CurrentContext.Test.Properties["Category"].Contains("PlanSheetPerformance");
        if (performance)
        {
            previousMetrics = Environment.GetEnvironmentVariable("GHPB_PLAN_METRICS");
            metricsPath = Path.Combine(root, "frames.jsonl");
            Environment.SetEnvironmentVariable("GHPB_PLAN_METRICS", metricsPath);
        }
        var rows = Enumerable.Range(1, performance ? 1000 : 100).Select(i => new PlanRow("I" + i, "Task " + i, "acme/repo") {
            Estimate = 8, Remaining = 8, Assignees = [performance ? "U" + ((i - 1) % 20 + 1) : "U1"],
            Predecessors = performance ? i % 10 == 1 ? [] : ["I" + (i - 1)] : i == 2 ? ["I1"] : []
        }).ToImmutableArray();
        session = await PlanSession.CreateAsync(new(root), new(new(new("github.com", 1), "P1"), new(rows, []),
            new(rows, new() { StatusDate = Today, DefaultRepository = "acme/repo",
                People = performance ? Enumerable.Range(1, 20).Select(i => new PlanResource("U" + i, "person-U" + i, 100, null, [])).ToImmutableArray() : [new("U1", "alice", 100, null, [])],
                Columns = [new(PlanField.Start, "start", "Start date", "DATE")] })), Today);
        await Ui.Run(() => sheet = new(session, () => clipboardReader is { } read ? read() : Task.FromResult(clipboard), value => { if (clipboardWriter is { } write) write(value); else clipboard = value; }));
        await Ui.Mount(sheet);
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
        try { await Ui.Idle(); }
        finally
        {
            try { await Ui.Unmount(sheet, check: false); await Ui.Idle(); }
            finally
            {
                await session.FlushAsync(); Directory.Delete(root, true);
                if (metricsPath is not null) Environment.SetEnvironmentVariable("GHPB_PLAN_METRICS", previousMetrics);
            }
        }
            }
        finally { Ui.EndTest(); }
    }
    [TestCase(false), TestCase(true), Category("PlanSheetReview4")]
    public async Task BlockingClipboardTimesOutWithoutBlockingUiOrChangingThePlan(bool paste)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Block()
        {
            started.TrySetResult();
            try { release.Wait(); }
            finally { ended.TrySetResult(); }
        }
        clipboardReader = () => { Block(); return Task.FromResult(new PlanClipboardContent("16", null)); };
        clipboardWriter = _ => Block();
        await Select(1, PlanField.Remaining);
        var before = session.Document.State; var undo = session.UndoCount;
        try
        {
            await Ui.Run(() => Ui.Click(paste ? "PlanSheetPaste" : "PlanSheetCopy"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The external owner is still blocked. The UI must be able to respond.
            await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanSheetFilter").IsEnabled, Is.True)).WaitAsync(TimeSpan.FromSeconds(3));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text == "クリップボードを使用できません。もう一度お試しください。");
            await Ui.Run(() => Assert.That(SelectProvider("PlanCell1_Remaining").IsSelected, Is.True));
        }
        finally { release.Set(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }
    [Test, Category("PlanSheetReview4")]
    public async Task InsertWithAFilterKeepsTheNewTitleFocusedAndExistingTasksUnchanged()
    {
        var before = session.Document.State.Rows;
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 2");
        await Ui.Until(() => !Ui.Find<ListView>("PlanTasks").Items.Contains("I1"));
        await Select(2, PlanField.Title); await Ui.ClickCommand("PlanSheetInsert");
        await Ui.Until(() => session.Document.State.Rows.Length == before.Length + 1);
        var added = session.Document.State.Rows.Single(r => !before.Any(old => old.Identity == r.Identity));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Contains(added.Identity));
        await Ui.Until(() => FocusManager.GetFocusedElement(Ui.Root.XamlRoot) is TextBox text && text.Text.Length == 0);
        await Ui.Run(() => ((TextBox)FocusManager.GetFocusedElement(Ui.Root.XamlRoot)).Text = "New planned task");
        await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == added.Identity).Title, Is.EqualTo("New planned task"));
        Assert.That(session.Document.State.Rows.Where(r => r.Identity != added.Identity), Is.EqualTo(before));
    }
    [TestCase(1), TestCase(2), Category("PlanSheetReview4")]
    public async Task RejectedZoomKeepsSelectionScaleAndLabelsTogether(int proposed)
    {
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedIndex = proposed);
        await Ui.Run(() => sheet.Run(() => Task.CompletedTask)); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ComboBox>("PlanGanttZoom").SelectedIndex, Is.Zero);
            Assert.That(Ui.Find<Rectangle>("PlanBar1").Width, Is.EqualTo(24));
            Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ComboBox>("PlanGanttZoom").SelectedIndex, Is.Zero);
            Assert.That(Ui.Find<TextBlock>("PlanTimelineMonths").Text, Does.Contain("2026"));
            var labels = Ui.Tree(sheet).OfType<TextBlock>().Where(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineLabel", StringComparison.Ordinal)).ToArray();
            Assert.That(labels, Is.Not.Empty);
            Assert.That(labels.All(t => t.Text.Length == 2 && t.Text.All(char.IsDigit)), Is.True);
        });
    }
    [Test, Category("PlanSheetReview4")]
    public async Task ReturningToExternalPredecessorDisplayClearsPendingWithoutParsingOrHistory()
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Predecessors, ImmutableArray.Create("outside"))]), Today);
        await Ui.Run(() => sheet.Refresh()); await Select(1, PlanField.Predecessors);
        var before = session.Document.State; var undo = session.UndoCount;
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Predecessors").Focus(FocusState.Programmatic));
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Predecessors").FocusState != FocusState.Unfocused);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Predecessors").Text = "2");
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Predecessors").Text = "計画外");
        await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before)); Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty));
    }
    [TestCase(PlanField.Start), TestCase(PlanField.End), Category("PlanSheetReview3")]
    public async Task ReturningToTheDisplayedCalculatedDateKeepsAutomaticSchedulingAndHistory(PlanField field)
    {
        await Select(1, field);
        var before = session.Document.State; var undo = session.UndoCount;
        string original = "";
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_" + field); original = cell.Text;
            Assert.That(cell.Focus(FocusState.Programmatic), Is.True);
        });
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_" + field).FocusState != FocusState.Unfocused);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_" + field).Text = original + "x");
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_" + field).Text = original);
        await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.Document.State.Rows[0].Fixed, Is.False);
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }
    [Test, Category("PlanSheetReview3")]
    public async Task RejectedFilterKeepsInvalidCellVisibleAndCanBeAppliedAfterCorrection()
    {
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 2");
        await Ui.Until(() => Ui.Find<TextBox>("PlanSheetFilter").Text.Length == 0); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Text, Is.Empty);
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Contains("I1"), Is.True);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("invalid"));
        });
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Remaining").FocusState != FocusState.Unfocused);
        await Edit(1, PlanField.Remaining, "4");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 2");
        await Ui.Until(() => !Ui.Find<ListView>("PlanTasks").Items.Contains("I1")); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Contains("I1"), Is.False);
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Contains("I2"), Is.True);
        });
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(4));
    }
    [Test, Category("PlanSheetReview3")]
    public async Task LeavingUnchangedEditingRestoresFullCellSelectionOnReturn()
    {
        await Select(1, PlanField.Title);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic);
            // Establish the edit mode shared by F2/double-click; focus events are native.
            cell.BeginEditing(); cell.Select(1, 0);
        });
        await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").FocusState == FocusState.Unfocused);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic));
        await Ui.Until(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); return cell.FocusState != FocusState.Unfocused && cell.SelectionLength == cell.Text.Length; });
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title");
            Assert.That(cell.SelectionLength, Is.EqualTo(cell.Text.Length));
        });
        Assert.That(session.UndoCount, Is.Zero);
    }
    [Test, Category("PlanSheetReview3")]
    public async Task UnloadingDuringClipboardReadCancelsPasteAndAllowsRemount()
    {
        var content = new TaskCompletionSource<PlanClipboardContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboardReader = () => { started.TrySetResult(); return content.Task; };
        await Select(1, PlanField.Remaining);
        var before = session.Document.State;
        await Ui.Run(() => Ui.Click("PlanSheetPaste"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Unmount(sheet); await Ui.Idle();
        await Ui.Mount(sheet);
        // Completing an OS read after detachment must not apply an old paste.
        content.SetResult(new("16", null)); await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before));
        await Edit(1, PlanField.Title, "Remounted");
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Remounted"));
    }
    [TestCase(false), TestCase(true), Category("PlanSheetReview3")]
    public async Task SheetUnloadsWithOpenPickerAndCanBeRemounted(bool calendar)
    {
        await Ui.Run(() => {
            if (calendar) Ui.Find<CalendarDatePicker>("PlanStatusDate").IsCalendarOpen = true;
            else Ui.Find<ComboBox>("PlanGanttZoom").IsDropDownOpen = true;
        });
        await Ui.Until(() => calendar ? Ui.Find<CalendarDatePicker>("PlanStatusDate").IsCalendarOpen : Ui.Find<ComboBox>("PlanGanttZoom").IsDropDownOpen);
        await Ui.Unmount(sheet); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(sheet.IsLoaded, Is.False);
            Assert.That(VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot).Any(p => p.IsOpen), Is.False);
        });
        await Ui.Mount(sheet);
        await Edit(1, PlanField.Title, "Remounted");
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Remounted"));
    }
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true), Category("PlanSheetReview2")]
    public async Task ClipboardContentionIsRetriedOrReportedWithoutChangingSelection(bool paste, bool persistent)
    {
        await Ui.Unmount(sheet);
        var attempts = 0;
        void Contention() { if (++attempts <= (persistent ? 100 : 1)) throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0)); }
        await Ui.Run(() => sheet = new(session, () => { Contention(); return Task.FromResult(new PlanClipboardContent("16", null)); }, value => { Contention(); clipboard = value; }));
        await Ui.Mount(sheet); await Select(1, PlanField.Remaining);
        var before = session.Document.State;
        await Ui.Run(() => Ui.Click(paste ? "PlanSheetPaste" : "PlanSheetCopy")); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(SelectProvider("PlanCell1_Remaining").IsSelected, Is.True);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo(persistent ? "クリップボードを使用できません。もう一度お試しください。" : ""));
        });
        if (persistent || !paste) Assert.That(session.Document.State, Is.EqualTo(before));
        else Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
        if (!persistent && !paste) Assert.That(clipboard.Text, Is.EqualTo("8"));
        Assert.That(attempts, Is.InRange(2, 5));
    }
    [TestCase(false), TestCase(true), Category("PlanSheetReview2")]
    public async Task MalformedClipboardMetadataReportsFailureWithoutEditing(bool missingValues)
    {
        clipboard = new("16", missingValues ? PlanJson.Text(new { project = session.Document.Project, fields = new[] { PlanField.Remaining }, identities = new[] { "I1" } }) : "invalid json");
        await Select(1, PlanField.Remaining);
        var before = session.Document.State;
        await Ui.Run(() => Ui.Click("PlanSheetPaste")); await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("コピー元を確認できません。"));
            Assert.That(SelectProvider("PlanCell1_Remaining").IsSelected, Is.True);
        });
        await Ui.Run(() => Ui.Click("PlanSheetCopy")); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("8"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty));
    }
    [TestCase("Latest"), TestCase("Task 1"), Category("PlanSheetReview2")]
    public async Task ReeditingBeforeQueuedCommitRunsPreservesLatestInputAndUndo(string latest)
    {
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Select(1, PlanField.Title);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Ui.Run(() => { _ = sheet.Run(() => release.Task); });
            await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.LostFocus += (_, _) => left.TrySetResult(); Assert.That(cell.Focus(FocusState.Programmatic), Is.True); });
            await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").FocusState != FocusState.Unfocused);
            await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Title").Text = "Earlier");
            await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
            await left.Task.WaitAsync(TimeSpan.FromSeconds(10));
            left = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.True));
            await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").FocusState != FocusState.Unfocused);
            await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Title").Text = latest);
            await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
            await left.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        await Ui.Run(() => sheet.FlushInput());
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo(latest));
        await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Earlier"));
    }
    private static ISelectionItemProvider SelectProvider(string id)
        => (ISelectionItemProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBox>(id)).GetPattern(PatternInterface.SelectionItem);
    private async Task Select(int row, PlanField field, bool extend = false)
    {
        await Ui.Ready<TextBox>($"PlanCell{row}_{field}");
        await Ui.Run(() => { var provider = SelectProvider($"PlanCell{row}_{field}"); if (extend) provider.AddToSelection(); else provider.Select(); });
    }
    private async Task Edit(int row, PlanField field, string text)
    {
        await Select(row, field);
        var before = session.UndoCount;
        await Ui.Run(() => { var cell = Ui.Find<TextBox>($"PlanCell{row}_{field}"); Assert.That(cell.Focus(FocusState.Programmatic), Is.True); cell.Text = text; });
        await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic), Is.True));
        await Ui.Until(() => session.UndoCount != before || Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Idle();
    }
    [Test, Category("PlanSheetReview")]
    public async Task ReturningToOriginalTextSurvivesHorizontalRefreshWithoutAnEdit()
    {
        await Select(1, PlanField.Title);
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic);
            cell.Text = "Task 1x"; cell.Text = "Task 1";
            Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset > 0);
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Text, Is.EqualTo("Task 1")));
        await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Task 1"));
        Assert.That(session.UndoCount, Is.Zero);
    }
    [TestCase(false), TestCase(true), Category("PlanSheetReview")]
    public async Task HistoryCommandKeepsInvalidNewRowAvailableForCorrection(bool redo)
    {
        await Ui.Run(() => Ui.Click("PlanSheetInsert")); await Ui.Idle();
        var index = Array.FindIndex(session.Document.State.Rows.ToArray(), r => r.Title == "");
        var number = index + 1;
        await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        var identity = session.Document.State.Rows[index].Identity;
        if (redo)
        {
            await Edit(number, PlanField.Title, "Temporary");
            await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
            Assert.That(session.RedoCount, Is.EqualTo(1));
        }
        var accepted = session.Document.State;
        var before = session.UndoCount;
        await Edit(number, PlanField.Remaining, "invalid");
        await Ui.Run(() => Ui.Find<ListView>("PlanTasks").ScrollIntoView("I75"));
        await Ui.Ready<TextBox>("PlanCell76_Title");
        await Ui.Run(() => Ui.Click(redo ? "PlanSheetRedo" : "PlanSheetUndo")); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == identity), Is.True);
        Assert.That(session.UndoCount, Is.EqualTo(before));
        Assert.That(session.Document.State, Is.EqualTo(accepted));
        await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        await Ui.Until(() => Ui.Find<TextBox>($"PlanCell{number}_Remaining").FocusState != FocusState.Unfocused);
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>($"PlanCell{number}_Remaining");
            Assert.That(cell.Text, Is.EqualTo("invalid"));
            Assert.That(cell.FocusState, Is.Not.EqualTo(FocusState.Unfocused));
        });
        await Edit(number, PlanField.Remaining, "4");
        await Ui.Run(() => sheet.FlushInput());
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == identity).Remaining, Is.EqualTo(4));
    }
    [Test, Category("PlanSheetReview")]
    public async Task InternalPredecessorCopyPreservesAnIdentityOutsideTheProject()
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Predecessors, ImmutableArray.Create("outside"))]), Today);
        await Ui.Unmount(sheet);
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Mount(sheet);
        await Select(1, PlanField.Predecessors);
        await Ui.Run(() => Ui.Click("PlanSheetCopy")); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("計画外"));
        await Select(3, PlanField.Predecessors);
        await Ui.Run(() => Ui.Click("PlanSheetPaste")); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Predecessors, Is.EqualTo(new[] { "outside" }));
    }
    [Test, Category("PlanSheetReview")]
    public async Task DefaultPlanningColumnsFitBesideGanttWithCompactRowsAndCalendarContext()
    {
        await Ui.Run(() => { sheet.Width = 1032; sheet.HorizontalAlignment = HorizontalAlignment.Left; });
        await Ui.Ready<TextBox>("PlanCell1_Predecessors");
        await Ui.Until(() => sheet.ActualWidth == 1032);
        await Ui.Run(() => {
            var last = Ui.Find<TextBox>("PlanCell1_Predecessors");
            var viewport = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            Assert.That(last.TransformToVisual(sheet).TransformPoint(new()).X + last.ActualWidth, Is.LessThanOrEqualTo(viewport.ActualWidth + 1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").ActualWidth, Is.GreaterThanOrEqualTo(230));
            var first = Ui.Find<TextBox>("PlanCell1_Title").TransformToVisual(sheet).TransformPoint(new()).Y;
            var second = Ui.Find<TextBox>("PlanCell2_Title").TransformToVisual(sheet).TransformPoint(new()).Y;
            Assert.That((second - first) * sheet.XamlRoot.RasterizationScale, Is.InRange(24, 28.1));
            Assert.That(Ui.Find<TextBlock>("PlanTimelineMonths").Text, Is.EqualTo("2026/9 – 10"));
        });
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Keyboard), Is.True));
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "compact-default-planning-columns"));
    }
    [Test, Category("PlanSheetReview")]
    public async Task DividerResizesBothViewportsAndDayHeaderKeepsYearContextAfterScrolling()
    {
        await Ui.Ready<TextBox>("PlanCell1_Title");
        double before = 0;
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            var provider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue);
            before = provider.Value;
            provider.SetValue(before - 100);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth == before - 100);
        await Ui.Run(() => {
            var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            Assert.That(chart.TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(before - 100).Within(1));
            chart.ChangeView(90 * 24, null, null, true);
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanTimelineMonths").Text.Contains("2027/1"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanTimelineMonths").Text, Does.StartWith("2026/12")));
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "compact-divider-year-boundary"));
    }
    [Test, Category("PlanSheetNative")]
    public async Task NativeDividerDragAndArrowKeyResizeTheSheetAndChart()
    {
        await Ui.Ready<PlanSheetDivider>("PlanSheetDivider");
        double before = 0, after = 0;
        await Ui.Run(() => before = Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth);
        var first = await SheetNativeInput.PointFor("PlanSheetDivider");
        await SheetNativeInput.Drag(first, new(first.X - 80, first.Y));
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth < before - 30);
        await Ui.Run(() => {
            after = Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth;
            Assert.That(Ui.Find<PlanSheetDivider>("PlanSheetDivider").Focus(FocusState.Keyboard), Is.True);
        });
        await SheetNativeInput.Press(Windows.System.VirtualKey.Right);
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth == after + 16);
    }
    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task NativeHistoryShortcutRefusesInvalidInputAndReturnsToItsCell(bool redo)
    {
        await Ui.Run(() => Ui.Click("PlanSheetInsert")); await Ui.Idle();
        var number = Array.FindIndex(session.Document.State.Rows.ToArray(), r => r.Title == "") + 1;
        if (redo)
        {
            await Edit(number, PlanField.Title, "Temporary");
            await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
        }
        var accepted = session.Document.State;
        await Edit(number, PlanField.Remaining, "invalid");
        await SheetNativeInput.Click("PlanCell3_Title");
        await SheetNativeInput.Press(redo ? Windows.System.VirtualKey.Y : Windows.System.VirtualKey.Z, Windows.System.VirtualKey.Control);
        await Ui.Until(() => Ui.Find<TextBox>($"PlanCell{number}_Remaining").FocusState != FocusState.Unfocused);
        Assert.That(session.Document.State, Is.EqualTo(accepted));
    }
    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task NativeEnterAndTabCommitOnceAndMoveToTheNextCell(bool tab)
    {
        await SheetNativeInput.Click("PlanCell1_Title");
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Title").Text = "Edited");
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Task 1"));
        await SheetNativeInput.Press(tab ? Windows.System.VirtualKey.Tab : Windows.System.VirtualKey.Enter);
        await Ui.Until(() => session.Document.State.Rows[0].Title == "Edited");
        await Ui.Until(() => Ui.Find<TextBox>(tab ? "PlanCell1_Assignees" : "PlanCell2_Title").FocusState != FocusState.Unfocused);
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }
    [Test, Category("PlanSheetNative")]
    public async Task NativeClipboardCopiesAndPastesTheDisplayedCell()
    {
        NativeClipboardScope? preserved = null;
        await Ui.Run(() => preserved = new NativeClipboardScope());
        try
        {
            await Ui.Unmount(sheet);
            await Ui.Run(() => sheet = new(session));
            await Ui.Mount(sheet);
            await Select(1, PlanField.Title);
            await Ui.Run(() => Ui.Click("PlanSheetCopy")); await Ui.Idle();
            await Select(3, PlanField.Title);
            await Ui.Run(() => Ui.Click("PlanSheetPaste")); await Ui.Idle();
            Assert.That(session.Document.State.Rows[2].Title, Is.EqualTo("Task 1"));
            Assert.That(session.UndoCount, Is.EqualTo(1));
        }
        finally { await Ui.Run(() => preserved?.Dispose()); }
    }
    [Test, Category("PlanSheetNative")]
    public async Task NativeCtrlDDeleteUndoRedoUseOneOperationForTheSelectedColumn()
    {
        await Edit(1, PlanField.Remaining, "16");
        await SheetNativeInput.Click("PlanCell1_Remaining");
        await SheetNativeInput.Click("PlanCell3_Remaining", Windows.System.VirtualKey.Shift);
        await SheetNativeInput.Press(Windows.System.VirtualKey.D, Windows.System.VirtualKey.Control);
        await Ui.Until(() => session.Document.State.Rows[2].Remaining == 16);
        Assert.That(session.UndoCount, Is.EqualTo(2));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Delete);
        await Ui.Until(() => session.Document.State.Rows.Take(3).All(r => r.Remaining is null));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Z, Windows.System.VirtualKey.Control);
        await Ui.Until(() => session.Document.State.Rows.Take(3).All(r => r.Remaining == 16));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Y, Windows.System.VirtualKey.Control);
        await Ui.Until(() => session.Document.State.Rows.Take(3).All(r => r.Remaining is null));
    }
    [Test, Category("PlanSheetNative")]
    public async Task NativeFillHandleChangesTheWholeColumnOnlyOnRelease()
    {
        await Edit(1, PlanField.Remaining, "16");
        await SheetNativeInput.Click("PlanCell1_Remaining");
        await SheetNativeInput.Drag("PlanFillHandle1_Remaining", "PlanCell3_Remaining", () => Ui.Run(() =>
            Assert.That(session.Document.State.Rows[2].Remaining, Is.EqualTo(8))));
        await Ui.Until(() => session.Document.State.Rows[2].Remaining == 16);
        Assert.That(session.UndoCount, Is.EqualTo(2));
    }
    [Test]
    public async Task BottomEmptyRowCreatesOneTaskInTheDefaultRepositoryAndUndoRemovesIt()
    {
        await Ui.Run(() => Ui.Find<ListView>("PlanTasks").ScrollIntoView(""));
        await Ui.Ready<TextBox>("PlanCell0_Title");
        await Edit(0, PlanField.Title, "New task");
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(101));
        Assert.That(session.Document.State.Rows[^1].Title, Is.EqualTo("New task"));
        Assert.That(session.Document.State.Rows[^1].Repository, Is.EqualTo("acme/repo"));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => Ui.Click("PlanSheetCopy")); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("New task"), "The created task stays selected without the empty placeholder.");
        await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(100));
    }
    [Test]
    public async Task InvalidTextSurvivesScrollingAndCorrectionDoesNotCreateAnUndoForTheOriginalValue()
    {
        await Edit(1, PlanField.Remaining, "-1");
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        await Ui.Run(() => Ui.Find<ListView>("PlanTasks").ScrollIntoView("I75"));
        await Ui.Ready<TextBox>("PlanCell75_Title");
        await Ui.Run(() => Ui.Find<ListView>("PlanTasks").ScrollIntoView("I1"));
        await Ui.Ready<TextBox>("PlanCell1_Remaining");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("-1")));
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining"); cell.Focus(FocusState.Programmatic); cell.Text = "8";
        });
        await Ui.Run(() => Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length == 0);
        await Ui.Idle();
        Assert.That(session.UndoCount, Is.Zero);
    }
    [Test]
    public async Task CalculatedDateFillUsesTheVisibleDateAndUndoRestoresTheInput()
    {
        Assert.That(session.Document.State.Rows[0].Start, Is.Null);
        await Select(1, PlanField.Start);
        await Select(3, PlanField.Start, true);
        await Ui.Run(() => Ui.Click("PlanSheetFillDown")); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].StartNoEarlierThan, Is.EqualTo(Today));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].StartNoEarlierThan, Is.Null);
    }
    [Test]
    public async Task SaveFailureKeepsTheEditAndRetryPersistsItWithoutAnotherUndo()
    {
        var store = new PlanStore(root);
        using (var writer = new FileStream(store.FileFor(session.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Edit(1, PlanField.Title, "Retained edit");
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Retained edit"));
            Assert.That(session.UndoCount, Is.EqualTo(1));
            await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanSheetRetrySave").Visibility, Is.EqualTo(Visibility.Visible)));
        }
        await Ui.Run(() => Ui.Click("PlanSheetRetrySave")); await Ui.Idle();
        await Ui.Until(() => Ui.Find<Button>("PlanSheetRetrySave").Visibility == Visibility.Collapsed);
        var loaded = await store.LoadAsync(session.Document.Project);
        Assert.That(loaded.Checkpoint!.Document.State.Rows[0].Title, Is.EqualTo("Retained edit"));
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }
    [TestCase(true), TestCase(false)]
    public async Task ATaskWithOnlyOneDateShowsTheKnownEndpoint(bool start)
    {
        await session.Execute(new InsertPlanRows([PlanRow.New("Partial", "acme/repo") with {
            Start = start ? Today : null, End = start ? null : Today }], "I1"), Today);
        await Ui.Unmount(sheet);
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Mount(sheet);
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Polygon>("PlanEndpoint1").Width, Is.EqualTo(8));
            Assert.That(Ui.Tree(sheet).OfType<Rectangle>().Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanBar1"), Is.False);
        });
    }
    [Test]
    public async Task SelectingAnOffscreenColumnMakesTheWholeCellReachableWithoutMovingTheChart()
    {
        await Ui.Run(() => Ui.Click("PlanSheetColumns"));
        await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumnStatus") is not null);
        await Ui.Run(() => { Ui.Popup<CheckBox>("PlanColumnStatus")!.IsChecked = true; Ui.Find<AppBarButton>("PlanSheetColumns").Flyout.Hide(); });
        await Ui.Idle();
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            var provider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue);
            provider.SetValue(560);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth > 0);
        await Select(1, PlanField.Status);
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").HorizontalOffset > 0);
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Status");
            var left = cell.TransformToVisual(sheet).TransformPoint(new()).X;
            var viewport = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            Assert.That(viewport.HorizontalOffset, Is.GreaterThan(0));
            Assert.That(left, Is.GreaterThanOrEqualTo(0));
            Assert.That(left + cell.ActualWidth, Is.LessThanOrEqualTo(viewport.ActualWidth + 1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset, Is.Zero);
        });
    }
    [Test]
    public async Task HorizontalGanttScrollDoesNotMoveSheetCellsOrItsHeader()
    {
        await Ui.Ready<TextBox>("PlanCell1_Title");
        double cellX = 0, headerX = 0;
        await Ui.Run(() => {
            cellX = Ui.Find<TextBox>("PlanCell1_Title").TransformToVisual(sheet).TransformPoint(new()).X;
            headerX = Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).X;
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Polyline>("PlanArrow1_2_2").Points.Count, Is.GreaterThan(3));
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Line>("PlanStatusLine1").X1, Is.EqualTo(120));
            Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset == 120);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ScrollViewer>("PlanSheetHorizontal").HorizontalOffset, Is.Zero);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(cellX));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(headerX));
        });
    }
    [Test, Category("PlanSheetPerformance")]
    public async Task ThousandTasksCommitToRenderedDatesAndBarsRecordsTwentySamples()
    {
        await Ui.Ready<TextBox>("PlanCell1_Remaining");
        for (var i = 0; i < 20; i++)
        {
            await Edit(1, PlanField.Remaining, i % 2 == 0 ? "16" : "8");
            await Ui.Until(() => File.Exists(metricsPath) && File.ReadAllLines(metricsPath!).Count(line => line.Contains("\"boundary\":\"rendered\"")) == i + 1);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBox>("PlanCell1_End").Text, Is.EqualTo(i % 2 == 0 ? "2026-10-06" : "2026-10-05"));
                Assert.That(Ui.Find<Rectangle>("PlanBar1").Width, Is.EqualTo(i % 2 == 0 ? 48 : 24));
            });
        }
        var records = File.ReadAllLines(metricsPath!).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var samples = records.Where(r => r.GetProperty("boundary").GetString() == "rendered").Select(r => r.GetProperty("elapsedMs").GetDouble()).Order().ToArray();
        Assert.That(samples.Length, Is.EqualTo(20));
        Assert.That(records.Length, Is.EqualTo(20), "Rejected, superseded and missing frames are not valid timing samples.");
        var evidence = Environment.GetEnvironmentVariable("GHPB_PLAN_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "ghpb-plan-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        File.Copy(metricsPath!, Path.Combine(evidence, "plan-frames.jsonl"), true);
        var summary = JsonSerializer.Serialize(new { rows = 1000, samples = samples.Length,
            medianMs = (samples[9] + samples[10]) / 2, maxMs = samples[^1], targetMs = 200,
            withinTarget = samples.All(ms => ms <= 200), environment = Environment.OSVersion.ToString(),
            boundary = "Cell commit through real Core acceptance and visible dates/bars to CompositionTarget.Rendered; autosave concurrent, hosted UI" });
        File.WriteAllText(Path.Combine(evidence, "plan-measurement.json"), summary);
        TestContext.Out.WriteLine(summary);
        var synthetic = Path.Combine(evidence, "synthetic-1000"); Directory.CreateDirectory(synthetic);
        File.WriteAllText(Path.Combine(synthetic, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        FakePlanEditor.Save(synthetic, new(session.Document.State.Rows.Select(r => new PlanFakeIssue(r, "", true)).ToImmutableArray(), 1001));
        // The same ordinary app opens this isolated Project through fake gh. Preserve the explicit status date.
        var store = new PlanStore(Path.Combine(synthetic, "data"));
        await PlanSession.CreateAsync(store, session.Document with { Baseline = new(session.Document.State.Rows, session.Document.Baseline.Columns) }, Today);
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "sheet-1000"));
        TestContext.Out.WriteLine("Synthetic ordinary-app GH_CONFIG_DIR: " + synthetic);
    }
    [Test]
    public async Task PendingTextStaysLocalUntilFocusCommitThenDatesBarsAndUndoChangeTogether()
    {
        await Ui.Ready<TextBox>("PlanCell1_Remaining");
        var undo = session.UndoCount;
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(cell.Focus(FocusState.Programmatic), Is.True); cell.Text = "16";
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        });
        await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanSheetCopy").Focus(FocusState.Programmatic), Is.True));
        await Ui.Idle();
        await Ui.Until(() => session.Document.State.Rows[0].Remaining == 16 || Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
            Assert.That(Ui.Find<TextBox>("PlanCell1_End").Text, Is.EqualTo("2026-10-06"));
            Assert.That(Ui.Find<Rectangle>("PlanBar1").Width, Is.EqualTo(48));
            Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
            Ui.Click("PlanSheetUndo");
        });
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        await Ui.Run(() => Ui.Click("PlanSheetRedo")); await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
    }
    [TestCase("paste"), TestCase("fill"), TestCase("clear")]
    public async Task NativeRangeCommandsAreOneUndoAndKeepSelection(string command)
    {
        {
            await Edit(1, PlanField.Remaining, "16");
            await Select(1, PlanField.Remaining);
            await Ui.Run(() => Ui.Click("PlanSheetCopy")); await Ui.Idle();
            await Select(2, PlanField.Remaining);
            await Select(3, PlanField.Remaining, true);
            if (command == "fill") { await Select(1, PlanField.Remaining); await Select(3, PlanField.Remaining, true); }
            var before = session.UndoCount;
            await Ui.Run(() => Ui.Click(command == "paste" ? "PlanSheetPaste" : command == "fill" ? "PlanSheetFillDown" : "PlanSheetClear"));
            await Ui.Idle();
            Assert.That(session.Document.State.Rows.Skip(1).Take(2).Select(r => r.Remaining), Is.All.EqualTo(command == "clear" ? null : (decimal?)16));
            Assert.That(session.UndoCount, Is.EqualTo(before + 1));
            await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
            Assert.That(session.Document.State.Rows.Skip(1).Take(2).Select(r => r.Remaining), Is.All.EqualTo(8));
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
        }
    }
    [Test]
    public async Task InsertAndIndentRollUpTheParentAndOutdentRestoresLeafEditing()
    {
        await Select(3, PlanField.Title);
        await Ui.Run(() => Ui.Click("PlanSheetInsert")); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(101));
        Assert.That(session.Document.State.Rows[2].Repository, Is.EqualTo("acme/repo"));
        await Ui.Run(() => Ui.Click("PlanSheetUndo")); await Ui.Idle();
        await Select(3, PlanField.Title);
        await Ui.Run(() => Ui.Click("PlanSheetIndent")); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Parent, Is.EqualTo("I2"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell2_Remaining").IsReadOnly, Is.True);
            Assert.That(Ui.Find<TextBox>("PlanCell2_Remaining").FontStyle, Is.EqualTo(Windows.UI.Text.FontStyle.Italic));
            Ui.Click("PlanSheetOutdent");
        });
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Parent, Is.Null);
    }
    [Test]
    public async Task FilterKeepsPlanIdsAndColumnToggleRestoresTheMappedHeader()
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 3");
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 12
            && (string)Ui.Find<ListView>("PlanTasks").Items[0] == "I3");
        await Ui.Ready<TextBlock>("PlanRowId3");
        await Ui.Ready<TextBox>("PlanCell3_Title");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanRowId3").Text, Is.EqualTo("3"));
            Ui.Click("PlanSheetColumns");
        });
        await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumnStart") is not null);
        await Ui.Run(() => Ui.Toggle(Ui.Popup<CheckBox>("PlanColumnStart")!)); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(sheet).OfType<TextBox>().Any(c =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanCell3_Start" && c.Visibility == Visibility.Visible), Is.False);
            Ui.Toggle(Ui.Popup<CheckBox>("PlanColumnStart")!);
        });
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PlanHeaderStart");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderStart").Text, Is.EqualTo("Start date")));
    }
    [Test]
    public async Task StatusDateAndSelectionShowTheSchedulerReasonAndCycleRejectionNamesIds()
    {
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        await Ui.Idle(); await Select(1, PlanField.Title);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain("状況日")));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(new DateOnly(2026, 10, 20)));
        await Edit(1, PlanField.Predecessors, "2");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Does.Contain("#1").And.Contain("#2")));
        Assert.That(session.Document.State.Rows[0].Predecessors, Is.Empty);
    }
    [TestCase("日", 24d), TestCase("週", 8d), TestCase("月", 2d)]
    public async Task ZoomAndVerticalScrollKeepSheetAndGanttOnTheSameRows(string zoom, double width)
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = zoom);
        await Ui.Idle();
        await Ui.Until(() => {
            var labels = Ui.Tree(sheet).OfType<TextBlock>().Where(label =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(label).StartsWith("PlanTimelineLabel", StringComparison.Ordinal)).ToArray();
            return labels.Length > 1 && labels.All(label => label.ActualWidth > 0);
        });
        double headerY = 0;
        await Ui.Run(() => {
            headerY = Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).Y;
            var labels = Ui.Tree(sheet).OfType<TextBlock>().Where(label =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(label).StartsWith("PlanTimelineLabel", StringComparison.Ordinal))
                .Select(label => (Left: label.TransformToVisual(sheet).TransformPoint(new()).X, Width: label.ActualWidth)).OrderBy(label => label.Left).ToArray();
            Assert.That(labels.Length, Is.GreaterThan(1));
            for (var i = 1; i < labels.Length; i++) Assert.That(labels[i].Left, Is.GreaterThanOrEqualTo(labels[i - 1].Left + labels[i - 1].Width), "Timeline labels must not overlap.");
            Assert.That(Ui.Find<Rectangle>("PlanBar1").Width, Is.EqualTo(width));
            var list = Ui.Find<ListView>("PlanTasks"); list.ScrollIntoView(list.Items[74]);
        });
        await Ui.Ready<TextBox>("PlanCell75_Title");
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell75_Title");
            var bar = Ui.Find<Rectangle>("PlanBar75");
            var y = cell.TransformToVisual(sheet).TransformPoint(new()).Y + cell.ActualHeight / 2;
            var barY = bar.TransformToVisual(sheet).TransformPoint(new()).Y + bar.ActualHeight / 2;
            Assert.That(barY, Is.EqualTo(y).Within(1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").ScrollableWidth, Is.GreaterThan(0));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(headerY));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "sheet-gantt-" + zoom));
    }
}

