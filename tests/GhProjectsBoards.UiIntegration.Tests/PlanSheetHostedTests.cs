using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.E2E.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;
[TestFixture, NonParallelizable, Category("PlanSheet")]
internal sealed partial class PlanSheetHostedTests
{
    private string root = null!;
    private PlanSession session = null!;
    private PlanSheetView sheet = null!;
    private Grid sheetHost = null!;
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
        var performance = TestContext.CurrentContext.Test.Properties["Category"].Contains("PlanSheetPerformance")
            || TestContext.CurrentContext.Test.MethodName == nameof(LongHorizonDayZoomAndScrollSeparatesLayoutFromCapture);
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
        // #236: a stored date is a plan value and a read does not invent one, so the shared sheet fixture states
        // the plan the app had laid out — an end per row, from which the sheet derives every start. Seeding the
        // end keeps the start calculated, which the date-origin cases require and the date cells need to exist.
        rows = rows.Select(row => row with { End = Today }).ToImmutableArray();
        session = await PlanSession.CreateAsync(new(root), new(new(new("github.com", 1), "P1"), new(rows, []),
            new(rows, new() { StatusDate = Today, DefaultRepository = "acme/repo",
                People = performance ? Enumerable.Range(1, 20).Select(i => new PlanResource("U" + i, "person-U" + i, 100, null)).ToImmutableArray() : [new("U1", "alice", 100, null)],
                Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "End date", "DATE"),
                    new(PlanField.Remaining, "remaining", "Remaining", "NUMBER")] })), Today);
        if (TestContext.CurrentContext.Test.MethodName is nameof(VersionRowsScrollRoundTripRecordsFrameIntervalsAndViewportPopulation)
            or nameof(VersionRowsCommitToRenderedDatesAndBarsRecordsTwentySamples)
            or nameof(VersionRowsFilterApplicationRecordsPauseAndFrameTimings))
        {
            var plan = GhProjectsBoards.Tests.EvaluationFixture.ReadPlan();
            var snapshot = GhProjectsBoards.Tests.EvaluationFixture.Simulate(plan, [plan.StatusDate]).Single();
            session = await PlanSession.CreateAsync(new(root), snapshot.Document, plan.StatusDate);
        }
        await Ui.Run(() => sheet = new(session, () => clipboardReader is { } read ? read() : Task.FromResult(clipboard), value => { if (clipboardWriter is { } write) write(value); else clipboard = value; }));
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
    }
    private async Task SetColumnVisible(string key,bool visible)
    {
        await Ui.Run(async () => {
            await sheet.FlushInput();
            var save=await session.Execute(new ReplacePlanSettings(PlanSheetColumns.Change(session.Document,key,visible)),Today);
            Assert.That(save.Succeeded,Is.True,save.Error); sheet.Refresh();
        });
        await Ui.Idle();
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
        try { await Ui.Idle(); }
        finally
        {
            try { await Ui.Unmount(sheetHost, check: false); await Ui.Idle(); await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1400, 1000))); }
            finally
            {
                await session.FlushAsync(); Directory.Delete(root, true);
                if (metricsPath is not null) Environment.SetEnvironmentVariable("GHPB_PLAN_METRICS", previousMetrics);
            }
        }
            }
        finally { Ui.EndTest(); }
    }
    [Test, Category("PlanSheetNative")]
    public async Task RequirementLinkShortcutsRenderChainUndoUnlinkAndFilterReasons()
    {
        ImmutableArray<PlanRow> rows = [new("I1", "Requirement", "acme/repo"),
            new("I2", "Task A", "acme/repo") { Parent = "I1" },
            new("I3", "Task B", "acme/repo") { Parent = "I1" },
            new("I4", "Task C", "acme/repo") { Parent = "I1" }];
        await MountPresentation(rows, rows);
        await SelectFocusedCell(1, PlanField.Estimate);
        var undo = session.UndoCount;
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2, Windows.System.VirtualKey.Control);
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell3_Predecessors").Text == "2"
            && Ui.Find<TextBox>("PlanCell4_Predecessors").Text == "3");
        Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Z, Windows.System.VirtualKey.Control);
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell3_Predecessors").Text == ""
            && Ui.Find<TextBox>("PlanCell4_Predecessors").Text == "");
        await SelectFocusedCell(2, PlanField.Title); await SelectFocusedCell(4, PlanField.Title, true);
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2, Windows.System.VirtualKey.Control);
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell4_Predecessors").Text == "3");
        await SelectFocusedCell(1, PlanField.Estimate);
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2, Windows.System.VirtualKey.Control, Windows.System.VirtualKey.Shift);
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell3_Predecessors").Text == ""
            && Ui.Find<TextBox>("PlanCell4_Predecessors").Text == "");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task");
        await Ui.Until(() => AutomationProperties.GetHelpText(Ui.CommandButton("PlanSheetLinkTasks")) == "絞り込み中はリンクできません");
        await Ui.Run(() => {
            foreach (var (id, label, reason) in new[] {
                ("PlanSheetLinkTasks", "タスクをリンク", "絞り込み中はリンクできません"),
                ("PlanSheetUnlinkTasks", "リンクを解除", "絞り込み中はリンクを解除できません") }) {
                var button = Ui.CommandButton(id);
                Assert.That(button.Label, Is.EqualTo(label)); Assert.That(button.IsEnabled, Is.False);
                Assert.That(button.AllowFocusWhenDisabled, Is.True);
                Assert.That(AutomationProperties.GetHelpText(button), Is.EqualTo(reason));
                Assert.That(ToolTipService.GetToolTip(button), Is.EqualTo(reason));
            }
        });
        await SelectFocusedCell(2, PlanField.Title);
        var before = session.Document; var history = (session.UndoCount, session.RedoCount);
        foreach (var unlink in new[] { false, true }) {
            await SheetNativeInput.Press(Windows.System.VirtualKey.F2, unlink
                ? [Windows.System.VirtualKey.Control, Windows.System.VirtualKey.Shift] : [Windows.System.VirtualKey.Control]);
            var reason = unlink ? "絞り込み中はリンクを解除できません" : "絞り込み中はリンクできません";
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text == reason);
            Assert.That(session.Document, Is.SameAs(before));
            Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo(history));
        }
    }
    [TestCase(false), TestCase(true)]
    public async Task SummaryCopiesBaselineFieldsAndExposesEveryAssignee(bool held)
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo") { Assignees = held ? ["U1", "U2"] : [], Predecessors = held ? ["I3"] : [], StartNoEarlierThan = held ? Today : null, Fixed = held, Status = held ? "Todo" : null };
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1", Estimate = 8, Assignees = ["U1"] };
        var predecessor = new PlanRow("I3", "Earlier", "acme/repo");
        await MountPresentation([parent with { Assignees = ["local"], Predecessors = [], StartNoEarlierThan = Today.AddDays(7), Fixed = !held, Status = "Local" }, child, predecessor],
            [parent, child, predecessor], [new("U1", "alice", 100, null), new("U2", "bob", 100, null)]);
        foreach (var field in new[] { PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status }) await SetColumnVisible(field,true);
        var undo = session.UndoCount;
        foreach (var (field, text) in new[] { (PlanField.Assignees, "alice, bob"), (PlanField.Predecessors, "3"),
            (PlanField.StartNoEarlierThan, "2026-10-05"), (PlanField.Fixed, "固定"), (PlanField.Status, "Todo") }) {
            await Select(1, field);
            await Ui.Idle();
            await Ui.Until(() => SelectProvider($"PlanCell1_{field}").IsSelected
                && Ui.Find<PlanSummaryCell>($"PlanCell1_{field}").FocusState != FocusState.Unfocused);
            await Ui.Run(async () => {
                Assert.That(FocusManager.GetFocusedElement(sheet.XamlRoot), Is.SameAs(Ui.Find<PlanSummaryCell>($"PlanCell1_{field}")), field);
                Assert.That(SelectProvider($"PlanCell1_{field}").IsSelected, Is.True, field);
                clipboard = new("not copied", null);
                await sheet.KeyboardCommand(Windows.System.VirtualKey.C);
                Assert.That(clipboard.Text, Is.EqualTo(held ? text : ""), field);
                Assert.That(clipboard.Metadata, Is.Not.Null, field);
                using var metadata = JsonDocument.Parse(clipboard.Metadata!);
                Assert.That(metadata.RootElement.GetProperty("fields").EnumerateArray().Select(v => v.GetString()), Is.EqualTo(new[] { field }), field);
                Assert.That(metadata.RootElement.GetProperty("values")[0][0].GetString(), Is.EqualTo(JsonSerializer.Serialize(PlanOperations.Value(parent, field), PlanJson.Options)), field);
            });
        }
        await Select(1, PlanField.Assignees);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSummaryCell>("PlanCell1_Assignees");
            Assert.That(cell.Text.Text, Is.EqualTo(held ? "alice +1" : ""));
            Assert.That(ToolTipService.GetToolTip(cell), Is.EqualTo(held ? "alice, bob" : ""));
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(cell);
            Assert.That(peer.GetHelpText(), held ? Does.Contain("alice, bob") : Is.Empty);
        });
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [TestCase(true, false), TestCase(true, true), TestCase(false, false), TestCase(false, true)]
    public async Task QueuedPasteRejectsARowRemovedByUndoAndKeepsTheQueueUsable(bool internalCopy, bool expand)
    {
        await Select(3, PlanField.Title);
        if (expand) await Select(4, PlanField.Title, true);
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.C));
        Assert.That(clipboard.Metadata, Is.Not.Null);
        if (!internalCopy) clipboard = new(clipboard.Text, null);
        var before = PlanJson.Text(session.Document); var history = session.UndoCount;
        var inserted = PlanRow.New("Temporary destination", "acme/repo");
        var save = await session.Execute(new InsertPlanRows([inserted], "I2"), Today);
        Assert.That(save.Succeeded, Is.True, save.Error);
        await Ui.Run(() => sheet.Refresh());
        await Select(expand ? 1 : 2, PlanField.Title);
        await Ui.Run(async () => {
            var undo = sheet.KeyboardCommand(Windows.System.VirtualKey.Z);
            var paste = sheet.KeyboardCommand(Windows.System.VirtualKey.V);
            await Task.WhenAll(undo, paste);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("空行を除いて範囲を選択してください。"));
            Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
            Assert.That(session.UndoCount, Is.EqualTo(history));
            Assert.That(session.RedoCount, Is.EqualTo(1));
            await sheet.KeyboardCommand(Windows.System.VirtualKey.Y);
            Assert.That(session.Document.State.Rows.Any(row => row.Identity == inserted.Identity), Is.True);
            Assert.That(session.UndoCount, Is.EqualTo(history + 1));
            Assert.That(session.RedoCount, Is.Zero);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty);
        });
    }

    [Test]
    public async Task QueuedSummaryCopyKeepsTheSelectionAtTheCommandRequest()
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo") { Fixed = true, Assignees = ["U1"] };
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1" };
        await MountPresentation([parent, child], [parent, child], [new("U1", "alice", 100, null)]);
        await SetColumnVisible(PlanField.Fixed, true);
        await Select(1, PlanField.Fixed);
        await Ui.Run(async () => {
            var copy = sheet.KeyboardCommand(Windows.System.VirtualKey.C);
            SelectProvider("PlanCell1_Assignees").Select();
            await copy;
            Assert.That(clipboard.Text, Is.EqualTo("固定"));
            using var metadata = JsonDocument.Parse(clipboard.Metadata!);
            Assert.That(metadata.RootElement.GetProperty("fields").EnumerateArray().Select(v => v.GetString()),
                Is.EqualTo(new[] { PlanField.Fixed }));
            Assert.That(metadata.RootElement.GetProperty("values")[0][0].GetString(), Is.EqualTo("true"));
        });
    }

    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task SummaryStartTabAndEnterOnlyNavigate(bool tab)
    {
        await session.Execute(new IndentPlanRows(["I3"]), Today);
        await Ui.Run(() => sheet.Refresh()); await Ui.Idle();
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        await Select(2, PlanField.Start); await SheetNativeInput.ActivateWindow();
        await SheetNativeInput.Click("PlanCell2_Start");
        await SheetNativeInput.Press(tab ? Windows.System.VirtualKey.Tab : Windows.System.VirtualKey.Enter);
        await Ui.Until(() => SelectProvider(tab ? "PlanCell2_End" : "PlanCell3_Start").IsSelected);
        await Ui.Run(() => {
            Assert.That(sheet.Pending, Is.Empty);
            Assert.That(sheet.Problems, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty);
        });
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [TestCase(Windows.System.VirtualKey.F10, false, false)]
    [TestCase(Windows.System.VirtualKey.Tab, true, false)]
    [TestCase(Windows.System.VirtualKey.Left, false, true)]
    [Category("PlanSheetNative")]
    public async Task SummaryLeavesWindowShortcutsUnhandled(Windows.System.VirtualKey key, bool control, bool alt)
    {
        await session.Execute(new IndentPlanRows(["I3"]), Today);
        await Ui.Run(() => sheet.Refresh()); await Ui.Idle();
        await Select(2, PlanField.Estimate); await SheetNativeInput.ActivateWindow();
        await SheetNativeInput.Click("PlanCell2_Estimate");
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler handler = (_, args) => { if (args.Key == key) observed.TrySetResult(args.Handled); };
        await Ui.Run(() => Ui.Find<PlanSummaryCell>("PlanCell2_Estimate").AddHandler(UIElement.KeyDownEvent, handler, true));
        try {
            await SheetNativeInput.Press(key, control ? [Windows.System.VirtualKey.Control] : alt ? [Windows.System.VirtualKey.Menu] : []);
            Assert.That(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        }
        finally { await Ui.Run(() => Ui.Find<PlanSummaryCell>("PlanCell2_Estimate").RemoveHandler(UIElement.KeyDownEvent, handler)); }
    }

    [TestCase(false), TestCase(true)]
    public async Task SummaryNonRolledUpCellsShowOnlyGitHubHeldValues(bool held)
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo") {
            Assignees = held ? ["U1"] : [], Predecessors = held ? ["I3"] : [],
            StartNoEarlierThan = held ? Today : null, Fixed = held, Status = held ? "Todo" : null };
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1", Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        var predecessor = new PlanRow("I3", "Earlier", "acme/repo");
        await MountPresentation([parent, child, predecessor], [parent, child, predecessor]);
        foreach (var field in new[] { PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status }) await SetColumnVisible(field,true);
        foreach (var (field, expected) in new[] { (PlanField.Assignees, "alice"), (PlanField.Predecessors, "3"),
            (PlanField.StartNoEarlierThan, "10/5 (月)"), (PlanField.Fixed, "固定"), (PlanField.Status, "Todo") }) {
            await Select(1, field);
            await Ui.Run(() => {
                var cell = Ui.Find<FrameworkElement>($"PlanCell1_{field}");
                Assert.That(cell, Is.Not.InstanceOf<TextBox>());
                Assert.That(Ui.Tree(cell).OfType<TextBlock>().Single().Text, Is.EqualTo(held ? expected : ""));
                Assert.That(FrameworkElementAutomationPeer.CreatePeerForElement(cell).GetName(), Does.Contain("要求事項の行では編集できません"));
            });
        }
    }

    [TestCase(PlanField.Estimate, "子タスクから集計（編集不可）")]
    [TestCase(PlanField.Assignees, "要求事項の行では編集できません")]
    public async Task SummaryCellsExposeStaticValuesAndSelectionReason(string field, string reason)
    {
        await session.Execute(new IndentPlanRows(["I3"]), Today);
        await Ui.Run(() => sheet.Refresh()); await Ui.Idle();
        await Select(2, field);
        await Ui.Run(() => {
            var cell = Ui.Find<FrameworkElement>($"PlanCell2_{field}");
            Assert.That(cell, Is.Not.InstanceOf<TextBox>());
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(cell);
            Assert.That(peer.GetPattern(PatternInterface.Text), Is.Null);
            Assert.That(peer.GetPattern(PatternInterface.Value), Is.Null);
            Assert.That(peer.GetName(), Does.Contain(reason));
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain(reason));
            Assert.That(Ui.Tree(sheet).OfType<Button>().Where(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == $"PlanFillHandle2_{field}")
                .Any(button => button.Visibility == Visibility.Visible), Is.False);
            Assert.That(SelectProvider($"PlanCell2_{field}").IsSelected, Is.True);
        });
    }
    [TestCase(PlanField.Estimate), TestCase(PlanField.Assignees)]
    [Category("PlanSheetNative")]
    public async Task SummaryPointerAndKeysNeverOpenEditorAndArrowsTraverseCells(string field)
    {
        await session.Execute(new IndentPlanRows(["I3"]), Today);
        await Ui.Run(() => sheet.Refresh()); await Ui.Idle();
        var before = PlanJson.Text(session.Document); var undo = session.UndoCount;
        await Select(2, field); await SheetNativeInput.ActivateWindow();
        var id = $"PlanCell2_{field}";
        await SheetNativeInput.Click(id);
        await Ui.Run(() => Assert.That(FocusManager.GetFocusedElement(Ui.Root.XamlRoot), Is.SameAs(Ui.Find<FrameworkElement>(id))));
        var doubleTapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DoubleTappedEventHandler observed = (_, _) => doubleTapped.TrySetResult();
        await Ui.Run(() => Ui.Root.AddHandler(UIElement.DoubleTappedEvent, observed, true));
        try {
            await SheetNativeInput.Click(id); await SheetNativeInput.Click(id);
            await doubleTapped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await Ui.Run(() => Ui.Root.RemoveHandler(UIElement.DoubleTappedEvent, observed)); }
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
        await SheetNativeInput.Press(Windows.System.VirtualKey.A);
        await Ui.Idle();
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => {
            Assert.That(sheet.Pending, Is.Empty);
            Assert.That(Ui.Find<FrameworkElement>(id), Is.Not.InstanceOf<TextBox>());
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain(PlanOperations.SummaryReadOnlyReason(field)));
        });
        await SheetNativeInput.Press(Windows.System.VirtualKey.Right);
        await Ui.Run(() => Assert.That(SelectProvider($"PlanCell2_{(field == PlanField.Assignees ? PlanField.Estimate : PlanField.Remaining)}").IsSelected, Is.True));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Left);
        await Ui.Run(() => Assert.That(SelectProvider(id).IsSelected, Is.True));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Tab);
        await Ui.Run(() => Assert.That(SelectProvider($"PlanCell2_{(field == PlanField.Assignees ? PlanField.Estimate : PlanField.Remaining)}").IsSelected, Is.True));
    }

    [TestCase(false, false), TestCase(false, true)]
    [Category("PlanSheetFollowup")]
    public async Task PointerRangeEndsAtEstimateAndClearChangesOnlyItsCells(bool hideId, bool hideIndicator)
    {
        if (hideIndicator) await SetColumnVisible("Indicator",false);
        var before = session.Document.State.Rows;
        await Select(1, PlanField.Estimate);
        await Ui.Until(() => {
            var left = Ui.Find<PlanSheetCell>("PlanCell2_Title").TransformToVisual(sheet).TransformPoint(new()).X + sheet.SheetOffset;
            return Math.Abs(left - (hideId ? 0 : 52) - (hideIndicator ? 0 : 28)) <= 1;
        });
        await Ui.Run(async () => {
            sheetHost.Background = PlanSheetView.Brush("WorkspaceCardBrush");
            await RenderedEvidence.Capture(sheetHost, $"followup-range-{hideId}-{hideIndicator}");
        });
        await Ui.Run(() => {
            var target = Ui.Find<PlanSheetCell>("PlanCell2_Estimate");
            var centre = target.TransformToVisual(sheet).TransformPoint(new(target.ActualWidth / 2, target.ActualHeight / 2));
            Ui.Trace($"Range position: x={centre.X}, offset={sheet.SheetOffset}, columns={string.Join(",", sheet.VisibleColumns.Select(c => c.Label + ":" + c.Width))}, titleX={Ui.Find<PlanSheetCell>("PlanCell2_Title").TransformToVisual(sheet).TransformPoint(new()).X}");
            sheet.ExtendRangeTo("I2", centre);
            Assert.That(sheet.IsRangeEnd("I2", PlanField.Estimate), Is.True);
        });
        await Ui.ClickCommand("PlanSheetClear"); await Ui.Idle();
        Assert.That(session.Document.State.Rows, Is.EqualTo(before.Select((row, i) => i < 2 ? row with { Estimate = null } : row)));
    }
    [Test, Category("PlanSheetFollowup")]
    public async Task DateEditTextFitsTheContentViewportWithoutWideningItsColumn()
    {
        await Select(1, PlanField.End);
        await Ui.Run(() => {
            sheetHost.Background = PlanSheetView.Brush("WorkspaceCardBrush");
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_End"); cell.Focus(FocusState.Programmatic); cell.BeginEditing();
        });
        await Ui.Idle();
        await Ui.Run(async () => {
            await RenderedEvidence.Capture(sheetHost, "followup-date-edit");
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_End");
            Assert.That(cell.Text, Is.EqualTo("2026-10-05"));
            Assert.That(cell.SelectedText, Is.EqualTo(cell.Text));
            var content = Ui.Tree(cell).OfType<ScrollViewer>().Single(v => v.Name == "ContentElement");
            var text = new TextBlock { Text = cell.Text, FontFamily = cell.FontFamily, FontSize = cell.FontSize, FontWeight = cell.FontWeight, CharacterSpacing = cell.CharacterSpacing };
            text.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            Assert.That(((FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(cell))).ActualWidth, Is.EqualTo(92).Within(1));
            Assert.That(text.DesiredSize.Width, Is.GreaterThan(60));
            Ui.Trace($"Date edit: text={text.DesiredSize.Width}, content={content.ActualWidth}, viewport={content.ViewportWidth}, padding={content.Padding}");
            Assert.That(content.ActualWidth - content.Padding.Left - content.Padding.Right, Is.GreaterThanOrEqualTo(text.DesiredSize.Width + 1), "The complete ISO edit text plus caret must fit.");
            Assert.That(content.HorizontalOffset, Is.Zero);
        });
    }
    [Test, Category("PlanSheetFollowup")]
    public async Task SelectionBandKeepsOneHeightAndDirectlyPrecedesTheHeader()
    {
        double headerBefore = 0;
        await Ui.Run(async () => {
            sheetHost.Background = PlanSheetView.Brush("WorkspaceCardBrush");
            var band = (Grid)VisualTreeHelper.GetParent(Ui.Find<TextBlock>("PlanSheetSelection"));
            Assert.That(((SolidColorBrush)band.Background).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("SheetSelectionLineBrush")).Color));
            Assert.That(band.BorderThickness.Bottom, Is.EqualTo(1));
            var insert = Ui.Find<AppBarButton>("PlanSheetInsert");
            Assert.That(insert.TransformToVisual(sheet).TransformPoint(new()).X, Is.LessThan(12), "Commands start at the card left edge.");
            var commands = (FrameworkElement)VisualTreeHelper.GetParent(Ui.Find<CommandBar>("PlanSheetCommands"));
            var header = (FrameworkElement)VisualTreeHelper.GetParent(Ui.Find<TextBlock>("PlanHeaderId"));
            headerBefore = header.TransformToVisual(sheet).TransformPoint(new()).Y;
            Assert.That(Ui.Find<TextBlock>("PlanSheetSelection").Text, Is.Empty);
            Assert.That(band.ActualHeight, Is.EqualTo(36).Within(1));
            Assert.That(band.TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(commands.ActualHeight).Within(1));
            Assert.That(headerBefore, Is.EqualTo(commands.ActualHeight + 36).Within(1));
            await RenderedEvidence.Capture(sheetHost, "followup-unselected-band");
        });
        await Select(1, PlanField.Title); await Ui.Idle();
        await Ui.Run(async () => {
            var header = (FrameworkElement)VisualTreeHelper.GetParent(Ui.Find<TextBlock>("PlanHeaderId"));
            Assert.That(Ui.Find<TextBlock>("PlanSheetSelection").Text, Is.EqualTo("ID 1"));
            Assert.That(header.TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(headerBefore).Within(1));
            await RenderedEvidence.Capture(sheetHost, "followup-selected-band");
        });
    }
    [Test]
    public async Task CalculatedRemainingIsEditableSecondaryTextAndEnteredEffortShowsMarkers()
    {
        await Edit(1, PlanField.Remaining, "");
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(cell.Text, Is.EqualTo("8h"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(cell), Is.EqualTo("ID 1 見込み残存工数"));
            Assert.That(ToolTipService.GetToolTip(cell), Is.EqualTo("見込み残存工数 · 8h"));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderRemaining").Text, Is.EqualTo("残 h"));
            Assert.That(cell.IsReadOnly, Is.False);
            Assert.That(((SolidColorBrush)cell.Foreground).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("TextFillColorSecondaryBrush")).Color));
        });
        Assert.That(session.Document.State.Rows[0].Remaining, Is.Null);
        await Edit(1, PlanField.Remaining, "16");
        await Ui.Run(() => {
            Assert.That(((SolidColorBrush)Ui.Find<TextBox>("PlanCell1_Remaining").Foreground).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("TextFillColorPrimaryBrush")).Color));
            var indicator = Ui.Find<FontIcon>("PlanIndicator1");
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator), Does.Contain("見積超過: 見込工数 16h（見積工数 8h、差異 +8h）"));
            Assert.That(ToolTipService.GetToolTip(indicator), Is.EqualTo(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator)));
            Assert.That(indicator.Glyph, Is.Not.Empty);
        });
        await Edit(1, PlanField.Actual, "16");
        await Edit(1, PlanField.Remaining, "0");
        await Ui.Run(() => {
            var indicator = Ui.Find<FontIcon>("PlanIndicator1");
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator), Does.Contain("残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません").And.Contain("見積超過"));
            Assert.That(indicator.Glyph, Is.Not.Empty);
        });
    }

    [TestCase("Enter", true), TestCase("Tab", true), TestCase("Leave", true)]
    [TestCase("Escape", false), TestCase("Select", false)]
    [Category("PlanSheetNative")]
    public async Task CalculatedRemainingEditSessionCommitsExplicitValueOrCancels(string exit, bool explicitValue)
    {
        var row = new PlanRow("I1", "Work", "acme/repo") { Estimate = 40, Actual = 8 };
        await MountPresentation([row], [row]);
        await Select(1, PlanField.Remaining);
        await SheetNativeInput.Click("PlanCell1_Remaining");
        if (exit != "Select") await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
        await Ui.Run(() => Assert.That(Ui.Find<PlanSheetCell>("PlanCell1_Remaining").Text, Is.EqualTo(exit == "Select" ? "32h" : "32")));
        if (exit is "Leave" or "Select")
            await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        else await SheetNativeInput.Press(exit == "Enter" ? Windows.System.VirtualKey.Enter
            : exit == "Tab" ? Windows.System.VirtualKey.Tab : Windows.System.VirtualKey.Escape);
        // WinUI raises LostFocus asynchronously after focus has already moved to the filter.
        await Ui.Until(() => !Ui.Find<PlanSheetCell>("PlanCell1_Remaining").Editing);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(explicitValue ? 32m : (decimal?)null));
            Assert.That(session.UndoCount, Is.EqualTo(explicitValue ? 1 : 0));
            Assert.That(sheet.Pending, Is.Empty);
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Remaining");
            Assert.That(cell.Editing, Is.False);
            Assert.That(((SolidColorBrush)cell.Foreground).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush(
                explicitValue ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")).Color));
        });
    }

    [Test, Category("SheetRowPresentation")]
    public async Task ClosedRemainingCannotEnterEditingAndSelectionExplainsWhy()
    {
        var row = new PlanRow("I1", "Closed task", "acme/repo") { Closed = true, CloseDate = Today,
            Estimate = 40, Remaining = 8, Start = Today, End = Today };
        await MountPresentation([row], [row]);
        await Select(1, PlanField.Remaining);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Remaining");
            Assert.That(cell.IsReadOnly, Is.True);
            cell.BeginEditing();
            Assert.That(cell.Editing, Is.False);
            Assert.That(Ui.Tree(sheet).OfType<Button>().Where(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "PlanFillHandle1_Remaining")
                .Any(button => button.Visibility == Visibility.Visible), Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain("完了したタスクは Issue のクローズで確定します（編集不可）"));
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell1_Estimate").IsReadOnly, Is.False);
        });
    }

    [Test, Category("SheetRowPresentation")]
    public async Task ClosedRemainingAndEndShowCalculatedCornersWithoutEnteredTint()
    {
        var row = new PlanRow("I1", "Closed task", "acme/repo") { Closed = true, CloseDate = Today,
            Estimate = 40, Actual = 40, Remaining = 8, Start = Today.AddDays(-7), End = Today.AddDays(2) };
        await MountPresentation([row], [row]);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("0h"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_End").Text, Is.EqualTo("10/5 (月)"));
            foreach (var field in new[] { PlanField.Remaining, PlanField.End }) {
                var cell = Ui.Find<PlanSheetCell>($"PlanCell1_{field}");
                var grid = (Grid)VisualTreeHelper.GetParent(cell);
                var frame = (Border)VisualTreeHelper.GetParent(grid);
                Assert.That(((SolidColorBrush)cell.Foreground).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("TextFillColorSecondaryBrush")).Color));
                Assert.That(grid.Children.OfType<Microsoft.UI.Xaml.Shapes.Polygon>().Single().Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(((SolidColorBrush)frame.Background).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("LayerFillColorDefaultBrush")).Color));
            }
        });
    }

    [TestCase("completed", "完了", "\uE73E")]
    [TestCase("late", "期限超過: 完了予定 10/2 を過ぎて未完了（見込み残存工数 8h）", "\uE7BA")]
    [TestCase("typed", "開始日を指定", "\uE718")]
    [TestCase("fixed", "日程固定", "\uE718")]
    [TestCase("changed", "未発行の変更あり", "\u2022")]
    [TestCase("failed", "発行失敗", "\uEA39")]
    [TestCase("unverified", "GitHub で未確認", "\uEA39")]
    [TestCase("summary", "配下に期限超過 1", "\uE7BA")]
    [Category("PlanSheetPhase2")]
    public async Task IndicatorPriorityAndSelectionExplainAllApplicableRowStates(string state, string expected, string glyph)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var row = new PlanRow("I1", "R05 顧客データの外部連携", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        if (state is "completed" or "failed" or "unverified") row = row with { Closed = true, CloseDate = Today, Remaining = 0, Actual = 8, Start = Today, End = Today, StartNoEarlierThan = Today };
        if (state == "typed") row = row with { StartNoEarlierThan = Today, End = new(2026, 10, 2) };
        // #236: an overdue row is overdue against the 基準日, so this state states the plan dates that put it
        // there; the published end no longer decides it.
        if (state == "late") row = row with { Start = new(2026, 10, 2), End = new(2026, 10, 2) };
        if (state == "fixed") row = row with { Fixed = true, Start = Today, End = Today };
        var baseline = row with { End = state is "late" or "failed" or "unverified" ? new(2026, 10, 2) : state == "summary" ? new(2026, 10, 1) : row.End };
        var rows = ImmutableArray.Create(row);
        var baselines = ImmutableArray.Create(baseline);
        if (state == "summary") {
            rows = rows.Add(row with { Identity = "I2", Parent = "I1" });
            baselines = baselines.Add(row with { Identity = "I2", Parent = "I1", End = new(2026, 10, 2) });
        }
        var document = new PlanDocument(session.Document.Project, new(baselines, []), new(rows, session.Document.State.Settings)) {
            Sync = new() { Failures = state == "failed" ? [new("I1", PlanField.End, "Rejected")] : [], Unverified = state == "unverified" ? ["I1"] : [],
                IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I1", new("acme/repo#1", "https://github.com/acme/repo/issues/1")) } };
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "indicator")), document, Today);
        await Ui.Run(() => { sheetHost.Children.Clear(); sheet = new(session); sheetHost.Children.Add(sheet.statusDate); sheetHost.Children.Add(sheet); Grid.SetRow(sheet, 1); });
        await Ui.Mount(sheetHost); await Select(1, PlanField.Title);
        if (state is "late" or "failed" or "unverified")
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetSlip").ActualWidth > 0 && Ui.Find<TextBlock>("PlanStartReason").ActualWidth > 0);
        await Ui.Run(() => {
            var indicator = Ui.Find<FontIcon>("PlanIndicator1");
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator), Does.Contain(expected));
            Assert.That(indicator.Glyph, Is.EqualTo(glyph));
            Assert.That(ToolTipService.GetToolTip(indicator), Is.EqualTo(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator)));
            Assert.That(Ui.Find<TextBlock>("PlanSheetSelection").Text, Is.EqualTo("ID 1"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Ui.Find<TextBlock>("PlanSheetSelection")), Does.Contain(row.Title));
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Is.Not.Empty);
            if (state == "typed") Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Is.EqualTo("開始: 開始日指定 10/5 · 終了: 開始から 8h"));
            if (state == "late") Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Is.EqualTo("開始: 開始日を維持 · 終了: 指定"));
            Assert.That(Ui.Find<HyperlinkButton>("PlanSheetIssue").Content, Is.EqualTo("acme/repo#1"));
            var slip = Ui.Find<TextBlock>("PlanSheetSlip").Text;
            if (state == "late") {
                Assert.That(slip, Is.EqualTo("完了予定 10/2 を過ぎて未完了 · +1 日"));
                var text = Ui.Find<TextBlock>("PlanStartReason");
                var pill = (Border)VisualTreeHelper.GetParent(Ui.Find<TextBlock>("PlanSheetSlip"));
                var gap = pill.TransformToVisual(sheet).TransformPoint(new()).X - text.TransformToVisual(sheet).TransformPoint(new()).X - text.ActualWidth;
                Assert.That(gap, Is.InRange(8d, 12d), "The late pill follows the reason immediately.");
                Assert.That(pill.CornerRadius.TopLeft, Is.GreaterThan(0));
            }
            // A closed task has no forecast, so the delivered model shows no slip for it: the only thing the
            // published end could have measured is gone with the moving dates.
            else if (state is "failed" or "unverified") Assert.That(slip, Is.Empty);
            else if (state == "summary") Assert.That(slip, Is.EqualTo("見込終了 10/5 (月) · 10/2 から +1 日"));
            else Assert.That(slip, Is.Empty);
            if (state is "failed" or "unverified") Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator), Does.Contain("完了").And.Contain("開始日を指定").And.Contain("未発行の変更あり"));
        });
    }
    // #236: the plan consumes a task from its own start — Actual + Remaining — so the end reason counts hours
    // rather than naming a 基準日, and a task needs its own dates for the reason to exist.
    [TestCase("started", "終了: 開始から 12h")]
    [TestCase("unstarted", "終了: 開始から 8h")]
    [TestCase("fixed", "終了: 指定")]
    [TestCase("complete", null)]
    [TestCase("summary", null)]
    [Category("SelectionLine")]
    public async Task SelectionExplainsEndReason(string state, string? expected)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"],
            Start = Today, End = Today };
        if (state == "started") row = row with { Actual = 4 };
        if (state == "fixed") row = row with { Fixed = true };
        if (state == "complete") row = row with { Closed = true, CloseDate = Today, Actual = 8, Remaining = 0 };
        var rows = ImmutableArray.Create(row);
        if (state == "summary") rows = rows.Add(row with { Identity = "I2", Parent = "I1" });
        await MountPresentation(rows, rows); await Select(1, PlanField.Title);
        await Ui.Run(() => {
            var reason = Ui.Find<TextBlock>("PlanStartReason");
            if (expected is null) Assert.That(reason.Text, Does.Not.Contain("終了:"));
            else Assert.That(reason.Text, Does.Contain(expected));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(reason), Is.EqualTo(reason.Text));
        });
    }

    [TestCase("finish", "完了予定 10/2 を過ぎて未完了 · +1 日")]
    [TestCase("start", "開始予定 10/2 を過ぎて未着手")]
    // #236 part B: the slip compares the plan with the forecast, so it names 見込終了 and the plan end it
    // slipped from; the published basis is retired.
    [TestCase("later", "見込終了 10/6 (火) · 10/5 から +1 日")]
    [Category("SelectionLine")]
    public async Task SelectionPillKeepsGregorianDatesUnderNonGregorianCulture(string state, string expected)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") {
            Estimate = 8, Remaining = state == "later" ? 16 : 8, Assignees = ["U1"] };
        // A finish-overdue plan needs a start no later than its end: the plan end is its own value now.
        var baseline = row with {
            Start = state == "finish" ? Today.AddDays(-3) : Today,
            End = state == "finish" ? Today.AddDays(-3) : state == "start" ? Today.AddDays(4) : Today };
        await MountPresentation([row], [baseline]);
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => {
            var previousCulture = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                Assert.That(CultureInfo.CurrentCulture.DateTimeFormat.Calendar, Is.Not.InstanceOf<GregorianCalendar>());
                SelectProvider("PlanCell1_Title").Select();
                var slip = Ui.Find<TextBlock>("PlanSheetSlip");
                Assert.That(slip.Text, Is.EqualTo(expected));
                Assert.That(((Border)VisualTreeHelper.GetParent(slip)).Visibility, Is.EqualTo(Visibility.Visible));
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        });
    }

    // #236 part B: the pill compares the plan with the forecast (「見込終了 M/d (曜) · M/d から +N 日」) and
    // the published-end basis is retired. Each state states the plan's own dates, because a task with no dates
    // has no forecast and no pill.
    [TestCase("finish", "完了予定 10/2 を過ぎて未完了 · +1 日", true, "")]
    [TestCase("start", "開始予定 10/2 を過ぎて未着手", true, "")]
    [TestCase("moved", "完了予定 10/2 を過ぎて未完了 · +1 日", true, "")]
    [TestCase("later", "見込終了 10/6 (火) · 10/5 から +1 日", false, "")]
    // The predecessor state names its cause through the selection line's link, not the pill's detail line.
    [TestCase("predecessor", "見込終了 10/7 (水) · 10/5 から +2 日", false, "")]
    // A typed start constraint moves the plan start, so the plan end follows it and no slip remains: the
    // delivered model has no lateness to show here (the published-basis slip this state used to show is gone).
    [TestCase("constraint", "", false, "")]
    [TestCase("long", "見込終了 10/6 (火) · 10/5 から +1 日", false, "")]
    [TestCase("summary-overdue", "見込終了 10/5 (月) · 10/2 から +1 日", true, "配下: 期限超過 1")]
    [TestCase("summary-later", "見込終了 10/6 (火) · 10/5 から +1 日", false, "配下: 予定より遅れ 1")]
    [TestCase("summary-counts", "見込終了 10/6 (火) · 10/5 から +1 日", true, "配下: 期限超過 1 · 予定より遅れ 1")]
    [TestCase("none", "", false, "")]
    [Category("SelectionLine")]
    public async Task SelectionPresentsLatenessAndKeepsIssueAtRight(string state, string expected, bool tinted, string detail)
    {
        var row = new PlanRow("I1", "Selected task", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"],
            Start = Today, End = Today };
        var old = row;
        if (state is "finish" or "moved" or "summary-overdue") {
            row = state == "finish" ? row with { Fixed = true, Start = Today.AddDays(-3), End = Today.AddDays(-3) }
                : row with { Start = Today.AddDays(-3), End = Today.AddDays(-3) };
            old = row;
        }
        if (state == "start") { row = row with { Start = Today.AddDays(-3), End = Today.AddDays(4) }; old = row; }
        if (state is "later" or "summary-later" or "long") row = row with { Remaining = 16 };
        if (state == "long") row = row with { Title = string.Concat(Enumerable.Repeat("長いタスク名と終了理由の表示確認", 8)) };
        if (state == "constraint") row = row with { StartNoEarlierThan = Today.AddDays(1) };
        var rows = ImmutableArray.Create(row); var baseline = ImmutableArray.Create(old);
        if (state == "predecessor") {
            // A 日程固定 task is the shape that can be 予定より遅れ through a predecessor: it keeps its plan
            // dates while its forecast start follows the predecessor's end. An unfixed task's plan start would
            // follow the predecessor too, and its end with it, leaving no gap to show.
            rows = rows.SetItem(0, row with { Fixed = true, Predecessors = ["I2"] })
                .Add(row with { Identity = "I2", Start = Today.AddDays(2), End = Today.AddDays(2) });
            baseline = baseline.Add(row with { Identity = "I2", Start = Today.AddDays(2), End = Today.AddDays(2) });
        }
        if (state.StartsWith("summary-")) {
            var child = state switch {
                "summary-overdue" => old with { Identity = "I2", Parent = "I1" },
                "summary-later" => old with { Identity = "I2", Parent = "I1", Start = Today, End = Today, Remaining = 16 },
                _ => old with { Identity = "I2", Parent = "I1" }
            };
            rows = rows.Add(child);
            baseline = baseline.Add(child);
        }
        if (state == "summary-counts") {
            // One overdue child and one later child: the summary's roll-up end is 10/5 and its forecast 10/6,
            // so the pill is tinted (a descendant is 期限超過) and both counts appear in the detail line.
            var overdueChild = old with { Identity = "I2", Parent = "I1", Start = Today.AddDays(-3), End = Today.AddDays(-3) };
            var later = old with { Identity = "I3", Parent = "I1", Start = Today, End = Today, Remaining = 16 };
            rows = rows.SetItem(1, overdueChild).Add(later);
            baseline = baseline.SetItem(1, overdueChild).Add(later);
        }
        await MountPresentation(rows, baseline); await Select(1, PlanField.Title); await Ui.Idle();
        await Ui.Until(() => Ui.Find<HyperlinkButton>("PlanSheetIssue").ActualWidth > 0
            && Ui.Find<TextBlock>("PlanStartReason").ActualWidth > 0);
        await Ui.Run(() => {
            var slip = Ui.Find<TextBlock>("PlanSheetSlip"); var pill = (Border)VisualTreeHelper.GetParent(slip);
            Assert.That(slip.Text, Is.EqualTo(expected));
            Assert.That(pill.Visibility, Is.EqualTo(expected.Length == 0 ? Visibility.Collapsed : Visibility.Visible));
            if (expected.Length > 0) {
                Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(pill), Is.EqualTo(expected));
                Assert.That(((SolidColorBrush)slip.Foreground).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("SystemFillColorCriticalBrush")).Color));
                if (tinted) Assert.That(((SolidColorBrush)pill.Background).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("GanttLateTintBrush")).Color));
                else {
                    Assert.That(pill.Background is null || pill.Background is SolidColorBrush { Color.A: 0 }, Is.True);
                    Assert.That(pill.BorderThickness.Left, Is.EqualTo(1));
                    Assert.That(((SolidColorBrush)pill.BorderBrush).Color, Is.EqualTo(((SolidColorBrush)slip.Foreground).Color));
                }
            }
            Assert.That(Ui.Find<TextBlock>("PlanSheetLatenessDetail").Text, Is.EqualTo(detail));
            // #236 part B: a predecessor-caused lateness names its cause with a link to that row.
            Assert.That(Ui.Find<HyperlinkButton>("PlanSelectionPredecessorLink").Visibility,
                Is.EqualTo(state == "predecessor" ? Visibility.Visible : Visibility.Collapsed));
            var issue = Ui.Find<HyperlinkButton>("PlanSheetIssue");
            Assert.That(issue.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(issue.Content, Is.EqualTo("acme/repo#1"));
            var issueX = issue.TransformToVisual(sheet).TransformPoint(new()).X;
            Assert.That(sheet.ActualWidth - issueX - issue.ActualWidth, Is.InRange(7d, 10d));
            var reason = Ui.Find<TextBlock>("PlanStartReason");
            Assert.That(reason.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(reason.TransformToVisual(sheet).TransformPoint(new()).X + reason.ActualWidth, Is.LessThan(issueX));
            if (expected.Length > 0) Assert.That(pill.TransformToVisual(sheet).TransformPoint(new()).X + pill.ActualWidth, Is.LessThan(issueX));
        });
        if (state is "moved" or "predecessor" or "long") await Ui.Run(() => RenderedEvidence.Capture(sheet, "selection-" + state));
    }

    [Test]
    [Category("SelectionLine")]
    public async Task EmptySelectionShowsTheHintAndHidesItWhenARowIsSelected()
    {
        // #236 part B: the Project-wide lateness counts are the status bar's filter buttons in the workspace,
        // so a bare sheet no longer owns a totals line. This case keeps its own subject — the empty-selection
        // hint — and the count buttons are exercised where they live.
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        var rows = ImmutableArray.Create(row, row with { Identity = "I2", Remaining = 16 });
        var baseline = ImmutableArray.Create(row with { End = Today.AddDays(-3) }, row with { Identity = "I2", End = Today });
        await MountPresentation(rows, baseline);
        await Ui.Run(() => {
            var hint = Ui.Find<TextBlock>("PlanSheetEmptyHint");
            Assert.That(hint.Text, Is.EqualTo("タスクを選択すると、開始日と終了日の根拠を表示します"));
            Assert.That(hint.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(hint), Is.EqualTo(hint.Text));
        });
        await Ui.Run(() => RenderedEvidence.Capture(sheet, "selection-empty"));
        await Select(1, PlanField.Title);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetEmptyHint").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    private async Task MountPresentation(ImmutableArray<PlanRow> rows, ImmutableArray<PlanRow> baseline,
        ImmutableArray<PlanResource> people = default, ImmutableDictionary<string, string>? logins = null, ImmutableArray<PlanColumnDefinition> columns = default)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var settings = session.Document.State.Settings;
        if (!people.IsDefault) settings = settings with { People = people };
        // #236 makes a stored date a plan value and stops a read from inventing one. A presentation fixture
        // that states GitHub's dates in its baseline and leaves the plan row without any means "as the read
        // left it" — the baseline — so the plan row takes those dates. A row whose baseline has no date either
        // stays undated, which is the #245 case the sheet must show as bar-less.
        var github = baseline.ToDictionary(row => row.Identity);
        rows = rows.Select(row => github.TryGetValue(row.Identity, out var source)
            ? row with { Start = row.Start ?? source.Start, End = row.End ?? source.End } : row).ToImmutableArray();
        var document = new PlanDocument(session.Document.Project, new(baseline, columns.IsDefault ? [] : columns), new(rows, settings)) {
            Sync = new() { PeopleNames = logins ?? ImmutableDictionary<string, string>.Empty,
                IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I1", new("acme/repo#1", "https://github.com/acme/repo/issues/1")) } };
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "presentation")), document, Today);
        await Ui.Run(() => {
            sheetHost.Children.Clear(); sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value);
            sheetHost.Background = PlanSheetView.Brush("WorkspaceCardBrush");
            sheetHost.Children.Add(sheet.statusDate); sheetHost.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
    }

    [TestCase("finish", "期限超過: 完了予定 10/2 を過ぎて未完了（見込み残存工数 8.5h）", "\uE7BA", true)]
    [TestCase("finish-estimate", "期限超過: 完了予定 10/2 を過ぎて未完了（見込み残存工数 8.5h）", "\uE7BA", true, Category = "OverdueMissingWork")]
    [TestCase("finish-unknown", "期限超過: 完了予定 10/2 を過ぎて未完了", "\uE7BA", true, Category = "OverdueMissingWork")]
    [TestCase("start", "期限超過: 開始予定 10/2 を過ぎて未着手", "\uE7BA", true)]
    // #236 part B: the mark compares the plan with the forecast, so 予定より遅れ names the cause and the
    // working days it slipped; the retired 「発行済み M/d から」 basis no longer appears anywhere.
    [TestCase("later", "予定より遅れ: +1 日（自分の残による）", "\uE7BA", false)]
    [TestCase("unchanged", "期限超過: 完了予定 10/2 を過ぎて未完了（見込み残存工数 8.5h）", "\uE7BA", true)]
    [TestCase("summary-overdue", "配下に期限超過 1", "\uE7BA", false)]
    [TestCase("summary-later", "配下に予定より遅れ 1", "\uE7BA", false)]
    [Category("SheetRowPresentation")]
    public async Task LatenessMarkerAndEndColorFollowOverdueAndOwnEndMovement(string state, string expected, string glyph, bool critical)
    {
        // The plan holds its own dates (#236), so every state states them: a row with no dates has no
        // forecast, no mark and no slip.
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8.5m, Remaining = 8.5m, Assignees = ["U1"],
            Start = Today.AddDays(-4), End = Today.AddDays(-3) };
        var baseline = row with { Start = Today, End = Today.AddDays(-3) };
        if (state == "finish-estimate") row = row with { Remaining = null };
        if (state == "finish-unknown") row = row with { Remaining = null, Estimate = null };
        if (state == "start") {
            row = row with { Start = Today.AddDays(-3), End = Today.AddDays(10) };
            baseline = baseline with { Start = Today.AddDays(-3), End = Today.AddDays(10) };
        }
        if (state == "later") {
            row = row with { Start = Today, End = Today, Remaining = 16 };
            baseline = baseline with { End = Today };
        }
        if (state == "unchanged") row = row with { Fixed = true, Start = Today.AddDays(-4), End = Today.AddDays(-3) };
        var rows = ImmutableArray.Create(row); var baselines = ImmutableArray.Create(baseline);
        if (state.StartsWith("summary")) {
            var child = row with { Identity = "I2", Parent = "I1", Start = Today.AddDays(-4), End = Today.AddDays(-3) };
            if (state == "summary-later") child = child with { Start = Today, End = Today, Remaining = 16 };
            rows = rows.Add(child);
            baselines = ImmutableArray.Create(baseline with { End = Today.AddDays(1) },
                child with { End = state == "summary-later" ? Today : Today.AddDays(-3) });
        }
        await MountPresentation(rows, baselines);
        await Ui.Run(async () => {
            var indicator = Ui.Find<FontIcon>("PlanIndicator1");
            var name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator);
            Assert.That(name, Does.Contain(expected).And.Not.Contain("発行済み"));
            if (state == "finish-unknown") Assert.That(name, Does.Not.Contain("（残"));
            Assert.That(indicator.Glyph, Is.EqualTo(glyph));
            Assert.That(ToolTipService.GetToolTip(indicator), Is.EqualTo(name));
            // A 期限超過 task also reports how far its forecast slipped; the published date is not compared.
            if (state == "finish") Assert.That(name, Does.Contain("見込終了は +"));
            var end = state.StartsWith("summary") ? Ui.Find<PlanSummaryCell>("PlanCell1_End").Text.Foreground
                : Ui.Find<PlanSheetCell>("PlanCell1_End").Foreground;
            var red = ((SolidColorBrush)PlanSheetView.Brush("SystemFillColorCriticalBrush")).Color;
            Assert.That(((SolidColorBrush)end).Color == red, Is.EqualTo(critical));
            await RenderedEvidence.Capture(sheetHost, "row-" + state);
        });
        if (state.StartsWith("summary")) {
            await Select(1, PlanField.Title);
            await Ui.Run(() => Ui.Click("PlanFold1"));
            await Ui.Until(() => sheet.RowIds.SequenceEqual(new[] { "I1", "" }));
            await Ui.Ready<FontIcon>("PlanIndicator1");
            await Ui.Run(() => Assert.That(Ui.Find<FontIcon>("PlanIndicator1").Glyph, Is.EqualTo(glyph)));
        }
    }

    [Test, Category("SheetRowPresentation")]
    public async Task EnteredCellsHaveTintButRecalculatedDatesOnlyHaveCornersAndSelectionWins()
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8, Remaining = 16, Assignees = ["U1"] };
        // The entered estimate must move the successor's re-derived dates off its baseline value (24h reaches
        // 10/7 where 16h lands back on 10/6), otherwise there is no recalculated date to carry a corner.
        await MountPresentation([row with { Estimate = 24, Assignees = ["U2"] }, row with { Identity = "I2", Predecessors = ["I1"] }],
            [row with { Start = Today, End = Today }, row with { Identity = "I2", Predecessors = ["I1"], Start = Today.AddDays(1), End = Today.AddDays(1) }],
            [new("U1", "alice", 100, null), new("U2", "bob", 100, null)]);
        await Ui.Run(() => {
            foreach (var (number, field, tinted) in new[] { (1, PlanField.Estimate, true), (1, PlanField.Assignees, true), (1, PlanField.End, false), (2, PlanField.End, false) }) {
                var cell = Ui.Find<PlanSheetCell>($"PlanCell{number}_{field}");
                var grid = (Grid)VisualTreeHelper.GetParent(cell); var frame = (Border)VisualTreeHelper.GetParent(grid);
                Assert.That(grid.Children.OfType<Microsoft.UI.Xaml.Shapes.Polygon>().Single().Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(((SolidColorBrush)frame.Background).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush(tinted ? "SheetChangedBrush" : "LayerFillColorDefaultBrush")).Color));
            }
        });
        await Select(1, PlanField.Estimate);
        await Ui.Run(() => {
            var frame = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(Ui.Find<PlanSheetCell>("PlanCell1_Estimate")));
            Assert.That(frame.BorderThickness.Left, Is.EqualTo(2));
            Assert.That(((SolidColorBrush)frame.Background).Color, Is.EqualTo(((SolidColorBrush)PlanSheetView.Brush("SheetSelectionBrush")).Color));
        });
    }

    [Test, Category("SheetRowPresentation")]
    public async Task SelectionAccentMovesAndEveryChildTitleStartsAfterItsParent()
    {
        var row = new PlanRow("I1", "Parent", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        ImmutableArray<PlanRow> rows = [row, row with { Identity = "I2", Parent = "I1" }, row with { Identity = "I3", Parent = "I1" }, row with { Identity = "I4", Parent = "I3" }];
        await MountPresentation(rows, rows);
        foreach (var selected in new[] { 1, 4 }) {
            await Select(selected, PlanField.Title); await Ui.Idle();
            await Ui.Run(() => {
                for (var i = 1; i <= 4; i++) Assert.That(Ui.Find<Border>("PlanSelectionAccent" + i).Visibility, Is.EqualTo(i == selected ? Visibility.Visible : Visibility.Collapsed));
                foreach (var (parent, child) in new[] { (1, 2), (1, 3), (3, 4) }) {
                    double X(int n) { var t = Ui.Find<TextBlock>("PlanTitleDisplay" + n); return t.TransformToVisual(sheet).TransformPoint(new()).X; }
                    Assert.That(X(child), Is.GreaterThan(X(parent)));
                }
            });
        }
    }

    [Test, Category("SheetRowPresentation")]
    public async Task LongTitleTrimsOnlyDisplayAndKeepsFullValueThroughEditingAndCopy()
    {
        var title = string.Concat(Enumerable.Repeat("長いタスク名と詳細説明", 12));
        var row = new PlanRow("I1", title, "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        await MountPresentation([row], [row]); await Select(1, PlanField.Title); await Ui.Idle();
        await Ui.Run(async () => {
            var display = Ui.Find<TextBlock>("PlanTitleDisplay1"); var cell = Ui.Find<PlanSheetCell>("PlanCell1_Title");
            Assert.That(display.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            Assert.That(display.IsTextTrimmed, Is.True);
            Assert.That(ToolTipService.GetToolTip(cell), Is.EqualTo(title));
            // The unchanged native TextBox peer reads this full value; the display must never replace it.
            Assert.That(cell.Text, Is.EqualTo(title));
            await RenderedEvidence.Capture(sheetHost, "row-long-title");
            await sheet.KeyboardCommand(Windows.System.VirtualKey.C); Assert.That(clipboard.Text, Is.EqualTo(title));
            cell.Focus(FocusState.Programmatic); cell.BeginEditing();
            Assert.That(display.Visibility, Is.EqualTo(Visibility.Collapsed)); Assert.That(cell.Text, Is.EqualTo(title));
            cell.Text = title + "追記";
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Until(() => session.Document.State.Rows[0].Title == title + "追記");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanTitleDisplay1").Visibility, Is.EqualTo(Visibility.Visible)));
    }

    [TestCase(false, 1), TestCase(false, 2), TestCase(true, 1), TestCase(true, 2)]
    [Category("SheetRowPresentation")]
    public async Task LongAssigneeNameTrimsAndAdditionalCountStaysInsideDefaultCell(bool displayName, int count)
    {
        const string login = "abcdefghijklmnopqrst";
        const string japanese = "山田太郎（プロジェクト管理担当）";
        var name = displayName ? japanese : login;
        var row = new PlanRow("I1", "Task", "acme/repo") { Assignees = count == 2 ? ["U1", "U2"] : ["U1"] };
        await MountPresentation([row], [row], displayName ? [new("U1", japanese, 100, null)] : [],
            ImmutableDictionary<string, string>.Empty.Add("U1", login).Add("U2", "suzuki"));
        foreach (var selected in new[] { false, true }) {
            await Select(1, selected ? PlanField.Assignees : PlanField.Title); await Ui.Idle();
            await Ui.Run(async () => {
                var cell = Ui.Find<PlanSheetCell>("PlanCell1_Assignees");
                var label = Ui.Find<TextBlock>("PlanAssigneeName1");
                var suffix = Ui.Find<TextBlock>("PlanAssigneeCount1");
                Assert.That(sheet.ConfiguredColumns.Single(c => c.Field == PlanField.Assignees).Width, Is.EqualTo(80));
                Assert.That(label.Text, Is.EqualTo(name));
                Assert.That(label.IsTextTrimmed, Is.True);
                Assert.That(suffix.Text, Is.EqualTo(count == 2 ? " +1" : ""));
                if (count == 2) {
                    var bounds = suffix.TransformToVisual(cell).TransformBounds(new(0, 0, suffix.ActualWidth, suffix.ActualHeight));
                    Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0));
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(cell.ActualWidth));
                    Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
                    Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(cell.ActualHeight));
                    Assert.That(suffix.ActualWidth, Is.GreaterThan(0));
                    Assert.That(suffix.IsTextTrimmed, Is.False);
                }
                Assert.That(cell.Text, Is.EqualTo(name + (count == 2 ? " +1" : "")));
                await RenderedEvidence.Capture(sheetHost, $"assignee-{displayName}-{count}-{selected}");
            });
        }
        await Ui.Run(async () => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Assignees");
            var full = login + (count == 2 ? ", suzuki" : "");
            await sheet.KeyboardCommand(Windows.System.VirtualKey.C);
            Assert.That(clipboard.Text, Is.EqualTo(full));
            cell.BeginEditing();
            Assert.That(cell.Text, Is.EqualTo(full));
            Assert.That(cell.Opacity, Is.EqualTo(1));
            Assert.That(((FrameworkElement)Ui.Find<TextBlock>("PlanAssigneeName1").Parent).Visibility, Is.EqualTo(Visibility.Collapsed));
            cell.EndEditing();
            Assert.That(((FrameworkElement)Ui.Find<TextBlock>("PlanAssigneeName1").Parent).Visibility, Is.EqualTo(Visibility.Visible));
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    [TestCase(0, ""), TestCase(1, "渡辺"), TestCase(2, "渡辺 +1"), TestCase(3, "alice"), TestCase(4, "担当者（未確認）")]
    [Category("SheetRowPresentation")]
    public async Task AssigneeDisplayUsesNameThenLoginWhileEditAndCopyKeepAllInputs(int kind, string expected)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { Assignees = kind switch { 0 => [], 2 => ["U1", "U2"], 4 => ["unknown"], _ => ["U1"] } };
        await MountPresentation([row], [row], kind == 3 ? [] : [new("U1", "渡辺", 100, null), new("U2", "鈴木", 100, null)],
            ImmutableDictionary<string, string>.Empty.Add("U1", "alice").Add("U2", "bob"));
        await Select(1, PlanField.Assignees);
        await Ui.Run(async () => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Assignees");
            Assert.That(cell.Text, Is.EqualTo(expected));
            Assert.That(Ui.Find<TextBlock>("PlanAssigneeName1").Text, Is.EqualTo(kind == 2 ? "渡辺" : expected));
            Assert.That(Ui.Find<TextBlock>("PlanAssigneeCount1").Text, Is.EqualTo(kind == 2 ? " +1" : ""));
            Assert.That(ToolTipService.GetToolTip(cell), Is.EqualTo(kind == 2 ? "渡辺, 鈴木" : expected));
            var full = kind switch { 0 => "", 2 => "alice, bob", 4 => "担当者（未確認）", _ => "alice" };
            Assert.That(sheet.EditForm("I1", PlanField.Assignees), Is.EqualTo(full));
            await sheet.KeyboardCommand(Windows.System.VirtualKey.C); Assert.That(clipboard.Text, Is.EqualTo(full));
            cell.BeginEditing(); Assert.That(cell.Text, Is.EqualTo(full));
            cell.EndEditing(); Assert.That(cell.Text, Is.EqualTo(expected));
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test, Category("PlanSheetNative"), Category("PlanSheetPhase2Keys")]
    public async Task NativeF2EnterEscapeAndCopyUseDateEditFormWithoutFixingCalculatedDates()
    {
        await Select(1, PlanField.Start); await SheetNativeInput.Click("PlanCell1_Start");
        var undo = session.UndoCount; var changes = session.Changes(Today).TaskCount;
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Start").Text == "2026-10-05");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Start").SelectedText, Is.EqualTo("2026-10-05")));
        await SheetNativeInput.Press(Windows.System.VirtualKey.Enter); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("10/5 (月)")));
        Assert.That(session.UndoCount, Is.EqualTo(undo)); Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(changes));
        Assert.That(session.Schedule(Today).First().Start.Origin, Is.EqualTo(DateOrigin.Calculated));
        await SheetNativeInput.Click("PlanCell1_Start"); await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Start").Text = "2026-10-14");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Escape); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("10/5 (月)")));
        await SheetNativeInput.Press(Windows.System.VirtualKey.C, Windows.System.VirtualKey.Control); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("2026-10-05")); Assert.That(session.UndoCount, Is.EqualTo(undo));
    }
    [Test, Category("EditNotification")]
    public async Task F2EditFormSurvivesRefreshDuringItsRealTextChangingEvent()
    {
        await Select(1, PlanField.Start);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
            cell.Focus(FocusState.Programmatic);
            var refreshed = false;
            void RefreshDuringChange(TextBox sender, TextBoxTextChangingEventArgs args)
            {
                if (refreshed || sender.Text != "2026-10-05") return;
                refreshed = true; sheet.Refresh();
            }
            cell.TextChanging += RefreshDuringChange;
            try { cell.BeginEditing(); }
            finally { cell.TextChanging -= RefreshDuringChange; }
            Assert.That(refreshed, Is.True, "The fixture must exercise the actual text notification.");
            Assert.That(cell.Text, Is.EqualTo("2026-10-05"));
            Assert.That(cell.SelectedText, Is.EqualTo("2026-10-05"));
            Assert.That(sheet.Pending, Is.Empty);
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test, Category("EditNotification")]
    public async Task EditFormTextNotificationAfterF2CreatesNoPendingInput()
    {
        await Select(1, PlanField.Start);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
            cell.Focus(FocusState.Programmatic); cell.BeginEditing();
        });
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
            void OnLateChange(TextBox sender, TextBoxTextChangingEventArgs args) => notified.TrySetResult();
            cell.TextChanging += OnLateChange;
            try { cell.SelectAll(); cell.SelectedText = "2026-10-05"; }
            finally { cell.TextChanging -= OnLateChange; }
        });
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
            Assert.That(cell.Text, Is.EqualTo("2026-10-05"));
            Assert.That(sheet.Pending, Is.Empty);
            typeof(PlanSheetCell).GetMethod("CommitAndNavigate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(cell, [sheet, "I1", cell.Text, false, false]);
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("10/5 (月)")));
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(session.Schedule(Today).First().Start.Origin, Is.EqualTo(DateOrigin.Calculated));
    }

    [TestCase(false, "unchanged"), TestCase(true, "unchanged")]
    [TestCase(false, "returned"), TestCase(true, "returned")]
    [TestCase(false, "changed"), TestCase(true, "changed")]
    [Category("EditExit")]
    public async Task DateKeyCommitRestoresDisplayAfterQueuedNavigation(bool tab, string variant)
    {
        var changed = variant == "changed";
        await Select(1, PlanField.Start);
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
            cell.Focus(FocusState.Programmatic); cell.BeginEditing();
            if (variant != "unchanged") cell.Text = "2026-10-14";
            if (variant == "returned") cell.Text = "2026-10-05";
            // Exercise the asynchronous cell lifecycle used by native key routing;
            // this does not establish physical-key or IME event delivery.
            typeof(PlanSheetCell).GetMethod("CommitAndNavigate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(cell, [sheet, "I1", cell.Text, tab, false]);
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo(changed ? "10/14 (水)" : "10/5 (月)"));
            Assert.That(Ui.Find<TextBox>(tab ? "PlanCell1_End" : "PlanCell2_Start").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
        });
        Assert.That(session.UndoCount, Is.EqualTo(changed ? 1 : 0));
        if (!changed) Assert.That(session.Schedule(Today).First().Start.Origin, Is.EqualTo(DateOrigin.Calculated));
    }

    [Test, Category("EditExit")]
    public async Task NewerDateInputSurvivesAnEarlierQueuedKeyCommit()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Select(1, PlanField.Start);
        try {
            await Ui.Run(() => {
                _ = sheet.Run(() => release.Task);
                var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
                cell.Focus(FocusState.Programmatic); cell.BeginEditing(); cell.Text = "2026-10-14";
                typeof(PlanSheetCell).GetMethod("CommitAndNavigate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(cell, [sheet, "I1", cell.Text, false, false]);
                cell.Text = "2026-10-15";
            });
        } finally { release.TrySetResult(); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("2026-10-15"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Start").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
        });
        Assert.That(session.Document.State.Rows[0].Start, Is.EqualTo(new DateOnly(2026, 10, 14)));
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start");
            Assert.That(cell.Text, Is.EqualTo("10/15 (木)"));
        });
        Assert.That(session.Document.State.Rows[0].Start, Is.EqualTo(new DateOnly(2026, 10, 15)));
    }

    [Test, Category("PlanSheetPhase2")]
    public async Task LabelledCommandsStayReachableInNarrowNativeOverflowIncludingColumnChoices()
    {
        await Ui.Run(() => { sheetHost.Width = 760; sheetHost.Height = 600; }); await Ui.Idle();
        // CommandBar moves primary commands into the overflow on a later layout pass than the resize,
        // so the narrow state is awaited rather than read once.
        await Ui.Until(() => Ui.Find<CommandBar>("PlanSheetCommands").PrimaryCommands.OfType<AppBarButton>().Any(b => b.IsInOverflow));
        await Ui.Run(() => {
            var bar = Ui.Find<CommandBar>("PlanSheetCommands");
            Assert.That(bar.DefaultLabelPosition, Is.EqualTo(CommandBarDefaultLabelPosition.Right));
            var buttons = bar.PrimaryCommands.Concat(bar.SecondaryCommands).OfType<AppBarButton>().ToArray();
            Assert.That(buttons.Select(b => b.Label), Is.SupersetOf(new[] { "行を挿入", "インデント", "アウトデント", "先行タスクを追加…", "タスクをリンク", "リンクを解除", "すべて折りたたむ", "すべて展開", "選択タスクの日程へ移動", "コピー", "貼り付け", "下へコピー", "クリア", "表示列" }));
            Assert.That(bar.SecondaryCommands.OfType<AppBarButton>().Where(b => b.Label == "コピー").Single().KeyboardAcceleratorTextOverride, Is.EqualTo("Ctrl+C"));
            Ui.Click("PlanSheetMore");
        });
        await Ui.Ready<MenuFlyoutItem>("PlanSheetCopy");
        await Ui.Run(() => {
            var bar = Ui.Find<CommandBar>("PlanSheetCommands");
            foreach (var button in bar.PrimaryCommands.Concat(bar.SecondaryCommands).OfType<AppBarButton>()) {
                var id = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button);
                var visible = button.IsInOverflow ? (FrameworkElement)Ui.Find<MenuFlyoutItem>(id) : button;
                Assert.That(visible.IsLoaded && visible.ActualWidth > 0 && visible.ActualHeight > 0, Is.True, button.Label);
                Assert.That(Ui.Tree(visible).OfType<TextBlock>().Any(t => t.Text == button.Label && t.ActualWidth > 0), Is.True, button.Label);
                var accessible = Ui.Tree(Ui.Root).Concat(VisualTreeHelper.GetOpenPopupsForXamlRoot(sheet.XamlRoot)
                    .Where(p => p.IsOpen).SelectMany(p => Ui.Tree(p.Child))).OfType<FrameworkElement>()
                    .Where(e => e.IsLoaded && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == id).Distinct();
                Assert.That(accessible.Count(), Is.EqualTo(1), id);
            }
        });
        await Ui.Run(() => Assert.That(Ui.CommandButton("PlanSheetColumns").Flyout, Is.Null));
    }
    [Test, Category("PlanSheetPhase2")]
    public async Task StandardHeadersAndShortDatesExposeMappedFieldsAndFullDates()
    {
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {
            Columns = session.Document.State.Settings.Columns.Where(column => column.Role != PlanField.Remaining).ToImmutableArray()
        }), Today);
        await Ui.Run(() => {
            sheet.Refresh();
            var start = Ui.Find<TextBlock>("PlanHeaderStart");
            Assert.That(start.Text, Is.EqualTo("開始日"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(start), Is.EqualTo("GitHub: Start date"));
            Assert.That(ToolTipService.GetToolTip(start), Is.EqualTo("GitHub: Start date"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<TextBlock>("PlanHeaderRemaining")), Is.EqualTo("見込み残存工数 · 未設定"));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").Text, Is.EqualTo("タスク名"));
            var cell = Ui.Find<TextBox>("PlanCell1_Start");
            Assert.That(cell.Text, Is.EqualTo("10/5 (月)"));
            Assert.That(ToolTipService.GetToolTip(cell), Is.EqualTo("2026-10-05 (月)"));
            Assert.That(cell.FontStyle, Is.EqualTo(Windows.UI.Text.FontStyle.Normal));
            Assert.That(sheet.RowHeight, Is.EqualTo(28));
        });
    }
    [Test, Category("PlanSheetPhase2")]
    public async Task DateEditFormAndCopyPreserveCalculatedOriginUntilTheDateChanges()
    {
        await Select(1, PlanField.Start);
        var undo = session.UndoCount; var unpublished = session.Changes(Today).Fields.Count;
        await Ui.Run(() => {
            var cell = Ui.Find<PlanSheetCell>("PlanCell1_Start"); cell.Focus(FocusState.Programmatic); cell.BeginEditing();
            Assert.That(cell.Text, Is.EqualTo("2026-10-05")); Assert.That(cell.SelectedText, Is.EqualTo(cell.Text));
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Start").Text == "10/5 (月)");
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Start"); cell.Focus(FocusState.Programmatic); cell.Text = "2026-10-05"; });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Start").Text == "10/5 (月)");
        Assert.That(session.UndoCount, Is.EqualTo(undo)); Assert.That(session.Changes(Today).Fields.Count, Is.EqualTo(unpublished));
        Assert.That(session.Document.State.Rows[0].Start, Is.Null); Assert.That(session.Document.State.Rows[0].Fixed, Is.False);
        await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("2026-10-05"));
        await Edit(1, PlanField.Start, "2026-10-14");
        Assert.That(session.Document.State.Rows[0].Start, Is.EqualTo(new DateOnly(2026, 10, 14)));
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("10/14 (水)")));
    }
    [Test]
    public async Task ValidationCalloutFollowsTheProblemAcrossRowRecycling()
    {
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is not null);
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanTasks"); list.ScrollIntoView(list.Items[90]);
        });
        await Ui.Ready<TextBox>("PlanCell91_Title"); await Ui.Idle();
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is null);
        await Ui.Run(() => {
            Assert.That(Ui.Popup<Border>("SheetInputProblem"), Is.Null,
                "An offscreen problem must not label the task now occupying its recycled editor.");
        });
        await Ui.Run(() => sheet.List.ScrollIntoView("I1"));
        await Ui.Ready<TextBox>("PlanCell1_Remaining");
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is not null);
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(cell.Text, Is.EqualTo("invalid"));
            Assert.That(VisualTreeHelper.GetOpenPopupsForXamlRoot(cell.XamlRoot)
                .Single(p => p.Child == Ui.Popup<Border>("SheetInputProblem")).PlacementTarget, Is.SameAs(cell));
            sheet.List.ScrollIntoView("I91");
        });
        await Ui.Ready<TextBox>("PlanCell91_Title");
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is null);
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        try {
            // Focus can return while native ScrollIntoView is still arranging its
            // viewport. The command is visibly complete when that editor's callout returns.
            await Ui.Until(() => {
                var row = (sheet.List.ContainerFromItem("I1") as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
                var cell = row?.Cells.Single(cell => cell.Field == PlanField.Remaining);
                var problem = Ui.Popup<Border>("SheetInputProblem");
                return row?.Owner == sheet && row.Identity == "I1" && cell is { IsLoaded: true, FocusState: not FocusState.Unfocused }
                    && problem is { IsLoaded: true }
                    && VisualTreeHelper.GetOpenPopupsForXamlRoot(cell.XamlRoot).Any(p => p.Child == problem && p.PlacementTarget == cell);
            });
        } catch (TimeoutException) {
            await Ui.Run(() => {
                var row = (sheet.List.ContainerFromItem("I1") as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
                var cell = row?.Cells.Single(cell => cell.Field == PlanField.Remaining);
                var bounds = cell?.TransformToVisual(sheet.List).TransformBounds(new(0, 0, cell.ActualWidth, cell.ActualHeight));
                Ui.Trace($"[VALIDATION RETURN] identity={row?.Identity}, owner={row?.Owner == sheet}, loaded={cell?.IsLoaded}, "
                    + $"focus={cell?.FocusState}, bounds={bounds}, viewport={sheet.SheetViewport}x{sheet.List.ActualHeight}, "
                    + $"popup={Ui.Popup<Border>("SheetInputProblem") is not null}, problem={sheet.Problems.ContainsKey(("I1", PlanField.Remaining))}");
            });
            throw;
        }
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(cell.Text, Is.EqualTo("invalid"));
            var problem = Ui.Popup<Border>("SheetInputProblem"); Assert.That(problem, Is.Not.Null);
            Assert.That(VisualTreeHelper.GetOpenPopupsForXamlRoot(cell.XamlRoot).Single(p => p.Child == problem).PlacementTarget, Is.SameAs(cell));
        });
        await Edit(1, PlanField.Remaining, "8");
    }

    [TestCase(false), TestCase(true), Category("PlanSheetPerformance")]
    public async Task LongHorizonDayZoomAndScrollSeparatesLayoutFromCapture(bool capture)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
            [new("I1000", PlanField.StartNoEarlierThan, Today.AddYears(20))]), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "週"; });
        await Ui.Idle();
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanTasks"); list.ScrollIntoView(list.Items[974]);
        });
        await Ui.Ready<TextBox>("PlanCell975_Title");
        await Ui.Run(() => {
            var labels = Ui.Tree(sheet).OfType<TextBlock>().Count(t =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineLabel", StringComparison.Ordinal));
            var bars = Ui.Tree(sheet).OfType<Rectangle>().Count(t =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanBar", StringComparison.Ordinal));
            Ui.Trace($"[HORIZON layout] capture={capture} milliseconds={timer.Elapsed.TotalMilliseconds:F1} days={sheet.DayCount} realizedRows={sheet.Realized.Count} labels={labels} bars={bars} viewport={sheet.ChartViewport:F1}");
            Assert.That(sheet.DayCount, Is.GreaterThan(7000));
            Assert.That(labels, Is.InRange(2, (int)Math.Ceiling(sheet.ChartViewport / sheet.DayWidth) + 1));
            Assert.That(bars, Is.InRange(1, 100));
        });
        if (capture) await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "long-horizon-day"));
        Ui.Trace($"[HORIZON complete] capture={capture} milliseconds={timer.Elapsed.TotalMilliseconds:F1}");
    }

    [TestCase(false), TestCase(true), Category("PlanSheetReview4")]
    public async Task SheetPendingInputSurvivesLeavingTheAcceptedFilter(bool redo)
    {
        await PrepareSheetPendingExit();
        await Ui.Run(() => Assert.That(Ui.Find<ListView>("PlanTasks").Items.Contains("I1"), Is.True,
            "The accepted title filter must retain a row containing invalid input."));
        await Ui.Run(() => sheet.KeyboardCommand(redo ? Windows.System.VirtualKey.Y : Windows.System.VirtualKey.Z)); await Ui.Idle();
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Remaining").FocusState != FocusState.Unfocused);
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(cell.Text, Is.EqualTo("invalid"));
            var problem = Ui.Popup<Border>("SheetInputProblem");
            Assert.That(problem, Is.Not.Null);
            Assert.That(((TextBlock)problem!.Child).Text, Is.Not.Empty);
            Assert.That(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(cell.XamlRoot).Single(p => p.Child == problem).PlacementTarget, Is.SameAs(cell));
        });
        if (!redo) await Ui.Run(async () => await RenderedEvidence.Capture(Ui.Popup<Border>("SheetInputProblem")!, "sheet-validation"));
        await Edit(1, PlanField.Remaining, "8");
        await Ui.Until(() => !Ui.Find<ListView>("PlanTasks").Items.Contains("I1"));
    }

    private async Task PrepareSheetPendingExit()
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 1");
        await Ui.Until(() => !Ui.Find<ListView>("PlanTasks").Items.Contains("I2"));
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.Run(() => {
            var title = Ui.Find<TextBox>("PlanCell1_Title"); title.Focus(FocusState.Programmatic); title.Text = "Outside filter";
            Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => session.Document.State.Rows[0].Title == "Outside filter"); await Ui.Idle();
    }

    [Test]
    public async Task SheetRefusedColumnChangeKeepsInvalidInputReachable()
    {
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.Run(async () => {
            Assert.That(async () => await sheet.FlushInput(), Throws.InstanceOf<InvalidOperationException>());
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Visibility, Is.EqualTo(Visibility.Visible));
            await Task.CompletedTask;
        });
    }

    [Test, Category("PlanSheetNative")]
    public async Task SheetEscapeDiscardsRetainedInput()
    {
        await PrepareSheetPendingExit(); var undo = session.UndoCount;
        await SheetNativeInput.Click("PlanCell1_Remaining");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Escape);
        await Ui.Until(() => !Ui.Find<ListView>("PlanTasks").Items.Contains("I1"));
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => Assert.That(Ui.Popup<Border>("SheetInputProblem") is not null, Is.False));
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
            await Ui.ClickCommand(paste ? "PlanSheetPaste" : "PlanSheetCopy");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The external owner is still blocked. The UI must be able to respond.
            await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanSheetFilter").IsEnabled, Is.True)).WaitAsync(TimeSpan.FromSeconds(3));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text == "クリップボードを使用できません。もう一度お試しください。");
            await Ui.Run(() => Assert.That(SelectProvider("PlanCell1_Remaining").IsSelected, Is.True));
        }
        finally {
            release.Set();
            // A command failure before entering the clipboard must remain the test's
            // failure, rather than being replaced by a cleanup timeout.
            if (started.Task.IsCompleted) await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
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
        var titleId = $"PlanCell{session.Document.State.Rows.IndexOf(added) + 1}_Title";
        try {
            await Ui.Ready<TextBox>(titleId);
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(Ui.Root.XamlRoot), Ui.Find<TextBox>(titleId)));
        } catch {
            await Ui.Run(() => {
                string DescribeRow(PlanSheetRow row) => $"identity={row.Identity}, owner={ReferenceEquals(row.Owner, sheet)}, loaded={row.IsLoaded}, realized={sheet.Realized.Contains(row)}, titleId={AutomationProperties.GetAutomationId(row.Cells.Single(c => c.Field == PlanField.Title))}, width={row.ActualWidth}, height={row.ActualHeight}";
                var container = sheet.List.ContainerFromItem(added.Identity) as ListViewItem;
                var focus = FocusManager.GetFocusedElement(Ui.Root.XamlRoot) as DependencyObject;
                Ui.Trace($"[INSERT] expected={added.Identity}/{titleId}, planId={sheet.PlanIds.GetValueOrDefault(added.Identity)}, itemCount={sheet.List.Items.Count}, containerContent={container?.Content}, containerData={container?.DataContext}, target={(container?.ContentTemplateRoot is PlanSheetRow target ? DescribeRow(target) : "none")}, focus={focus?.GetType().Name}/{(focus is null ? "" : AutomationProperties.GetAutomationId(focus))}, {sheet.WorkDescription}");
                foreach (var row in Ui.Tree(sheet.List).OfType<PlanSheetRow>()) Ui.Trace("[INSERT ROW] " + DescribeRow(row));
            });
            throw;
        }
        await Ui.Run(() => {
            var title = Ui.Find<TextBox>(titleId);
            Assert.That(title.Text, Is.Empty);
            title.Text = "New planned task";
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Single(r => r.Identity == added.Identity).Title, Is.EqualTo("New planned task"));
        Assert.That(session.Document.State.Rows.Where(r => r.Identity != added.Identity), Is.EqualTo(before));
    }
    [TestCase(1), TestCase(2), Category("PlanSheetReview4")]
    public async Task RejectedZoomKeepsSelectionScaleAndLabelsTogether(int proposed)
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
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
            Assert.That(string.Join(" ", Ui.Tree(sheet).OfType<TextBlock>().Where(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineUpper", StringComparison.Ordinal)).Select(t => t.Text)), Does.Contain("2026"));
            var labels = Ui.Tree(sheet).OfType<TextBlock>().Where(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineLabel", StringComparison.Ordinal)).ToArray();
            Assert.That(labels, Is.Not.Empty);
            Assert.That(labels.All(t => t.Text.Length is 1 or 2 && t.Text.All(char.IsDigit)), Is.True);
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
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Run(() => sheet.FlushInput()); await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before)); Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty));
    }
    [TestCase(PlanField.Start), TestCase(PlanField.End), Category("PlanSheetReview3")]
    public async Task ReturningToTheDisplayedCalculatedDateKeepsAutomaticSchedulingAndHistory(string field)
    {
        await Select(1, field);
        var before = session.Document.State; var undo = session.UndoCount;
        string original = "";
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_" + field); original = sheet.EditForm("I1", field);
            Assert.That(cell.Focus(FocusState.Programmatic), Is.True);
        });
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_" + field).FocusState != FocusState.Unfocused);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_" + field).Text = original + "x");
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_" + field).Text = original);
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
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
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
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
        await Ui.ClickCommand("PlanSheetPaste");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Ui.Unmount(sheetHost); await Ui.Run(() => sheetHost.Children.Clear()); await Ui.Idle();
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
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
        await Ui.Unmount(sheetHost); await Ui.Run(() => sheetHost.Children.Clear()); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(sheet.IsLoaded, Is.False);
            Assert.That(VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot).Any(p => p.IsOpen), Is.False);
        });
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
        await Edit(1, PlanField.Title, "Remounted");
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Remounted"));
    }
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true), Category("PlanSheetReview2")]
    public async Task ClipboardContentionIsRetriedOrReportedWithoutChangingSelection(bool paste, bool persistent)
    {
        await Ui.Unmount(sheetHost); await Ui.Run(() => sheetHost.Children.Clear());
        var attempts = 0;
        void Contention() { if (++attempts <= (persistent ? 100 : 1)) throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0)); }
        await Ui.Run(() => sheet = new(session, () => { Contention(); return Task.FromResult(new PlanClipboardContent("16", null)); }, value => { Contention(); clipboard = value; }));
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost); await Select(1, PlanField.Remaining);
        var before = session.Document.State;
        await Ui.ClickCommand(paste ? "PlanSheetPaste" : "PlanSheetCopy"); await Ui.Idle();
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
        await Ui.ClickCommand("PlanSheetPaste"); await Ui.Idle();
        Assert.That(session.Document.State, Is.EqualTo(before));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("コピー元を確認できません。"));
            Assert.That(SelectProvider("PlanCell1_Remaining").IsSelected, Is.True);
        });
        await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
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
            await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
            await left.Task.WaitAsync(TimeSpan.FromSeconds(10));
            left = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.True));
            await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").FocusState != FocusState.Unfocused);
            await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Title").Text = latest);
            await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
            await left.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        await Ui.Run(() => sheet.FlushInput());
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo(latest));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Earlier"));
    }
    private static ISelectionItemProvider SelectProvider(string id)
        => (ISelectionItemProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>(id)).GetPattern(PatternInterface.SelectionItem);
    private async Task Select(int row, string field, bool extend = false)
    {
        await Ui.Ready<FrameworkElement>($"PlanCell{row}_{field}");
        await Ui.Run(() => { var provider = SelectProvider($"PlanCell{row}_{field}"); if (extend) provider.AddToSelection(); else provider.Select(); });
    }
    private async Task SelectFocusedCell(int row, string field, bool extend = false)
    {
        await Select(row, field, extend);
        // Selection requests focus and scrolling; loaded cells alone do not mean native layout has settled.
        await Ui.Until(() => Ui.Find<Control>($"PlanCell{row}_{field}").FocusState != FocusState.Unfocused
            && SelectProvider($"PlanCell{row}_{field}").IsSelected);
        await SheetNativeInput.Rendered();
    }
    private async Task Edit(int row, string field, string text)
    {
        await Select(row, field);
        var before = session.UndoCount;
        await Ui.Run(() => { var cell = Ui.Find<TextBox>($"PlanCell{row}_{field}"); Assert.That(cell.Focus(FocusState.Programmatic), Is.True); cell.Text = text; });
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic), Is.True));
        await Ui.Until(() => session.UndoCount != before || Ui.Popup<Border>("SheetInputProblem") is not null
            || Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
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
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Task 1"));
        Assert.That(session.UndoCount, Is.Zero);
    }
    [TestCase(false), TestCase(true), Category("PlanSheetReview")]
    public async Task HistoryCommandKeepsInvalidNewRowAvailableForCorrection(bool redo)
    {
        await Ui.ClickCommand("PlanSheetInsert"); await Ui.Idle();
        var index = Array.FindIndex(session.Document.State.Rows.ToArray(), r => r.Title == "");
        var number = index + 1;
        await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        var identity = session.Document.State.Rows[index].Identity;
        if (redo)
        {
            await Edit(number, PlanField.Title, "Temporary");
            await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
            Assert.That(session.RedoCount, Is.EqualTo(1));
        }
        var accepted = session.Document.State;
        var before = session.UndoCount;
        await Edit(number, PlanField.Remaining, "invalid");
        await Ui.Run(() => Ui.Find<ListView>("PlanTasks").ScrollIntoView("I75"));
        await Ui.Ready<TextBox>("PlanCell76_Title");
        await Ui.Run(() => sheet.KeyboardCommand(redo ? Windows.System.VirtualKey.Y : Windows.System.VirtualKey.Z)); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Any(r => r.Identity == identity), Is.True);
        Assert.That(session.UndoCount, Is.EqualTo(before));
        Assert.That(session.Document.State, Is.EqualTo(accepted));
        await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        await Ui.Until(() => Ui.Tree(sheet).OfType<TextBox>().Any(cell =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(cell) == $"PlanCell{number}_Remaining"
            && cell.FocusState != FocusState.Unfocused));
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
        await Ui.Unmount(sheetHost); await Ui.Run(() => sheetHost.Children.Clear());
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
        await Select(1, PlanField.Predecessors);
        await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("計画外"));
        await Select(3, PlanField.Predecessors);
        await Ui.ClickCommand("PlanSheetPaste"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Predecessors, Is.EqualTo(new[] { "outside" }));
    }
    [Test, Category("PlanSheetReview")]
    public async Task DefaultPlanningColumnsFitBesideGanttWithCompactRowsAndCalendarContext()
    {
        // #236 part B: the twelve default columns measure 944 px (ID 52 + 状態 28 + タスク名 224 + 工程 48 +
        // 担当者 80 + 見積 56 + 残 56 + 実績 56 + 開始日 92 + 終了日 92 + 見込終了 96 + 先行 64) and the
        // divider 6 px, so 944 + 6 + the 320 px Gantt minimum is a 1270 px sheet — the honest threshold the
        // spec and manual now quote as about 1,300 effective pixels of window.
        await Ui.Run(() => { sheet.Width = 1270; Ui.Window.AppWindow.Resize(new(1600, 960)); sheet.HorizontalAlignment = HorizontalAlignment.Left; });
        await Ui.Ready<TextBox>("PlanCell1_Predecessors");
        await Ui.Until(() => sheet.ActualWidth == 1270);
        await Ui.Run(() => {
            var last = Ui.Find<TextBox>("PlanCell1_Predecessors");
            var viewport = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            Assert.That(last.TransformToVisual(sheet).TransformPoint(new()).X + last.ActualWidth, Is.LessThanOrEqualTo(viewport.ActualWidth + 1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").ActualWidth, Is.GreaterThanOrEqualTo(320));
            var first = Ui.Find<TextBox>("PlanCell1_Title").TransformToVisual(sheet).TransformPoint(new()).Y;
            var second = Ui.Find<TextBox>("PlanCell2_Title").TransformToVisual(sheet).TransformPoint(new()).Y;
            Assert.That(second - first, Is.EqualTo(28).Within(1));
            Assert.That(string.Join(" ", Ui.Tree(sheet).OfType<TextBlock>().Where(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineUpper", StringComparison.Ordinal)).Select(t => t.Text)), Does.Contain("2026年").And.Contain("10月"));
        });
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Keyboard), Is.True));
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "compact-default-planning-columns"));
    }
    [Test, Category("PlanSheetReview")]
    public async Task DividerResizesBothViewportsAndDayHeaderKeepsYearContextAfterScrolling()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await Ui.Ready<TextBox>("PlanCell1_Title");
        double before = 0;
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            var provider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue);
            before = provider.Value;
            provider.SetValue(before - 100);
        });
        await Ui.Until(() => Math.Abs(Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth - (before - 100)) <= 1);
        await Ui.Run(() => {
            var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            Assert.That(chart.TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(before - 100).Within(1));
            chart.ChangeView((new DateOnly(2026, 12, 28).DayNumber - sheet.FirstDay.DayNumber) * sheet.DayWidth, null, null, true);
        });
        await Ui.Until(() => string.Join(" ", Ui.Tree(sheet).OfType<TextBlock>().Where(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineUpper", StringComparison.Ordinal)).Select(t => t.Text)).Contains("2027年1月"));
        await Ui.Run(() => Assert.That(string.Join(" ", Ui.Tree(sheet).OfType<TextBlock>().Where(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t).StartsWith("PlanTimelineUpper", StringComparison.Ordinal)).Select(t => t.Text)), Does.StartWith("2026年12月")));
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
        await Ui.ClickCommand("PlanSheetInsert"); await Ui.Idle();
        var number = Array.FindIndex(session.Document.State.Rows.ToArray(), r => r.Title == "") + 1;
        if (redo)
        {
            await Edit(number, PlanField.Title, "Temporary");
            await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        }
        var accepted = session.Document.State;
        await Edit(number, PlanField.Remaining, "invalid");
        await SheetNativeInput.Click($"PlanCell{number}_Title");
        await SheetNativeInput.Press(redo ? Windows.System.VirtualKey.Y : Windows.System.VirtualKey.Z, Windows.System.VirtualKey.Control);
        await Ui.Until(() => Ui.Tree(sheet).OfType<TextBox>().Any(cell =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(cell) == $"PlanCell{number}_Remaining"
            && cell.FocusState != FocusState.Unfocused));
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
        await Ui.Until(() => Ui.Find<TextBox>(tab ? "PlanCell1_Phase" : "PlanCell2_Title").FocusState != FocusState.Unfocused);
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }
    [Test, Category("PlanSheetNative")]
    public async Task NativeClipboardCopiesAndPastesTheDisplayedCell()
    {
        NativeClipboardScope? preserved = null;
        await Ui.Run(() => preserved = new NativeClipboardScope());
        try
        {
            await Ui.Unmount(sheetHost); await Ui.Run(() => sheetHost.Children.Clear());
            await Ui.Run(() => sheet = new(session));
            await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
            await Select(1, PlanField.Title);
            await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
            await Select(3, PlanField.Title);
            await Ui.ClickCommand("PlanSheetPaste"); await Ui.Idle();
            Assert.That(session.Document.State.Rows[2].Title, Is.EqualTo("Task 1"));
            Assert.That(session.UndoCount, Is.EqualTo(1));
        }
        finally { await Ui.Run(() => preserved?.Dispose()); }
    }
    [TestCase(1, false), TestCase(2, false)]
    [TestCase(1, true, Category = "PlanSheetNative"), TestCase(2, true, Category = "PlanSheetNative")]
    public async Task SingleCellCtrlDCopiesAboveAsOneUndoOrDoesNothingOnFirstRow(int number, bool native)
    {
        await Select(number, PlanField.Title);
        if (native) {
            await SheetNativeInput.Click($"PlanCell{number}_Title");
            await SheetNativeInput.Press(Windows.System.VirtualKey.D, Windows.System.VirtualKey.Control);
        } else await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.D));
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[number - 1].Title, Is.EqualTo("Task 1"));
        Assert.That(session.UndoCount, Is.EqualTo(number == 1 ? 0 : 1));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty));
        if (number == 2) {
            await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
            Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Task 2"));
            Assert.That(session.UndoCount, Is.Zero);
        }
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>($"PlanCell{number}_Title").Text, Is.EqualTo($"Task {number}")));
    }

    [Test]
    public async Task SingleCellCtrlDSkipsReadOnlySourceWithoutChangingHistory()
    {
        await session.Execute(new IndentPlanRows(["I3"]), Today);
        await Ui.Run(() => sheet.Refresh()); await Ui.Idle();
        await Select(3, PlanField.Remaining);
        var before = PlanJson.Text(session.Document);
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.D)); await Ui.Idle();
        Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty));
    }

    [Test]
    public async Task SingleCellCtrlDUsesPreviousVisibleRow()
    {
        await Edit(1, PlanField.Title, "Visible source");
        await Edit(3, PlanField.Title, "Visible target");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Visible");
        await Ui.Until(() => sheet.RowIds.Contains("I3") && !sheet.RowIds.Contains("I2"));
        await Select(3, PlanField.Title);
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.D)); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Title, Is.EqualTo("Visible source"));
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Task 2"));
        Assert.That(session.UndoCount, Is.EqualTo(3));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Title, Is.EqualTo("Visible target"));
    }

    [TestCase(false), TestCase(true), Category("PlanSheetNative")]
    public async Task SheetSearchEscapeReturnsToTheSameCellAndKeepsFilter(bool summary)
    {
        if (summary) {
            await Ui.Run(async () => {
                await session.Execute(new IndentPlanRows(["I3"]), Today); sheet.Refresh();
            });
            await Ui.Idle();
        }
        var field = summary ? PlanField.Estimate : PlanField.Title;
        await Select(2, field);
        await SheetNativeInput.Click($"PlanCell2_{field}");
        await SheetNativeInput.Press(Windows.System.VirtualKey.F, Windows.System.VirtualKey.Control);
        await Ui.Run(() => Assert.That(FocusManager.GetFocusedElement(sheet.XamlRoot), Is.SameAs(Ui.Find<TextBox>("PlanSheetFilter"))));
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Escape);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(FocusManager.GetFocusedElement(sheet.XamlRoot), Is.SameAs(Ui.Find<Control>($"PlanCell2_{field}")));
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Text, Is.EqualTo("Task"));
        });
        Assert.That(session.UndoCount, Is.EqualTo(summary ? 1 : 0));
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
        await Ui.Run(() => sheet.List.ScrollIntoView(""));
        await Ui.Ready<TextBox>("PlanCell0_Title");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell0_Title").Text, Is.Empty));
        await Edit(0, PlanField.Title, "New task");
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(101));
        Assert.That(session.Document.State.Rows[^1].Title, Is.EqualTo("New task"));
        Assert.That(session.Document.State.Rows[^1].Repository, Is.EqualTo("acme/repo"));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("New task"), "The created task stays selected without the empty placeholder.");
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(100));
        await Ui.Run(() => sheet.List.ScrollIntoView(""));
        await Ui.Ready<TextBox>("PlanCell0_Title");
        await Ui.Run(() => {
            var row = (sheet.List.ContainerFromItem("") as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
            Assert.That(row?.Owner, Is.SameAs(sheet));
            Assert.That(row!.Identity, Is.Empty);
            Assert.That(Ui.Find<TextBox>("PlanCell0_Title", row).Text, Is.Empty,
                "Undo restores the empty editor without the removed task's text.");
        });
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
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
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
        await Ui.ClickCommand("PlanSheetFillDown"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].StartNoEarlierThan, Is.EqualTo(Today));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].StartNoEarlierThan, Is.Null);
    }
    [Test]
    public async Task SaveFailureKeepsTheEditAndRetryPersistsItWithoutAnotherUndo()
    {
        var store = new PlanStore(root);
        using (var writer = new FileStream(store.FileFor(session.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Edit(1, PlanField.Title, "Retained edit");
            await Ui.Until(() => Ui.Find<InfoBar>("PlanSheetSaveFailure").IsOpen);
            await Ui.Ready<Button>("PlanSheetRetrySave");
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Retained edit"));
            Assert.That(session.UndoCount, Is.EqualTo(1));
            await Ui.Run(() => {
                var failure = Ui.Find<InfoBar>("PlanSheetSaveFailure");
                Assert.That(failure.Title, Is.EqualTo("保存できませんでした"));
                Assert.That(failure.Severity, Is.EqualTo(InfoBarSeverity.Error));
                Assert.That(failure.IsClosable, Is.False);
                Assert.That(failure.ActionButton, Is.SameAs(Ui.Find<Button>("PlanSheetRetrySave")));
                Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Collapsed));
            });
        }
        await Ui.Run(() => Ui.Click("PlanSheetRetrySave")); await Ui.Idle();
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanSheetSaveFailure").IsOpen);
        var loaded = await store.LoadAsync(session.Document.Project);
        Assert.That(loaded.Checkpoint!.Document.State.Rows[0].Title, Is.EqualTo("Retained edit"));
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }
    [TestCase(true), TestCase(false)]
    public async Task ATaskWithOnlyOneDateShowsTheKnownEndpoint(bool start)
    {
        await session.Execute(new InsertPlanRows([PlanRow.New("Partial", "acme/repo") with {
            Start = start ? Today : null, End = start ? null : Today }], "I1"), Today);
        await Ui.Unmount(sheetHost); await Ui.Run(() => sheetHost.Children.Clear());
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => {
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Polygon>("PlanEndpoint1").Width, Is.EqualTo(8));
            Assert.That(Ui.Tree(sheet).OfType<Rectangle>().Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanBar1"), Is.False);
        });
    }
    [Test]
    public async Task SelectingAnOffscreenColumnMakesTheWholeCellReachableWithoutMovingTheChart()
    {
        await SetColumnVisible(PlanField.Status,true);
        await Ui.Idle();
        // The wider sheet can still move the chart offset after normal-priority work; measure after it settles.
        await Ui.LowPriorityIdle();
        double barX = 0;
        await Ui.Run(() => barX = Ui.Find<Rectangle>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X - sheet.SheetViewport);
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            var provider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue);
            provider.SetValue(560);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth > 0);
        // Finish the divider's native date-viewport compensation before measuring column selection.
        await Ui.Until(() => Math.Abs(Ui.Find<Rectangle>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X - sheet.SheetViewport - barX) < 1,
            () => $"bar={Ui.Find<Rectangle>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X - sheet.SheetViewport}, expected={barX}, "
                + $"sheetViewport={sheet.SheetViewport}, chartOffset={sheet.ChartOffset}, chartScrollable={Ui.Find<ScrollViewer>("PlanGanttHorizontal").ScrollableWidth}, "
                + $"sheetScrollable={Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth}, width={sheet.ActualWidth}, {sheet.WorkDescription}");
        await Ui.Idle();
        double chartOffset = 0;
        await Ui.Run(() => chartOffset = sheet.ChartOffset);
        await Select(1, PlanField.Status);
        await Ui.Until(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Status");
            var left = cell.TransformToVisual(sheet).TransformPoint(new()).X;
            var viewport = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            return viewport.HorizontalOffset > 0 && left >= 0 && left + cell.ActualWidth <= viewport.ActualWidth + 1;
        });
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Status");
            var left = cell.TransformToVisual(sheet).TransformPoint(new()).X;
            var viewport = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            Assert.That(viewport.HorizontalOffset, Is.GreaterThan(0));
            Assert.That(left, Is.GreaterThanOrEqualTo(0));
            Assert.That(left + cell.ActualWidth, Is.LessThanOrEqualTo(viewport.ActualWidth + 1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset, Is.EqualTo(chartOffset));
        });
    }
    [Test]
    public async Task HorizontalGanttScrollDoesNotMoveSheetCellsOrItsHeader()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await Ui.Ready<TextBox>("PlanCell1_Title");
        double cellX = 0, headerX = 0, barX = 0, arrowX = 0, offset = 0;
        await Ui.Run(() => {
            barX = Ui.Find<Rectangle>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X;
            arrowX = Ui.Find<Microsoft.UI.Xaml.Shapes.Polygon>("PlanArrowHead1_2_2").TransformToVisual(sheet).TransformPoint(new()).X;
            offset = sheet.ChartOffset;
            cellX = Ui.Find<TextBox>("PlanCell1_Title").TransformToVisual(sheet).TransformPoint(new()).X;
            headerX = Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).X;
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Polyline>("PlanArrow1_2_2").Points.Count, Is.EqualTo(3));
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Line>("PlanStatusLine").X1, Is.EqualTo(sheet.X(Today)));
            Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(120, null, null, true);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset == 120);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ScrollViewer>("PlanSheetHorizontal").HorizontalOffset, Is.Zero);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(cellX));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(headerX));
            Assert.That(Ui.Find<Rectangle>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(barX + offset - 120).Within(1));
            Assert.That(Ui.Find<Microsoft.UI.Xaml.Shapes.Polygon>("PlanArrowHead1_2_2").TransformToVisual(sheet).TransformPoint(new()).X, Is.EqualTo(arrowX + offset - 120).Within(1));
        });
    }
    [Test, Category("PlanSheetPerformance")]
    public async Task ThousandTasksCommitToRenderedDatesAndBarsRecordsTwentySamples()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await Ui.Ready<TextBox>("PlanCell1_Remaining");
        for (var i = 0; i < 20; i++)
        {
            await Edit(1, PlanField.Remaining, i % 2 == 0 ? "16" : "8");
            await Ui.Until(() => File.Exists(metricsPath) && File.ReadAllLines(metricsPath!).Count(line => line.Contains("\"boundary\":\"rendered\"")) == i + 1);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBox>("PlanCell1_End").Text, Is.EqualTo(i % 2 == 0 ? "10/6 (火)" : "10/5 (月)"));
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
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "sheet-1000"));
    }
    [Test]
    public async Task PendingTextStaysLocalUntilFocusCommitThenDatesBarsAndUndoChangeTogether()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await Ui.Ready<TextBox>("PlanCell1_Remaining");
        var undo = session.UndoCount;
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(cell.Focus(FocusState.Programmatic), Is.True); cell.Text = "16";
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        });
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic), Is.True));
        await Ui.Idle();
        await Ui.Until(() => session.Document.State.Rows[0].Remaining == 16 || Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
            Assert.That(Ui.Find<TextBox>("PlanCell1_End").Text, Is.EqualTo("10/6 (火)"));
            Assert.That(Ui.Find<Rectangle>("PlanBar1").Width, Is.EqualTo(48));
            Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
        });
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(8));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Y)); await Ui.Idle();
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
    }
    [TestCase("paste"), TestCase("fill"), TestCase("clear")]
    public async Task NativeRangeCommandsAreOneUndoAndKeepSelection(string command)
    {
        {
            await Edit(1, PlanField.Remaining, "16");
            await Select(1, PlanField.Remaining);
            await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
            await Select(2, PlanField.Remaining);
            await Select(3, PlanField.Remaining, true);
            if (command == "fill") { await Select(1, PlanField.Remaining); await Select(3, PlanField.Remaining, true); }
            var before = session.UndoCount;
            await Ui.ClickCommand(command == "paste" ? "PlanSheetPaste" : command == "fill" ? "PlanSheetFillDown" : "PlanSheetClear");
            await Ui.Idle();
            Assert.That(session.Document.State.Rows.Skip(1).Take(2).Select(r => r.Remaining), Is.All.EqualTo(command == "clear" ? null : (decimal?)16));
            Assert.That(session.UndoCount, Is.EqualTo(before + 1));
            await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
            Assert.That(session.Document.State.Rows.Skip(1).Take(2).Select(r => r.Remaining), Is.All.EqualTo(8));
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(16));
        }
    }
    [Test]
    public async Task InsertAndIndentRollUpTheParentAndOutdentRestoresLeafEditing()
    {
        await SelectFocusedCell(3, PlanField.Title);
        await Ui.ClickCommand("PlanSheetInsert"); await Ui.Idle();
        var inserted = await session.FlushAsync();
        Assert.That(inserted.Succeeded, Is.True, inserted.Error);
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(101));
        Assert.That(session.Document.State.Rows[2].Repository, Is.EqualTo("acme/repo"));
        await Ui.Ready<TextBox>("PlanCell3_Title");
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell3_Title").Text == ""
            && Ui.Find<TextBox>("PlanCell3_Title").FocusState != FocusState.Unfocused);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        await Ui.Until(() => Ui.Tree(sheet).OfType<TextBox>().Any(cell =>
            AutomationProperties.GetAutomationId(cell) == "PlanCell3_Title" && cell.IsLoaded && cell.Text == "Task 3"));
        await SelectFocusedCell(3, PlanField.Title);
        await Ui.ClickCommand("PlanSheetIndent"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Parent, Is.EqualTo("I2"));
        await Ui.Ready<PlanSummaryCell>("PlanCell2_Remaining");
        await Ui.Run(() => {
            Assert.That(Ui.Find<FrameworkElement>("PlanCell2_Remaining"), Is.Not.InstanceOf<TextBox>());
        });
        await Ui.ClickCommand("PlanSheetOutdent");
        await Ui.Until(() => session.Document.State.Rows[2].Parent is null);
        Assert.That(session.Document.State.Rows[2].Parent, Is.Null);
        await Ui.Ready<TextBox>("PlanCell2_Remaining");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell2_Remaining").IsReadOnly, Is.False));
        await Ui.Idle();
        await SelectFocusedCell(2, PlanField.Remaining);
        await Edit(2, PlanField.Remaining, "16");
        Assert.That(session.Document.State.Rows[1].Remaining, Is.EqualTo(16));
    }
    [Test]
    public async Task RefusedIndentNamesTheBlockingLinkAndClearsWithTheNextSelection()
    {
        // Row 2 depends on row 1, so indenting it under row 1 is refused (#241).
        await Select(2, PlanField.Title);
        var before = session.Document; var history = (session.UndoCount, session.RedoCount);
        await Ui.ClickCommand("PlanSheetIndent"); await Ui.Idle();
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text,
                Is.EqualTo("ID 2 は ID 1 の後に始まるため、ID 2 を ID 1 の下にインデントできません。先にリンクを解除してください。"));
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Visible));
        });
        Assert.That(session.Document, Is.SameAs(before));
        Assert.That((session.UndoCount, session.RedoCount), Is.EqualTo(history));
        await Select(3, PlanField.Title);
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length == 0);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Collapsed)));
        // The arrow keys move the selection through MoveSelection, which must dismiss the refusal too.
        await Select(2, PlanField.Title);
        await Ui.ClickCommand("PlanSheetIndent"); await Ui.Idle();
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Run(() => sheet.MoveSelection(Windows.System.VirtualKey.Down, false));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length == 0);
    }
    [TestCase(false), TestCase(true)]
    public async Task AutomationSelectionMovesKeyboardFocusSoAnotherCellCannotReclaimTheSelection(bool extend)
    {
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Keyboard), Is.True));
        if (extend) await Select(2, PlanField.Title);
        await Select(3, PlanField.Title, extend);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(FocusManager.GetFocusedElement(Ui.Root.XamlRoot), Is.SameAs(Ui.Find<TextBox>("PlanCell3_Title")));
            Assert.That(SelectProvider("PlanCell1_Title").IsSelected, Is.False);
            Assert.That(SelectProvider("PlanCell2_Title").IsSelected, Is.EqualTo(extend));
            Assert.That(SelectProvider("PlanCell3_Title").IsSelected, Is.True);
        });
    }
    [Test]
    public async Task FilterKeepsPlanIdsAndColumnToggleRestoresTheMappedHeader()
    {
        await Ui.Run(() => {
            var filter = Ui.Find<TextBox>("PlanSheetFilter");
            var icon = Ui.Find<FontIcon>("PlanSheetFilterSearch");
            Assert.That(icon.Glyph, Is.EqualTo("\uE721"));
            Assert.That(icon.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(icon.IsHitTestVisible, Is.False);
            Assert.That(icon.IsTabStop, Is.False);
            var bounds = icon.TransformToVisual(filter).TransformBounds(new Windows.Foundation.Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
            Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0));
            Assert.That(bounds.Right, Is.LessThanOrEqualTo(filter.ActualWidth));
            Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
            Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(filter.ActualHeight));
            filter.Text = "Task 3";
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 12
            && (string)Ui.Find<ListView>("PlanTasks").Items[0] == "I3");
        await Ui.Ready<TextBlock>("PlanRowId3");
        await Ui.Ready<TextBox>("PlanCell3_Title");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanRowId3").Text, Is.EqualTo("3"));
        });
        await SetColumnVisible(PlanField.Start,false);
        await Ui.Run(() => Assert.That(Ui.Tree(sheet).OfType<TextBox>().Any(c =>
            AutomationProperties.GetAutomationId(c) == "PlanCell3_Start" && c.Visibility == Visibility.Visible), Is.False));
        await SetColumnVisible(PlanField.Start,true);
        await Ui.Ready<TextBlock>("PlanHeaderStart");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderStart").Text, Is.EqualTo("開始日")));
    }
    [Test]
    public async Task StatusDateAndSelectionShowTheSchedulerReasonAndCycleRejectionNamesIds()
    {
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        await Ui.Idle(); await Select(1, PlanField.Title);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain("基準日")));
        Assert.That(session.Document.State.Settings.StatusDate, Is.EqualTo(new DateOnly(2026, 10, 20)));
        await Edit(1, PlanField.Predecessors, "2");
        await Ui.Run(() => Assert.That(((TextBlock)Ui.Popup<Border>("SheetInputProblem")!.Child).Text,
            Does.Contain("#1").And.Contain("#2")));
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
        double headerY = 0, calendarTop = 0;
        await Ui.Run(() => {
            if (zoom != "月") calendarTop = Ui.Find<Rectangle>("PlanNonWorking20261003").TransformToVisual(sheet).TransformPoint(new()).Y;
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
        PlanSheetRow? CurrentRow()
        {
            var row = (sheet.List.ContainerFromItem("I75") as ListViewItem)?.ContentTemplateRoot as PlanSheetRow;
            return row is { IsLoaded: true, Identity: "I75" } && row.Owner == sheet ? row : null;
        }
        void WriteRowLayout(string phase)
        {
            var row = CurrentRow();
            var cell = row is null ? null : Ui.Tree(row).OfType<TextBox>().SingleOrDefault(cell => AutomationProperties.GetAutomationId(cell) == "PlanCell75_Title");
            var bar = row is null ? null : Ui.Tree(row).OfType<Rectangle>().SingleOrDefault(bar => AutomationProperties.GetAutomationId(bar) == "PlanBar75");
            TestContext.Out.WriteLine($"Zoom row layout ({zoom}, {phase}): current-row={row is not null}; "
                + $"row-height={row?.ActualHeight}; cell-height={cell?.ActualHeight}; cell-y={cell?.TransformToVisual(sheet).TransformPoint(new()).Y}; "
                + $"bar-height={bar?.ActualHeight}; bar-y={bar?.TransformToVisual(sheet).TransformPoint(new()).Y}");
        }
        await Ui.Run(() => WriteRowLayout("loaded"));
        try {
            // Loaded does not imply that a newly realized native editor has been
            // arranged. Compare the current row only after both parts have layout.
            await Ui.Until(() => CurrentRow() is { } row
                && Ui.Tree(row).OfType<TextBox>().Any(cell => AutomationProperties.GetAutomationId(cell) == "PlanCell75_Title" && cell.ActualHeight > 0)
                && Ui.Tree(row).OfType<Rectangle>().Any(bar => AutomationProperties.GetAutomationId(bar) == "PlanBar75" && bar.ActualHeight > 0));
        } catch (TimeoutException) {
            await Ui.Run(() => WriteRowLayout("layout timeout"));
            throw;
        }
        await Ui.Run(() => {
            WriteRowLayout("arranged");
            var cell = Ui.Find<TextBox>("PlanCell75_Title", CurrentRow()!);
            var bar = Ui.Find<Rectangle>("PlanBar75", CurrentRow()!);
            var y = cell.TransformToVisual(sheet).TransformPoint(new()).Y + cell.ActualHeight / 2;
            var barY = bar.TransformToVisual(sheet).TransformPoint(new()).Y + bar.ActualHeight / 2;
            Assert.That(barY, Is.EqualTo(y).Within(1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").ScrollableWidth, Is.GreaterThan(0));
            Assert.That(Ui.Find<TextBlock>("PlanHeaderTitle").TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(headerY));
            if (zoom != "月") {
                var shade = Ui.Find<Rectangle>("PlanNonWorking20261003");
                Assert.That(shade.TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(calendarTop));
                Assert.That(Canvas.GetLeft(shade), Is.EqualTo(sheet.X(new(2026, 10, 3))));
            }
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "sheet-gantt-" + zoom));
    }
}
