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
        await Ui.Mount(sheet);
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
                try { if (sheet is not null) await Ui.Unmount(sheet, check: false); await Ui.Idle(); }
                finally
                {
                    if (session is not null) await session.FlushAsync();
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
        }
        finally { Ui.EndTest(); }
    }

    [Test]
    public async Task FoldedRequirementsKeepPhaseBulkEditingAndOneStepUndo()
    {
        await SelectCell(2, PlanField.Title);
        await Ui.ClickCommand("PlanSheetCollapseAll", focus: true);
        await Ui.Idle();
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
        await Ui.ClickCommand("PlanSheetUndo");
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
            Ui.Click("PlanSheetGoToDate");
        });
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
        await Ui.ClickCommand("PlanSheetUndo");
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
