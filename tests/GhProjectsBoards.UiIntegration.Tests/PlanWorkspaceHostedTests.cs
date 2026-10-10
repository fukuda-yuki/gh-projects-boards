using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Polygon = Microsoft.UI.Xaml.Shapes.Polygon;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;
[TestFixture, NonParallelizable, Category("PlanWorkspace")]
internal sealed class PlanWorkspaceHostedTests
{
    [TestCase(1280, 800), TestCase(1920, 1032), TestCase(1000, 720), TestCase(900, 720)]
    [Category("PlanSheetReview")]
    public async Task DefaultDividerFitsColumnsOrKeepsMinimumGanttAtWorkspaceClientSize(int width, int height)
    {
        FakePlanEditor.Save(root, new(Enumerable.Range(1, 40).Select(i => new PlanFakeIssue(
            new("I" + i, i == 1 ? "設計" : "Planning task " + i, "acme/repo")
                { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)).ToImmutableArray(), 41));
        await Open();
        await Ui.Run(() => ResizeWorkspace(width, height));
        await Ui.Until(() => Math.Abs(Ui.Root.ActualWidth - width) < 1 && Math.Abs(Ui.Root.ActualHeight - height) < 1);
        await Ui.Idle();
        await Ui.Run(() => {
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            var horizontal = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            var chart = Ui.Find<ScrollViewer>("PlanGanttHorizontal");
            var headers = new[] { "Id", "Indicator", "Title", "Assignees", "Estimate", "Remaining", "Actual", "Start", "End", "Predecessors" }
                .Select(name => Ui.Find<TextBlock>("PlanHeader" + name)).ToArray();
            var rowBounds = Ui.Tree(sheet.List).OfType<TextBox>()
                .Where(cell => AutomationProperties.GetAutomationId(cell).EndsWith("_Title", StringComparison.Ordinal))
                .Select(cell => cell.TransformToVisual(sheet.List).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight))).ToArray();
            var fullRows = rowBounds.Count(bounds => bounds.Top >= 0 && bounds.Bottom <= sheet.List.ActualHeight);
            Console.WriteLine($"Default divider: client={Ui.Root.ActualWidth}x{Ui.Root.ActualHeight}, scale={Ui.Root.XamlRoot.RasterizationScale}, sheetControl={sheet.ActualWidth}, sheetViewport={horizontal.ViewportWidth}, sheetExtent={horizontal.ExtentWidth}, sheetOffset={horizontal.HorizontalOffset}, Gantt={chart.ViewportWidth}, weeks={chart.ViewportWidth / (7 * sheet.DayWidth):F2}, rowViewport={sheet.List.ActualHeight}, fullRows={fullRows}");
            Assert.That(horizontal.HorizontalOffset, Is.Zero);
            Assert.That(chart.ViewportWidth, Is.GreaterThanOrEqualTo(320));
            if (width >= 1280) {
                foreach (var header in headers) {
                    var bounds = header.TransformToVisual(sheet).TransformBounds(new Rect(0, 0, header.ActualWidth, header.ActualHeight));
                    Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(0), header.Text);
                    Assert.That(bounds.Right, Is.LessThanOrEqualTo(horizontal.ViewportWidth), header.Text);
                }
                Assert.That(horizontal.ScrollableWidth, Is.Zero);
            } else Assert.That(horizontal.ExtentWidth, Is.GreaterThan(horizontal.ViewportWidth));
            if (width == 1920) {
                // Fixed chrome leaves 1920 - 42 (card) - 20 (right reservation) - 854 (sheet) = 1004.
                // Even removing the sheet's 6 px clearance cannot reach the approximate 1040 px target.
                Assert.That(chart.ViewportWidth, Is.EqualTo(1004).Within(2), "Current ten-column budget, allowing pixel rounding.");
                Assert.That(fullRows, Is.EqualTo(29).Within(2));
            }
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, $"default-divider-{width}x{height}"));
    }

    [TestCase(1000), TestCase(1440), Category("ShellChrome")]
    public async Task TitleBarUsesGanttGlyphAndPlacesGearBesideNativeCaptions(int width)
    {
        await Open();
        await Ui.Run(() => {
            Ui.Window.ExtendsContentIntoTitleBar = true;
            Ui.Window.SetTitleBar(view.WorkspaceTitleBar);
            ResizeWorkspace(width, 720);
        });
        try {
            await Ui.Until(() => Math.Abs(Ui.Root.ActualWidth - width) < 1 && Math.Abs(view.ActualWidth - width) < 1);
            await Ui.Run(() => {
                var bar = view.WorkspaceTitleBar;
                var gear = Ui.Find<Button>("PlanShowSettings");
                var bounds = gear.TransformToVisual(Ui.Root).TransformBounds(new Rect(0, 0, gear.ActualWidth, gear.ActualHeight));
                var captionLeft = Ui.Root.ActualWidth - Ui.Window.AppWindow.TitleBar.RightInset / Ui.Root.XamlRoot.RasterizationScale;
                Console.WriteLine($"Title bar geometry: width={Ui.Root.ActualWidth}, scale={Ui.Root.XamlRoot.RasterizationScale}, rightInset={Ui.Window.AppWindow.TitleBar.RightInset}, gearRight={bounds.Right}, gap={captionLeft - bounds.Right}");
                Assert.That(captionLeft - bounds.Right, Is.InRange(0d, 8d));
                Assert.That(bar.IconSource, Is.Null);
                Assert.That(bar.LeftHeader, Is.TypeOf<PathIcon>());
                var geometry = ((PathIcon)bar.LeftHeader).Data as Microsoft.UI.Xaml.Media.PathGeometry;
                Assert.That(geometry, Is.Not.Null);
                Assert.That(geometry!.Figures.Select(f => f.StartPoint), Is.EqualTo(new[] { new Point(1, 2), new Point(6, 7), new Point(3, 12) }));
                Assert.That(geometry.Figures.All(f => f.IsClosed), Is.True);
            });
        } finally { await Ui.Run(() => { Ui.Window.SetTitleBar(null); Ui.Window.ExtendsContentIntoTitleBar = false; }); }
    }

    [Test, Category("ShellChrome")]
    public async Task CommandRowUsesSubtleHistoryAndSeparatesTheThreeGroups()
    {
        await Open();
        await Ui.Run(() => {
            var undo = Ui.Find<Button>("PlanUndo"); var redo = Ui.Find<Button>("PlanRedo");
            Assert.That(undo.Style, Is.SameAs(Application.Current.Resources["SubtleButtonStyle"]));
            Assert.That(redo.Style, Is.SameAs(undo.Style));
            var group = (Panel)undo.Parent;
            var dividers = group.Children.OfType<Border>().Where(b => b.Width == 1 && b.Height == 20).ToArray();
            Assert.That(dividers, Has.Length.EqualTo(2));
            var date = Ui.Find<CalendarDatePicker>("PlanStatusDate");
            double X(FrameworkElement e) => e.TransformToVisual(group).TransformPoint(new Point()).X;
            Assert.That(X(dividers[0]), Is.GreaterThan(X(date) + date.ActualWidth - 1).And.LessThan(X(undo)));
            Assert.That(X(dividers[1]), Is.GreaterThan(X(redo) + redo.ActualWidth - 1).And.LessThan(X(Ui.Find<TextBlock>("PlanUnpublished"))));
            var commands = Ui.Find<CommandBar>("PlanSheetCommands").SecondaryCommands;
            Assert.That(commands.Select(c => c is AppBarButton b ? b.Label : "|"),
                Is.EqualTo(new[] { "コピー", "貼り付け", "下へコピー", "クリア", "|", "CSV から追加…", "表示列" }));
            Assert.That(commands.OfType<AppBarButton>().Take(4).Select(b => b.KeyboardAcceleratorTextOverride),
                Is.EqualTo(new[] { "Ctrl+C", "Ctrl+V", "Ctrl+D", "Delete" }));
        });
    }

    [Test, Category("ShellChrome")]
    public async Task GanttLegendOnlyAppearsOnPlanWhileProjectCountsRemain()
    {
        await Open();
        foreach (var command in new[] { "PlanShowTasks", "PlanShowPeople", "PlanShowSettings", "PlanShowTasks", "PlanPublish" }) {
            await Ui.Run(() => Ui.Click(command)); await Ui.Idle();
            await Ui.Run(() => {
                Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility,
                    Is.EqualTo(command == "PlanShowTasks" ? Visibility.Visible : Visibility.Collapsed));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 0 · タスク 1"));
            });
        }
        foreach (var command in new[] { "PlanChooseProject", "PlanConnection" }) {
            await PickerCommand(command);
            await Ui.Run(() => {
                Assert.That(Ui.Find<StackPanel>("PlanGanttLegend").Visibility, Is.EqualTo(Visibility.Collapsed));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 0 · タスク 1"));
            });
        }
    }

    [TestCase(false, false, "要求事項 0 · タスク 2")]
    [TestCase(true, false, "要求事項 0 · タスク 2 · 期限超過 1")]
    [TestCase(false, true, "要求事項 0 · タスク 2 · 予定より遅れ 1")]
    [TestCase(true, true, "要求事項 0 · タスク 2 · 期限超過 1 · 予定より遅れ 1")]
    [Category("ShellChrome")]
    public async Task StatusCountsOmitZeroLatenessParts(bool overdue, bool later, string expected)
    {
        var date = new DateOnly(2026, 10, 5);
        var state = FakePlanEditor.Load(root);
        var row = state.Issues[0].Row with { Start = date, End = date.AddDays(2), Fixed = true };
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = row with { Start = overdue ? date.AddDays(-3) : date, End = overdue ? date.AddDays(-3) : date.AddDays(2) } },
            new(row with { Identity = "I2", Title = "後続" }, "", true)] });
        await Open();
        await Ui.Run(async () => {
            var session = workspace.Session!;
            await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { StatusDate = date }), date);
            if (later) await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.End, date.AddDays(3))]), date);
            Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh();
            Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo(expected));
        });
    }

    [Test, Category("ShellChrome")]
    public async Task ProjectPickerChecksOnlyOpenProjectAndSeparatesIconCommands()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedItem = workspace.Available.Single(p => p.Id.NodeId == "P2"));
        await Ui.Idle();
        await OpenProjectPicker();
        await WaitForProjectPickerItems("P2");
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("RegisteredProjects");
            var titleXs = new List<double>();
            for (var i = 0; i < list.Items.Count; i++) {
                var choice = (ProjectChoice)list.Items[i];
                var item = (ListViewItem)list.ContainerFromIndex(i);
                var check = Ui.Tree(item).OfType<FontIcon>().Single(f => f.Glyph == "\uE73E");
                Assert.That(check.Opacity, Is.EqualTo(choice.Id == workspace.Selected!.Id ? 1d : 0d));
                var texts = Ui.Tree(item).OfType<TextBlock>().ToArray();
                Assert.That(texts.Any(t => t.Text == $"{choice.OwnerLogin} · Project {choice.Number}"), Is.True, string.Join(" | ", texts.Select(t => t.Text)));
                titleXs.Add(texts.Single(t => t.Text == choice.Title).TransformToVisual(list).TransformPoint(new Point()).X);
            }
            Assert.That(titleXs.Distinct().Count(), Is.EqualTo(1));
            var content = (StackPanel)((Flyout)Ui.Find<Button>("PlanProjectPicker").Flyout).Content;
            Assert.That(content.Children[1], Is.TypeOf<Border>());
            Assert.That(((Border)content.Children[1]).Height, Is.EqualTo(1));
            foreach (var id in new[] { "PlanChooseProject", "PlanConnection" })
                Assert.That(Ui.Tree(Ui.Find<Button>(id)).OfType<FontIcon>().Any(), Is.True);
        });
    }

    [Test, Category("ShellChrome")]
    public async Task ProjectPickerChecksCurrentProjectAfterTwoSwitchesAndReopening()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedItem = workspace.Available.Single(p => p.Id.NodeId == "P2"));
        await Ui.Idle();
        await OpenProjectPicker();
        await WaitForProjectPickerItems("P2");
        foreach (var project in new[] { "P1", "P2" }) {
            await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == project));
            await Ui.Until(() => workspace.Selected?.Id.NodeId == project);
            await OpenProjectPicker();
            await WaitForProjectPickerItems(project);
            await Ui.Run(() => Ui.Find<Button>("PlanProjectPicker").Flyout.Hide());
            await OpenProjectPicker();
            await WaitForProjectPickerItems(project);
        }
    }

    private async Task WaitForProjectPickerItems(string currentProject)
    {
        await Ui.Until(() => {
            var list = Ui.Find<ListView>("RegisteredProjects");
            if (list.Items.Count != 2 || workspace.Selected?.Id.NodeId != currentProject) return false;
            var checkedProjects = new List<string>();
            for (var i = 0; i < list.Items.Count; i++) {
                if (list.ContainerFromIndex(i) is not ListViewItem { IsLoaded: true } item) return false;
                var choice = (ProjectChoice)list.Items[i];
                var texts = Ui.Tree(item).OfType<TextBlock>().ToArray();
                if (!texts.Any(t => t.Text == choice.Title) ||
                    !texts.Any(t => t.Text == $"{choice.OwnerLogin} · Project {choice.Number}")) return false;
                var check = Ui.Tree(item).OfType<FontIcon>().SingleOrDefault(f => f.Glyph == "\uE73E");
                if (check is null || (check.Opacity != 0 && check.Opacity != 1)) return false;
                if (check.Opacity == 1) checkedProjects.Add(choice.Id.NodeId);
            }
            return checkedProjects.SequenceEqual(new[] { currentProject });
        });
    }

    [TestCase(false), TestCase(true), Category("PlanSheetPhase2")]
    public async Task ShellStatusDateResolvesTheActiveViewsInputAndCreatesOneSettingsUndo(bool people)
    {
        await Open();
        if (people) { await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1"); }
        var before = workspace.Session!.Document.State.Settings.StatusDate;
        var undo = workspace.Session.UndoCount;
        var inputId = people ? "PeopleAllowance_U1" : "PlanCell1_Remaining";
        await Ui.Run(() => {
            var picker = Ui.Find<CalendarDatePicker>("PlanStatusDate");
            Assert.That(Ui.Tree(Ui.Tree(view).OfType<PlanSheetView>().Single()).Contains(picker), Is.False);
            var input = Ui.Find<TextBox>(inputId); input.Focus(FocusState.Programmatic); input.Text = "invalid";
            picker.Date = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        });
        await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.StatusDate, Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>(inputId).Text, Is.EqualTo("invalid"));
            Assert.That(Ui.Find<CalendarDatePicker>("PlanStatusDate").Date!.Value.Date,
                Is.EqualTo((before ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue)));
            Ui.Find<TextBox>(inputId).Text = people ? "80" : "8";
            Ui.Find<CalendarDatePicker>("PlanStatusDate").Focus(FocusState.Programmatic);
        });
        await Ui.Idle();
        if (people) { await Ui.Run(() => Ui.Click("PlanShowTasks")); await Ui.Idle(); }
        undo = workspace.Session.UndoCount;
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.StatusDate, Is.EqualTo(new DateOnly(2026, 10, 20)));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Settings.StatusDate, Is.EqualTo(before));
        await Ui.Run(() => {
            var localSheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            Assert.That(localSheet.Pending, Is.Empty, string.Join(";", localSheet.Pending.Select(p => $"{p.Key}: {p.Value.Text} original={p.Value.OriginalText}")));
        });
    }

    [Test, Category("WorkspaceShell")]
    public async Task QueuedViewSelectionsRetainTheLastRequestAfterPendingRefresh()
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdQuery = "ProjectFields"
        }));
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Run(() => { Ui.Click("PlanShowPeople"); Ui.Click("PlanShowTasks"); });
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.True);
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowPeople").IsSelected, Is.False);
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Focus(FocusState.Programmatic), Is.True);
        });
    }

    [Test, Category("WorkspaceShell")]
    public async Task RejectedViewSelectionReturnsToTheActualPlanningTab()
    {
        await Open();
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanCell1_Remaining"); input.Focus(FocusState.Programmatic); input.Text = "invalid";
            Ui.Click("PlanShowPeople");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.True);
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowPeople").IsSelected, Is.False);
            var input = Ui.Find<TextBox>("PlanCell1_Remaining");
            Assert.That(input.Text, Is.EqualTo("invalid"));
            Assert.That(input.Focus(FocusState.Programmatic), Is.True);
            input.Text = "8";
        });
    }

    [TestCase(false), TestCase(true), Category("WorkspaceShell")]
    public async Task SettingsBackRestoresTheSelectedPlanningView(bool people)
    {
        await Open();
        if (people) { await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1"); }
        await Settings();
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.False);
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowPeople").IsSelected, Is.False);
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Ready<TextBox>(people ? "PeopleAllowance_U1" : "PlanCell1_Title");
        await Ui.Run(() => Assert.That(Ui.Find<SelectorBarItem>(people ? "PlanShowPeople" : "PlanShowTasks").IsSelected, Is.True));
    }

    [TestCase(false), TestCase(true), Category("WorkspaceShell")]
    public async Task WorkspaceHistoryCommitsThenReversesAndReappliesOneOperation(bool people)
    {
        await Open();
        if (people) { await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1"); }
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>(people ? "PeopleAllowance_U1" : "PlanCell1_Title");
            input.Focus(FocusState.Programmatic); input.Text = people ? "80" : "変更した設計";
            Ui.Click("PlanUndo");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("設計"));
            if (people) Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.Null);
            Ui.Click("PlanRedo");
        });
        await Ui.Idle();
        await Ui.Ready<TextBox>(people ? "PeopleAllowance_U1" : "PlanCell1_Title");
        await Ui.Run(() => {
            if (people) Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.EqualTo("80"));
            else Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Text, Is.EqualTo("変更した設計"));
        });
    }

    [TestCase("PlanUndo"), TestCase("PlanRedo"), Category("WorkspaceShell")]
    public async Task WorkspaceHistoryRejectsInvalidSheetInput(string command)
    {
        await Open();
        await Ui.Run(() => { var title = Ui.Find<TextBox>("PlanCell1_Title"); title.Focus(FocusState.Programmatic); title.Text = "履歴"; Ui.Click("PlanShowSettings"); });
        await Ui.Idle();
        if (command == "PlanRedo") { await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle(); }
        await Ui.Run(() => Ui.Click("PlanShowTasks")); await Ui.Idle();
        var accepted = PlanJson.Text(workspace.Session!.Document.State);
        var undo = workspace.Session.UndoCount; var redo = workspace.Session.RedoCount;
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanCell1_Remaining"); input.Focus(FocusState.Programmatic); input.Text = "invalid";
            Ui.Click(command);
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(PlanJson.Text(workspace.Session!.Document.State), Is.EqualTo(accepted));
            Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo)); Assert.That(workspace.Session.RedoCount, Is.EqualTo(redo));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("invalid"));
            Ui.Find<TextBox>("PlanCell1_Remaining").Text = "8";
        });
    }

    [Test, Category("WorkspaceShell")]
    public async Task StatusCountsIncludeRequirementsAndTasksAndWorkspaceOverridesDarkHost()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0],
            new(new("I2", "子", "acme/repo") { Parent = "I1" }, "", true),
            new(new("I3", "独立", "acme/repo"), "", true)] });
        await Ui.Run(() => { Ui.Root.RequestedTheme = ElementTheme.Dark; ResizeWorkspace(1280, 720); });
        try {
            await Open();
            await Ui.Run(() => {
                Assert.That(view.ActualTheme, Is.EqualTo(ElementTheme.Light));
                var caption = Ui.Window.AppWindow.TitleBar;
                Assert.That(caption.ButtonForegroundColor, Is.EqualTo(Microsoft.UI.Colors.Black));
                Assert.That(caption.ButtonHoverForegroundColor, Is.EqualTo(Microsoft.UI.Colors.Black));
                Assert.That(caption.ButtonInactiveForegroundColor, Is.EqualTo(Windows.UI.Color.FromArgb(102, 0, 0, 0)));
                Assert.That(Ui.Find<TextBlock>("PlanStatusCounts").Text, Is.EqualTo("要求事項 1 · タスク 2"));
            });
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "workspace-shell-light"));
            await Ui.Run(async () => await RenderedEvidence.Capture((FrameworkElement)Ui.Find<Button>("PlanRefresh").Parent, "workspace-command-row"));
        } finally { await Ui.Run(() => Ui.Root.RequestedTheme = ElementTheme.Default); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SiblingReviewAndUnpublishedCountAgreeWhenNativeOrderIsAbsent(bool reverse)
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = state.Issues[0].Row with { Remaining = null, Estimate = null, Assignees = [] } },
            new(new("I2", "子A", "acme/repo") { Parent = "I1" }, "", true),
            new(new("I3", "子B", "acme/repo") { Parent = "I1" }, "", true)] });
        await Open();
        await workspace.Session!.SaveSync(workspace.Session.Document.Sync with {
            NativeOrders = reverse ? ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("I1", ["I3", "I2"]) : ImmutableDictionary<string, ImmutableArray<string>>.Empty });
        await Ui.Ready<TextBox>("PlanCell2_Title");
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell2_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "子Aの変更"; Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("子A → 子Aの変更")));
        await Ui.Run(() => {
            Assert.That(workspace.Session.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount, Is.EqualTo(reverse ? 2 : 1));
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo((reverse ? "2" : "1")));
            var lines = Ui.Tree(Ui.Find<ListView>("PlanPublishLines")).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.That(lines.Count(t => t.Contains("子タスクの順序")), Is.EqualTo(reverse ? 1 : 0));
        });
    }

    [TestCase("VerificationMismatch", "Project への追加を確認できません")]
    [TestCase("NotDispatched", "まだGitHubへ送信されていません")]
    public async Task NewIssueReviewUsesProposedValuesAndReadableMembershipFailure(string reason, string text)
    {
        await Open();
        var parent = PlanRow.New("CSV group", "acme/repo");
        var row = PlanRow.New("CSV task", "acme/repo") with { Estimate = 16, Parent = parent.Identity };
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new InsertPlanRows([parent, row]), DateOnly.FromDateTime(DateTime.Today));
            await workspace.Session.SaveSync(workspace.Session.Document.Sync with { Failures = [new(row.Identity, PlanField.NewTask, reason)] });
            Ui.Tree(view).OfType<PlanSheetView>().Single().Refresh();
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("CSV task")));
        await Ui.Run(async () => {
            var lines = Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroup" + row.Identity)).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.That(string.Join(" ", lines), Does.Not.Contain("未入力 →").And.Not.Contain(reason).And.Contain(text));
            Assert.That(lines.Any(t => t.Contains("見積 h") && t.Contains("16")), Is.True);
            if (reason == "NotDispatched") Assert.That(string.Join(" ", lines), Does.Contain("未送信").And.Not.Contain("発行失敗"));
            if (reason == "NotDispatched") await RenderedEvidence.Capture(view, "publish-pending-reason");
            Ui.Click("PlanPublishClose");
        });
        await Ui.Ready<PlanSheetCell>("PlanCell3_Title");
        await Ui.Run(() => {
            var help = Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<PlanSheetCell>("PlanCell3_Title"));
            Assert.That(help, Does.Contain(text).And.Not.Contain(reason));
            if (reason == "NotDispatched") Assert.That(help, Does.Contain("未送信").And.Not.Contain("発行失敗"));
        });
    }
    private static string FakeExecutable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GHPB_UI_FAKE_GH_PATH") is { Length: > 0 } configured) return configured;
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "GhProjectsBoards.sln"))) folder = folder.Parent;
            return Path.Combine(folder?.FullName ?? throw new InvalidOperationException("Repository root required."),
                "tests", "GhProjectsBoards.Tests", "bin", "Release", "net10.0-windows", "GhProjectsBoards.Tests.exe");
        }
    }
    private string root = null!;
    private PlanWorkspace workspace = null!;
    private PlanWorkspaceView view = null!;
    private TaskCompletionSource? pickerClosed;
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-workspace-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)], 2) { Drafts = 2, PullRequests = 1, Redacted = 1, HiddenItems = 1 });
        workspace = new(new(root));
        await Ui.Run(() => view = new(workspace, (_, host) => new(FakeExecutable, host,
            new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }))));
        if (TestContext.CurrentContext.Test.MethodName == nameof(TitleBarUsesGanttGlyphAndPlacesGearBesideNativeCaptions))
            await Ui.Run(() => { Ui.Window.ExtendsContentIntoTitleBar = true; Ui.Window.SetTitleBar(view.WorkspaceTitleBar); });
        await Ui.Mount(view);
        await Ui.Run(() => {
            var flyout = Ui.Find<Button>("PlanProjectPicker").Flyout;
            flyout.Closing += (_, _) => { if (flyout.IsOpen && (pickerClosed is null || pickerClosed.Task.IsCompleted)) pickerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously); };
            flyout.Closed += (_, _) => pickerClosed?.TrySetResult();
        });
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
        var stopped = false; var problem = "";
        try
        {
            await Ui.Run(async () => { stopped = await view.StopAsync(); problem = Ui.Find<TextBlock>("PlanError", view).Text; });
        }
        finally
        {
            try { await Ui.Unmount(view, check: false); await Ui.Run(() => { Ui.Window.SetTitleBar(null); Ui.Window.ExtendsContentIntoTitleBar = false; }); await Ui.Idle(); await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1400, 1000))); }
            finally
            {
                try { await workspace.Flush(); }
                catch when (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Passed)
                { await workspace.RetrySave(); }
                finally { Directory.Delete(root, true); }
            }
        }
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed) Assert.That(stopped, Is.True, problem);
        else TestContext.Out.WriteLine("Close after failed test: " + problem);
            }
        finally { Ui.EndTest(); }
    }

    [Test]
    public async Task CsvPickerValidatesWholeFileThenImportsAndUndoesOneOperation()
    {
        await Open();
        var unpublishedBeforeCsv = workspace.Session!.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount;
        var path = Path.Combine(root, "tasks.csv");
        await File.WriteAllTextAsync(path, "キー,タイトル,見積,担当者,先行タスク,親\na,,8,,,\nb,確認,4,,missing,\n");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "設計");
        await Ui.Run(() => { view.PickFile = purpose => { Assert.That(purpose, Is.EqualTo("csv")); return Task.FromResult<string?>(path); }; });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.DialogReady("PlanCsvErrors");
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("PlanCsvErrors"), Does.Contain("2行: タイトル").And.Contain("3行: 参照"));
            Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
        });
        await File.WriteAllTextAsync(path, "キー,タイトル,見積,リポジトリ\na,準備,8,unknown/repo\nb,確認,4,unknown/repo\n");
        await Ui.Run(() => Ui.DialogButton("PlanCsvErrors", "PrimaryButton"));
        await Ui.Until(() => Ui.Dialog("PlanCsvErrors") is not null && Ui.DialogText("PlanCsvErrors").Contains("リポジトリ"));
        await Ui.Run(() => {
            Assert.That(Ui.DialogText("PlanCsvErrors"), Does.Contain("2行:").And.Contain("3行:"));
            Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
        });
        await File.WriteAllTextAsync(path, "キー,タイトル,見積,担当者,先行タスク,親\np,準備,,,,\na,設計の追加,8,alice,,p\nb,確認,4,alice,a,p\n");
        await Ui.Run(() => Ui.DialogButton("PlanCsvErrors", "PrimaryButton"));
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Length == 4);
        await Ui.Run(async () => await Ui.Tree(view).OfType<PlanSheetView>().Single().FlushInput());
        await Ui.Ready<PlanSheetCell>("PlanCell3_Title");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo((unpublishedBeforeCsv + 3).ToString()));
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell3_Title").Text, Is.EqualTo("設計の追加"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.DialogReady("PlanCsvDuplicate");
        await Ui.Run(() => Ui.DialogButton("PlanCsvDuplicate", "CloseButton"));
        await Ui.Until(() => Ui.Dialog("PlanCsvDuplicate") is null);
        await Ui.Run(async () => await RenderedEvidence.Capture(Ui.Tree(view).OfType<PlanSheetView>().Single(), "csv-imported-plan"));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session!.Document.State.Rows.Length == 1);
        await Ui.Run(() => { view.PickFile = _ => Task.FromResult<string?>(null); });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.Run(async () => await Ui.Tree(view).OfType<PlanSheetView>().Single().FlushInput());
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
    }

    [Test]
    public async Task ClosingDismissesCsvErrorsWithoutImporting()
    {
        await Open();
        var path = Path.Combine(root, "invalid.csv");
        File.WriteAllText(path, "キー,タイトル,見積\na,,8\n");
        await Ui.Run(() => { view.PickFile = _ => Task.FromResult<string?>(path); });
        await Ui.ClickCommand("PlanSheetCsv");
        await Ui.DialogReady("PlanCsvErrors");
        Task<bool> stop = null!;
        await Ui.Run(() => { stop = view.StopAsync(); });
        var completed = await Task.WhenAny(stop, Task.Delay(3000)) == stop;
        try { Assert.That(completed, Is.True, "Close must dismiss the CSV dialog without requiring another click."); }
        finally
        {
            await Ui.Run(() => Ui.Dialog("PlanCsvErrors")?.Hide());
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(await stop, Is.True);
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task PeoplePendingInputSurvivesLeavingTheDrillDown()
    {
        await PreparePeoplePendingExit();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "PeopleTask_I1_Actual"), Is.True,
                "Completing Remaining must not remove another pending editor.");
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Until(() => Ui.Find<TextBox>("PeopleTask_I1_Actual").FocusState != FocusState.Unfocused);
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PeopleTask_I1_Actual");
            Assert.That(cell.Text, Is.EqualTo("invalid"));
            var problem = Ui.Popup<Border>("PeopleInputProblem");
            Assert.That(problem, Is.Not.Null);
            Assert.That(((TextBlock)problem!.Child).Text, Is.Not.Empty);
            Assert.That(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(cell.XamlRoot).Single(p => p.Child == problem).PlacementTarget, Is.SameAs(cell));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(Ui.Popup<Border>("PeopleInputProblem")!, "people-validation"));
        await Ui.Run(() => { Ui.Find<TextBox>("PeopleTask_I1_Actual").Text = "0"; Ui.Click("PlanShowTasks"); });
        await Ui.Ready<TextBox>("PlanCell1_Title");
        Assert.That(workspace.Session!.Document.State.Rows[0].Actual, Is.Zero);
    }

    private async Task PreparePeoplePendingExit()
    {
        await OpenPeopleTasks("PeopleTask_I1_Actual");
        await Ui.Run(() => {
            var actual = Ui.Find<TextBox>("PeopleTask_I1_Actual"); actual.Focus(FocusState.Programmatic); actual.Text = "invalid";
            var remaining = Ui.Find<TextBox>("PeopleTask_I1_Remaining"); remaining.Focus(FocusState.Programmatic); remaining.Text = "0";
            Ui.Find<ComboBox>("PeopleScale").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Remaining == 0); await Ui.Idle();
        await Ui.Ready<Grid>("PeopleRow_U1");
    }

    // The drill-down lists the work in the first displayed day. Without a status date that day is
    // today, which has no work on a weekend or holiday, so pin it to a working day.
    private async Task OpenPeopleTasks(string input)
    {
        await Open();
        var monday = new DateOnly(2026, 10, 5);
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = monday }), monday);
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<Button>("PeopleExpand_U1");
        await Ui.Run(() => Ui.Click("PeopleExpand_U1")); await Ui.Ready<TextBox>(input);
    }

    [TestCase("fixed"), TestCase("assignee"), TestCase("zoom")]
    public async Task PeopleRefusedControlsReflectTheDocument(string control)
    {
        await OpenPeopleTasks("PeopleTask_I1_Remaining");
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PeopleTask_I1_Remaining"); cell.Focus(FocusState.Programmatic); cell.Text = "invalid";
            if (control == "fixed") Ui.Toggle(Ui.Find<CheckBox>("PeopleTask_I1_Fixed"));
            else Ui.Find<ComboBox>(control == "assignee" ? "PeopleTask_I1_Assignees" : "PeopleScale").SelectedIndex = control == "assignee" ? 0 : 1;
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PeopleError").Text.Length > 0); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<CheckBox>("PeopleTask_I1_Fixed").IsChecked, Is.EqualTo(workspace.Session!.Document.State.Rows[0].Fixed));
            Assert.That(((ComboBoxItem)Ui.Find<ComboBox>("PeopleTask_I1_Assignees").SelectedItem).Tag, Is.EqualTo("U1"));
            Assert.That(Ui.Find<ComboBox>("PeopleScale").SelectedIndex, Is.Zero);
            Assert.That(Ui.Find<TextBox>("PeopleTask_I1_Remaining").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Ui.Find<TextBox>("PeopleTask_I1_Remaining").Text = "8"; Ui.Click("PeopleNext");
        });
        await Ui.Idle();
    }

    [Test, Category("PeopleRefresh")]
    public async Task PeopleRefreshAfterRetriedSaveKeepsTheAcceptedInputAndFocus()
    {
        await PreparePeopleRetriedSave();
        var undo = workspace.Session!.UndoCount;
        await Ui.Run(() => {
            Ui.Find<TextBox>("PeopleAllowance_U1").Focus(FocusState.Programmatic);
            Ui.Tree(view).OfType<PlanPeopleView>().Single().Refresh();
        });
        await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PeopleAllowance_U1");
            Assert.That(cell.Text, Is.EqualTo("80"));
            Assert.That(cell.FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowPeople").IsSelected, Is.True);
        });
        Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
    }

    private async Task PreparePeopleRetriedSave()
    {
        await Open(); await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Ui.Run(() => { var cell = Ui.Find<TextBox>("PeopleAllowance_U1"); cell.Focus(FocusState.Programmatic); cell.Text = "80"; Ui.Click("PeopleNext"); });
            await Ui.Until(() => Ui.Find<TextBlock>("PeopleError").Text.Length > 0);
        }
        await workspace.RetrySave();
    }

    [Test, Category("PlanSheetNative")]
    public async Task PeopleEscapeRestoresTheCurrentDocumentAfterFailedSave()
    {
        await PreparePeopleRetriedSave(); var undo = workspace.Session!.UndoCount;
        await SheetNativeInput.Click("PeopleAllowance_U1");
        await Ui.Run(() => Ui.Find<TextBox>("PeopleAllowance_U1").Text = "90");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Escape);
        // KeyUp proves key delivery, but refreshed ListView containers load during layout.
        await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PeopleAllowance_U1");
            Assert.That(cell.Text, Is.EqualTo("80"));
            Assert.That(cell.FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowPeople").IsSelected, Is.True);
            Assert.That(Ui.Find<TextBlock>("PeopleError").Text, Is.Empty);
            Assert.That(Ui.Popup<Border>("PeopleInputProblem"), Is.Null);
        });
        Assert.That(workspace.Session.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
    }

    [TestCase("fixed"), TestCase("assignee")]
    public async Task PeopleFailedSaveControlsReflectTheAcceptedDocument(string control)
    {
        await OpenPeopleTasks("PeopleTask_I1_Remaining");
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Ui.Run(() => {
                if (control == "fixed") Ui.Toggle(Ui.Find<CheckBox>("PeopleTask_I1_Fixed"));
                else Ui.Find<ComboBox>("PeopleTask_I1_Assignees").SelectedIndex = 0;
            });
            await Ui.Until(() => Ui.Find<TextBlock>("PeopleError").Text.Length > 0); await Ui.Idle();
            await Ui.Run(() => {
                var row = workspace.Session.Document.State.Rows[0];
                if (control == "fixed") { Assert.That(row.Fixed, Is.True); Assert.That(Ui.Find<CheckBox>("PeopleTask_I1_Fixed").IsChecked, Is.True); }
                else { Assert.That(row.Assignees, Is.Empty); Assert.That(Ui.Find<ComboBox>("PeopleTask_I1_Assignees").SelectedIndex, Is.Zero); }
            });
        }
        await workspace.RetrySave();
    }

    [Test, Category("PlanSheetNative")]
    public async Task PeopleEscapeDiscardsRetainedInput()
    {
        await PreparePeoplePendingExit();
        var undo = workspace.Session!.UndoCount;
        await SheetNativeInput.Click("PeopleTask_I1_Actual");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Escape);
        await Ui.Until(() => !Ui.Tree(view).OfType<TextBox>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "PeopleTask_I1_Actual"));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(workspace.Session.Document.State.Rows[0].Actual, Is.Null);
        await Ui.Run(() => Ui.Click("PlanShowTasks")); await Ui.Ready<TextBox>("PlanCell1_Title");
    }

    [Test]
    public async Task PeopleAllowancesStayWithTheSelectedProject()
    {
        await Open(); await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => { var input = Ui.Find<TextBox>("PeopleAllowance_U1"); input.Focus(FocusState.Programmatic); input.Text = "80"; });
        await PickerCommand("PlanChooseProject");
        await Ui.Until(() => workspace.Session!.Document.State.Settings.People.Any(p => p.Allowance == 80));
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Session!.Document.Project.NodeId == "P2");
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.Empty);
        });
        await SelectRegistered("P1");
        await Ui.Until(() => workspace.Session!.Document.Project.NodeId == "P1");
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.EqualTo("80")));
    }

    [TestCase(228, "-108 超過")]
    [TestCase(1120, "-1000 超過")]
    public async Task PeopleOverAllowanceDifferenceShowsTheWholeWarning(int forecast, string expected)
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with {
            Row = state.Issues[0].Row with { Estimate = forecast, Remaining = forecast } }] });
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = [new("U1", "alice", 100, 120)] }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => { ResizeWorkspace(1280, 720); Ui.Click("PlanShowPeople"); });
        await Ui.Ready<TextBlock>("PeopleTotal_U1_4");
        await Ui.Run(() => {
            view.UpdateLayout();
            var difference = Ui.Find<TextBlock>("PeopleTotal_U1_4");
            Assert.That(difference.ActualWidth, Is.GreaterThan(0));
            using (Assert.EnterMultipleScope()) {
                Assert.That(difference.Text, Is.EqualTo(expected));
                Assert.That(difference.IsTextTrimmed, Is.False, "The amount and warning must both fit after layout.");
                Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(difference), Does.EndWith(expected));
            }
        });
    }

    [Test]
    public async Task PeopleGroupDifferenceIsNotApplicableWhilePersonAllowanceIsMissing()
    {
        await Open();
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.Empty);
            var person = Ui.Find<TextBlock>("PeopleTotal_U1_4");
            Assert.That(person.Text, Is.EqualTo("未入力"));
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(person), Does.EndWith(" 差分 未入力"));
            foreach (var (identity, name) in new[] { (PlanPeople.Unassigned, "担当者なし"), (PlanPeople.Multiple, "担当者が複数") }) {
                var difference = Ui.Find<TextBlock>($"PeopleTotal_{identity}_4");
                Assert.That(difference.Text, Is.EqualTo("—"), name);
                Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(difference), Is.EqualTo(name + " 差分 —"));
                Assert.That(ToolTipService.GetToolTip(difference), Is.EqualTo("—"));
            }
        });
    }

    [Test]
    public async Task PeopleAllowanceEntryKeepsTheNextCellFocusedAcrossRecalculation()
    {
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = [new("U1", "alice", 100, null), new("U2", "bob", 100, null)] }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U2");
        await Ui.Run(() => {
            var first = Ui.Find<TextBox>("PeopleAllowance_U1"); first.Focus(FocusState.Programmatic); first.Text = "80";
            var second = Ui.Find<TextBox>("PeopleAllowance_U2"); second.Focus(FocusState.Programmatic); second.Text = "7";
        });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[0].Allowance == 80);
        await Ui.Idle();
        await Ui.Ready<TextBox>("PeopleAllowance_U2");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PeopleTotal_U1_1").Text, Is.EqualTo("0"));
            Assert.That(Ui.Find<TextBlock>("PeopleTotal_U1_3").Text, Is.EqualTo("8"));
            Assert.That(Ui.Find<TextBlock>("PeopleTotal_U1_4").Text, Is.EqualTo("72"));
        });
        Assert.That(workspace.Session.Document.State.Settings.People[1].Allowance, Is.Null, "Typing in the next cell must not be committed by the previous cell's focus loss.");
        await Ui.Run(() => Assert.That(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(view.XamlRoot), Is.SameAs(Ui.Find<TextBox>("PeopleAllowance_U2"))));
        await Ui.Run(() => { Assert.That(Ui.Find<TextBox>("PeopleAllowance_U2").Text, Is.EqualTo("7")); Ui.Find<TextBox>("PeopleAllowance_U2").Text = "75"; Ui.Click("PeopleNext"); });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[1].Allowance == 75);
    }

    [Test]
    public async Task PeopleQueuedEditsAndInvalidTextSurviveOtherCellsSaving()
    {
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = [new("U1", "alice", 100, null), new("U2", "bob", 100, null), new("U3", "carol", 100, null)] }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U3");
        await Ui.Run(() => {
            foreach (var (id, text) in new[] { ("U1", "80"), ("U2", "75"), ("U3", "7") }) {
                var box = Ui.Find<TextBox>("PeopleAllowance_" + id); box.Focus(FocusState.Programmatic); box.Text = text;
            }
        });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[0].Allowance == 80);
        await Ui.Idle();
        await Ui.Ready<TextBox>("PeopleAllowance_U3");
        await Ui.Run(() => {
            Assert.That(workspace.Session.Document.State.Settings.People[1].Allowance, Is.EqualTo(75), "The queued second edit must survive the first redraw.");
            Assert.That(Ui.Find<TextBox>("PeopleAllowance_U3").Text, Is.EqualTo("7"));
            Assert.That(workspace.Session.Document.State.Settings.People[2].Allowance, Is.Null);
            var first = Ui.Find<TextBox>("PeopleAllowance_U1"); first.Focus(FocusState.Programmatic); first.Text = "bad";
            var second = Ui.Find<TextBox>("PeopleAllowance_U2"); second.Focus(FocusState.Programmatic); second.Text = "70";
            Ui.Find<TextBox>("PeopleAllowance_U3").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[1].Allowance == 70);
        await Ui.Idle();
        await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.EqualTo("bad"));
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => { Ui.Find<TextBox>("PeopleAllowance_U1").Text = "85"; Ui.Click("PeopleNext"); });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People[0].Allowance == 85);
    }

    [Test]
    public async Task PeopleAllowancePersistsAndInvalidInputBlocksNavigationUntilCorrected()
    {
        await Open(); await Ui.Run(() => Ui.Click("PlanShowPeople"));
        await Ui.Ready<TextBox>("PeopleAllowance_U1");
        var undo = workspace.Session!.UndoCount;
        await Ui.Run(() => { var input = Ui.Find<TextBox>("PeopleAllowance_U1"); input.Focus(FocusState.Programmatic); input.Text = "-1"; Ui.Click("PlanShowTasks"); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => { Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").IsLoaded, Is.True); Ui.Find<TextBox>("PeopleAllowance_U1").Text = "80"; Ui.Click("PlanShowTasks"); });
        await Ui.Until(() => workspace.Session.Document.State.Settings.People.Any(p => p.Identity == "U1" && p.Allowance == 80));
        await workspace.Session.FlushAsync();
        var loaded = await PlanSession.OpenAsync(new(root), workspace.Session.Document.Project, DateOnly.FromDateTime(DateTime.Today));
        Assert.That(loaded.Session!.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.Document.State.Settings.People.SingleOrDefault(p => p.Identity == "U1")?.Allowance is null);
    }

    [TestCase(PlanField.Actual, "56")]
    [TestCase(PlanField.Assignees, "person-U2")]
    [TestCase(PlanField.Assignees, "担当者なし")]
    [TestCase(PlanField.Fixed, "固定")]
    public async Task PeopleTaskEditsShareTheSheetOperationAndUndo(PlanField field, string value)
    {
        await Open();
        var day = new DateOnly(2026, 10, 5);
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Start, day), new("I1", PlanField.End, day)]), day);
        await workspace.Session.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = day,
            People = [new("U1", "alice", 100, 80), new("U2", "bob", 100, 80)] }), day);
        if (field == PlanField.Fixed) await workspace.Session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", field, false)]), day);
        var before = workspace.Session.Document.State;
        var undo = workspace.Session.UndoCount;
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<Button>("PeopleExpand_U1");
        await Ui.Run(() => Ui.Click("PeopleExpand_U1")); await Ui.Ready<TextBox>("PeopleTask_I1_Actual");
        await Ui.Run(() => {
            if (field == PlanField.Fixed) {
                var box = Ui.Find<CheckBox>("PeopleTask_I1_Fixed");
                var peer = new Microsoft.UI.Xaml.Automation.Peers.CheckBoxAutomationPeer(box);
                ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)).Toggle();
            } else if (field == PlanField.Assignees) {
                var box = Ui.Find<ComboBox>("PeopleTask_I1_Assignees");
                Assert.That(box.Items.OfType<ComboBoxItem>().Select(i => i.Content), Does.Contain("担当者なし").And.Contain("person-U2"));
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(i => i.Content.ToString() == value);
            } else { var box = Ui.Find<TextBox>("PeopleTask_I1_" + field); box.Focus(FocusState.Programmatic); box.Text = value; Ui.Click("PeoplePeriod_0"); }
        });
        await Ui.Until(() => workspace.Session.UndoCount == undo + 1);
        var changed = workspace.Session.Document.State.Rows[0];
        if (field == PlanField.Assignees) Assert.That(changed.Assignees, Is.EqualTo(value == "person-U2" ? new[] { "U2" } : Array.Empty<string>()));
        else Assert.That(PlanValues.Get(changed, field), Is.EqualTo(field == PlanField.Actual ? "56" : "true"));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.UndoCount == undo);
        Assert.That(PlanJson.Text(workspace.Session.Document.State), Is.EqualTo(PlanJson.Text(before)));
    }

    [Test]
    public async Task PeopleViewRendersOverloadAndTaskEditsRecalculateWithOneStepUndo()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            new(new("I1", "設計", "acme/repo") { Estimate = 4, Remaining = 4, Actual = 0, Assignees = ["U1"] }, "", true),
            new(new("I2", "検証", "acme/repo") { Estimate = 4, Remaining = 4, Actual = 0, Assignees = ["U1"] }, "", true)], NextId = 3 });
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            StatusDate = new(2026, 10, 5), People = [new("U1", "alice", 50, 80)] }), new(2026, 10, 5));
        await Ui.Run(() => Ui.Click("PlanShowPeople"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("200% 超過")));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PeopleLoad_U1_0").Foreground, Is.EqualTo(Application.Current.Resources["SystemFillColorCriticalBrush"]));
            Ui.Click("PeopleExpand_U1");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBox>().Any(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "PeopleTask_I2_Remaining"));
        var undo = workspace.Session.UndoCount;
        await Ui.Run(() => { var box = Ui.Find<TextBox>("PeopleTask_I2_Remaining"); box.Focus(FocusState.Programmatic); box.Text = "0"; Ui.Click("PeopleNext"); });
        await Ui.Until(() => workspace.Session.Document.State.Rows[1].Remaining == 0);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.Document.State.Rows[1].Remaining == 4);
        await Ui.Run(() => Ui.Click("PeoplePrevious"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "PeopleLoad_U1_0" && t.Text.Contains("200% 超過")));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task PeopleViewFitsTwentyPeopleAndAssignmentGroupsAt1280By720()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8, Actual = 0, Assignees = ["U1"] }, "", true),
            new(new("I2", "検証", "acme/repo") { Estimate = 8, Remaining = 8, Actual = 0, Assignees = ["U1"] }, "", true)], NextId = 3 });
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            StatusDate = new(2026, 10, 5),
            People = Enumerable.Range(1, 20).Select(i => new PlanResource("U" + i, "person-" + i, 100, 80)).ToImmutableArray() }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => { ResizeWorkspace(1280, 720); Ui.Click("PlanShowPeople"); });
        for (var scale = 0; scale < 3; scale++) {
        await Ui.Run(() => Ui.Find<ComboBox>("PeopleScale").SelectedIndex = scale);
        await Ui.Idle();
        await Ui.Until(() => Ui.Tree(view).OfType<FrameworkElement>().Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PeopleRow_multiple"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, $"people-{scale}-1280x720"));
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PeopleRows");
            Assert.That(list.Items.Count, Is.EqualTo(22));
            foreach (var person in workspace.Session.Document.State.Settings.People.Select(p => p.Identity).Concat(["unassigned", "multiple"])) {
                var row = Ui.Find<Grid>("PeopleRow_" + person);
                var bounds = row.TransformToVisual(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(-0.5), person);
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(list.ActualHeight + 0.5), person);
                Assert.That(row.ActualHeight, Is.GreaterThanOrEqualTo(24), person);
            }
            if (scale > 0) {
                var load = Ui.Find<TextBlock>("PeopleLoad_U1_0");
                Assert.That(load.Text, Does.Contain("日超過1日").And.Contain("最大200%"));
                Assert.That(load.IsTextTrimmed, Is.False);
                Assert.That(load.ActualHeight, Is.LessThanOrEqualTo(24));
            }
        });
        }
    }

    [Test]
    public async Task ReviewShowsNonConflictingNativeOrderAndUnretrievedRelationships()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [
            state.Issues[0] with { Row = state.Issues[0].Row with { Parent = "I99", Predecessors = ["I98"] } },
            new(new("I2", "子A", "acme/repo") { Parent = "I1" }, "", true),
            new(new("I3", "子B", "acme/repo") { Parent = "I1" }, "", true)],
            SubOrders = state.SubOrders.SetItem("I1", ["I3", "I2"]), NextId = 4 });
        await Open();
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Parent, null), new("I1", PlanField.Predecessors, ImmutableArray<string>.Empty)]), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("親タスク")));
        await Ui.Run(() => {
            var text = string.Join("\n", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("子タスクの順序  3 子B、2 子A → 2 子A、3 子B"));
            Assert.That(text, Does.Contain("親タスク  計画外Issue（未取得） → 未入力"));
            Assert.That(text, Does.Contain("先行  計画外Issue（未取得） → 未入力"));
            Assert.That(Ui.Tree(view).OfType<HyperlinkButton>().Any(b => b.NavigateUri?.AbsoluteUri == "https://github.com/acme/repo/issues/1"), Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        });
    }

    [Test]
    public async Task PublishReviewRequiresExplicitConfirmationAndUndoStaysLocal()
    {
        await Open();
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "設計の変更"; Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 設計の変更")));
        await Ui.Run(() => {
            Assert.That(string.Join(" ", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("設計 → 設計の変更"));
            Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計"));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => workspace.Session!.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount == 0);
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計の変更"));
        await Ui.Run(() => { Ui.Click("PlanPublishClose"); Ui.Click("PlanUndo"); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "1");
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計の変更"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("1")));
    }

    [TestCase(false), TestCase(true), Category("OperationStates")]
    public async Task RemoteWorkShowsRunningCommandAndLocksCellsUntilCompletion(bool publish)
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdQuery = publish ? "mutation PlanPublish(" : "ProjectFields"
        }));
        if (publish) { await Ui.Run(() => Ui.Click("PlanPublish")); await Ui.Idle(); }
        await Ui.Run(() => Ui.Click(publish ? "PlanPublishConfirm" : "PlanRefresh"));
        try {
            await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
            await Ui.Run(() => {
                var progress = Ui.Tree(view).OfType<ProgressBar>().Single();
                Assert.That(progress.Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(progress.IsIndeterminate, Is.True);
                Assert.That(progress.ActualHeight, Is.InRange(1d, 4d));
                var button = Ui.Find<Button>(publish ? "PlanPublish" : "PlanRefresh");
                var label = publish ? "発行中…" : "更新中…";
                Assert.That(Ui.Tree(button).OfType<TextBlock>().Any(t => t.Text == label), Is.True);
                Assert.That(AutomationProperties.GetName(button), Is.EqualTo(label));
                if (publish) Ui.Click("PlanPublishClose");
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.True);
            });
            await Ui.Run(async () => await RenderedEvidence.Capture(view, publish ? "t6-publishing" : "t6-refreshing"));
        } finally { File.WriteAllText(Path.Combine(root, "release-gh"), "release"); }
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<ProgressBar>().Single().Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>(publish ? "PlanPublish" : "PlanRefresh")), Is.EqualTo(publish ? "発行…" : "最新の情報に更新"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.False);
        });
    }

    [Test, Category("OperationStates")]
    public async Task LocalUndoDoesNotShowRemoteProgress()
    {
        await Open();
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "変更"; Ui.Click("PlanPublish"); });
        await Ui.Idle();
        var shown = false;
        long callback = 0;
        await Ui.Run(() => {
            var progress = Ui.Tree(view).OfType<ProgressBar>().Single();
            callback = progress.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => shown |= progress.Visibility == Visibility.Visible);
            Ui.Click("PlanUndo");
        });
        await Ui.Idle();
        await Ui.Run(() => Ui.Tree(view).OfType<ProgressBar>().Single().UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, callback));
        Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.EqualTo("設計"));
        Assert.That(shown, Is.False);
    }

    [Test, Category("OperationStates")]
    public async Task SheetSaveFailureAppearsAboveWorkspaceAndKeepsOneRetry()
    {
        await Open();
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Ui.Run(() => {
                var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "保存待ち";
                Ui.Find<TextBox>("PlanCell1_Estimate").Focus(FocusState.Programmatic);
            });
            await Ui.Ready<Button>("PlanSheetRetrySave");
            await Ui.Run(() => {
                AssertFailure("PlanSheetSaveFailure", "保存できませんでした", "保存を再試行");
                Assert.That(Ui.Tree(view).OfType<InfoBar>().Count(b => b.IsOpen && b.Title == "保存できませんでした"), Is.EqualTo(1));
            });
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "t6-sheet-save-failure"));
        }
        await Ui.Run(() => Ui.Click("PlanSheetRetrySave")); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanSheetSaveFailure").IsOpen, Is.False));
    }

    private void AssertFailure(string id, string title, string retry)
    {
        var bar = Ui.Find<InfoBar>(id);
        Assert.That(bar.IsOpen, Is.True);
        Assert.That(bar.IsClosable, Is.False);
        Assert.That(bar.Severity, Is.EqualTo(InfoBarSeverity.Error));
        Assert.That(bar.Title, Is.EqualTo(title));
        Assert.That(bar.Message, Is.Not.Empty);
        Assert.That(((Button)bar.ActionButton).Content, Is.EqualTo(retry));
        var card = Ui.Find<Border>("PlanWorkCard");
        var bounds = bar.TransformToVisual(card).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
        Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0));
        Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(card.ActualHeight));
        var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
        if (sheet.Visibility == Visibility.Visible && sheet.ActualHeight > 0)
            Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(sheet.TransformToVisual(card).TransformPoint(new Point()).Y + 1));
        Assert.That(Ui.Find<TextBlock>("PlanError").Visibility, Is.EqualTo(Visibility.Collapsed));
        Assert.That(Ui.Tree(view).OfType<ProgressBar>().Single().Visibility, Is.EqualTo(Visibility.Collapsed));
        Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanRefresh")), Is.EqualTo("最新の情報に更新"));
        Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanPublish")), Is.EqualTo("発行…"));
    }

    [Test]
    public async Task FailedSaveWhileClosingPublicationKeepsRetryAndEditingReachable()
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, holdQuery = "mutation PlanPublish(" }));
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("未入力 →")));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => File.Exists(Path.Combine(root, "held-gh.pid")));
        bool stopped = true;
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(async () => stopped = await view.StopAsync());
            Assert.That(stopped, Is.False);
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => {
                Assert.That(Ui.Find<Button>("PlanRetrySave").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(Ui.Find<Button>("PlanRefresh").IsEnabled, Is.True);
                Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanPublish")), Is.EqualTo("発行…"));
                Assert.That(Ui.Tree(view).OfType<ProgressBar>().Single().Visibility, Is.EqualTo(Visibility.Collapsed));
            });
        }
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanSaveFailure").IsOpen);
    }

    [Test]
    public async Task PublishSaveFailureOffersRetryBeforeAnyWrite()
    {
        await Open();
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("開始日")));
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
            await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => AssertFailure("PlanSaveFailure", "保存できませんでした", "保存を再試行"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanSaveFailure").IsOpen && Ui.Find<TextBlock>("PlanError").Text.Length == 0);
    }

    [Test]
    public async Task PublishShowsHierarchyPredecessorAndOrderStagesAndRemoteResults()
    {
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = state.Issues.Add(new(new("I2", "前提作業", "acme/repo"), "", true)), NextId = 3 });
        await Open();
        var newRow = PlanRow.New("子タスク", "acme/repo") with { Parent = "I1", Predecessors = ["I2"] };
        await workspace.Session!.Execute(new InsertPlanRows([newRow]), DateOnly.FromDateTime(DateTime.Today));
        await workspace.Session.Execute(new MovePlanRows(["I2"], "I1"), DateOnly.FromDateTime(DateTime.Today));
        var stages = new List<string>(); long callback = 0;
        await Ui.Run(() => {
            var progress = Ui.Find<TextBlock>("PlanPublishStage");
            callback = progress.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => stages.Add(progress.Text));
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("新規 Issue")));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "publish-review"));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Find<TextBlock>("PlanPublishStage").UnregisterPropertyChangedCallback(TextBlock.TextProperty, callback));
        var remote = FakePlanEditor.Load(root);
        Assert.That(remote.Issues.Single(i => i.Row.Title == "子タスク").Row.Parent, Is.EqualTo("I1"));
        Assert.That(remote.Issues.Single(i => i.Row.Title == "子タスク").Row.Predecessors, Is.EqualTo(new[] { "I2" }));
        Assert.That(remote.Issues[0].Row.Identity, Is.EqualTo("I2"));
        Assert.That(stages, Does.Contain("発行中: 親子関係").And.Contain("発行中: 先行タスク").And.Contain("発行中: 表示順"));
    }

    [TestCase("partial")]
    [TestCase("verificationfailure")]
    public async Task PublishFailureOrUnverifiedRowsRemainVisibleAndRetryReconciles(string fault)
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new { planEditor = true, workspace = true, planFault = fault }));
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "発行予定"; Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 発行予定")));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanPublishFailure").IsOpen && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Ready<Button>("PlanRetryPublish");
        await Ui.Run(() => {
            AssertFailure("PlanPublishFailure", "発行できませんでした", "再試行");
            Assert.That(string.Join(" ", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text)), Does.Contain(fault == "partial" ? "発行失敗" : "未検証"));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => {
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Title")), Does.Contain(fault == "partial" ? "Synthetic failure" : "未検証"));
        });
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        var writesBeforeRetry = FakePlanEditor.Load(root).MutationBatches;
        await Ui.Run(() => Ui.Click("PlanRetryPublish"));
        await Ui.Idle();
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(writesBeforeRetry));
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        Assert.That(workspace.Session!.Document.Sync.Failures, Is.Empty);
        Assert.That(workspace.Session.Document.Sync.Unverified, Is.Empty);
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("発行予定"));
    }

    [Test]
    public async Task IncompleteRefreshKeepsLocalValuesAndCanBeRetried()
    {
        await Open();
        var before = workspace.Session!.Document;
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"planFault\":\"readfailure\"}");
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen);
        await Ui.Ready<Button>("PlanRetryRefresh");
        await Ui.Run(() => AssertFailure("PlanRefreshFailure", "最新の情報に更新できませんでした", "再試行"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "t6-refresh-failure"));
        Assert.That(workspace.Session.Document, Is.EqualTo(before));
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "更新済み" } }] });
        await Ui.Run(() => Ui.Click("PlanRetryRefresh"));
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").Text == "更新済み");
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen, Is.False));
    }

    [Test]
    public async Task NewIssueReviewCanCloseDuringPublicationWithVisibleStages()
    {
        await Open();
        var stages = new List<string>(); long callback = 0;
        await Ui.Run(() => {
            var progress = Ui.Find<TextBlock>("PlanPublishStage");
            callback = progress.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => stages.Add(progress.Text));
            var input = Ui.Find<TextBox>("PlanCell0_Title"); input.Focus(FocusState.Programmatic); input.Text = "新しい計画";
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("新規 Issue")));
        Assert.That(FakePlanEditor.Load(root).Issues.Length, Is.EqualTo(1));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanPublishStage").Text.StartsWith("発行中:"));
        await Ui.Run(() => { Ui.Click("PlanPublishClose"); Ui.Click("PlanPublish"); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Find<TextBlock>("PlanPublishStage").UnregisterPropertyChangedCallback(TextBlock.TextProperty, callback));
        Assert.That(FakePlanEditor.Load(root).Issues.Count(i => i.Row.Title == "新しい計画" && i.Added), Is.EqualTo(1));
        Assert.That(stages, Does.Contain("発行中: 新規 Issue").And.Contain("発行中: フィールド・担当者").And.Contain("発行中: 検証"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "publish-result"));
    }

    [TestCase(false), TestCase(true)]
    public async Task ParentConflictResolutionKeepsReviewAndPublishAvailabilityConsistent(bool initiallySummary)
    {
        var initial = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, initial with { Issues = [initial.Issues[0],
            new(new("I2", "Child", "acme/repo") { Parent = "I4" }, "", true),
            new(new("I3", "Other", "acme/repo"), "", true),
            new(new("I4", "Previous parent", "acme/repo"), "", true)], NextId = 5 });
        await Open();
        var session = workspace.Session!;
        var today = DateOnly.FromDateTime(DateTime.Today);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Assignees, new[] { "U2" }),
            new("I2", PlanField.Parent, initiallySummary ? "I1" : null)]), today);
        var rows = session.Document.Baseline.Rows.Select(r => r.Identity == "I1" ? r with { Assignees = ["U3"] }
            : r.Identity == "I2" ? r with { Parent = initiallySummary ? null : "I1" } : r).ToImmutableArray();
        await session.AcceptRefresh(new(session.Document.Baseline with { Rows = rows }, ImmutableDictionary<string, string>.Empty, [], 0, 0), today);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanResolveI2_Parent_True"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanPublishBlockedReason").Text, Does.Contain("競合"));
            Ui.Click("PlanResolveI2_Parent_True");
        });
        await Ui.Until(() => session.Document.Sync.Conflicts.Length == 1
            && Ui.Find<Button>("PlanPublishConfirm").IsEnabled == !initiallySummary
            && Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanResolveI1_Assignees_True") == initiallySummary);
        await Ui.Run(() => {
            Assert.That(session.Document.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.Assignees));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.EqualTo(!initiallySummary));
            var reason = Ui.Find<TextBlock>("PlanPublishBlockedReason");
            Assert.That(reason.Text, initiallySummary ? Does.Contain("競合") : Is.Empty);
            Assert.That(reason.Visibility, Is.EqualTo(initiallySummary ? Visibility.Visible : Visibility.Collapsed));
            Assert.That(Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "PlanResolveI1_Assignees_True"), Is.EqualTo(initiallySummary));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RefreshMarksConflictsAndReviewResolvesWithoutWriting(bool useGitHub)
    {
        await Open();
        await Ui.Run(() => { var cell = Ui.Find<TextBox>("PlanCell1_Title"); cell.Focus(FocusState.Programmatic); cell.Text = "ローカル"; Ui.Click("PlanShowSettings"); });
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Title == "ローカル");
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "GitHub変更", Actual = 1 } }] });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.Length == 1);
        await Ui.Run(() => Ui.Click("PlanShowTasks"));
        await Ui.Until(() => AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Title")).Contains("競合"));
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PlanCell1_Title");
            cell.Focus(FocusState.Programmatic);
            var frame = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(cell));
            var critical = ((SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"]).Color;
            Assert.That(((SolidColorBrush)frame.BorderBrush).Color, Is.EqualTo(critical));
            Assert.That(frame.BorderThickness.Left, Is.InRange(1d, 2d));
            Assert.That(((SolidColorBrush)frame.Background).Color, Is.Not.EqualTo(((SolidColorBrush)Application.Current.Resources["SheetChangedBrush"]).Color));
            var corner = Ui.Tree(frame).OfType<Polygon>().Single();
            Assert.That(corner.Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(((SolidColorBrush)corner.Fill).Color, Is.EqualTo(critical));
            Assert.That(Ui.Find<TextBlock>("PlanStartReason").Text, Does.Contain("競合: GitHub では GitHub変更"));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "t6-conflict-cell-" + useGitHub));
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows[0].Actual, Is.EqualTo(1));
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Tree(view).OfType<Button>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == $"PlanResolveI1_Title_{useGitHub}"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "publish-conflict-" + useGitHub));
        await Ui.Run(() => { Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False); Ui.Click($"PlanResolveI1_Title_{useGitHub}"); });
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.IsEmpty);
        Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.EqualTo(useGitHub ? "GitHub変更" : "ローカル"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PendingSettingsSaveDoesNotDropTheNextCommand(bool switchProject)
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2");
        await Ui.Idle();
        await Settings();
        var previous = workspace.Session!;
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanProjectStart");
            input.Focus(FocusState.Programmatic);
            input.Text = "2026-10-20";
            RoutedEventHandler? nextCommand = null;
            nextCommand = async (_, _) => {
                input.LostFocus -= nextCommand;
                if (switchProject) {
                    Ui.Click("PlanProjectPicker");
                    await Ui.Ready<ListView>("RegisteredProjects");
                    Ui.Find<ListView>("RegisteredProjects").SelectedIndex = 0;
                }
                else Ui.Click("PlanShowTasks");
            };
            input.LostFocus += nextCommand;
            Ui.Find<SelectorBarItem>("PlanShowTasks").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => previous.Document.State.Settings.ProjectStart is not null);
        await Ui.Until(() => view.IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(previous.Document.State.Settings.ProjectStart, Is.EqualTo(new DateOnly(2026, 10, 20)));
            if (switchProject)
            {
                Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
                Assert.That(workspace.Selected.Id.NodeId, Is.EqualTo("P1"));
            }
            Assert.That(Ui.Find<ScrollViewer>("PlanSettingsScroll").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }
    [Test]
    public async Task RefreshWhileOnSettingsShowsNewPeopleAndColumnChoices()
    {
        await Open(); await Settings();
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with {
            Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Assignees = ["U2"] } }],
            AddedFields = ["StartNoEarlierThan"] });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.People.Any(p => p.Identity == "U2"));
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<NumberBox>().Any(n => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(n) == "PlanRateU2"), Is.True);
            Assert.That(Ui.Find<ComboBox>("PlanMapStartNoEarlierThan").Items.Cast<PlanColumnDefinition>().Any(c => c.Name == "開始日指定"), Is.True);
            Assert.That(Ui.Find<ScrollViewer>("PlanSettingsScroll").Visibility, Is.EqualTo(Visibility.Visible));
        });
    }
    [Test]
    public async Task ConnectListsPersonalAndOrganizationProjectsAndOneClickOpensMappedTasks()
    {
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => workspace.Available.Count == 2 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        string problem = "";
        await Ui.Run(() => problem = Ui.Find<TextBlock>("PlanError").Text);
        Assert.That(problem, Is.Empty);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("AvailableProjects").Items.Count, Is.EqualTo(2));
            Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
        });
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Until(() => Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo("開発計画"));
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>().Count(id => id.Length > 0), Is.EqualTo(1));
            Assert.That(workspace.Session!.Document.State.Settings.Columns.Length, Is.EqualTo(5));
            Assert.That(string.Join(" ", Ui.Tree(view).Select(t => t is TextBlock label ? label.Text : t is TextBox input ? input.Text : "")), Does.Contain("開始日").And.Contain("設計"));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "workspace"));
        await Settings();
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "settings"));
        await Ui.Run(() => { var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll"); scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSettingsScroll").VerticalOffset > 0);
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "settings-calendar-people"));
    }
    private void ResizeWorkspace(int width, int height)
    {
        var scale = Ui.Root.XamlRoot.RasterizationScale;
        Ui.Window.AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)));
        view.Width = width; view.Height = height;
    }
    private async Task OpenProjectPicker()
    {
        await Ui.Idle();
        if (pickerClosed is { } closingPicker) await closingPicker.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Ui.Run(() => Ui.Click("PlanProjectPicker"));
        try { await Ui.Ready<ListView>("RegisteredProjects"); }
        catch {
            await Ui.Diagnose("project picker", view);
            await Ui.Run(async () => { TestContext.Out.WriteLine("Picker error: " + Ui.Find<TextBlock>("PlanError").Text); await RenderedEvidence.Capture(view, "picker-failure"); });
            throw;
        }
    }
    private async Task PickerCommand(string id)
    {
        await OpenProjectPicker();
        await Ui.Run(() => Ui.Click(id));
        await Ui.Idle();
    }
    private async Task SelectRegistered(string project)
    {
        await OpenProjectPicker();
        await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == project));
    }
    private async Task Open()
    {
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => workspace.Available.Count == 2 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
            Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
        });
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Until(() => Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"));
        await Ui.Idle();
    }
    private async Task Settings()
    {
        await Ui.Run(() => Ui.Click("PlanShowSettings"));
        await Ui.Ready<ComboBox>("PlanMapEstimate");
        await Ui.Idle();
    }
    private async Task CommitText(string id, string value)
    {
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>(id);
            Assert.That(input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic), Is.True);
            input.Text = value;
        });
        await Ui.Run(() => Ui.Find<Button>("PlanShowSettings").Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
        await Ui.Idle();
    }
    [Test]
    public async Task SwitchingPreservesEachProjectsUnpublishedWorkAndRestartsTheSelectedProject()
    {
        await Open();
        await Ui.Run(async () => await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Title, "未発行の設計")]), new(2026, 10, 5)));
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2");
        await Ui.Idle();
        await SelectRegistered("P1");
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1");
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("1"));
        });
        var restarted = new PlanWorkspace(new(root));
        await restarted.Connect(new(FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
        Assert.That(restarted.Selected!.Id.NodeId, Is.EqualTo("P1"));
        Assert.That(restarted.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
    }
    [Test]
    public async Task ExplicitAddFieldButtonAddsAndMapsBothSchedulingFieldsWithoutImplicitWrites()
    {
        await Open();
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await Settings();
        await Ui.Run(() => Ui.Click("PlanAddFields"));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.Columns.Length == 7 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty);
            Assert.That(((GhProjectsBoards.Core.PlanEditor.PlanColumnDefinition)Ui.Find<ComboBox>("PlanMapFixed").SelectedItem).Name, Is.EqualTo("日程固定"));
            Assert.That(Ui.Tree(view).OfType<Button>().Any(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "PlanAddFields"), Is.False);
        });
        Assert.That(FakePlanEditor.Load(root).AddedFields.Length, Is.EqualTo(2));
    }
    [TestCase("rate", "2026-10-06")]
    [TestCase("company", "2026-10-06")]
    public async Task SettingChangesRecalculateAndOneUndoRestoresThePreviousDates(string setting, string expected)
    {
        await Open();
        await Ui.Run(async () => await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = new(2026, 10, 5) }), new(2026, 10, 5)));
        var history = workspace.Session!.UndoCount;
        await Settings();
        if (setting == "rate") await Ui.Run(() => Ui.Find<NumberBox>("PlanRateU1").Value = 50);
        else await AddDay("PlanCompanyDaysOff", new(2026, 10, 5));
        await Ui.Until(() => workspace.Session.UndoCount == history + 1);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session.Schedule(new(2026, 10, 5)).Single().End.Value!.Value.ToString("yyyy-MM-dd"), Is.EqualTo(expected));
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("PlanTasks")).OfType<TextBox>().Any(t => t.Text == PlanSheetView.DateText(DateOnly.Parse(expected))));
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.UndoCount == history);
        Assert.That(workspace.Session.Schedule(new(2026, 10, 5)).Single().End.Value, Is.EqualTo(new DateOnly(2026, 10, 5)));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
    [Test]
    public async Task HolidayImportRecalculatesAndSettingsFileRoundTripsThroughRealControls()
    {
        await Open();
        await Ui.Run(async () => await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = new(2026, 10, 5) }), new(2026, 10, 5)));
        var holidayPath = Path.Combine(root, "holidays.csv");
        var exported = Path.Combine(root, "settings.json");
        var preset = GhProjectsBoards.Core.Projects.PlanningContract.BundledHolidays();
        File.WriteAllText(holidayPath, "国民の祝日・休日月日,国民の祝日・休日名称" + Environment.NewLine +
            string.Join(Environment.NewLine, preset.Dates.Select(d => $"{d.Date:yyyy/M/d},{d.Name}").Append("2026/10/5,追加休日")));
        // Only the OS picker is substituted; file parsing, export, import and storage are real.
        await Ui.Run(() => view.PickFile = purpose => Task.FromResult<string?>(purpose == "holiday" ? holidayPath : exported));
        await Settings();
        await Ui.Run(() => Ui.Click("PlanHolidayImport"));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.ImportedHolidays is not null || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty));
        await Ui.Idle();
        Assert.That(workspace.Session!.Schedule(new(2026, 10, 5)).Single().End.Value, Is.EqualTo(new DateOnly(2026, 10, 6)));
        await Ui.Until(() => Ui.Find<Button>("PlanExportSettings").IsLoaded && Ui.Find<Button>("PlanExportSettings").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanExportSettings")); await Ui.Idle();
        Assert.That(File.Exists(exported), Is.True);
        var before = PlanJson.Text(workspace.Session.Document.State.Settings);
        await AddDay("PlanCompanyDaysOff", new(2026, 10, 6));
        await Ui.Run(() => Ui.Click("PlanImportSettings")); await Ui.Idle();
        Assert.That(PlanJson.Text(workspace.Session.Document.State.Settings), Is.EqualTo(before));
    }

    [Test]
    public async Task ClosingCommitsTheFocusedSettingsInputBeforeFlushing()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanProjectStart");
            input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            input.Text = "2026-10-07";
        });
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        var loaded = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 5));
        Assert.That(loaded.Session!.Document.State.Settings.ProjectStart, Is.EqualTo(new DateOnly(2026, 10, 7)));
        Assert.That(loaded.Session.UndoCount, Is.EqualTo(1));
    }
    [Test]
    public async Task InvalidSettingsKeepTheCurrentProjectAndInputUntilCorrected()
    {
        await Open(); await Settings();
        await CommitText("PlanProjectStart", "invalid");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Not.Empty);
        });
        await PickerCommand("PlanChooseProject");
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanProjectStart").Text, Is.EqualTo("invalid"));
            Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
            Assert.That(workspace.Session!.UndoCount, Is.Zero);
        });
        await CommitText("PlanProjectStart", "2026-10-07");
        await Ui.Until(() => workspace.Session!.Document.State.Settings.ProjectStart == new DateOnly(2026, 10, 7));
    }

    [Test]
    public async Task SettingsCountsDraftsAndPullRequestsWhileThePlanOnlyShowsIssues()
    {
        await Open(); await Settings();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanExcludedCounts").Text, Is.EqualTo("計画対象外  Draft 2 / Pull request 1 / 参照できない項目 2")));
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedReconnectHidesPriorProjectDataAndOffersConnectionAgain()
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"state\":\"notLoggedIn\"}");
        await PickerCommand("PlanConnection");
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanProjectPicker").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"), Is.False);
            Assert.That(workspace.Registered, Is.Empty);
            Assert.That(Ui.Find<Button>("PlanConnect").IsEnabled, Is.True);
        });
    }

    [TestCase("mapping"), TestCase("rate")]
    public async Task RejectedSettingsRestoreTheAcceptedControlValue(string kind)
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            if (kind == "rate") Ui.Find<NumberBox>("PlanRateU1").Value = 0;
            else {
                var combo = Ui.Find<ComboBox>("PlanMapEstimate");
                combo.SelectedItem = combo.Items.Cast<PlanColumnDefinition>().Single(c => c.Name == "Remaining");
            }
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session!.UndoCount, Is.Zero);
            if (kind == "rate") Assert.That(Ui.Find<NumberBox>("PlanRateU1").Value, Is.EqualTo(100));
            else Assert.That(((PlanColumnDefinition)Ui.Find<ComboBox>("PlanMapEstimate").SelectedItem).Name, Is.EqualTo("Estimate"));
        });
    }

    [Test]
    public async Task ClosingCommitsTheFocusedNativeRateInput()
    {
        await Open(); await Settings();
        await Ui.Run(() => {
            var rate = Ui.Find<NumberBox>("PlanRateU1");
            var input = Ui.Tree(rate).OfType<TextBox>().Single();
            input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            input.Text = "50";
        });
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        var loaded = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 5));
        Assert.That(loaded.Session!.Document.State.Settings.People.Single(p => p.Identity == "U1").Rate, Is.EqualTo(50));
        Assert.That(loaded.Session.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ReplacedSettingsControlsCannotChangeAnotherProject()
    {
        await Open(); await Settings();
        NumberBox oldRate = null!;
        await Ui.Run(() => oldRate = Ui.Find<NumberBox>("PlanRateU1"));
        await PickerCommand("PlanChooseProject");
        await Ui.Ready<ListView>("AvailableProjects");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected!.Id.NodeId == "P2"); await Ui.Idle();
        await Ui.Run(() => oldRate.Value = 50); await Ui.Idle();
        Assert.That(workspace.Session!.UndoCount, Is.Zero);
        Assert.That(workspace.People.Single(p => p.Identity == "U1").Rate, Is.EqualTo(100));
    }

    private async Task AddDay(string id, DateOnly day)
    {
        await Ui.Run(() => {
            Ui.Find<CalendarDatePicker>(id + "Date").Date = new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, TimeSpan.Zero);
            Ui.Click(id + "Add");
        });
        await Ui.Idle();
    }

    [TestCase(false), TestCase(true), Category("PlanWorkspaceReview")]
    public async Task SuccessfulChangeClearsPreviousSettingsSaveFailure(bool fromPeople)
    {
        await Open(); await Settings();
        var store = new PlanStore(root);
        var firstDay = new DateOnly(2026, 10, 7);
        var secondDay = new DateOnly(2026, 10, 8);
        using (var writer = new FileStream(store.FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await AddDay("PlanCompanyDaysOff", firstDay);
            await Ui.Ready<Button>("PlanRetrySave");
            await Ui.Run(() => {
                var failure = Ui.Find<InfoBar>("PlanSaveFailure");
                Assert.That(failure.IsOpen, Is.True);
                Assert.That(failure.Title, Is.EqualTo("保存できませんでした"));
                Assert.That(((Button)failure.ActionButton).Visibility, Is.EqualTo(Visibility.Visible));
            });
        }
        if (fromPeople) {
            await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
            await Ui.Run(() => {
                var input = Ui.Find<TextBox>("PeopleAllowance_U1"); input.Focus(FocusState.Programmatic); input.Text = "80";
                Ui.Click("PeopleNext");
            });
            await Ui.Idle();
        } else await AddDay("PlanCompanyDaysOff", secondDay);
        var stored = await store.LoadAsync(workspace.Session.Document.Project);
        Assert.That(stored.Checkpoint!.Document.State.Settings.CompanyDaysOff, Is.EqualTo(fromPeople ? new[] { firstDay } : new[] { firstDay, secondDay }));
        if (fromPeople) Assert.That(stored.Checkpoint.Document.State.Settings.People.Single(p => p.Identity == "U1").Allowance, Is.EqualTo(80));
        await Ui.Run(() => {
            var failure = Ui.Find<InfoBar>("PlanSaveFailure");
            Assert.That(failure.IsOpen, Is.False, "A successful later save must remove the obsolete failure.");
            Assert.That(((Button)failure.ActionButton).Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task SaveRetryRecoversPendingSettingsAndAllowsRefreshAndNormalClose()
    {
        await Open(); await Settings();
        var path = new PlanStore(root).FileFor(workspace.Selected!.Id);
        try
        {
            using (var writer = new FileStream(path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await CommitText("PlanProjectStart", "2026-10-07");
                await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen);
                await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.False));
            }
            await Ui.Run(() => Ui.Click("PlanRetrySave")); await Ui.Idle();
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty));
            var reopened = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 6));
            Assert.That(reopened.Session!.Document.State.Settings.ProjectStart, Is.EqualTo(new DateOnly(2026, 10, 7)));
            Assert.That(reopened.Session.UndoCount, Is.EqualTo(1));
            await Ui.Run(() => Ui.Click("PlanRefresh")); await Ui.Idle();
            await Ui.Run(async () => { Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty); Assert.That(await view.StopAsync(), Is.True); });
        }
        finally { await workspace.RetrySave(); }
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task FailedCatalogSaveKeepsSettingsAndSelectionOnTheOriginalProject()
    {
        await Open();
        await PickerCommand("PlanChooseProject");
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2"); await Ui.Idle();
        await SelectRegistered("P1");
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1"); await Ui.Idle();
        await Settings();
        var original = workspace.Session;
        using (var lockedCatalog = new FileStream(Path.Combine(workspace.Root, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await SelectRegistered("P2");
            await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen); await Ui.Idle();
            Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
            await OpenProjectPicker();
            await Ui.Run(() => {
                Assert.That(((ProjectChoice)Ui.Find<ListView>("RegisteredProjects").SelectedItem).Id.NodeId, Is.EqualTo("P1"));
                Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo("開発計画"));
            });
            await Ui.Run(() => Ui.Find<Button>("PlanProjectPicker").Flyout.Hide());
            await AddDay("PlanCompanyDaysOff", new(2026, 10, 7));
            Assert.That(original!.Document.State.Settings.CompanyDaysOff, Is.EqualTo(new[] { new DateOnly(2026, 10, 7) }));
        }
        await SelectRegistered("P2");
        await Ui.Until(() => workspace.Selected!.Id.NodeId == "P2"); await Ui.Idle();
        Assert.That(workspace.Session!.Document.State.Settings.CompanyDaysOff, Is.Empty);
        Assert.That(workspace.Session.UndoCount, Is.Zero);
    }

    [TestCase("connect"), TestCase("open"), TestCase("refresh"), TestCase("csv"), Category("PlanWorkspaceReview")]
    public async Task ClosingCancelsTheOwnedGhProcessBeforeItResponds(string stage)
    {
        if (stage == "open") {
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => workspace.Available.Count == 2); await Ui.Idle();
        }
        else if (stage is "refresh" or "csv") await Open();
        if (stage == "csv")
        {
            var csvPath = Path.Combine(root, "tasks.csv");
            File.WriteAllText(csvPath, "キー,タイトル,見積\na,A,8\n");
            await Ui.Run(() => view.PickFile = _ => Task.FromResult<string?>(csvPath));
        }
        var before = workspace.Session is { } session ? PlanJson.Text(session.Document) : null;
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdOperation = stage == "connect" ? "auth" : "none",
            holdQuery = stage == "connect" ? "none" : stage == "csv" ? "PlanCsvRepository" : "ProjectFields"
        }));
        await Ui.Run(() => {
            if (stage == "open") Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
            else if (stage != "csv") Ui.Click(stage == "connect" ? "PlanConnect" : "PlanRefresh");
        });
        if (stage == "csv") await Ui.ClickCommand("PlanSheetCsv");
        var marker = Path.Combine(root, "held-gh.pid");
        int pid = 0;
        await Ui.Until(() => File.Exists(marker) && int.TryParse(File.ReadAllText(marker), out pid));
        using var process = Process.GetProcessById(pid);
        Task<bool> stop = null!;
        await Ui.Run(() => { stop = view.StopAsync(); });
        var completedBeforeResponse = await Task.WhenAny(stop, Task.Delay(3000)) == stop;
        try { Assert.That(completedBeforeResponse, Is.True, "Close must cancel its gh process rather than await its delayed response."); }
        finally {
            File.WriteAllText(Path.Combine(root, "release-gh"), "release");
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(await stop, Is.True);
        if (stage == "refresh") await Ui.Run(() => {
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanRefresh")), Is.EqualTo("最新の情報に更新"));
            Assert.That(Ui.Tree(view).OfType<ProgressBar>().Single().Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title").IsReadOnly, Is.False);
        });
        process.Refresh(); Assert.That(process.HasExited, Is.True);
        if (before is not null) Assert.That(PlanJson.Text(workspace.Session!.Document), Is.EqualTo(before));
        else Assert.That(workspace.Session, Is.Null);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanWorkspaceReview")]
    public async Task ManyRegisteredProjectsScrollWithoutDisplacingCommandsAndKeepTitlesReadable()
    {
        var scope = new ConnectionScope("github.com", 42);
        var choices = Enumerable.Range(1, 40).Select(i => new ProjectChoice(new(scope, "P" + i), new(scope, "O1"),
            "fixture-user", "User", i, "https://github.com/users/fixture-user/projects/" + i,
            i == 1 ? "codex-sandbox" : "計画 " + i)).ToImmutableArray();
        Directory.CreateDirectory(workspace.Root);
        File.WriteAllText(Path.Combine(workspace.Root, "workspace.json"), PlanJson.Text(new PlanWorkspaceCatalog(1, choices, null)));
        await Ui.Run(() => { view.Width = 900; view.Height = 620; Ui.Click("PlanConnect"); });
        await Ui.Until(() => workspace.Registered.Count == 40); await Ui.Idle();
        await OpenProjectPicker();
        await Ui.Until(() => Ui.Find<ListView>("RegisteredProjects").ContainerFromIndex(0) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => {
            var open = Ui.Find<Button>("PlanChooseProject"); var connect = Ui.Find<Button>("PlanConnection");
            foreach (var command in new[] { open, connect }) {
                var bounds = command.TransformToVisual(view).TransformBounds(new Rect(0, 0, command.ActualWidth, command.ActualHeight));
                Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(view.ActualHeight));
            }
            var list = Ui.Find<ListView>("RegisteredProjects");
            Assert.That(Ui.Tree((ListViewItem)list.ContainerFromIndex(0)).OfType<TextBlock>().Any(t => t.Text == "codex-sandbox"), Is.True,
                "The title must have its own presentation, without an appended owner consuming the title line.");
            list.ScrollIntoView(list.Items[^1]);
        });
        await Ui.Until(() => {
            var list = Ui.Find<ListView>("RegisteredProjects");
            if (list.ContainerFromIndex(39) is not ListViewItem { IsLoaded: true } last) return false;
            var bounds = last.TransformToVisual(list).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
            return bounds.Top >= 0 && bounds.Bottom <= list.ActualHeight + 1;
        });
        await Ui.Run(async () => {
            var flyout = (Flyout)Ui.Find<Button>("PlanProjectPicker").Flyout;
            var content = (FrameworkElement)flyout.Content;
            var popup = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).Single(p => Ui.Tree(p.Child).Contains(content));
            // Evidence awaits rendering; unrelated desktop focus must not dismiss its target mid-capture.
            var dismiss = popup.IsLightDismissEnabled; popup.IsLightDismissEnabled = false;
            try {
                Assert.That(flyout.IsOpen && content.IsLoaded, Is.True);
                var list = Ui.Find<ListView>("RegisteredProjects"); var last = (ListViewItem)list.ContainerFromIndex(39);
                var bounds = last.TransformToVisual(list).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
                Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(list.ActualHeight + 1));
                await RenderedEvidence.Capture(content, "many-projects");
            } finally { popup.IsLightDismissEnabled = dismiss; }
        });
    }

    [TestCase("PlanCompanyDaysOff"), Category("PlanWorkspaceReview")]
    public async Task CalendarDateListsAddOnceRemoveAndUndoWithoutTypedDateFormats(string id)
    {
        await Open(); await Settings();
        var day = new DateOnly(2026, 10, 7);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>(id + "Add").IsEnabled, Is.False);
            Assert.That(Ui.Tree(view).OfType<FrameworkElement>().Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e).StartsWith("PlanDaysOff") || e is TextBlock { Text: "個人休日" }), Is.False);
            Ui.Find<CalendarDatePicker>(id + "Date").Date = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        });
        Assert.That(workspace.Session!.UndoCount, Is.Zero, "Selection alone must not change the calendar.");
        await AddDay(id, day); await AddDay(id, day);
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => {
            var dates = Ui.Find<ListView>(id + "Dates");
            Assert.That(dates.Items.Count, Is.EqualTo(1)); dates.SelectedIndex = 0;
            Ui.Click(id + "Remove");
        }); await Ui.Idle();
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(2));
        await Ui.Run(() => Ui.Click("PlanUndo")); await Ui.Idle();
        var reopened = await PlanSession.OpenAsync(new(root), workspace.Selected!.Id, new(2026, 10, 6));
        var settings = reopened.Session!.Document.State.Settings;
        Assert.That(settings.CompanyDaysOff, Is.EqualTo(new[] { day }));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

}
