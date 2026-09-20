using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed partial class PlanningHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private string clipboard = "16";
    [Test]
    public async Task FreshProjectConfiguresMappingsAndWeightOnceThenEstimateCreatesDatesForItsNativeAssignee()
    {
        await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        project = project with { Snapshot = project.Snapshot with { Fields = project.Snapshot.Fields.Select(f => f.Id.NodeId == "F-Finish" ? f with { Name = "EndDate" } : f).ToArray() } };
        var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-first-plan-" + Guid.NewGuid())), work, 0);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true), readClipboard: () => Task.FromResult(clipboard)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_2");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.ClickCommand("GridPlanningSettings"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Find<TextBox>("PlanProjectStart", dialog).Text = "2026-10-05 09:00";
            Ui.Find<TextBox>("PlanCutoff", dialog).Text = "2026-10-09 18:00";
            foreach (var role in PlanningContract.Roles)
            {
                var box = Ui.Find<ComboBox>("PlanField-" + role, dialog);
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == "F-" + role);
            }
            Ui.Tree(dialog).OfType<Expander>().Single(e => (string)e.Header == "担当者・配賦").IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("PlanningDialog")!).OfType<CheckBox>().Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "PlanPerson-U1" && c.IsLoaded && c.IsEnabled));
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
            Ui.Toggle(Ui.Find<CheckBox>("PlanPerson-U1", dialog)); Ui.Find<TextBox>("PlanWeight-U1", dialog).Text = "50";
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Ui.Ready<TextBlock>("GridHeader6");
        await Ui.Run(() => {
            Assert.That(work.Planning("P1")!.Tasks, Is.Empty, "Project setup must not invent per-task owners or dates.");
            Assert.That(Ui.Tree(Ui.Find<Grid>("SheetHeader")).OfType<TextBlock>().Any(t => t.Text == "EndDate"), Is.True);
            Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard);
        });
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        clipboard = "8"; await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_6").Text == "2026-10-06");
        await Ui.Run(() => {
            Assert.That(work.PlanFor(project).Tasks[0].Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 18:00")));
            Assert.That(work.Planning("P1")!.Tasks.Single().OwnerId, Is.EqualTo("U1")); Assert.That(work.Journal, Is.Empty);
        });
    }

    [Test]
    public async Task DateCellPasteShowsManualBeforeCommitAndCalendarTimeControlsRetainMinutePrecision()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard)); await Ui.ClickCommand("GridPaste");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_5").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Start");
        clipboard = "2026-10-05 12:07"; await Ui.ClickCommand("GridPaste");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DateInputState").Text, Does.StartWith("日時を指定（入力中）"));
            Assert.That(session.Workspace.PlanFor(project).Tasks[0].Mode, Is.EqualTo(PlanningMode.Auto));
            Ui.Click("DateInputEditor");
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor")?.IsLoaded == true);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Assert.That(Ui.Find<RadioButtons>("ScheduleMethod", editor).SelectedIndex, Is.EqualTo(1));
            Ui.Find<CalendarDatePicker>("ScheduleFinish-Date", editor).Date = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.FromHours(9));
            Ui.Find<TimePicker>("ScheduleFinish-Time", editor).SelectedTime = new TimeSpan(16, 19, 0);
        });
        await Ui.Until(() => Ui.Find<TextBox>("ScheduleFinish", Ui.Popup<StackPanel>("SchedulingEditor")).Text == "2026-10-06 16:19");
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("ScheduleApply", Ui.Popup<StackPanel>("SchedulingEditor"))));
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
        await Ui.Run(() => {
            var result = session.Workspace.PlanFor(project).Tasks[0];
            Assert.That(result.Start, Is.EqualTo(PlanningContractTests.At("2026-10-05 12:07")));
            Assert.That(result.Finish, Is.EqualTo(PlanningContractTests.At("2026-10-06 16:19")));
            Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T1"));
        });
    }
    [Test]
    public async Task MappedActualAcceptsPendingTextWithoutChangingItsReportOrNumberProjection()
    {
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("GridCell0_4");
            Assert.That(cell.Focus(FocusState.Keyboard), Is.True);
            Assert.That(cell.IsReadOnly, Is.False, "Mapped Actual needs the typed report input route.");
            cell.Text = "7";
        });
        await Ui.Run(() => {
            var cell = session.Workspace.Open(project)[0].Cells.Single(c => c.Key?.FieldId == "F-Actual");
            Assert.That(session.Workspace.Buffer(cell), Is.EqualTo("7"));
            Assert.That(session.Workspace.Value(cell), Is.Null, "Typing alone cannot invent a worker, date or report.");
            Assert.That(session.Workspace.Planning("P1")!.Tasks[0].Actuals, Is.Null);
            Assert.That(cell.Editable, Is.False, "Generic NUMBER writes must remain prohibited.");
        });
    }
    [Test]
    public async Task ContextualActualUpdateAndNextRowPasteShareTheConfirmedDateAndRetainUndo()
    {
        await Ui.Unmount(grid);
        project = project with { Snapshot = project.Snapshot with { Issues = project.Snapshot.Issues.ToDictionary(p => p.Key, p => p.Value with {
            Native = p.Value.Native! with { Assignees = [new(new(project.Snapshot.Id.Scope, "U1"), "Owner")] } }) } };
        var w = session.Workspace; w.SetRegistrations([project]);
        w.CommitPlanning(project, w.Planning("P1")! with { Tasks = [w.Planning("P1")!.Tasks[0] with { Actuals = [new("U1", 5, new(2026, 10, 6))] }] }, w.Revision,
            [new("P1T1", "Remaining", "4")]);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true), readClipboard: () => Task.FromResult(clipboard)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_4");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_4").Focus(FocusState.Keyboard));
        await Ui.Until(() => Ui.Find<StackPanel>("ActualCellEditor").Visibility == Visibility.Visible);
        await Ui.Ready<CalendarDatePicker>("ActualReportedThrough");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("ActualInputHeading").Text, Does.Contain("過去の報告担当者を保持"));
            Ui.Find<TextBox>("GridCell0_4").Text = "7";
            Ui.Find<CalendarDatePicker>("ActualReportedThrough").Date = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(9));
            Ui.Click("ActualUpdate");
        });
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        clipboard = "9"; await Ui.ClickCommand("GridPaste");
        await Ui.Run(() => {
            Assert.That(Ui.Find<CalendarDatePicker>("ActualReportedThrough").Date?.Day, Is.EqualTo(13));
            Assert.That(Ui.Dialog("PlanningDialog"), Is.Null);
            Assert.That(w.Buffer(w.Open(project)[1].Cells[4]), Is.EqualTo("9")); Ui.Click("ActualUpdate");
        });
        await Ui.Run(() => {
            Assert.That(w.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Actuals![0], Is.EqualTo(new ActualContribution("U1", 7, new(2026, 10, 13))));
            Assert.That(w.Planning("P1")!.Tasks.Single(t => t.Id == "I2").Actuals![0], Is.EqualTo(new ActualContribution("U1", 9, new(2026, 10, 13))));
            Assert.That(w.Value(w.Open(project)[0].Cells[2]), Is.Null); Assert.That(w.Value(w.Open(project)[0].Cells[3]), Is.EqualTo("4"));
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => {
            Assert.That(w.Value(w.Open(project)[1].Cells[4]), Is.Null); Assert.That(w.Buffer(w.Open(project)[1].Cells[4]), Is.EqualTo("9"));
            Assert.That(w.Planning("P1")!.Tasks.Single(t => t.Id == "I1").Actuals![0].Hours, Is.EqualTo(7));
            Assert.That(w.Journal, Is.Empty);
        });
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_3").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Remaining");
        clipboard = "3"; await Ui.ClickCommand("GridPaste");
        await Ui.Run(() => {
            Assert.That(w.Value(w.Open(project)[0].Cells[3]), Is.EqualTo("3"));
            Assert.That(w.Value(w.Open(project)[0].Cells[4]), Is.EqualTo("7"));
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => {
            Assert.That(w.Value(w.Open(project)[0].Cells[3]), Is.EqualTo("4"));
            Assert.That(w.Value(w.Open(project)[0].Cells[4]), Is.EqualTo("7"));
        });
    }
    [Test]
    public async Task MultipleWorkerActualRequiresItsBreakdownAndCancelPreservesTheTypedTotal()
    {
        await Ui.Unmount(grid); var w = session.Workspace;
        var original = new[] { new ActualContribution("U1", 3, new(2026, 10, 6)), new ActualContribution(null, 2, new(2026, 10, 6)) };
        w.CommitPlanning(project, w.Planning("P1")! with { Tasks = [w.Planning("P1")!.Tasks[0] with { Actuals = original }] }, w.Revision);
        await Ui.Run(() => grid = new(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_4");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_4").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Actual");
        await Ui.Ready<Button>("ActualUpdate");
        await Ui.Run(() => { Ui.Find<TextBox>("GridCell0_4").Text = "7"; Assert.That(Ui.Find<Button>("ActualUpdate").IsEnabled, Is.False); Ui.Click("ActualDetails"); });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is { IsLoaded: true } e && Ui.Find<Button>("ActualReportsCancel", e).IsLoaded);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
            Ui.Find<TextBox>("ActualReportHours-U1", editor).Text = "5";
            Ui.Click(Ui.Find<Button>("ActualReportsCancel", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is null);
        await Ui.Run(() => {
            Assert.That(w.Planning("P1")!.Tasks[0].Actuals, Is.EqualTo(original));
            Assert.That(w.Buffer(w.Open(project)[0].Cells[4]), Is.EqualTo("7")); Ui.Click("ActualDetails");
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is { IsLoaded: true } e && Ui.Find<Button>("ActualReportsUpdate", e).IsLoaded);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("ActualReportsEditor")!;
            Ui.Find<TextBox>("ActualReportHours-U1", editor).Text = "5";
            Ui.Click(Ui.Find<Button>("ActualReportsUpdate", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("ActualReportsEditor") is null);
        await Ui.Run(() => {
            Assert.That(w.Value(w.Open(project)[0].Cells[4]), Is.EqualTo("7"));
            Assert.That(w.Planning("P1")!.Tasks[0].Actuals!.Single(r => r.PersonId is null), Is.EqualTo(original[1]));
        });
        await Ui.ClickCommand("GridUndo");
        await Ui.Run(() => {
            Assert.That(w.Planning("P1")!.Tasks[0].Actuals, Is.EqualTo(original));
            Assert.That(w.Buffer(w.Open(project)[0].Cells[4]), Is.EqualTo("7"));
        });
    }

    [Test]
    public async Task TaskDetailsClosureRetainsTheNextActualInputAndItsFocus()
    {
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            // Supply the next public control input as soon as the modal boundary ends.
            // Reading an earlier UIA element can hide its replacement during close.
            Ui.Dialog("PlanningDialog")!.Closed += (_, _) => {
                var input = Ui.Find<TextBox>("GridCell0_4"); input.Focus(FocusState.Keyboard); input.Text = "5";
            };
            Ui.DialogButton("PlanningDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("PlanningDialog") is null);
        await Task.Delay(400); // Inspect the input after the modal closing animation has settled.
        await Ui.Ready<TextBox>("GridCell0_4");
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("GridCell0_4");
            Assert.That(input.Text, Is.EqualTo("5"));
            Assert.That(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(input));
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[4]), Is.EqualTo("5"));
        });
    }

    [Test]
    public async Task ActualNativeF2TypingKeepsTheInputFocusedAndVisibleBeforeReportConfirmation()
    {
        await SheetNativeInput.Click("GridCell0_4");
        await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
        await SheetNativeInput.Press(Windows.System.VirtualKey.Number5);
        await Ui.Until(() => Ui.Find<TextBox>("GridCell0_4").Text == "5");
        await SheetNativeInput.Rendered();
        await Ui.Run(async () => {
            var input = Ui.Find<TextBox>("GridCell0_4");
            Assert.That(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(input));
            Assert.That(input.Text, Is.EqualTo("5"));
            await ApplyInformationEvidence.Capture(grid, "actual-native-pending-five");
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[4]), Is.Null);
        });
    }

    [SetUp]
    public async Task Setup()
    {
        project = PlanningAssignmentTests.Assigned("U1"); var work = new EditingWorkspace(project.Snapshot.Id.Scope);
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
            var retained = w.Planning("P1")!.Tasks.Single(t => t.Id == "I2");
            Assert.That(retained.Assignment?.Legacy, Is.True, "The versioned upgrade explicitly retains old owner semantics.");
            Assert.That(retained with { Assignment = null }, Is.EqualTo(manual));
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
        await Ui.Ready<TextBox>("GridCell1_2");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell1_2").Focus(FocusState.Keyboard));
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
        clipboard = "4"; await Ui.ClickCommand("GridPaste");
        await Ui.Until(() => Ui.Find<TextBox>("GridCell1_2").Text == "4");
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
        await Ui.Run(() => {
            var dialog = Ui.Dialog("PlanningDialog")!;
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
        await Ui.ClickCommand("GridTaskDetails"); await Ui.DialogReady("PlanningDialog");
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
            Assert.That(Ui.Find<TextBox>("GridCell0_6").IsReadOnly, Is.False);
            Assert.That(session.Workspace.Open(project)[0].Cells[6].Editable, Is.False, "Date input routes through typed planning, never a generic DATE write.");
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
        await Ui.ClickCommand("GridPlanning"); await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor")?.IsLoaded == true);
        await Ui.Run(() => {
            var editor = Ui.Popup<StackPanel>("SchedulingEditor")!;
            Ui.Find<TextBox>("ScheduleStart", editor).Text = "2026-10-05 12:07";
            Assert.That(Ui.Find<RadioButtons>("ScheduleMethod", editor).SelectedIndex, Is.EqualTo(1), "Direct entry immediately shows the manual method.");
            Ui.Click(Ui.Find<Button>("ScheduleApply", editor));
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("SchedulingEditor") is null);
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
