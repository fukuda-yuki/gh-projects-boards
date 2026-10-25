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
            People = [new("U1", "alice", 100, null), new("U2", "bob", 100, null)]
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

    // #236: a stored date is a plan value and a read does not invent one, so the task rows the geometry
    // measures state the plan's own dates. One day of work from the stated day is the plan this fixture had
    // laid out before a read stopped laying dates out.
    private static PlanRow TaskRow(string id, string title, string parent, DateOnly start) => new(id, title, "acme/repo") {
        Parent = parent, Estimate = 8, Remaining = 8, Actual = 0, Assignees = ["U1"],
        Start = start, End = start
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
            Start = Today.AddMonths(-3), End = Today.AddMonths(-3) } : r).ToImmutableArray();
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "initial")),
            new(session.Document.Project, new(rows, []), new(rows, session.Document.State.Settings)), Today);
        await Ui.Run(() => { sheetHost.Children.Clear(); sheet = new(session); sheetHost.Children.Add(sheet); });
        await Ui.Mount(sheetHost); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem, Is.EqualTo("週"));
            Assert.That(sheet.DayWidth, Is.EqualTo(8));
            if (earlierTask) Assert.That(sheet.ChartOffset, Is.GreaterThan(0));
            // The range starts a short margin before the earliest date, so a 基準日 on the plan's first day sits
            // at that margin rather than a fifth in; with earlier work there is room for the fifth (#265). The
            // sheet opens on the empty new-task row, which carries no dates, so it does not move the opening place.
            Assert.That(sheet.X(Today), Is.EqualTo(earlierTask ? sheet.ChartViewport / 5 : PlanSheetView.TimelineMarginDays * sheet.DayWidth).Within(1));
            Assert.That(Ui.Find<Line>("PlanStatusLine").X1, Is.EqualTo(sheet.X(Today)).Within(.1));
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    // #265: a sheet opens on the empty new-task row, which is the only selection it makes by itself. That row
    // has no dates, so opening, a scale change and 基準日へ移動 all keep the 基準日 a fifth from the Gantt's
    // left edge even though the earliest tasks lie months before it, and none of them edits the plan.
    [Test, Category("GanttPhase3")]
    public async Task OpeningScaleChangeAndGoToStatusDateKeepTheStatusDateAFifthInWithNoTaskSelected()
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var early = Today.AddDays(-90);
        var rows = session.Document.State.Rows.Select(r => r.Parent == "I1" ? r with { Start = early, End = early } : r).ToImmutableArray();
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "automatic")),
            new(session.Document.Project, new(rows, []), new(rows, session.Document.State.Settings)), Today);
        await Ui.Run(() => { sheetHost.Children.Clear(); sheet = new(session); sheetHost.Children.Add(sheet); });
        await Ui.Mount(sheetHost); await Ui.Idle();
        void AssertStatusAtFifth(string step) {
            sheet.UpdateLayout();
            Assert.That(sheet.X(Today), Is.EqualTo(sheet.ChartViewport / 5).Within(2), step);
            Assert.That(Ui.Find<Line>("PlanStatusLine").X1, Is.EqualTo(sheet.X(Today)).Within(.1), step);
        }
        await Ui.Run(() => {
            Assert.That(sheet.IsSelectedRow(""), Is.True, "A sheet opens on the empty new-task row.");
            Assert.That(sheet.IsSelectedRow("I1"), Is.False, "No task is selected until the PMO selects one.");
            AssertStatusAtFifth("open");
        });
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(sheet.DayWidth, Is.EqualTo(PlanSheetView.DayScaleWidth));
            AssertStatusAtFifth("日");
            Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(0, null, null, true);
        });
        await Ui.Until(() => sheet.ChartOffset < 1);
        await Ui.Run(() => Assert.That(sheet.X(Today), Is.GreaterThan(sheet.ChartViewport), "Scrolled away from the 基準日."));
        await Ui.Run(() => Ui.Click("PlanGanttGoToStatusDate")); await Ui.Idle();
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Ui.Find<Button>("PlanGanttGoToStatusDate")), Is.EqualTo("基準日へ移動"));
            Assert.That(Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem, Is.EqualTo("日"), "Going to the 基準日 keeps the scale.");
            AssertStatusAtFifth("基準日へ移動");
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    // #265: a fold can hide the row the PMO selected, and the sheet then picks a visible row by itself. That
    // pick is not a PMO selection, so a scale change keeps the 基準日 a fifth in rather than jumping to the
    // picked row's dates; once the PMO selects a row again, a scale change anchors on it.
    [Test, Category("GanttPhase3")]
    public async Task AFoldThatHidesTheSelectedTaskKeepsTheScaleOnTheStatusDateUntilThePmoSelectsAgain()
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var early = Today.AddDays(-90);
        var rows = session.Document.State.Rows.Select(r => r.Parent == "I1" ? r with { Start = early, End = early } : r).ToImmutableArray();
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "hidden")),
            new(session.Document.Project, new(rows, []), new(rows, session.Document.State.Settings)), Today);
        await Ui.Run(() => { sheetHost.Children.Clear(); sheet = new(session); sheetHost.Children.Add(sheet); });
        await Ui.Mount(sheetHost); await Ui.Idle();
        // I6 is a dated task under the I4 summary, whose rolled-up start (4/12) lies months after the 基準日.
        await SelectCell(6, PlanField.Title);
        await Ui.Run(() => sheet.SetFold("I4", true)); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(sheet.IsSelectedRow("I6"), Is.False);
            Assert.That(sheet.IsSelectedRow("I4"), Is.True, "The sheet picks a visible row itself.");
        });
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日"); await Ui.Idle();
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            sheet.UpdateLayout();
            Assert.That(sheet.X(Today), Is.EqualTo(sheet.ChartViewport / 5).Within(2), "The sheet's own pick does not anchor a scale change.");
            Assert.That(Ui.Find<Line>("PlanStatusLine").X1, Is.EqualTo(sheet.X(Today)).Within(.1));
        });
        // Choosing a row again is a PMO selection: the next scale change anchors that row's start.
        await SelectCell(2, PlanField.Title);
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "週"); await Ui.Idle();
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            var anchor = Today.AddDays(-90);
            var wanted = Math.Min(PlanSheetView.AnchorOffset(anchor, sheet.FirstDay, sheet.DayWidth, sheet.ChartViewport),
                Ui.Find<ScrollViewer>("PlanGanttHorizontal").ScrollableWidth);
            Assert.That(sheet.X(anchor), Is.EqualTo((anchor.DayNumber - sheet.FirstDay.DayNumber) * sheet.DayWidth - wanted).Within(2),
                "A PMO selection anchors its own task.");
        });
        Assert.That(session.UndoCount, Is.Zero);
    }

    [TestCase("日"), TestCase("週"), TestCase("月"), TestCase("全期間"), Category("GanttPhase3")]
    public async Task TwoTierTimescaleRetainsContextAcrossMonthAndYearWithoutOverlap(string scale)
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [
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
                    } else if (scale == "日") {
                        // 日 carries the weekday initial of its own date (#265).
                        var number = int.Parse(label.Text[..^1]);
                        var labelDay = DateOnly.FromDayNumber(int.Parse(Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(label)["PlanTimelineLabel".Length..]));
                        Assert.That(number, Is.EqualTo(labelDay.Day));
                        Assert.That(label.Text[^1], Is.EqualTo("日月火水木金土"[(int)labelDay.DayOfWeek]));
                    } else Assert.That(label.Text, Does.Match(@"^\d{1,2}月$"));
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
            // #236: a stored date is a plan value and a read does not invent one, so the rows the geometry
            // measures state the plan's own dates. I2 stores both endpoints and I6 is a zero-work
            // milestone on its date; without them those bars do not exist to measure. I2's end is the day its
            // remaining work reaches on this fixture's calendar (10/7 company day off, 10/8 imported holiday,
            // 10/12 bundled holiday), so its forecast ends on its plan end and the row stays out of the
            // 予定より遅れ set this case measures on rows 1 and 4.
            task with { Identity = "I2", Title = "進捗未取得", Actual = null, Start = Today.AddDays(3), End = new(2026, 10, 13) },
            task with { Identity = "I3", Title = "完了", Remaining = 0, End = Today.AddDays(3) },
            new PlanRow("I4", "集計", "acme/repo"), task with { Identity = "I5", Parent = "I4" },
            task with { Identity = "I6", Title = "承認", Estimate = 0, Actual = 0, Remaining = 0, Predecessors = ["I1"], Start = Today, End = Today });
        if (variant is "large" or "balanced") rows = rows.SetItem(1, rows[1] with {
            Actual = decimal.MaxValue, Remaining = variant == "large" ? 1 : decimal.MaxValue });
        if (variant is "closed-summary" or "complete-summary") rows = rows.SetItem(3, rows[3] with {
            Closed = variant == "closed-summary", Actual = variant == "closed-summary" ? null : 8, Remaining = variant == "closed-summary" ? null : 0 });
        // #236 part B: the slip is the plan-versus-forecast gap, so the late rows are the ones whose plan end
        // sits before the 基準日; a stored later end cannot create one.
        if (variant is "lateness" or "overdue-summary") rows = rows.SetItem(1, rows[1] with { Start = Today.AddDays(-3), End = Today.AddDays(-3) });
        if (variant == "overdue-summary") rows = rows.SetItem(4, rows[4] with { Start = Today.AddDays(-3), End = Today.AddDays(-3) });
        var baseline = rows.Select(r => r with { End = r.Identity is "I1" or "I4" || variant == "overdue-summary" && r.Identity == "I5" ? new(2026, 10, 2) : variant is "lateness" or "overdue-summary" && r.Identity == "I2" ? Today.AddDays(1) : r.End }).ToImmutableArray();
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

    // #236 part B: the slip is the plan-versus-forecast gap, so the case measures the forecast slip a 予定より
    // 遅れ row carries; the published-end basis it used to measure is retired.
    [TestCase("日"), TestCase("週"), Category("GanttPhase3")]
    public async Task GanttShowsProgressForecastSlipAndCalendarAtTheirDateCoordinates(string scale)
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
            Assert.That(route.Points.Last(), Is.EqualTo(new Windows.Foundation.Point(Canvas.GetLeft(head) + 3, Canvas.GetTop(head))));
            var successor = Ui.Find<Shape>("PlanBar6");
            Assert.That(Canvas.GetLeft(head) + 3, Is.InRange(Canvas.GetLeft(successor), Canvas.GetLeft(successor) + successor.Width));
            Assert.That(Canvas.GetTop(head) + 6, Is.EqualTo(Canvas.GetTop(successor)));
            Assert.That(head.Points.ToArray(), Is.EqualTo(new Windows.Foundation.Point[] { new(0, 0), new(6, 0), new(3, 6) }));
            var outgoing = Ui.Find<Polyline>("PlanArrow1_6_1");
            var predecessor = Ui.Find<Shape>("PlanBar1");
            Assert.That(outgoing.Points[1].Y, Is.EqualTo(outgoing.Points[0].Y));
            Assert.That(outgoing.Points[1].X, Is.GreaterThan(outgoing.Points[0].X));
            Assert.That(outgoing.Points[0], Is.EqualTo(new Windows.Foundation.Point(Canvas.GetLeft(predecessor) + predecessor.Width, sheet.RowHeight / 2)));
            var bar = Ui.Find<Rectangle>("PlanBar1");
            Assert.That(bar.Height, Is.EqualTo(14));
            Assert.That(Ui.Find<Rectangle>("PlanProgress1").Width, Is.EqualTo(bar.Width / 2).Within(.1));
            Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanProgress2"), Is.False);
            Assert.That(Ui.Find<Rectangle>("PlanProgress3").Width, Is.EqualTo(Ui.Find<Rectangle>("PlanBar3").Width));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(bar), Does.Contain("完了 50%").And.Contain("+1日"));
            foreach (var id in new[] { 1, 4 }) {
                var late = Ui.Find<Shape>("PlanLate" + id);
                // #236 part B: the slip starts after the inclusive plan end (10/5) and runs to the forecast end.
                Assert.That(late.TransformToVisual(sheet).TransformPoint(new()).X - sheet.SheetViewport,
                    Is.EqualTo(sheet.X(new(2026, 10, 6))).Within(.1));
                Assert.That(late.StrokeDashArray, Is.EqualTo(new double[] { 3, 2 }));
                // Only a 期限超過 row is labelled: these two are 予定より遅れ, so the count stays in the name.
                Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanLateLabel" + id), Is.False);
            }
            Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanLate2"), Is.False);
            Assert.That(Ui.Find<Polygon>("PlanBar4").Fill, Is.SameAs(PlanSheetView.Brush("GanttSummaryBrush")));
            Assert.That(Ui.Find<Polygon>("PlanBar6").Fill, Is.SameAs(PlanSheetView.Brush("GanttTaskBrush")));
            var status = Ui.Find<Line>("PlanStatusLine");
            Assert.That(status.X1, Is.EqualTo(sheet.X(Today)).Within(.1));
            Assert.That(status.StrokeThickness, Is.EqualTo(2));
            var pill = Ui.Find<Border>("PlanStatusDateLabel");
            Assert.That(((TextBlock)pill.Child).Text, Is.EqualTo("基準日 10/5"));
            Assert.That(Canvas.GetLeft(pill), Is.InRange(0d, sheet.ChartViewport - pill.ActualWidth));
            if (scale == "日") Assert.That(Canvas.GetLeft(pill) + pill.ActualWidth / 2, Is.EqualTo(sheet.X(Today)).Within(1));
            AssertNoOverlap(TimelineLabels("PlanTimelineUpper").Cast<FrameworkElement>().Append(pill));
            Assert.That(TimelineLabels("PlanTimelineUpper").Select(t => t.Text), Does.Contain("2026年9月").And.Contain("10月"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Ui.Find<Polygon>("PlanBar4")), Does.Contain("集計").And.Contain("+1日"));
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

    [TestCase(false), TestCase(true)]
    public async Task ScaleChangesPlaceTheSelectedDateOrStatusAtFifthViewport(bool selectTask)
    {
        if (selectTask) await SelectCell(5, PlanField.Title);
        foreach (var scale in new[] { "日", "月", "週" }) {
            await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale);
            await Ui.Idle();
            await Ui.Run(() => TestContext.Out.WriteLine($"Before frame: scale={scale}; offset={sheet.ChartOffset}; anchorX={(selectTask ? BarXInChart(5) : Ui.Find<Line>("PlanStatusLine").X1)}"));
            // Dispatcher work can finish before the scroll-driven rendering callback.
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                sheet.UpdateLayout();
                var x = selectTask ? BarXInChart(5) : Ui.Find<Line>("PlanStatusLine").X1;
                TestContext.Out.WriteLine($"After frame: scale={scale}; offset={sheet.ChartOffset}; anchorX={x}");
                // The range ends at the version, so an anchor near either end sits as close to a fifth as the
                // range allows; it is always in view.
                var anchor = selectTask ? new DateOnly(2027, 4, 12) : Today;
                var wanted = PlanSheetView.AnchorOffset(anchor, sheet.FirstDay, sheet.DayWidth, sheet.ChartViewport);
                var offset = Math.Min(wanted, Ui.Find<ScrollViewer>("PlanGanttHorizontal").ScrollableWidth);
                Assert.That(x, Is.InRange(0d, sheet.ChartViewport), "The anchor stays in view.");
                Assert.That(x, Is.EqualTo((anchor.DayNumber - sheet.FirstDay.DayNumber) * sheet.DayWidth - offset).Within(2), "Anchor tolerance is two pixels.");
            });
        }
        Assert.That(session.UndoCount, Is.Zero);
    }

    [TestCase("日"), TestCase("週"), TestCase("月")]
    public async Task EveryScaleEndsAtTheVersionWithAShortMargin(string scale)
    {
        // 月 once padded the chart with three quarters of a viewport and a year minimum, a year of empty
        // months after a six-month version. The Project period counts as part of the version.
        var end = new DateOnly(2027, 6, 30);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { ProjectEnd = end }), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale; });
        await Ui.Idle();
        await Ui.Run(() => {
            var margin = PlanSheetView.TimelineMarginDays;
            Assert.That(sheet.FirstDay, Is.EqualTo(Today.AddDays(-margin)));
            Assert.That(sheet.FirstDay.AddDays(sheet.DayCount - 1), Is.EqualTo(end.AddDays(margin)));
            Assert.That(Ui.Find<Line>("PlanStatusLine").X1, Is.InRange(0d, sheet.ChartViewport), "The 基準日 stays in view.");
        });
    }

    [TestCase("週"), TestCase("月")]
    public async Task EditingAnotherTaskAfterSelectingEarliestWorkKeepsTheDateViewport(string scale)
    {
        var early = Today.AddMonths(-3);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
            new[] { "I3", "I6" }.SelectMany(id => new PlanCellChange[] {
                new(id, PlanField.Start, early), new(id, PlanField.End, early)
            }).ToImmutableArray()), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale; });
        await Ui.Idle();
        await Ui.Run(() => Ui.Find<ScrollViewer>("PlanGanttHorizontal").ChangeView(0, null, null, true));
        await Ui.Until(() => sheet.ChartOffset < 1);
        await SelectCell(3, PlanField.Title);
        await Ui.Idle();
        double barX = 0, offset = 0;
        await Ui.Run(() => {
            sheet.UpdateLayout();
            barX = BarXInChart(3);
            offset = Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset;
        });

        // A title edit on another task does not move any scheduled endpoint.
        await SelectCell(6, PlanField.Title);
        await Ui.Run(() => {
            var editor = Ui.Find<TextBox>("PlanCell6_Title");
            Assert.That(editor.Focus(FocusState.Programmatic), Is.True);
            editor.Text = "承認（改）";
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Until(() => Row("I6").Title == "承認（改）");
        await Ui.Idle();
        await Ui.Run(() => {
            sheet.UpdateLayout();
            Assert.That(sheet.Schedule["I3"].Start.Value, Is.EqualTo(early));
            Assert.That(sheet.Schedule["I6"].End.Value, Is.EqualTo(early));
            Assert.That(BarXInChart(3), Is.EqualTo(barX).Within(1));
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").HorizontalOffset, Is.EqualTo(offset).Within(1));
        });
    }

    private double BarXInChart(int number)
    {
        sheet.UpdateLayout();
        return Ui.Find<Shape>("PlanBar" + number).TransformToVisual(sheet).TransformPoint(new()).X
            - Ui.Find<ScrollViewer>("PlanGanttHorizontal").TransformToVisual(sheet).TransformPoint(new()).X;
    }

    [TestCase(false), TestCase(true)]
    public async Task ChartWidthChangesKeepVisibleDatesThroughTheNextRefresh(bool resizeWindow)
    {
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue)).SetValue(600);
        });
        await Ui.ClickCommand("PlanSheetExpandAll");
        await Ui.Idle();
        double x = 0, width = 0;
        var renderedPositions = new List<double>();
        void RecordPosition(object? sender, RenderedEventArgs args)
        {
            if (sheet.ChartViewport <= width + 50) return;
            var bar = Ui.Find<Shape>("PlanBar3");
            var scroll = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            renderedPositions.Add(bar.TransformToVisual(sheet).TransformPoint(new()).X
                - scroll.TransformToVisual(sheet).TransformPoint(new()).X);
        }
        var windowSize = default(Windows.Graphics.SizeInt32);
        await Ui.Run(() => {
            x = BarXInChart(3); width = sheet.ChartViewport; windowSize = Ui.Window.AppWindow.Size;
            CompositionTarget.Rendered += RecordPosition;
        });
        try {
            await Ui.Run(() => {
                if (resizeWindow) Ui.Window.AppWindow.Resize(new(windowSize.Width + 200, windowSize.Height));
                else {
                    var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
                    ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue)).SetValue(400);
                }
            });
            await Ui.Until(() => sheet.ChartViewport > width + 50);
            await Ui.Idle();
            await Ui.Run(() => Assert.That(BarXInChart(3), Is.EqualTo(x).Within(1), "Resize must preserve the date viewport immediately."));
            // A width change must not defer its date-coordinate jump until the next ordinary refresh.
            await Ui.ClickCommand("PlanSheetExpandAll");
            await Ui.Idle();
            await Ui.Run(() => Assert.That(BarXInChart(3), Is.EqualTo(x).Within(1), "The next refresh must preserve the same dates."));
            await Ui.Until(() => renderedPositions.Count > 0);
            await Ui.Run(() => {
                TestContext.Out.WriteLine($"Resize frames: expectedX={x}; actual=[{string.Join(",", renderedPositions)}]");
                Assert.That(renderedPositions, Is.All.EqualTo(x).Within(1), "Every rendered frame must preserve the date coordinate.");
            });
        }
        finally {
            await Ui.Run(() => {
                CompositionTarget.Rendered -= RecordPosition;
                if (resizeWindow) Ui.Window.AppWindow.Resize(windowSize);
            });
        }
    }

    [TestCase("週"), TestCase("月")]
    public async Task EditingAnEarlierStartKeepsAnotherVisibleBarAtTheSameDateCoordinate(string scale)
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [
            new("I3", PlanField.Start, Today), new("I3", PlanField.End, Today.AddDays(3))]), Today);
        await Ui.Run(() => sheet.Refresh());
        await SelectCell(5, PlanField.Title);
        // Use explicit zoom navigation to bring the unchanged later task into the viewport.
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日");
        await Ui.Idle();
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale);
        await Ui.Idle();
        await Ui.Run(() => TestContext.Out.WriteLine($"Before frame: scale={scale}; offset={sheet.ChartOffset}; barX={BarXInChart(5)}"));
        await SheetNativeInput.Rendered();
        double x = 0;
        await Ui.Run(() => {
            x = BarXInChart(5);
            TestContext.Out.WriteLine($"After frame: scale={scale}; offset={sheet.ChartOffset}; barX={x}");
            Assert.That(x, Is.InRange(0d, sheet.ChartViewport));
        });
        var early = Today.AddMonths(-3);
        await SelectCell(3, PlanField.Start);
        await Ui.Run(() => {
            var editor = Ui.Find<TextBox>("PlanCell3_Start");
            Assert.That(editor.Focus(FocusState.Programmatic), Is.True);
            editor.Text = early.ToString("yyyy-MM-dd");
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Until(() => Row("I3").Start == early);
        await Ui.Idle();
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(sheet.Schedule["I5"].Start.Value, Is.EqualTo(new DateOnly(2027, 4, 12)));
            Assert.That(BarXInChart(5), Is.EqualTo(x).Within(1));
        });
    }

    [TestCase(120, 400), TestCase(1200, 230), TestCase(1, 230)]
    public async Task WholePeriodKeepsFirstAndLastFixedWidthMarksInsideTheViewport(int days, double chartWidth)
    {
        await Ui.Unmount(sheetHost); await session.FlushAsync();
        var last = Today.AddDays(days - 1);
        var rows = ImmutableArray.Create(
            new PlanRow("I1", "First milestone", "acme/repo") { Start = Today, End = Today, Estimate = 0, Remaining = 0, Actual = 0 },
            new PlanRow("I2", "Last milestone", "acme/repo") { Start = last, End = last, Estimate = 0, Remaining = 0, Actual = 0 },
            new PlanRow("I3", "First endpoint", "acme/repo") { Start = Today },
            new PlanRow("I4", "Last endpoint", "acme/repo") { End = last });
        session = await PlanSession.CreateAsync(new(Path.Combine(root, "end-marks")),
            new(session.Document.Project, new(rows, []), new(rows, session.Document.State.Settings)), Today);
        await Ui.Run(() => {
            sheetHost.Children.Clear(); sheet = new(session) { Width = chartWidth + 420, HorizontalAlignment = HorizontalAlignment.Left };
            sheetHost.Children.Add(sheet);
        });
        await Ui.Mount(sheetHost);
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue)).SetValue(400);
            Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "全期間";
        });
        await Ui.Idle();
        await Ui.Run(() => {
            sheet.UpdateLayout();
            var viewport = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            Assert.That(viewport.ActualWidth, Is.EqualTo(chartWidth).Within(1));
            Assert.That(viewport.ScrollableWidth, Is.EqualTo(0).Within(1), "Full period must fit without scrolling.");
            foreach (var id in new[] { "PlanBar1", "PlanBar2", "PlanEndpoint3", "PlanEndpoint4" }) {
                var mark = Ui.Find<Polygon>(id);
                var bounds = mark.TransformToVisual(viewport).TransformBounds(new(0, 0, mark.ActualWidth, mark.ActualHeight));
                Assert.That(mark.ActualWidth, Is.EqualTo(id.StartsWith("PlanBar") ? 10 : 8));
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0), id + " left edge");
                Assert.That(bounds.Right, Is.LessThanOrEqualTo(viewport.ActualWidth), id + " right edge");
            }
            var status = Ui.Find<Line>("PlanStatusLine");
            Assert.That(status.X1 - status.StrokeThickness / 2, Is.GreaterThanOrEqualTo(0));
            Assert.That(status.X1 + status.StrokeThickness / 2, Is.LessThanOrEqualTo(viewport.ActualWidth));
            var pill = Ui.Find<Border>("PlanStatusDateLabel");
            Assert.That(Canvas.GetLeft(pill), Is.GreaterThanOrEqualTo(0));
            Assert.That(Canvas.GetLeft(pill) + pill.ActualWidth, Is.LessThanOrEqualTo(viewport.ActualWidth));
        });
        if (days == 120) await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "issue109-full-period-edge-marks"));
    }

    [TestCase(-365), TestCase(730)]
    public async Task WholePeriodIncludesStatusOutsideAllScheduledDates(int statusOffset)
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, session.Document.State.Rows.Where(r => r.Parent is not null)
            .SelectMany(r => new PlanCellChange[] { new(r.Identity, PlanField.Start, Today), new(r.Identity, PlanField.End, Today.AddDays(2)) }).ToImmutableArray()), Today);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = Today.AddDays(statusOffset) }), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "全期間"; });
        await Ui.Idle();
        await Ui.Run(() => {
            sheet.UpdateLayout();
            Assert.That(Ui.Find<Line>("PlanStatusLine").X1, Is.InRange(0d, sheet.ChartViewport));
            var pill = Ui.Find<Border>("PlanStatusDateLabel");
            Assert.That(pill.ActualWidth, Is.GreaterThan(0));
            var status = Ui.Find<Line>("PlanStatusLine");
            Assert.That(status.X1 - status.StrokeThickness / 2, Is.GreaterThanOrEqualTo(0));
            Assert.That(status.X1 + status.StrokeThickness / 2, Is.LessThanOrEqualTo(sheet.ChartViewport));
            Assert.That(Canvas.GetLeft(pill), Is.GreaterThanOrEqualTo(0));
            Assert.That(Canvas.GetLeft(pill) + pill.ActualWidth, Is.LessThanOrEqualTo(sheet.ChartViewport));
            Assert.That(TimelineLabels("PlanTimelineUpper").First().Text, Is.EqualTo((statusOffset < 0 ? Today.AddDays(statusOffset).Year : Today.Year) + "年"));
            AssertNoOverlap(TimelineLabels("PlanTimelineUpper").Cast<FrameworkElement>().Append(pill));
        });
    }

    [Test]
    public async Task DependencyToAnEarlierRowEntersTheSuccessorFromBelow()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "全期間");
        await Ui.Idle();
        await Ui.Run(() => {
            var bar = Ui.Find<Shape>("PlanBar2");
            var head = Ui.Find<Polygon>("PlanArrowHead5_2_2");
            Assert.That(head.Points.ToArray(), Is.EqualTo(new Windows.Foundation.Point[] { new(0, 6), new(6, 6), new(3, 0) }));
            Assert.That(Canvas.GetTop(head), Is.EqualTo(Canvas.GetTop(bar) + bar.Height));
            Assert.That(Canvas.GetLeft(head) + 3, Is.InRange(Canvas.GetLeft(bar), Canvas.GetLeft(bar) + bar.Width));
            var line = Ui.Find<Polyline>("PlanArrow5_2_5");
            var predecessor = Ui.Find<Shape>("PlanBar5");
            Assert.That(line.Points[0], Is.EqualTo(new Windows.Foundation.Point(Canvas.GetLeft(predecessor) + predecessor.Width, sheet.RowHeight / 2)));
            Assert.That(line.StrokeThickness, Is.EqualTo(1));
        });
    }

    [TestCase(0, 24, 400, 0), TestCase(10, 24, 400, 160), TestCase(100, 2, 400, 120), TestCase(3, 24, 400, 0)]
    public void AnchorGeometryReservesFifthViewportAndClampsAtRangeStart(int days, double dayWidth, double viewport, double expected)
    {
        Assert.That(PlanSheetView.AnchorOffset(Today.AddDays(days), Today, dayWidth, viewport), Is.EqualTo(expected));
    }

    [Test]
    public async Task ChartSeparatorsAlignWithRealizedSheetRowBottoms()
    {
        await Ui.Run(() => {
            sheet.UpdateLayout();
            foreach (var row in sheet.Realized.Where(r => r.Identity.Length > 0)) {
                var number = sheet.PlanIds[row.Identity];
                var separator = Ui.Find<Border>("PlanChartSeparator" + number);
                var bottom = separator.TransformToVisual(sheet).TransformPoint(new(0, separator.ActualHeight)).Y;
                Assert.That(bottom, Is.EqualTo(row.TransformToVisual(sheet).TransformPoint(new(0, row.ActualHeight)).Y).Within(1));
                Assert.That(separator.ActualWidth, Is.EqualTo(sheet.ChartViewport).Within(1));
                Assert.That(separator.BorderThickness.Bottom, Is.EqualTo(1));
                Assert.That(separator.BorderBrush, Is.SameAs(PlanSheetView.Brush("SheetSeparatorBrush")));
            }
        });
    }

    [TestCase("週", false), TestCase("月", false), TestCase("全期間", false)]
    [TestCase("週", true), TestCase("月", true), TestCase("全期間", true)]
    public async Task LatenessTintAndLabelsDistinguishOverdueFromLaterOnly(string scale, bool overdueSummary)
    {
        await MountGanttExample(overdueSummary ? "overdue-summary" : "lateness");
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = scale);
        await Ui.Idle();
        await Ui.Run(() => {
            foreach (var number in new[] { 1, 2, 4 }) {
                // I2 is 期限超過 (its plan end is before the 基準日) and so is the summary in its variant; I1
                // and the ordinary summary are 予定より遅れ, which is the untinted dashed outline.
                var overdue = number == 2 || number == 4 && overdueSummary;
                var days = overdue ? 2 : 1;
                var segment = Ui.Find<Shape>("PlanLate" + number);
                Assert.That(segment.StrokeDashArray, Is.EqualTo(new double[] { 3, 2 }));
                Assert.That(segment.IsHitTestVisible, Is.False, "The overlay must not intercept the bar tooltip.");
                if (overdue) Assert.That(segment.Fill, Is.SameAs(PlanSheetView.Brush("GanttLateTintBrush")));
                else Assert.That(segment.Fill is null || segment.Fill is SolidColorBrush { Color.A: 0 }, Is.True);
                Assert.That(Ui.Tree(sheet).Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PlanLateLabel" + number), Is.EqualTo(overdue && scale == "週"));
                var bar = Ui.Find<Shape>("PlanBar" + number);
                Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(bar), Does.Contain($"+{days}日"));
                Assert.That(ToolTipService.GetToolTip(bar)?.ToString(), Does.Contain($"+{days}日"));
                if (overdue && scale == "週") {
                    var label = Ui.Find<TextBlock>("PlanLateLabel" + number);
                    Assert.That(label.Text, Is.EqualTo($"+{days}日"));
                    Assert.That(label.FontSize, Is.EqualTo(11));
                    Assert.That(label.FontWeight, Is.EqualTo(Microsoft.UI.Text.FontWeights.SemiBold));
                }
            }
        });
        if (!overdueSummary && scale == "月") await Ui.Run(async () => await RenderedEvidence.Capture(sheet, "issue109-gantt-month-lateness"));
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
            Assert.That(Ui.Find<TextBlock>("PlanRowId2").Text, Is.EqualTo("#102"));
            Assert.That(Ui.Find<TextBlock>("PlanRowId5").Text, Is.EqualTo("#105"));
        });
        var selectionTrace = new List<string>();
        RoutedEventHandler focusChanged = (_, _) => TraceSelection("GotFocus");
        MenuFlyout menu = null!;
        EventHandler<object> menuOpening = (_, _) => TraceSelection("menu Opening");
        EventHandler<object> menuClosed = (_, _) => TraceSelection("menu Closed");
        await Ui.Run(() => {
            menu = (MenuFlyout)Ui.Find<AppBarButton>("PlanSheetMore").Flyout;
            Ui.Root.GotFocus += focusChanged;
            menu.Opening += menuOpening;
            menu.Closed += menuClosed;
            TraceSelection("before selection");
        });
        try {
            await SelectCell(2, PlanField.Assignees);
            await Ui.Run(() => TraceSelection("after Select I2"));
            await SelectCell(5, PlanField.Assignees, extend: true);
            await Ui.Run(() => {
                TraceSelection("after AddToSelection I5");
                foreach (var number in new[] { 2, 5 }) {
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>($"PlanCell{number}_Assignees"));
                    Assert.That(((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).IsSelected, Is.True,
                        $"Row {number} must belong to the visible bulk-edit selection before paste.");
                }
            });
            clipboard = new("bob", null);
            await Ui.ClickCommand("PlanSheetPaste");
            await Ui.Idle();
            await Ui.Run(() => TraceSelection("after paste"));
            Assert.That(Row("I2").Assignees.ToArray(), Is.EqualTo(new[] { "U2" }));
            Assert.That(Row("I5").Assignees.ToArray(), Is.EqualTo(new[] { "U2" }));
            Assert.That(Row("I3").Assignees.ToArray(), Is.EqualTo(new[] { "U1" }));
            Assert.That(Row("I6").Assignees.ToArray(), Is.EqualTo(new[] { "U1" }));
            Assert.That(session.UndoCount, Is.EqualTo(1));
        }
        finally {
            await Ui.Run(() => {
                TraceSelection("final");
                Ui.Root.GotFocus -= focusChanged;
                menu.Opening -= menuOpening;
                menu.Closed -= menuClosed;
            });
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, selectionTrace));
        }

        void TraceSelection(string phase)
        {
            if (selectionTrace.Count >= 40) return;
            var focus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(sheet.XamlRoot) as FrameworkElement;
            var cells = sheet.Realized.Select(row => {
                var cell = row.Cells.SingleOrDefault(cell => cell.Field == PlanField.Assignees);
                var peer = cell is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(cell);
                var selected = peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider provider && provider.IsSelected;
                return $"{row.Identity}:context={row.DataContext},loaded={row.IsLoaded},owner={ReferenceEquals(row.Owner, sheet)},cell={Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(cell ?? (DependencyObject)row)},selected={selected},focus={cell?.FocusState}";
            });
            selectionTrace.Add($"{phase}: focus={focus?.GetType().Name}/{(focus is null ? "" : Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(focus))}; undo={session.UndoCount}; I2=[{string.Join(',', Row("I2").Assignees)}]; I5=[{string.Join(',', Row("I5").Assignees)}]; pending=[{string.Join(';', sheet.Pending.Select(pair => $"{pair.Key.Identity}/{pair.Key.Field}={pair.Value.Text}"))}]; rows=[{string.Join(';', cells)}]");
        }

        await Filter("", ["I1", "I4", ""]);
        await Ui.Ready<FrameworkElement>("PlanCell4_Assignees");
        await Ui.Run(() => {
            Assert.That(VisibleRows(), Is.EqualTo(new[] { "I1", "I4", "" }));
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("PlanCell4_Assignees"));
            Assert.That(peer.GetName(), Does.Contain("#104").And.Contain("担当者"));
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
            Assert.That(((TextBlock)Ui.Popup<Border>("SheetInputProblem")!.Child).Text, Is.Not.Empty);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Collapsed));
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
            // The flyout's own command names the job; the search box carries it only as its accessible name.
            var search = Ui.Popup<TextBox>("PlanPredecessorSearch")!;
            Assert.That(search.Header, Is.Null);
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(search), Is.EqualTo("先行タスクを検索"));
            Assert.That(Ui.Popup<Button>("PlanPredecessorConfirm")!.Style, Is.SameAs(Application.Current.Resources["AccentButtonStyle"]));
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
        // #263: the requirement R01 受注 (#101) is never offered; its task 承認 matches through it.
        await SearchPredecessors("受注", 1);
        await Ui.Run(() => Assert.That(string.Join(" ", Ui.Tree(Ui.Popup<ListView>("PlanPredecessorCandidates")!).OfType<TextBlock>().Select(b => b.Text)),
            Does.StartWith("#103 承認")));
        await SearchPredecessors("承認", 2);
        await SearchPredecessors("＃１０６", 1);
        await Ui.Run(() => {
            var list = Ui.Popup<ListView>("PlanPredecessorCandidates")!;
            var text = string.Join(" ", Ui.Tree(list).OfType<TextBlock>().Select(block => block.Text));
            Assert.That(text, Does.StartWith("#106 承認").And.Contain(" · 終了 ").And.Not.Contain("acme/repo").And.Not.Contain("ID"), "One line: task name, title, assignee, plan end.");
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
    private bool RowIsFocused(int number) => Ui.Tree(sheet).OfType<Control>().Any(cell =>
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
    private static async Task SelectCell(int number, string field, bool extend = false)
    {
        var id = $"PlanCell{number}_{field}";
        await Ui.Ready<FrameworkElement>(id);
        await Ui.Run(() => {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>(id));
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
