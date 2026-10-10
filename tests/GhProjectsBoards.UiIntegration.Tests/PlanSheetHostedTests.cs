using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
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
        session = await PlanSession.CreateAsync(new(root), new(new(new("github.com", 1), "P1"), new(rows, []),
            new(rows, new() { StatusDate = Today, DefaultRepository = "acme/repo",
                People = performance ? Enumerable.Range(1, 20).Select(i => new PlanResource("U" + i, "person-U" + i, 100, null)).ToImmutableArray() : [new("U1", "alice", 100, null)],
                Columns = [new(PlanField.Start, "start", "Start date", "DATE")] })), Today);
        await Ui.Run(() => sheet = new(session, () => clipboardReader is { } read ? read() : Task.FromResult(clipboard), value => { if (clipboardWriter is { } write) write(value); else clipboard = value; }));
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
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
    [TestCase(false), TestCase(true)]
    public async Task SummaryCopiesBaselineFieldsAndExposesEveryAssignee(bool held)
    {
        var parent = new PlanRow("I1", "Requirement", "acme/repo") { Assignees = held ? ["U1", "U2"] : [], Predecessors = held ? ["I3"] : [], StartNoEarlierThan = held ? Today : null, Fixed = held, Status = held ? "Todo" : null };
        var child = new PlanRow("I2", "Task", "acme/repo") { Parent = "I1", Estimate = 8, Assignees = ["U1"] };
        var predecessor = new PlanRow("I3", "Earlier", "acme/repo");
        await MountPresentation([parent with { Assignees = ["local"], Predecessors = [], StartNoEarlierThan = Today.AddDays(7), Fixed = !held, Status = "Local" }, child, predecessor],
            [parent, child, predecessor], [new("U1", "alice", 100, null), new("U2", "bob", 100, null)]);
        await Ui.ClickCommand("PlanSheetColumns");
        foreach (var field in new[] { PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status }) {
            await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumn" + field) is { IsLoaded: true, IsEnabled: true });
            await Ui.Run(() => Ui.Popup<CheckBox>("PlanColumn" + field)!.IsChecked = true);
        }
        await Ui.Run(() => Ui.Find<AppBarButton>("PlanSheetColumns").Flyout.Hide()); await Ui.Idle();
        foreach (var (field, text) in new[] { (PlanField.Assignees, "alice, bob"), (PlanField.Predecessors, "3"),
            (PlanField.StartNoEarlierThan, "2026-10-05"), (PlanField.Fixed, "固定"), (PlanField.Status, "Todo") }) {
            await Select(1, field);
            await Ui.Run(async () => {
                await sheet.KeyboardCommand(Windows.System.VirtualKey.C);
                Assert.That(clipboard.Text, Is.EqualTo(held ? text : ""));
                using var metadata = JsonDocument.Parse(clipboard.Metadata!);
                Assert.That(metadata.RootElement.GetProperty("values")[0][0].GetString(), Is.EqualTo(JsonSerializer.Serialize(PlanOperations.Value(parent, field), PlanJson.Options)));
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
        Assert.That(session.UndoCount, Is.Zero);
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

    [TestCase(Windows.System.VirtualKey.F6, false, false)]
    [TestCase(Windows.System.VirtualKey.F10, false, false)]
    [TestCase(Windows.System.VirtualKey.F, true, false)]
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
        await Ui.ClickCommand("PlanSheetColumns");
        foreach (var field in new[] { PlanField.StartNoEarlierThan, PlanField.Fixed, PlanField.Status }) {
            await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumn" + field) is { IsLoaded: true, IsEnabled: true });
            await Ui.Run(() => Ui.Popup<CheckBox>("PlanColumn" + field)!.IsChecked = true);
        }
        await Ui.Run(() => Ui.Find<AppBarButton>("PlanSheetColumns").Flyout.Hide()); await Ui.Idle();
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
    public async Task SummaryCellsExposeStaticValuesAndSelectionReason(PlanField field, string reason)
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
            Assert.That(Ui.Find<Button>($"PlanFillHandle2_{field}").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(SelectProvider($"PlanCell2_{field}").IsSelected, Is.True);
        });
    }
    [TestCase(PlanField.Estimate), TestCase(PlanField.Assignees)]
    [Category("PlanSheetNative")]
    public async Task SummaryPointerAndKeysNeverOpenEditorAndArrowsTraverseCells(PlanField field)
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

    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
    [Category("PlanSheetFollowup")]
    public async Task PointerRangeEndsAtEstimateAndClearChangesOnlyItsCells(bool hideId, bool hideIndicator)
    {
        if (hideId || hideIndicator) {
            await Ui.ClickCommand("PlanSheetColumns");
            await Ui.Until(() => (!hideId || Ui.Popup<CheckBox>("PlanColumnId") is { IsLoaded: true, IsEnabled: true })
                && (!hideIndicator || Ui.Popup<CheckBox>("PlanColumnIndicator") is { IsLoaded: true, IsEnabled: true }));
            if (hideId) { await Ui.Run(() => Ui.Popup<CheckBox>("PlanColumnId")!.IsChecked = false); await Ui.Idle(); }
            if (hideIndicator) { await Ui.Run(() => Ui.Popup<CheckBox>("PlanColumnIndicator")!.IsChecked = false); await Ui.Idle(); }
            await Ui.Run(() => Ui.Find<AppBarButton>("PlanSheetColumns").Flyout.Hide());
        }
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
    [TestCase("completed", "完了", "\uE73E")]
    [TestCase("late", "期限超過: 完了予定 10/2 を過ぎて未完了（残 8h）", "\uE814")]
    [TestCase("typed", "開始日を指定", "\uE718")]
    [TestCase("fixed", "日程固定", "\uE718")]
    [TestCase("changed", "未発行の変更あり", "\u2022")]
    [TestCase("failed", "発行失敗", "\uEA39")]
    [TestCase("unverified", "未検証", "\uEA39")]
    [TestCase("summary", "配下に期限超過 1", "\uE814")]
    [Category("PlanSheetPhase2")]
    public async Task IndicatorPriorityAndSelectionExplainAllApplicableRowStates(string state, string expected, string glyph)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var row = new PlanRow("I1", "R05 顧客データの外部連携", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        if (state is "completed" or "failed" or "unverified") row = row with { Remaining = 0, Actual = 8, Start = Today, End = Today, StartNoEarlierThan = Today };
        if (state is "typed" or "late") row = row with { StartNoEarlierThan = Today };
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
            if (state is "typed" or "late") Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Is.EqualTo("開始: 開始日指定 10/5 · 終了: 開始から 8h"));
            Assert.That(Ui.Find<HyperlinkButton>("PlanSheetIssue").Content, Is.EqualTo("acme/repo#1"));
            var slip = Ui.Find<TextBlock>("PlanSheetSlip").Text;
            if (state is "late" or "failed" or "unverified") {
                Assert.That(slip, Is.EqualTo(state == "late" ? "完了予定 10/2 を過ぎて未完了 · +1 日" : "発行済み 10/2 から +1 日"));
                var text = Ui.Find<TextBlock>("PlanStartReason");
                var pill = (Border)VisualTreeHelper.GetParent(Ui.Find<TextBlock>("PlanSheetSlip"));
                var gap = pill.TransformToVisual(sheet).TransformPoint(new()).X - text.TransformToVisual(sheet).TransformPoint(new()).X - text.ActualWidth;
                Assert.That(gap, Is.InRange(8d, 12d), "The late pill follows the reason immediately.");
                Assert.That(pill.CornerRadius.TopLeft, Is.GreaterThan(0));
            }
            else if (state == "summary") Assert.That(slip, Is.EqualTo("発行済み 10/1 から +2 日"));
            else Assert.That(slip, Is.Empty);
            if (state is "failed" or "unverified") Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator), Does.Contain("完了").And.Contain("開始日を指定").And.Contain("未発行の変更あり"));
        });
    }
    [TestCase("started", "終了: 状況日 10/5 から残り 8h")]
    [TestCase("unstarted", "終了: 開始から 8h")]
    [TestCase("fixed", "終了: 指定")]
    [TestCase("complete", null)]
    [TestCase("summary", null)]
    [Category("SelectionLine")]
    public async Task SelectionExplainsEndReason(string state, string? expected)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        if (state == "started") row = row with { Actual = 4 };
        if (state == "fixed") row = row with { Fixed = true, Start = Today, End = Today };
        if (state == "complete") row = row with { Actual = 8, Remaining = 0 };
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
    [TestCase("later", "発行済み 10/5 から +1 日")]
    [Category("SelectionLine")]
    public async Task SelectionPillKeepsGregorianDatesUnderNonGregorianCulture(string state, string expected)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") {
            Estimate = 8, Remaining = state == "later" ? 16 : 8, Assignees = ["U1"] };
        var baseline = row with {
            Start = state == "start" ? Today.AddDays(-3) : Today,
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

    [TestCase("finish", "完了予定 10/2 を過ぎて未完了", true, "")]
    [TestCase("start", "開始予定 10/2 を過ぎて未着手", true, "")]
    [TestCase("moved", "完了予定 10/2 を過ぎて未完了 · +1 日", true, "")]
    [TestCase("later", "発行済み 10/5 から +1 日", false, "")]
    [TestCase("predecessor", "発行済み 10/5 から +1 日", false, "先行の遅れによる")]
    [TestCase("constraint", "発行済み 10/5 から +1 日", false, "")]
    [TestCase("long", "発行済み 10/5 から +1 日", false, "")]
    [TestCase("summary-overdue", "発行済み 10/2 から +1 日", true, "配下: 期限超過 1")]
    [TestCase("summary-later", "発行済み 10/5 から +1 日", false, "配下: 予定より遅れ 1")]
    [TestCase("summary-counts", "", false, "配下: 期限超過 1 · 予定より遅れ 1")]
    [TestCase("none", "", false, "")]
    [Category("SelectionLine")]
    public async Task SelectionPresentsLatenessAndKeepsIssueAtRight(string state, string expected, bool tinted, string detail)
    {
        var row = new PlanRow("I1", "Selected task", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        var old = row with { Start = Today, End = Today };
        if (state is "finish" or "moved" or "summary-overdue") old = old with { End = Today.AddDays(-3) };
        if (state == "finish") row = row with { Fixed = true, Start = Today.AddDays(-3), End = Today.AddDays(-3) };
        if (state == "start") old = old with { Start = Today.AddDays(-3), End = Today.AddDays(4) };
        if (state is "later" or "summary-later") row = row with { Remaining = 16 };
        if (state is "constraint" or "long") row = row with { StartNoEarlierThan = Today.AddDays(1) };
        if (state == "long") row = row with { Title = string.Concat(Enumerable.Repeat("長いタスク名と終了理由の表示確認", 8)) };
        var rows = ImmutableArray.Create(row); var baseline = ImmutableArray.Create(old);
        if (state == "predecessor") {
            rows = rows.SetItem(0, row with { Predecessors = ["I2"] }).Add(row with { Identity = "I2" });
            baseline = baseline.Add(row with { Identity = "I2", Start = Today, End = Today });
        }
        if (state.StartsWith("summary-")) {
            rows = rows.Add(row with { Identity = "I2", Parent = "I1" });
            baseline = baseline.Add(old with { Identity = "I2", Parent = "I1" });
        }
        if (state == "summary-counts") {
            rows = rows.Add(row with { Identity = "I3", Parent = "I1", Remaining = 16 });
            baseline = baseline.SetItem(0, old with { End = null })
                .SetItem(1, old with { Identity = "I2", Parent = "I1", End = Today.AddDays(-3) })
                .Add(old with { Identity = "I3", Parent = "I1" });
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

    [TestCase(0, 0, "")]
    [TestCase(1, 0, "期限超過 1")]
    [TestCase(0, 1, "予定より遅れ 1")]
    [TestCase(1, 1, "期限超過 1 · 予定より遅れ 1")]
    [Category("SelectionLine")]
    public async Task EmptySelectionShowsHintAndOnlyNonzeroTotals(int overdue, int later, string expected)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] };
        var rows = ImmutableArray.Create(row, row with { Identity = "I2", Remaining = later == 1 ? 16 : 8 });
        var baseline = ImmutableArray.Create(row with { End = overdue == 1 ? Today.AddDays(-3) : Today }, row with { Identity = "I2", End = Today });
        await MountPresentation(rows, baseline);
        await Ui.Run(() => {
            var hint = Ui.Find<TextBlock>("PlanSheetEmptyHint"); var totals = Ui.Find<TextBlock>("PlanSheetTotals");
            Assert.That(hint.Text, Is.EqualTo("タスクを選ぶと、開始と終了の理由がここに出ます"));
            Assert.That(hint.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(hint), Is.EqualTo(hint.Text));
            Assert.That(totals.Text, Is.EqualTo(expected));
            Assert.That(totals.Visibility, Is.EqualTo(expected.Length == 0 ? Visibility.Collapsed : Visibility.Visible));
        });
        if (overdue == 1 && later == 1) await Ui.Run(() => RenderedEvidence.Capture(sheet, "selection-empty"));
        await Select(1, PlanField.Title);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanSheetEmptyHint").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBlock>("PlanSheetTotals").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    private async Task MountPresentation(ImmutableArray<PlanRow> rows, ImmutableArray<PlanRow> baseline,
        ImmutableArray<PlanResource> people = default, ImmutableDictionary<string, string>? logins = null)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var settings = session.Document.State.Settings;
        if (!people.IsDefault) settings = settings with { People = people };
        var document = new PlanDocument(session.Document.Project, new(baseline, []), new(rows, settings)) {
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

    [TestCase("finish", "期限超過: 完了予定 10/2 を過ぎて未完了（残 8.5h）", "\uE814", true)]
    [TestCase("finish-estimate", "期限超過: 完了予定 10/2 を過ぎて未完了（残 8.5h）", "\uE814", true, Category = "OverdueMissingWork")]
    [TestCase("finish-unknown", "期限超過: 完了予定 10/2 を過ぎて未完了", "\uE814", false, Category = "OverdueMissingWork")]
    [TestCase("start", "期限超過: 開始予定 10/2 を過ぎて未着手", "\uE814", false)]
    [TestCase("later", "予定より遅れ: 発行済み 10/5 から +1 日", "\uE7BA", false)]
    [TestCase("unchanged", "期限超過: 完了予定 10/2 を過ぎて未完了（残 8.5h）", "\uE814", false)]
    [TestCase("summary-overdue", "配下に期限超過 1", "\uE814", false)]
    [TestCase("summary-later", "配下に予定より遅れ 1", "\uE7BA", false)]
    [Category("SheetRowPresentation")]
    public async Task LatenessMarkerAndEndColorFollowOverdueAndOwnEndMovement(string state, string expected, string glyph, bool critical)
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { Estimate = 8.5m, Remaining = 8.5m, Assignees = ["U1"] };
        var baseline = row with { Start = Today, End = Today.AddDays(-3) };
        if (state == "finish-estimate") row = row with { Remaining = null };
        if (state == "finish-unknown") row = row with { Remaining = null, Estimate = null };
        if (state == "start") baseline = baseline with { Start = Today.AddDays(-3), End = Today.AddDays(10) };
        if (state == "later") baseline = baseline with { End = Today };
        if (state == "unchanged") row = row with { Fixed = true, Start = Today.AddDays(-4), End = Today.AddDays(-3) };
        var rows = ImmutableArray.Create(row); var baselines = ImmutableArray.Create(baseline);
        if (state.StartsWith("summary")) {
            rows = rows.Add(row with { Identity = "I2", Parent = "I1" });
            baselines = ImmutableArray.Create(baseline with { End = Today.AddDays(1) },
                baseline with { Identity = "I2", Parent = "I1", End = state == "summary-later" ? Today : Today.AddDays(-3) });
        }
        await MountPresentation(rows, baselines);
        await Ui.Run(async () => {
            var indicator = Ui.Find<FontIcon>("PlanIndicator1");
            var name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(indicator);
            Assert.That(name, Does.Contain(expected).And.Not.Contain("発行済みより"));
            if (state == "finish-unknown") Assert.That(name, Does.Not.Contain("（残"));
            Assert.That(indicator.Glyph, Is.EqualTo(glyph));
            Assert.That(ToolTipService.GetToolTip(indicator), Is.EqualTo(name));
            if (state == "finish") Assert.That(name, Does.Contain("予定より遅れ: 発行済み 10/2 から +2 日"));
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
        await MountPresentation([row with { Estimate = 16, Assignees = ["U2"] }, row with { Identity = "I2", Predecessors = ["I1"] }],
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
            Assert.That(buttons.Select(b => b.Label), Is.SupersetOf(new[] { "行を挿入", "インデント", "アウトデント", "先行タスクを追加…", "すべて折りたたむ", "すべて展開", "選択タスクの日程へ移動", "コピー", "貼り付け", "下へコピー", "クリア", "表示列" }));
            Assert.That(bar.SecondaryCommands.OfType<AppBarButton>().Where(b => b.Label == "コピー").Single().KeyboardAcceleratorTextOverride, Is.EqualTo("Ctrl+C"));
            bar.IsOpen = true;
        });
        await Ui.Ready<AppBarButton>("PlanSheetCopy");
        await Ui.Run(() => {
            var bar = Ui.Find<CommandBar>("PlanSheetCommands");
            foreach (var button in bar.PrimaryCommands.Concat(bar.SecondaryCommands).OfType<AppBarButton>()) {
                Assert.That(button.IsLoaded && button.ActualWidth > 0 && button.ActualHeight > 0, Is.True, button.Label);
                Assert.That(Ui.Tree(button).OfType<TextBlock>().Any(t => t.Text == button.Label && t.ActualWidth > 0), Is.True, button.Label);
            }
        });
        await Ui.ClickCommand("PlanSheetColumns");
        await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumnStart") is not null);
        await Ui.Run(() => {
            Assert.That(Ui.Popup<CheckBox>("PlanColumnStart")!.Content, Is.EqualTo("開始日"));
            Assert.That(Ui.Popup<CheckBox>("PlanColumnIndicator")!.Content, Is.EqualTo("インジケーター"));
        });
    }
    [Test, Category("PlanSheetPhase2")]
    public async Task StandardHeadersAndShortDatesExposeMappedFieldsAndFullDates()
    {
        await Ui.Run(() => {
            var start = Ui.Find<TextBlock>("PlanHeaderStart");
            Assert.That(start.Text, Is.EqualTo("開始日"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(start), Is.EqualTo("GitHub: Start date"));
            Assert.That(ToolTipService.GetToolTip(start), Is.EqualTo("GitHub: Start date"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<TextBlock>("PlanHeaderRemaining")), Is.EqualTo("未設定"));
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
        PlanSheetRow originalRow = null!;
        await Ui.Run(() => {
            originalRow = Ui.Tree(sheet).OfType<PlanSheetRow>().Single(r => r.Identity == "I1");
            var list = Ui.Find<ListView>("PlanTasks"); list.ScrollIntoView(list.Items[90]);
        });
        await Ui.Ready<TextBox>("PlanCell91_Title"); await Ui.Idle();
        await Ui.Run(() => {
            // Native recycling may keep a popup anchor cached offscreen. Exercise the
            // actual DataContextChanged event as well, without calling its handler.
            originalRow.DataContext = "I91";
        });
        await Ui.Run(() => {
            Assert.That(Ui.Popup<Border>("SheetInputProblem"), Is.Null,
                "An offscreen problem must not label the task now occupying its recycled editor.");
            originalRow.ClearValue(FrameworkElement.DataContextProperty);
        });
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Remaining").FocusState != FocusState.Unfocused);
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
    public async Task SheetRefusedColumnToggleRestoresAcceptedVisibility()
    {
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.ClickCommand("PlanSheetColumns");
        await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumnRemaining") is { IsLoaded: true, IsEnabled: true });
        CheckBox toggle = null!;
        await Ui.Run(() => { toggle = Ui.Popup<CheckBox>("PlanColumnRemaining")!; Ui.Toggle(toggle); });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(toggle.IsChecked, Is.True);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Visibility, Is.EqualTo(Visibility.Visible));
            Ui.Find<AppBarButton>("PlanSheetColumns").Flyout.Hide();
        });
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Remaining").FocusState != FocusState.Unfocused);
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
    public async Task ReturningToTheDisplayedCalculatedDateKeepsAutomaticSchedulingAndHistory(PlanField field)
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
    private async Task Select(int row, PlanField field, bool extend = false)
    {
        await Ui.Ready<FrameworkElement>($"PlanCell{row}_{field}");
        await Ui.Run(() => { var provider = SelectProvider($"PlanCell{row}_{field}"); if (extend) provider.AddToSelection(); else provider.Select(); });
    }
    private async Task Edit(int row, PlanField field, string text)
    {
        await Select(row, field);
        var before = session.UndoCount;
        await Ui.Run(() => { var cell = Ui.Find<TextBox>($"PlanCell{row}_{field}"); Assert.That(cell.Focus(FocusState.Programmatic), Is.True); cell.Text = text; });
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic), Is.True));
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
        await Ui.Run(() => { sheet.Width = 1248; Ui.Window.AppWindow.Resize(new(1600, 960)); sheet.HorizontalAlignment = HorizontalAlignment.Left; });
        await Ui.Ready<TextBox>("PlanCell1_Predecessors");
        await Ui.Until(() => sheet.ActualWidth == 1248);
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
        await Ui.ClickCommand("PlanSheetCopy"); await Ui.Idle();
        Assert.That(clipboard.Text, Is.EqualTo("New task"), "The created task stays selected without the empty placeholder.");
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
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
        await Ui.ClickCommand("PlanSheetColumns");
        await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumnStatus") is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => { Ui.Popup<CheckBox>("PlanColumnStatus")!.IsChecked = true; Ui.Find<AppBarButton>("PlanSheetColumns").Flyout.Hide(); });
        await Ui.Idle();
        double barX = 0;
        await Ui.Run(() => barX = Canvas.GetLeft(Ui.Find<Rectangle>("PlanBar1")));
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            var provider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue);
            provider.SetValue(560);
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSheetHorizontal").ScrollableWidth > 0);
        // Finish the divider's native date-viewport compensation before measuring column selection.
        await Ui.Until(() => Math.Abs(Canvas.GetLeft(Ui.Find<Rectangle>("PlanBar1")) - barX) < 1);
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
        double cellX = 0, headerX = 0;
        await Ui.Run(() => {
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
        await Select(3, PlanField.Title);
        await Ui.ClickCommand("PlanSheetInsert"); await Ui.Idle();
        Assert.That(session.Document.State.Rows.Length, Is.EqualTo(101));
        Assert.That(session.Document.State.Rows[2].Repository, Is.EqualTo("acme/repo"));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z)); await Ui.Idle();
        await Select(3, PlanField.Title);
        await Ui.ClickCommand("PlanSheetIndent"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Parent, Is.EqualTo("I2"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<FrameworkElement>("PlanCell2_Remaining"), Is.Not.InstanceOf<TextBox>());
        });
        await Ui.ClickCommand("PlanSheetOutdent");
        await Ui.Idle();
        Assert.That(session.Document.State.Rows[2].Parent, Is.Null);
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanCell2_Remaining").IsReadOnly, Is.False));
        await Edit(2, PlanField.Remaining, "16");
        Assert.That(session.Document.State.Rows[1].Remaining, Is.EqualTo(16));
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
        await Ui.ClickCommand("PlanSheetColumns");
        await Ui.Until(() => Ui.Popup<CheckBox>("PlanColumnStart") is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() => Ui.Toggle(Ui.Popup<CheckBox>("PlanColumnStart")!)); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(sheet).OfType<TextBox>().Any(c =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanCell3_Start" && c.Visibility == Visibility.Visible), Is.False);
            Ui.Toggle(Ui.Popup<CheckBox>("PlanColumnStart")!);
        });
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PlanHeaderStart");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanHeaderStart").Text, Is.EqualTo("開始日")));
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
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell75_Title");
            var bar = Ui.Find<Rectangle>("PlanBar75");
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
