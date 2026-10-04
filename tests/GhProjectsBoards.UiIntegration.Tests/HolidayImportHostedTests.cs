using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class HolidayImportHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration project = null!;
    private readonly TaskCompletionSource<HolidayImportFile?> picker = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DateTime At(string value) => PlanningContractTests.At(value);
    [SetUp]
    public async Task Setup()
    {
        project = PlanningPathTests.Registration(); var work = new EditingWorkspace(project.Snapshot.Id.Scope); work.SetRegistrations([project]);
        var plan = PlanningPathTests.Plan();
        var pinned = plan.Calendar.Holidays with { Version = "prior-calendar", Dates = plan.Calendar.Holidays.Dates.Where(day => day.Date != new DateOnly(2026, 10, 12)).ToArray() };
        work.CommitPlanning(project, plan with { Start = At("2026-10-12 09:00"),
            Calendar = plan.Calendar with { Holidays = pinned, Exceptions = [new(new(2026, 10, 13), "U1", [new(600, 720)])] }, Tasks = [
                new("I1", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-06 16:19"), Actuals: [new("U1", 5, new(2026, 10, 6))]),
                new("I2", PlanningMode.Auto, "U1")] }, work.Revision, [new("P1T2", "Estimate", "8")]);
        work.CaptureBaseline(project, work.Revision, null, DateTimeOffset.UtcNow);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-holiday-import-" + Guid.NewGuid().ToString("N"))), work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)) { PickHolidayCsv = () => picker.Task });
        await Ui.Mount(grid); await Ui.ClickCommand("GridPlanningSettings"); await Ui.Ready<Button>("PlanSettingsSave");
        await Ui.Run(() => Ui.Tree(Ui.Find<Grid>("PlanningSettingsPage")).OfType<Expander>().Single(section => (string)section.Header == "カレンダー・祝日").IsExpanded = true);
        await Ui.Ready<Button>("PlanImportHolidays");
    }
    [TearDown]
    public async Task Teardown()
    {
        picker.TrySetResult(null); await Ui.Unmount(grid); Assert.That(await session.FlushAsync(), Is.True); await Ui.Idle();
    }

    [TestCase("adopt"), TestCase("cancel"), TestCase("save-unselected")]
    public async Task ImportedPreviewNeedsExplicitAdoptionAndSaveAndRetainsProtectedWork(string action)
    {
        var before = session.Workspace.Planning("P1")!;
        var history = session.Workspace.Snapshot().History.Length;
        var protectedBaseline = System.Text.Json.JsonSerializer.Serialize(before.Summary!.Baseline);
        picker.SetResult(new("syukujitsu.csv", HolidayCsvImportTests.OfficialBytes()));
        await Ui.Run(() => Ui.Click("PlanImportHolidays"));
        await Ui.Until(() => Ui.Find<CheckBox>("PlanAdoptImportedHolidays").Visibility == Visibility.Visible);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("HolidayImportStatus").Text, Does.Contain("2025–2027年").And.Contain("変更1日"));
            Assert.That(Ui.Find<CheckBox>("PlanAdoptImportedHolidays").IsChecked, Is.False);
            Assert.That(session.Workspace.Planning("P1")!.Calendar.Holidays.Version, Is.EqualTo("prior-calendar"));
            if (action != "save-unselected") Ui.Find<CheckBox>("PlanAdoptImportedHolidays").IsChecked = true;
            Assert.That(session.Workspace.Snapshot().History.Length, Is.EqualTo(history));
        });
        await Ui.Run(async () => await ApplyInformationEvidence.Capture(grid, "holiday-import-preview-" + action));
        await Ui.Run(() => Ui.Click(action == "cancel" ? "PlanSettingsCancel" : "PlanSettingsSave"));
        await Ui.Until(() => !grid.PlanningSettingsOpen);
        await Ui.Run(() => {
            var after = session.Workspace.Planning("P1")!;
            Assert.That(after.Calendar.Holidays.Version, action == "adopt" ? Does.StartWith("csv-2025-2027-") : Is.EqualTo("prior-calendar"));
            var task = after.Tasks.Single(task => task.Id == "I1");
            Assert.That(task.ManualStart, Is.EqualTo(before.Tasks[0].ManualStart)); Assert.That(task.ManualFinish, Is.EqualTo(before.Tasks[0].ManualFinish));
            Assert.That(task.Actuals, Is.EqualTo(before.Tasks[0].Actuals));
            Assert.That(after.Calendar.Exceptions.Single().Intervals, Is.EqualTo(before.Calendar.Exceptions.Single().Intervals));
            Assert.That(System.Text.Json.JsonSerializer.Serialize(after.Summary!.Baseline), Is.EqualTo(protectedBaseline));
            Assert.That(session.Workspace.PlanFor(project).Tasks.Single(task => task.Id == "I2").Finish,
                Is.EqualTo(At(action == "adopt" ? "2026-10-14 16:00" : "2026-10-12 18:00")));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
        if (action == "adopt")
        {
            await Ui.ClickCommand("GridUndo");
            await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Calendar.Holidays.Version, Is.EqualTo("prior-calendar")));
        }
    }

    [TestCase("picker-cancel"), TestCase("invalid"), TestCase("late")]
    public async Task CancelledInvalidOrLatePickerCannotAdoptCalendar(string outcome)
    {
        var before = System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot());
        if (outcome != "late") picker.SetResult(outcome == "picker-cancel" ? null : new("broken.csv", System.Text.Encoding.UTF8.GetBytes("date,name\n")));
        await Ui.Run(() => Ui.Click("PlanImportHolidays"));
        if (outcome == "invalid") await Ui.Until(() => Ui.Find<TextBlock>("HolidayImportStatus").Text.Contains("内閣府形式"));
        await Ui.Run(() => Ui.Click("PlanSettingsCancel")); await Ui.Until(() => !grid.PlanningSettingsOpen);
        if (outcome == "late") picker.SetResult(new("syukujitsu.csv", HolidayCsvImportTests.OfficialBytes()));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(System.Text.Json.JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before)));
    }
}
