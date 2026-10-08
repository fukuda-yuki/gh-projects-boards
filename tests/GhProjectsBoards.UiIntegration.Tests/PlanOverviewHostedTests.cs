using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Path = System.IO.Path;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanOverview")]
internal sealed class PlanOverviewHostedTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private string root = null!;
    private PlanSession session = null!;
    private PlanSheetView sheet = null!;
    private Grid sheetHost = null!;
    private PlanClipboardContent clipboard = new("", null);

    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-overview-" + Guid.NewGuid().ToString("N"));
        var rows = ImmutableArray.Create(
            new PlanRow("I1", "R01 受注", "acme/repo"),
            TaskRow("I2", "R01 OT-001 運用", "I1", Today) with { Predecessors = ["outside", "I5"] },
            TaskRow("I3", "承認", "I1", Today),
            new PlanRow("I4", "R02 出荷", "acme/repo"),
            TaskRow("I5", "R02 OT-001 運用", "I4", new(2027, 4, 12)),
            TaskRow("I6", "承認", "I4", new(2027, 4, 12)));
        var document = new PlanDocument(new(new("github.com", 1), "P1"), new(rows, []), new(rows, new() {
            StatusDate = Today, DefaultRepository = "acme/repo",
            People = [new("U1", "alice", 100, null, []), new("U2", "bob", 100, null, [])]
        })) { Sync = new() { IssueLinks = rows.Select((row, index) => new KeyValuePair<string, PlanIssueLink>(
            row.Identity, new($"acme/repo#{101 + index}", $"https://github.com/acme/repo/issues/{101 + index}"))).ToImmutableDictionary() } };
        session = await PlanSession.CreateAsync(new(root), document, Today);
        clipboard = new("", null);
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Run(() => {
            var host = sheetHost = new Grid(); host.RowDefinitions.Add(new() { Height = GridLength.Auto }); host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
        await Ui.Ready<TextBox>("PlanCell2_Title");
    }

    private static PlanRow TaskRow(string id, string title, string parent, DateOnly start) => new(id, title, "acme/repo") {
        Parent = parent, Estimate = 8, Remaining = 8, Actual = 0, Assignees = ["U1"], StartNoEarlierThan = start
    };

    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            try { await Ui.Idle(); }
            finally
            {
                try { if (sheet is not null) await Ui.Unmount(sheetHost, check: false); await Ui.Idle(); }
                finally
                {
                    if (session is not null) await session.FlushAsync();
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
        }
        finally { Ui.EndTest(); }
    }

    [TestCase(false), TestCase(true), Category("GanttPhase3")]
    public async Task InitialWeekViewRevealsStatusDateWithoutEditingThePlan(bool earlierTask)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var rows = session.Document.State.Rows.Select(r => earlierTask && r.Identity == "I3" ? r with {
            Fixed = true, Start = Today.AddMonths(-3), End = Today.AddMonths(-3) } : r).ToImmutableArray();
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "initial")),
            new(session.Document.Project, new(rows, []), new(rows, session.Document.State.Settings)), Today);
        await Ui.Run(() => { sheetHost.Children.Clear(); sheet = new(session); sheetHost.Children.Add(sheet); });
        await Ui.Mount(sheetHost); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem, Is.EqualTo("週"));
            Assert.That(sheet.DayWidth, Is.EqualTo(8));
            if (earlierTask) Assert.That(sheet.ChartOffset, Is.GreaterThan(0));
            Assert.That(sheet.X(Today), Is.EqualTo(sheet.ChartViewport / 4).Within(1));
            Assert.That(Ui.Find<Line>("PlanStatusLine").X1, Is.EqualTo(sheet.X(Today)).Within(.1));
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    [TestCase("日"), TestCase("週"), TestCase("月"), TestCase("全期間"), Category("GanttPhase3")]
    public async Task TwoTierTimescaleRetainsContextAcrossMonthAndYearWithoutOverlap(string scale)
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I3", PlanField.Fixed, true),
            new("I3", PlanField.Start, new DateOnly(2026, 9, 1)), new("I3", PlanField.End, new DateOnly(2026, 9, 1))]), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale; });
        await Ui.Idle();
        foreach (var date in new[] { new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 26), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 4) }) {
            double target = 0;
            await Ui.Run(() => {
                var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
                target = Math.Clamp((date.DayNumber - sheet.FirstDay.DayNumber) * sheet.DayWidth, 0, chart.ScrollableWidth);
                chart.ChangeView(target, null, null, true);
            });
            await Ui.Until(() => Math.Abs(sheet.ChartOffset - target) < .1);
            await Ui.Idle();
            await Ui.Run(() => {
                var upper = TimelineLabels("PlanTimelineUpper");
                var lower = TimelineLabels("PlanTimelineLabel");
                Assert.That(upper, Is.Not.Empty);
                Assert.That(lower, Is.Not.Empty);
                var period = DateOnly.FromDayNumber(int.Parse(Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(upper[0])["PlanTimelineUpper".Length..]));
                Assert.That(Canvas.GetLeft(upper[0]), Is.EqualTo(3).Within(.1));
                var first = sheet.FirstDay.AddDays((int)(sheet.ChartOffset / sheet.DayWidth));
                Assert.That(period, Is.EqualTo(new DateOnly(first.Year, scale is "月" or "全期間" ? 1 : first.Month, 1)));
                Assert.That(upper[0].Text, Does.StartWith(period.Year + "年"));
                Assert.That(upper[0].Text.Contains("月"), Is.EqualTo(scale is "日" or "週"));
                foreach (var label in lower) {
                    Assert.That(Canvas.GetTop(label), Is.InRange(24d, 28d));
                    if (scale == "週") {
                        var parts = label.Text.Split('/');
                        Assert.That(parts, Has.Length.EqualTo(2));
                        var year = first.Year + (int.Parse(parts[0]) < first.Month ? 1 : 0);
                        Assert.That(new DateOnly(year, int.Parse(parts[0]), int.Parse(parts[1])).DayOfWeek, Is.EqualTo(DayOfWeek.Monday));
                    } else Assert.That(label.Text, Does.Match(scale == "日" ? @"^\d{1,2}$" : @"^\d{1,2}月$"));
                }
                AssertNoOverlap(upper.Cast<FrameworkElement>().Concat(Ui.Tree(sheet).OfType<Border>().Where(e =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanStatusDateLabel")).ToArray());
                AssertNoOverlap(lower);
            });
        }
    }

    private TextBlock[] TimelineLabels(string prefix) => Ui.Tree(sheet).OfType<TextBlock>().Where(e =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e).StartsWith(prefix, StringComparison.Ordinal))
        .OrderBy(Canvas.GetLeft).ToArray();

    private static void AssertNoOverlap(IEnumerable<FrameworkElement> elements)
    {
        var ordered = elements.OrderBy(Canvas.GetLeft).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            Assert.That(Canvas.GetLeft(ordered[i]), Is.GreaterThanOrEqualTo(Canvas.GetLeft(ordered[i - 1]) + ordered[i - 1].ActualWidth));
    }

    private async Task MountGanttExample(string? variant = null)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var task = new PlanRow("I1", "基本設計書の作成", "acme/repo") {
            Estimate = 32, Actual = 16, Remaining = 16, Start = Today.AddDays(-5), Assignees = ["U1"] };
        var rows = ImmutableArray.Create(task,
            task with { Identity = "I2", Title = "進捗未取得", Actual = null, Fixed = true, End = Today.AddDays(3) },
            task with { Identity = "I3", Title = "完了", Remaining = 0, End = Today.AddDays(3) },
            new PlanRow("I4", "集計", "acme/repo"), task with { Identity = "I5", Parent = "I4" },
            task with { Identity = "I6", Title = "承認", Estimate = 0, Actual = 0, Remaining = 0, Predecessors = ["I1"] });
        if (variant is "large" or "balanced") rows = rows.SetItem(1, rows[1] with {
            Actual = decimal.MaxValue, Remaining = variant == "large" ? 1 : decimal.MaxValue });
        if (variant is "closed-summary" or "complete-summary") rows = rows.SetItem(3, rows[3] with {
            Closed = variant == "closed-summary", Actual = variant == "closed-summary" ? null : 8, Remaining = variant == "closed-summary" ? null : 0 });
        var baseline = rows.Select(r => r with { End = r.Identity is "I1" or "I4" ? new(2026, 10, 2) : r.End }).ToImmutableArray();
        var settings = session.Document.State.Settings with { CompanyDaysOff = [new(2026, 10, 7)],
            ImportedHolidays = new("test", "fixture", new string('a', 64), DateTimeOffset.UtcNow, 2026, 2027, [new(new(2026, 10, 8), "Imported holiday")]) };
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "gantt")),
            new(session.Document.Project, new(baseline, []), new(rows, settings)), Today);
        await Ui.Run(() => {
            sheetHost.Children.Clear(); sheet = new(session); sheetHost.Children.Add(sheet);
            sheet.Width = 1248; sheet.HorizontalAlignment = HorizontalAlignment.Left;
            sheet.Background = PlanSheetView.Brush("WorkspaceCardBrush");
            Ui.Window.AppWindow.Resize(new(1600, 960));
        });
        await Ui.Mount(sheetHost);
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue)).SetValue(500);
        });
        await Ui.Idle();
    }

    [TestCase("large", 100), TestCase("balanced", 50), Category("GanttPhase3")]
    public async Task ExtremeAcceptedEffortRendersProgressWithoutOverflow(string variant, int percent)
    {
        await MountGanttExample(variant);
        await Ui.Run(() => {
            var bar = Ui.Find<Rectangle>("PlanBar2");
            Assert.That(bar.ActualWidth, Is.GreaterThan(0));
            Assert.That(Ui.Find<Rectangle>("PlanProgress2").Width, Is.EqualTo(bar.Width * percent / 100).Within(.001));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(bar), Does.Contain($"完了 {percent}%"));
            Assert.That(session.Document.State.Rows[1].Actual, Is.EqualTo(decimal.MaxValue));
        });
    }

    [TestCase("closed-summary"), TestCase("complete-summary"), Category("GanttPhase3")]
    public async Task SummaryProgressUsesChildrenDespiteItsOwnCompletedInput(string variant)
    {
        await MountGanttExample(variant);
        await Ui.Run(() => Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Ui.Find<Polygon>("PlanBar4")),
            Does.Contain("集計").And.Contain("完了 50%").And.Not.Contain("完了 100%")));
    }

    [TestCase("日"), TestCase("週"), Category("GanttPhase3")]
    public async Task GanttShowsProgressPublishedSlipAndCalendarAtTheirDateCoordinates(string scale)
    {
        await MountGanttExample();
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale); await Ui.Idle();
        double offset = 0;
        await Ui.Run(() => {
            var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            offset = Math.Clamp((new DateOnly(2026, 9, 25).DayNumber - sheet.FirstDay.DayNumber) * sheet.DayWidth, 0, chart.ScrollableWidth);
            chart.ChangeView(offset, null, null, true);
        });
        await Ui.Until(() => Math.Abs(sheet.ChartOffset - offset) < .1);
        await SelectCell(1, PlanField.Title);
        await Ui.Run(() => {
            // Scroll/selection can recreate the header; measure its arranged controls.
            sheet.UpdateLayout();
            var row = sheet.Realized.Single(r => r.Identity == "I1");
            Assert.That(row.Background, Is.SameAs(PlanSheetView.Brush("SheetSelectionBrush")));
            var route = Ui.Find<Polyline>("PlanArrow1_6_6");
            Assert.That(route.Stroke, Is.SameAs(PlanSheetView.Brush("GanttArrowBrush")));
            var head = Ui.Find<Polygon>("PlanArrowHead1_6_6");
            Assert.That(head.Fill, Is.SameAs(route.Stroke));
            Assert.That(head.Width, Is.EqualTo(6)); Assert.That(head.Height, Is.EqualTo(6));
            Assert.That(head.Points.Count, Is.EqualTo(3));
            Assert.That(Canvas.GetLeft(head) + head.Width, Is.EqualTo(sheet.X(sheet.Schedule["I6"].Start.Value!.Value)));
            Assert.That(Canvas.GetTop(head) + head.Height / 2, Is.EqualTo(sheet.RowHeight / 2));
            Assert.That(route.Points.Last(), Is.EqualTo(new Windows.Foundation.Point(Canvas.GetLeft(head) + head.Width, sheet.RowHeight / 2)));
            var bar = Ui.Find<Rectangle>("PlanBar1");
            Assert.That(bar.Height, Is.EqualTo(14));
            Assert.That(Ui.Find<Rectangle>("PlanProgress1").Width, Is.EqualTo(bar.Width / 2).Within(.1));
            Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanProgress2"), Is.False);
            Assert.That(Ui.Find<Rectangle>("PlanProgress3").Width, Is.EqualTo(Ui.Find<Rectangle>("PlanBar3").Width));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(bar), Does.Contain("完了 50%").And.Contain("発行済みより 2 日遅れ"));
            foreach (var id in new[] { 1, 4 }) {
                var late = Ui.Find<Shape>("PlanLate" + id);
                // Slip starts after the inclusive published end, including the intervening weekend.
                Assert.That(Canvas.GetLeft(late), Is.EqualTo(sheet.X(new(2026, 10, 3))).Within(.1));
                Assert.That(late.StrokeDashArray, Is.EqualTo(new double[] { 3, 2 }));
                Assert.That(Ui.Find<TextBlock>("PlanLateLabel" + id).Text, Is.EqualTo("+2日"));
            }
            Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanLate2"), Is.False);
            Assert.That(Ui.Find<Polygon>("PlanBar4").Fill, Is.SameAs(PlanSheetView.Brush("GanttSummaryBrush")));
            Assert.That(Ui.Find<Polygon>("PlanBar6").Fill, Is.SameAs(PlanSheetView.Brush("GanttTaskBrush")));
            var status = Ui.Find<Line>("PlanStatusLine");
            Assert.That(status.X1, Is.EqualTo(sheet.X(Today)).Within(.1));
            Assert.That(status.StrokeThickness, Is.EqualTo(2));
            var pill = Ui.Find<Border>("PlanStatusDateLabel");
            Assert.That(((TextBlock)pill.Child).Text, Is.EqualTo("状況日 10/5"));
            Assert.That(Canvas.GetLeft(pill), Is.InRange(0d, sheet.ChartViewport - pill.ActualWidth));
            if (scale == "日") Assert.That(Canvas.GetLeft(pill) + pill.ActualWidth / 2, Is.EqualTo(sheet.X(Today)).Within(1));
            AssertNoOverlap(TimelineLabels("PlanTimelineUpper").Cast<FrameworkElement>().Append(pill));
            Assert.That(TimelineLabels("PlanTimelineUpper").Select(t => t.Text), Does.Contain("2026年9月").And.Contain("10月"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Ui.Find<Polygon>("PlanBar4")), Does.Contain("集計").And.Contain("発行済みより 2 日遅れ"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Ui.Find<Polygon>("PlanBar6")), Does.Contain("マイルストーン"));
            foreach (var day in new[] { new DateOnly(2026, 10, 3), new(2026, 10, 7), new(2026, 10, 8), new(2026, 10, 12) }) {
                var shade = Ui.Find<Rectangle>("PlanNonWorking" + day.ToString("yyyyMMdd"));
                Assert.That(Canvas.GetLeft(shade), Is.EqualTo(sheet.X(day)).Within(.1));
                Assert.That(shade.Width, Is.EqualTo(sheet.DayWidth));
                Assert.That(shade.ActualHeight, Is.GreaterThan(6 * sheet.RowHeight));
            }
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "issue109-gantt-" + scale));
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "月"); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e).StartsWith("PlanNonWorking", StringComparison.Ordinal)), Is.False));
    }

    [Test]
    public async Task FoldedRequirementsKeepPhaseBulkEditingAndOneStepUndo()
    {
        await SelectCell(2, PlanField.Title);
        await Ui.ClickCommand("PlanSheetCollapseAll", focus: true);
        await Ui.Idle();
        await Ui.Until(() => RowIsFocused(1));
        await Ui.Run(() => {
            Assert.That(VisibleRows(), Is.EqualTo(new[] { "I1", "I4", "" }));
            Assert.That(RowIsFocused(1), Is.True);
        });
        await Filter("OT-001", ["I2", "I5", ""]);
        await Ui.Ready<TextBlock>("PlanRowId2");
        await Ui.Ready<TextBlock>("PlanRowId5");
        await Ui.Run(() => {
            Assert.That(VisibleRows(), Is.EqualTo(new[] { "I2", "I5", "" }), "Ancestor context must not become a bulk-edit target.");
            Assert.That(Ui.Find<TextBlock>("PlanRowId2").Text, Does.StartWith("2"));
            Assert.That(Ui.Find<TextBlock>("PlanRowId5").Text, Does.StartWith("5"));
        });
        await SelectCell(2, PlanField.Assignees);
        await SelectCell(5, PlanField.Assignees, extend: true);
        clipboard = new("bob", null);
        await Ui.ClickCommand("PlanSheetPaste");
        await Ui.Idle();
        Assert.That(Row("I2").Assignees, Is.EqualTo(new[] { "U2" }));
        Assert.That(Row("I5").Assignees, Is.EqualTo(new[] { "U2" }));
        Assert.That(Row("I3").Assignees, Is.EqualTo(new[] { "U1" }));
        Assert.That(Row("I6").Assignees, Is.EqualTo(new[] { "U1" }));
        Assert.That(session.UndoCount, Is.EqualTo(1));

        await Filter("", ["I1", "I4", ""]);
        await Ui.Ready<TextBox>("PlanCell4_Assignees");
        await Ui.Run(() => {
            Assert.That(VisibleRows(), Is.EqualTo(new[] { "I1", "I4", "" }));
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBox>("PlanCell4_Assignees"));
            Assert.That(((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).IsSelected, Is.True);
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").FocusState, Is.Not.EqualTo(FocusState.Unfocused),
                "Changing the filter must keep typing focus in the filter.");
        });
        await Ui.ClickCommand("PlanSheetExpandAll");
        await Ui.Idle();
        await Ui.Run(() => Assert.That(VisibleRows(), Is.EqualTo(new[] { "I1", "I2", "I3", "I4", "I5", "I6", "" })));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Idle();
        Assert.That(Row("I2").Assignees, Is.EqualTo(new[] { "U1" }));
        Assert.That(Row("I5").Assignees, Is.EqualTo(new[] { "U1" }));
        Assert.That(session.UndoCount, Is.Zero);
        Assert.That(session.Document.State.Rows.Select(row => row.Parent), Is.EqualTo(new string?[] { null, "I1", "I1", null, "I4", "I4" }));
        await Ui.Run(() => Ui.Click("PlanFold1"));
        await Ui.Until(() => VisibleRows().SequenceEqual(new[] { "I1", "I4", "I5", "I6", "" }));
        await Ui.Ready<Button>("PlanFold1");
        await Ui.Run(() => Ui.Click("PlanFold1"));
        await Ui.Until(() => VisibleRows().Count() == 7);
        await SelectCell(5, PlanField.Title);
        await Ui.ClickCommand("PlanSheetCollapseAll", focus: true);
        await Ui.Until(() => RowIsFocused(4));
        Assert.That(session.UndoCount, Is.Zero, "Folding changes the view without adding plan edits.");
    }

    [Test]
    public async Task InvalidChildInputRefusesFoldingAndKeepsTheEditorReachable()
    {
        await SelectCell(2, PlanField.Remaining);
        await Ui.Run(() => {
            var editor = Ui.Find<TextBox>("PlanCell2_Remaining");
            Assert.That(editor.Focus(FocusState.Programmatic), Is.True);
            editor.Text = "invalid";
        });
        await Ui.ClickCommand("PlanSheetCollapseAll", focus: true);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(VisibleRows(), Does.Contain("I2"));
            var editor = Ui.Find<TextBox>("PlanCell2_Remaining");
            Assert.That(editor.Text, Is.EqualTo("invalid"));
            Assert.That(editor.FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Not.Empty);
        });
        Assert.That(Row("I2").Remaining, Is.EqualTo(8));
        Assert.That(session.UndoCount, Is.Zero);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell2_Remaining").Text = "8");
        await Ui.ClickCommand("PlanSheetCollapseAll", focus: true);
        await Ui.Idle();
        await Ui.Run(() => Assert.That(VisibleRows(), Is.EqualTo(new[] { "I1", "I4", "" })));
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task WholePeriodAndSelectedDateRevealLateWorkWithoutMovingTheSheet()
    {
        await Ui.Run(() => {
            var zoom = Ui.Find<ComboBox>("PlanGanttZoom");
            Assert.That(zoom.Items.Contains("全期間"), Is.True, "The whole version needs a fit-to-period view.");
            zoom.SelectedItem = "全期間";
        });
        await Ui.Idle();
        await Ui.Until(() => BarIsVisible("PlanBar3") && BarIsVisible("PlanBar5"));
        await SelectCell(5, PlanField.Title);
        double before = 0;
        await Ui.Run(() => {
            before = Ui.Find<ScrollViewer>("PlanSheetHorizontal").HorizontalOffset;
            Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日";
        });
        await Ui.ClickCommand("PlanSheetGoToDate");
        await Ui.Idle();
        await Ui.Run(() => {
            var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            Ui.Trace($"GoToDate dayWidth={sheet.DayWidth} days={sheet.DayCount} offset={chart.HorizontalOffset} extent={chart.ExtentWidth} viewport={chart.ViewportWidth}");
        });
        await Ui.Until(() => BarIsVisible("PlanBar5"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<ScrollViewer>("PlanSheetHorizontal").HorizontalOffset, Is.EqualTo(before));
            Assert.That(Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem, Is.EqualTo("日"));
            var cell = Ui.Find<TextBox>("PlanCell5_Title");
            var bar = Ui.Find<Shape>("PlanBar5");
            Assert.That(bar.TransformToVisual(sheet).TransformPoint(new()).Y + bar.ActualHeight / 2,
                Is.EqualTo(cell.TransformToVisual(sheet).TransformPoint(new()).Y + cell.ActualHeight / 2).Within(1));
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task PredecessorPickerKeepsCandidatesAndActionsVisibleBelowTheSheetToolbar()
    {
        await Ui.Run(() => sheet.Margin = new(236, 104, 16, 16));
        await Ui.Until(() => sheet.TransformToVisual(Ui.Root).TransformPoint(new()).Y >= 104);
        await SelectCell(2, PlanField.Title);
        await Ui.ClickCommand("PlanSheetPredecessorAdd", focus: true);
        await Ui.Until(() => Ui.Popup<Button>("PlanPredecessorCancel")?.ActualHeight > 0);
        await Ui.Idle();
        await Ui.Run(() => {
            var candidates = Ui.Popup<ListView>("PlanPredecessorCandidates")!;
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(sheet.XamlRoot)
                .Single(p => Ui.Tree(p.Child).Contains(candidates));
            var presenter = Ui.Tree(popup.Child).OfType<FlyoutPresenter>().Single();
            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(Ui.Root)
                .TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            var visible = Bounds(presenter);
            Assert.That(visible.Left, Is.GreaterThanOrEqualTo(0));
            Assert.That(visible.Top, Is.GreaterThanOrEqualTo(0));
            Assert.That(visible.Right, Is.LessThanOrEqualTo(Ui.Root.XamlRoot.Size.Width));
            Assert.That(visible.Bottom, Is.LessThanOrEqualTo(Ui.Root.XamlRoot.Size.Height));
            Assert.That(candidates.ActualHeight, Is.GreaterThanOrEqualTo(180), "The chooser needs a usable candidate viewport.");
            foreach (var control in new FrameworkElement[] { Ui.Popup<TextBox>("PlanPredecessorSearch")!, candidates,
                Ui.Popup<Button>("PlanPredecessorConfirm")!, Ui.Popup<Button>("PlanPredecessorCancel")! })
            {
                var bounds = Bounds(control);
                var name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(control);
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(visible.Left), name);
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(visible.Top), name);
                Assert.That(bounds.Right, Is.LessThanOrEqualTo(visible.Right), name);
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(visible.Bottom), name + " must not be clipped by the flyout.");
            }
            Ui.Click(Ui.Popup<Button>("PlanPredecessorCancel")!);
        });
        await Ui.Idle();
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task PredecessorSearchAddsTheNamedIssuePreservesExternalLinksAndCancelsWithoutAnEdit()
    {
        await Filter("OT-001", ["I2", "I5", ""]);
        await SelectCell(2, PlanField.Predecessors);
        await Ui.ClickCommand("PlanSheetPredecessorAdd");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanPredecessorSearch")?.IsLoaded == true);
        await SearchPredecessors("承認", 2);
        await SearchPredecessors("#106", 1);
        await Ui.Run(() => {
            var list = Ui.Popup<ListView>("PlanPredecessorCandidates")!;
            var text = string.Join(" ", Ui.Tree(list).OfType<TextBlock>().Select(block => block.Text));
            Assert.That(text, Does.Contain("R02").And.Contain("承認").And.Contain("acme/repo#106"));
            list.SelectedItem = list.Items[0];
            Ui.Click(Ui.Popup<Button>("PlanPredecessorConfirm")!);
        });
        await Ui.Idle();
        Assert.That(Row("I2").Predecessors, Is.EquivalentTo(new[] { "outside", "I5", "I6" }));
        Assert.That(Row("I5").Predecessors, Is.Empty);
        Assert.That(session.UndoCount, Is.EqualTo(1));

        await Ui.ClickCommand("PlanSheetPredecessorAdd");
        await Ui.Until(() => Ui.Popup<TextBox>("PlanPredecessorSearch")?.IsLoaded == true);
        await Ui.Run(() => {
            Ui.Popup<TextBox>("PlanPredecessorSearch")!.Text = "R01";
            Ui.Click(Ui.Popup<Button>("PlanPredecessorCancel")!);
        });
        await Ui.Idle();
        Assert.That(Row("I2").Predecessors, Is.EquivalentTo(new[] { "outside", "I5", "I6" }));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Idle();
        Assert.That(Row("I2").Predecessors, Is.EquivalentTo(new[] { "outside", "I5" }));
        Assert.That(session.UndoCount, Is.Zero);
    }

    private PlanRow Row(string identity) => session.Document.State.Rows.Single(row => row.Identity == identity);
    private static string[] VisibleRows() => Ui.Find<ListView>("PlanTasks").Items.Cast<string>().ToArray();
    private bool RowIsFocused(int number) => Ui.Tree(sheet).OfType<TextBox>().Any(cell =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(cell).StartsWith($"PlanCell{number}_", StringComparison.Ordinal)
        && cell.FocusState != FocusState.Unfocused);
    private static async Task Filter(string text, string[] visible)
    {
        await Ui.Run(() => {
            var filter = Ui.Find<TextBox>("PlanSheetFilter");
            Assert.That(filter.Focus(FocusState.Keyboard), Is.True);
            filter.Text = text;
        });
        await Ui.Until(() => VisibleRows().SequenceEqual(visible));
        await Ui.Idle();
    }
    private static async Task SelectCell(int number, PlanField field, bool extend = false)
    {
        var id = $"PlanCell{number}_{field}";
        await Ui.Ready<TextBox>(id);
        await Ui.Run(() => {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<TextBox>(id));
            var provider = (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem);
            if (extend) provider.AddToSelection(); else provider.Select();
        });
    }
    private async Task SearchPredecessors(string query, int count)
    {
        await Ui.Run(() => Ui.Popup<TextBox>("PlanPredecessorSearch")!.Text = query);
        await Ui.Until(() => Ui.Popup<ListView>("PlanPredecessorCandidates")?.Items.Count == count);
        await Ui.Until(() => {
            var list = Ui.Popup<ListView>("PlanPredecessorCandidates")!;
            return list.ContainerFromIndex(0) is ListViewItem { IsLoaded: true } item &&
                Ui.Tree(item).OfType<TextBlock>().Any(block => block.Text == (string)list.Items[0]);
        });
        await Ui.Idle();
    }
    private bool BarIsVisible(string id)
    {
        var bar = Ui.Find<Shape>(id);
        var viewport = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
        var left = bar.TransformToVisual(sheet).TransformPoint(new()).X;
        var viewportLeft = viewport.TransformToVisual(sheet).TransformPoint(new()).X;
        return bar.IsLoaded && bar.ActualWidth > 0 && left >= viewportLeft - 1 &&
            left + bar.ActualWidth <= viewportLeft + viewport.ActualWidth + 1;
    }
}
