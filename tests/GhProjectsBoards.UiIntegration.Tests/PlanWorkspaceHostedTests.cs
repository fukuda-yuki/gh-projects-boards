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
            finally { await workspace.Flush(); Directory.Delete(root, true); }
        }
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed) Assert.That(stopped, Is.True, problem);
        else TestContext.Out.WriteLine("Close after failed test: " + problem);
            }
        finally { Ui.EndTest(); }
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
