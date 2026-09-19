using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class PlanningHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private string clipboard = "16";
    [SetUp]
    public async Task Setup()
    {
        project = PlanningPathTests.Registration(); var work = new EditingWorkspace(project.Snapshot.Id.Scope);
        work.SetRegistrations([project]);
        work.CommitPlanning(project, PlanningPathTests.Plan() with { Tasks = [new("I1", PlanningMode.Auto, "U1")] }, work.Revision);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-planning-ui-" + Guid.NewGuid().ToString("N"))), work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true), readClipboard: () => Task.FromResult(clipboard)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_2"); await Ui.Idle();
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => { Ui.Dialog("PlanningDialog")?.Hide(); Ui.Dialog("PlanningBatchDialog")?.Hide(); Ui.Dialog("ConflictDialog")?.Hide(); }); await Ui.Unmount(grid);
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle();
    }
    [Test]
    public async Task ExplicitHolidayAdoptionReplansAutoAndRetainsManualExceptionsActualsAndPendingText()
    {
        await Ui.Unmount(grid);
        var w = session.Workspace; var old = w.Planning("P1")!;
        var manual = new PlanningTask("I2", PlanningMode.Manual, "U1", PlanningContractTests.At("2026-10-05 12:07"),
            PlanningContractTests.At("2026-10-06 16:19"), Actuals: [new("U1", 5, new(2026, 10, 6))]);
        var exception = new CalendarException(new(2026, 10, 13), "U1", [new(600, 720)]);
        var pinned = old.Calendar.Holidays with { Version = "controlled-earlier-preset", Dates = old.Calendar.Holidays.Dates.Where(d => d.Date != new DateOnly(2026, 10, 12)).ToArray() };
        w.CommitPlanning(project, old with { Start = PlanningContractTests.At("2026-10-12 09:00"),
            Calendar = old.Calendar with { Holidays = pinned, Exceptions = [exception] }, Tasks = [old.Tasks[0], manual] }, w.Revision,
            [new("P1T1", "Estimate", "8"), new("P1T2", "Estimate", "16"), new("P1T2", "Remaining", "3")]);
        var pending = w.Open(project)[0].Cells[2]; w.SetBuffer(pending, "24未確定");
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_2");
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            Assert.That(w.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-12 18:00")));
            Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "カレンダー・祝日").IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<CheckBox>().Any(c =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanAdoptHolidays" && c.IsLoaded));
        await Ui.Run(() => {
            Assert.That(Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<TextBlock>().Any(t => t.Text.Contains("変更日: 2026-10-12")), Is.True);
            Ui.Find<CheckBox>("PlanAdoptHolidays", Ui.Dialog("PlanningDialog")).IsChecked = true;
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            w = session.Workspace;
            Assert.That(w.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-14 16:00")));
            Assert.That(w.Planning("P1")!.Tasks.Single(t => t.Id == "I2"), Is.EqualTo(manual));
            Assert.That(w.Planning("P1")!.Calendar.Exceptions.Single().Intervals, Is.EqualTo(exception.Intervals));
            Assert.That(w.Buffer(pending), Is.EqualTo("24未確定")); Assert.That(w.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Calendar.Holidays.Version, Is.EqualTo(pinned.Version)));
    }
    [Test]
    public async Task UnavailablePlanningFieldCanBeCanceledThroughComparisonAndUndoneWithItsPendingText()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1" && grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridPaste");
        await Ui.Unmount(grid);
        var w = session.Workspace; var key = w.Open(project)[0].Cells[2].Key!; w.SetBuffer(w.Open(project)[0].Cells[2], "未確定");
        var missing = project with { RetrievedAt = project.RetrievedAt.AddMinutes(1), Snapshot = project.Snapshot with {
            Fields = project.Snapshot.Fields.Where(f => f.Id.NodeId != "F-Estimate").ToArray(),
            Items = project.Snapshot.Items.Select(i => i with { Values = i.Values.Where(v => v.FieldId?.NodeId != "F-Estimate").ToArray() }).ToArray() } };
        w.Reconcile(project, missing); w.SetRegistrations([missing]); project = missing;
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.ClickCommand("GridConflicts"); await Ui.DialogReady("ConflictDialog");
        await Ui.Run(() => Ui.Find<ComboBox>("ConflictField", Ui.Dialog("ConflictDialog")).SelectedIndex =
            w.Fields.Where(f => f.Conflict || f.Observation?.Reason is not null).ToList().FindIndex(f => f.Key == key));
        await Ui.Run(() => {
            var cancel = Ui.Find<Button>("ConflictCancelUnavailable", Ui.Dialog("ConflictDialog"));
            Assert.That(cancel.Visibility, Is.EqualTo(Visibility.Visible)); Ui.Click(cancel);
        });
        await Ui.Until(() => Ui.Dialog("ConflictDialog") is null);
        await Ui.Run(() => { w = session.Workspace; Assert.That(w.Fields.Single(f => f.Key == key).Change, Is.Null); Assert.That(w.Fields.Single(f => f.Key == key).Buffer, Is.Null); });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => {
            Assert.That(w.Fields.Single(f => f.Key == key).Change?.Value, Is.EqualTo("16"));
            Assert.That(w.Fields.Single(f => f.Key == key).Buffer, Is.EqualTo("未確定")); Assert.That(w.Journal, Is.Empty);
        });
    }
    [Test]
    public async Task SelectedUnplannedRowUsesBatchPreviewAndOneUndo()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell1_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        await Ui.ClickCommand("GridPaste");
        await Ui.ClickCommand("GridInitializePlans"); await Ui.DialogReady("PlanningBatchDialog");
        await Ui.Run(() => Ui.Find<ComboBox>("PlanBatchOwner", Ui.Dialog("PlanningBatchDialog")).SelectedIndex = 1);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanBatchPreview", Ui.Dialog("PlanningBatchDialog")).Items.Cast<string>().Single(), Does.Contain("2026-10-06 18:00"));
            Ui.DialogButton("PlanningBatchDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningBatchDialog") is null);
        await Ui.Run(() => Assert.That(session.Workspace.PlanFor(project).Tasks.Single(t => t.Id == "I2").Mode, Is.EqualTo(PlanningMode.Auto)));
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => Assert.That(session.Workspace.PlanFor(project).Tasks.Single(t => t.Id == "I2").Mode, Is.EqualTo(PlanningMode.Unplanned)));
    }
    [Test]
    public async Task NativeSettingsReplanAutoAndDependencySelectorUsesTheWholePlan()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1" && grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridPaste");
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            foreach (var e in Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().ToArray()) e.IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<TextBox>().Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanWeight-U1" && c.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<TextBox>("PlanWeight-U1", dialog).Text = "80";
            Ui.Find<CheckBox>("PlanIgnoreHolidays", dialog).IsChecked = true;
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => Assert.That(session.Workspace.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-07 13:00"))));
        await Ui.Run(() => Ui.Find<TextBox>("GridCell1_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        clipboard = "4"; await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell1_2").Text == "4");
        await Ui.ClickCommand("GridPlanning"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<ComboBox>("PlanMode", dialog).SelectedIndex = 1;
            Ui.Find<ComboBox>("PlanOwner", dialog).SelectedIndex = 1;
            Ui.Tree(dialog).OfType<Expander>().Single(e => (string)e.Header == "先行Issue（終了→開始）").IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<ListView>().Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanPredecessors" && c.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!; var list = Ui.Find<ListView>("PlanPredecessors", dialog);
            list.SelectedItems.Add(list.Items[0]); Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            var plan = session.Workspace.PlanFor(project);
            Assert.That(plan.Tasks.Single(t => t.Id == "I2").Start, Is.EqualTo(PlanningContractTests.At("2026-10-07 14:00")), System.Text.Json.JsonSerializer.Serialize(plan));
            Assert.That(plan.Inputs!.Single(t => t.Task.Id == "I2").Predecessors.Single().PredecessorId, Is.EqualTo("I1"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }
    [Test]
    public async Task WeeklyReportEditsIndependentRemainingAndActualAttributionThroughNativeControls()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == "2026-10-06");
        await Ui.ClickCommand("GridPlanning"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<Expander>().Single(e => (string)e.Header == "工数・進捗・実績").IsExpanded = true);
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<ComboBox>().Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanProgress" && c.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<ComboBox>("PlanProgress", dialog).SelectedIndex = 1;
            Ui.Find<TextBox>("PlanWork-Remaining", dialog).Text = "3";
            Ui.Find<TextBox>("PlanActualStart", dialog).Text = "2026-10-05 09:00";
            Ui.Tree(dialog).OfType<Expander>().Single(e => (string)e.Header == "累積実績・担当者別内訳").IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<TextBox>().Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanActualHours-U1" && c.IsLoaded));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<CheckBox>("PlanReportsEnabled", dialog).IsChecked = true;
            Ui.Find<TextBox>("PlanActualHours-U1", dialog).Text = "5";
            Ui.Find<TextBox>("PlanReportedThrough-U1", dialog).Text = "2026-10-05";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            var plan = session.Workspace.PlanFor(project);
            Assert.That(plan.Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-05 12:00")));
            var row = session.Workspace.Open(project)[0];
            Assert.That(session.Workspace.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Estimate")), Is.EqualTo("16"));
            Assert.That(session.Workspace.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Remaining")), Is.EqualTo("3"));
            Assert.That(session.Workspace.Value(row.Cells.Single(c => c.Key?.FieldId == "F-Actual")), Is.EqualTo("5"));
            Assert.That(plan.Inputs![0].Task.Actuals!.Single().ReportedThrough, Is.EqualTo(new DateOnly(2026, 10, 5)));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Until(() => session.Workspace.PlanFor(project).Tasks[0].Finish == PlanningContractTests.At("2026-10-06 18:00"));
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks[0].Actuals, Is.Null));
    }
    [Test]
    public async Task PasteRendersAutoDatesAndUndoRestoresInputAndDerivedDatesTogether()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => session.Workspace.DifferenceCount > 0);
        await Ui.Run(() => Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("16"),
            System.Text.Json.JsonSerializer.Serialize(session.Workspace.Fields.Where(f => f.Change is not null))));
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == "2026-10-06");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("GridCell0_6").IsReadOnly, Is.True);
            Assert.That(session.Workspace.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_2").Text == "" && Ui.Find<TextBox>("GridCell0_6").Text == "");
        Assert.That(session.Workspace.DifferenceCount, Is.Zero);
    }
    [Test]
    public async Task PendingInputDoesNotRecalculateAndManualSaveRetainsTheOtherEndpoint()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T1" && grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == "2026-10-06");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "24");
        await Ui.Run(() => Assert.That(session.Workspace.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00"))));
        await Ui.ClickCommand("GridPlanning"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanTaskStart", Ui.Dialog("PlanningDialog")).Text = "2026-10-05 12:07";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Run(() => {
            var task = session.Workspace.Planning("P1")!.Tasks.Single();
            Assert.That(task.Mode, Is.EqualTo(PlanningMode.Manual));
            Assert.That(task.ManualStart, Is.EqualTo(PlanningContractTests.At("2026-10-05 12:07")));
            Assert.That(task.ManualFinish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("24"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }
}
