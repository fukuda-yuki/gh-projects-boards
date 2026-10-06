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
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;
[TestFixture, NonParallelizable, Category("PlanWorkspace")]
internal sealed class PlanWorkspaceHostedTests
{
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
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-workspace-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        FakePlanEditor.Save(root, new([new(new("I1", "設計", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true)], 2) { Drafts = 2, PullRequests = 1 });
        workspace = new(new(root));
        await Ui.Run(() => view = new(workspace, (_, host) => new(FakeExecutable, host,
            new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }))));
        await Ui.Mount(view);
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
            try { await Ui.Unmount(view, check: false); await Ui.Idle(); }
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
    public async Task PeopleAllowancesStayWithTheSelectedProject()
    {
        await Open(); await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => { var input = Ui.Find<TextBox>("PeopleAllowance_U1"); input.Focus(FocusState.Programmatic); input.Text = "80"; Ui.Click("PlanChooseProject"); });
        await Ui.Until(() => workspace.Session!.Document.State.Settings.People.Any(p => p.Allowance == 80));
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Session!.Document.Project.NodeId == "P2");
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.Empty);
            Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == "P1");
        });
        await Ui.Until(() => workspace.Session!.Document.Project.NodeId == "P1");
        await Ui.Run(() => Ui.Click("PlanShowPeople")); await Ui.Ready<TextBox>("PeopleAllowance_U1");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PeopleAllowance_U1").Text, Is.EqualTo("80")));
    }

    [Test]
    public async Task PeopleAllowanceEntryKeepsTheNextCellFocusedAcrossRecalculation()
    {
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = [new("U1", "alice", 100, null, []), new("U2", "bob", 100, null, [])] }), DateOnly.FromDateTime(DateTime.Today));
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
            People = [new("U1", "alice", 100, null, []), new("U2", "bob", 100, null, []), new("U3", "carol", 100, null, [])] }), DateOnly.FromDateTime(DateTime.Today));
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
    [TestCase(PlanField.Assignees, "bob")]
    [TestCase(PlanField.Assignees, "担当者なし")]
    [TestCase(PlanField.Fixed, "固定")]
    public async Task PeopleTaskEditsShareTheSheetOperationAndUndo(PlanField field, string value)
    {
        await Open();
        var day = new DateOnly(2026, 10, 5);
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Start, day), new("I1", PlanField.End, day)]), day);
        await workspace.Session.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = day,
            People = [new("U1", "alice", 100, 80, []), new("U2", "bob", 100, 80, [])] }), day);
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
                Assert.That(box.Items.OfType<ComboBoxItem>().Select(i => i.Content), Does.Contain("担当者なし").And.Contain("bob"));
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(i => i.Content.ToString() == value);
            } else { var box = Ui.Find<TextBox>("PeopleTask_I1_" + field); box.Focus(FocusState.Programmatic); box.Text = value; Ui.Click("PeoplePeriod_0"); }
        });
        await Ui.Until(() => workspace.Session.UndoCount == undo + 1);
        var changed = workspace.Session.Document.State.Rows[0];
        if (field == PlanField.Assignees) Assert.That(changed.Assignees, Is.EqualTo(value == "bob" ? new[] { "U2" } : Array.Empty<string>()));
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
            StatusDate = new(2026, 10, 5), People = [new("U1", "alice", 50, 80, [])] }), new(2026, 10, 5));
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
        await Open();
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with {
            People = Enumerable.Range(1, 20).Select(i => new PlanResource("U" + i, "person-" + i, 100, 80, [])).ToImmutableArray() }), DateOnly.FromDateTime(DateTime.Today));
        await Ui.Run(() => { view.Width = 1280; view.Height = 720; Ui.Click("PlanShowPeople"); });
        await Ui.Until(() => Ui.Tree(view).OfType<FrameworkElement>().Any(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == "PeopleRow_multiple"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "people-day-1280x720"));
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
            Ui.Find<ComboBox>("PeopleScale").SelectedIndex = 1;
        });
        await Ui.Until(() => Ui.Find<Button>("PeoplePeriod_0").Content.ToString()!.Contains("週"));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "people-week-1280x720"));
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
            Assert.That(text, Does.Contain("先行タスク  計画外Issue（未取得） → 未入力"));
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
        await Ui.Until(() => FakePlanEditor.Load(root).Issues[0].Row.Title == "設計の変更");
        await Ui.Until(() => workspace.Session!.Changes(DateOnly.FromDateTime(DateTime.Today)).TaskCount == 0);
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => { Ui.Click("PlanPublishClose"); Ui.Click("PlanUndo"); });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 1 タスク");
        Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("設計の変更"));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 1 タスク")));
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
            await Ui.Run(() => {
                Assert.That(Ui.Find<Button>("PlanRetrySave").Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(Ui.Find<Button>("PlanRefresh").IsEnabled, Is.True);
            });
        }
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => Ui.Find<Button>("PlanRetrySave").Visibility == Visibility.Collapsed);
    }

    [Test]
    public async Task PublishSaveFailureOffersRetryBeforeAnyWrite()
    {
        await Open();
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("Start date")));
        using (var writer = new FileStream(new PlanStore(root).FileFor(workspace.Session!.Document.Project) + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0 && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
            await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanRetrySave").Visibility, Is.EqualTo(Visibility.Visible)));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => Ui.Find<Button>("PlanRetrySave").Visibility == Visibility.Collapsed && Ui.Find<TextBlock>("PlanError").Text.Length == 0);
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
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0 && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => {
            Assert.That(string.Join(" ", Ui.Tree(view).OfType<TextBlock>().Select(t => t.Text)), Does.Contain(fault == "partial" ? "発行失敗" : "未検証"));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => {
            Assert.That(Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(Ui.Find<TextBox>("PlanCell1_Title")), Does.Contain(fault == "partial" ? "Synthetic failure" : "未検証"));
        });
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        await Ui.Run(() => Ui.Click("PlanPublish"));
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
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        Assert.That(workspace.Session.Document, Is.EqualTo(before));
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        var state = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, state with { Issues = [state.Issues[0] with { Row = state.Issues[0].Row with { Title = "更新済み" } }] });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell1_Title").Text == "更新済み");
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
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text == "競合"));
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
        await Ui.Run(() => Ui.Click("PlanChooseProject"));
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2");
        await Ui.Idle();
        await Settings();
        var previous = workspace.Session!;
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("PlanProjectStart");
            input.Focus(FocusState.Programmatic);
            input.Text = "2026-10-20";
            input.LostFocus += (_, _) => {
                if (switchProject) Ui.Find<ListView>("RegisteredProjects").SelectedIndex = 0;
                else Ui.Click("PlanShowTasks");
            };
            Ui.Find<Button>("PlanShowTasks").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => previous.Document.State.Settings.ProjectStart is not null);
        await Ui.Until(() => view.IsEnabled);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(previous.Document.State.Settings.ProjectStart, Is.EqualTo(new DateOnly(2026, 10, 20)));
            if (switchProject)
            {
                Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
                Assert.That(((ProjectChoice)Ui.Find<ListView>("RegisteredProjects").SelectedItem).Id, Is.EqualTo(workspace.Selected.Id));
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
            Assert.That(string.Join(" ", Ui.Tree(view).Select(t => t is TextBlock label ? label.Text : t is TextBox input ? input.Text : "")), Does.Contain("Start date").And.Contain("設計"));
        });
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "workspace"));
        await Settings();
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "settings"));
        await Ui.Run(() => { var scroll = Ui.Find<ScrollViewer>("PlanSettingsScroll"); scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSettingsScroll").VerticalOffset > 0);
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "settings-calendar-people"));
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
        await Ui.Run(() => Ui.Click("PlanChooseProject"));
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2");
        await Ui.Idle();
        await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1");
        await Ui.Run(() => {
            Assert.That(workspace.Session!.Document.State.Rows.Single().Title, Is.EqualTo("未発行の設計"));
            Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo("未発行 1 タスク"));
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
        });
        Assert.That(FakePlanEditor.Load(root).AddedFields.Length, Is.EqualTo(2));
    }
    [TestCase("rate", "2026-10-06")]
    [TestCase("company", "2026-10-06")]
    [TestCase("personal", "2026-10-06")]
    public async Task SettingChangesRecalculateAndOneUndoRestoresThePreviousDates(string setting, string expected)
    {
        await Open();
        await Ui.Run(async () => await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = new(2026, 10, 5) }), new(2026, 10, 5)));
        var history = workspace.Session!.UndoCount;
        await Settings();
        if (setting == "rate") await Ui.Run(() => Ui.Find<NumberBox>("PlanRateU1").Value = 50);
        else await AddDay(setting == "company" ? "PlanCompanyDaysOff" : "PlanDaysOffU1", new(2026, 10, 5));
        await Ui.Until(() => workspace.Session.UndoCount == history + 1);
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(workspace.Session.Schedule(new(2026, 10, 5)).Single().End.Value!.Value.ToString("yyyy-MM-dd"), Is.EqualTo(expected));
            Ui.Click("PlanShowTasks");
        });
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("PlanTasks")).OfType<TextBox>().Any(t => t.Text == expected));
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
            Ui.Click("PlanChooseProject");
        });
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
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanExcludedCounts").Text, Is.EqualTo("計画対象外  Draft 2 / Pull request 1")));
        Assert.That(workspace.Session!.Document.State.Rows.Length, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedReconnectHidesPriorProjectDataAndOffersConnectionAgain()
    {
        await Open();
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"state\":\"notLoggedIn\"}");
        await Ui.Run(() => Ui.Click("PlanConnection"));
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo("GitHub Projects"));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "設計"), Is.False);
            Assert.That(Ui.Find<ListView>("RegisteredProjects").Items, Is.Empty);
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
        await Ui.Run(() => { oldRate = Ui.Find<NumberBox>("PlanRateU1"); Ui.Click("PlanChooseProject"); });
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
                await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
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
        await Ui.Run(() => Ui.Click("PlanChooseProject"));
        await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P2"); await Ui.Idle();
        await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedIndex = 0);
        await Ui.Until(() => workspace.Selected?.Id.NodeId == "P1"); await Ui.Idle();
        await Settings();
        var original = workspace.Session;
        using (var lockedCatalog = new FileStream(Path.Combine(workspace.Root, "workspace.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == "P2"));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0); await Ui.Idle();
            Assert.That(workspace.Selected!.Id.NodeId, Is.EqualTo("P1"));
            await Ui.Run(() => {
                Assert.That(((ProjectChoice)Ui.Find<ListView>("RegisteredProjects").SelectedItem).Id.NodeId, Is.EqualTo("P1"));
                Assert.That(Ui.Find<TextBlock>("OpenProjectName").Text, Is.EqualTo("開発計画"));
            });
            await AddDay("PlanCompanyDaysOff", new(2026, 10, 7));
            Assert.That(original!.Document.State.Settings.CompanyDaysOff, Is.EqualTo(new[] { new DateOnly(2026, 10, 7) }));
        }
        await Ui.Run(() => Ui.Find<ListView>("RegisteredProjects").SelectedItem = workspace.Registered.Single(p => p.Id.NodeId == "P2"));
        await Ui.Until(() => workspace.Selected!.Id.NodeId == "P2"); await Ui.Idle();
        Assert.That(workspace.Session!.Document.State.Settings.CompanyDaysOff, Is.Empty);
        Assert.That(workspace.Session.UndoCount, Is.Zero);
    }

    [TestCase("connect"), TestCase("open"), TestCase("refresh"), Category("PlanWorkspaceReview")]
    public async Task ClosingCancelsTheOwnedGhProcessBeforeItResponds(string stage)
    {
        if (stage == "open") {
            await Ui.Run(() => Ui.Click("PlanConnect"));
            await Ui.Until(() => workspace.Available.Count == 2); await Ui.Idle();
        }
        else if (stage == "refresh") await Open();
        var before = workspace.Session is { } session ? PlanJson.Text(session.Document) : null;
        File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
            planEditor = true, workspace = true, holdOperation = stage == "connect" ? "auth" : "none",
            holdQuery = stage == "connect" ? "none" : "ProjectFields"
        }));
        await Ui.Run(() => {
            if (stage == "open") Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0;
            else Ui.Click(stage == "connect" ? "PlanConnect" : "PlanRefresh");
        });
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
        await Ui.Until(() => Ui.Find<ListView>("RegisteredProjects").ContainerFromIndex(39) is ListViewItem { IsLoaded: true });
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("RegisteredProjects"); var last = (ListViewItem)list.ContainerFromIndex(39);
            var bounds = last.TransformToVisual(list).TransformBounds(new Rect(0, 0, last.ActualWidth, last.ActualHeight));
            Assert.That(bounds.Top, Is.GreaterThanOrEqualTo(0)); Assert.That(bounds.Bottom, Is.LessThanOrEqualTo(list.ActualHeight + 1));
            await RenderedEvidence.Capture(view, "many-projects");
        });
    }

    [TestCase("PlanCompanyDaysOff"), TestCase("PlanDaysOffU1"), Category("PlanWorkspaceReview")]
    public async Task CalendarDateListsAddOnceRemoveAndUndoWithoutTypedDateFormats(string id)
    {
        await Open(); await Settings();
        var day = new DateOnly(2026, 10, 7);
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>(id + "Add").IsEnabled, Is.False);
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
        Assert.That(id == "PlanCompanyDaysOff" ? settings.CompanyDaysOff : settings.People.Single(p => p.Identity == "U1").DaysOff, Is.EqualTo(new[] { day }));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

}
