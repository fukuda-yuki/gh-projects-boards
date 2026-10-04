using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class SummaryHostedTests
{
    private EditCell RemainingCell(string row = "P1T2") => session.Workspace.Open(project)
        .Single(r => r.ItemId == row).Cells.Single(c => c.Key?.FieldId == "F-Remaining");

    private Task SelectSummaryPerson(string id) => Ui.Run(() => {
        var people = Ui.Find<ListView>("SummaryPeople");
        people.SelectedItem = people.Items.Cast<PersonSummary>().Single(p => p.Id == id);
    });

    private async Task ManualSummaryTask()
    {
        await Ui.Run(() => {
            var work = session.Workspace; var plan = work.Planning("P1")!;
            work.CommitPlanning(project, plan with { Tasks = plan.Tasks.Select(t => t.Id == "I2" ? t with {
                Mode = PlanningMode.Manual, ManualStart = new(2026, 10, 5, 10, 17, 0), ManualFinish = new(2026, 10, 8, 16, 43, 0)
            } : t).ToArray() }, work.Revision);
        });
    }

    private void AssertManualDates()
    {
        var task = session.Workspace.Planning("P1")!.Tasks.Single(t => t.Id == "I2");
        Assert.That(task.Mode, Is.EqualTo(PlanningMode.Manual));
        Assert.That(task.ManualStart, Is.EqualTo(new DateTime(2026, 10, 5, 10, 17, 0)));
        Assert.That(task.ManualFinish, Is.EqualTo(new DateTime(2026, 10, 8, 16, 43, 0)));
    }

    [Test, Category("SummaryContinuity")]
    public async Task SummaryEffortActionUsesDailyUpdateAndReturnsToItsTaskAfterCancelCommitAndUndo()
    {
        await ManualSummaryTask(); await Open(); await SelectSummaryPerson("B");
        await Ui.ClickCommand("SummaryEdit"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            Assert.That(Ui.Dialog("PlanningDialog"), Is.Null);
            Ui.Find<TextBox>("DailyActual", Ui.Dialog("DailyProgressDialog")).Text = "60";
            Ui.Find<TextBox>("DailyRemaining", Ui.Dialog("DailyProgressDialog")).Text = "8";
            Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            Assert.That(grid.CurrentProjectView, Is.EqualTo(ProjectView.Summary));
            Assert.That(grid.SummaryPersonId, Is.EqualTo("B"));
            Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2"));
        });
        await Ui.ClickCommand("SummaryEdit"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            Ui.Find<TextBox>("DailyActual", Ui.Dialog("DailyProgressDialog")).Text = "60";
            Ui.Find<TextBox>("DailyRemaining", Ui.Dialog("DailyProgressDialog")).Text = "8";
            Ui.DialogButton("DailyProgressDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null); await Ui.Idle();
        await Ui.Run(() => {
            var summary = Ui.Find<SummaryView>("SummaryView");
            Assert.That(summary.AdoptedSummary!.People.Single(p => p.Id == "B").Forecast.Hours, Is.EqualTo(68));
            Assert.That(summary.SelectedRowId, Is.EqualTo("P1T2"));
            Assert.That(grid.SummaryPersonId, Is.EqualTo("B"));
            AssertManualDates(); Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("SummaryUndo");
        await Ui.Run(() => {
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            Assert.That(session.Workspace.Planning("P1")!.Tasks.Single(t => t.Id == "I2").Actuals!.Single().Hours, Is.EqualTo(56));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            AssertManualDates(); Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test, Category("SummaryContinuity")]
    public async Task SummaryRemainingCandidateSurvivesRefreshPersonAndViewChangesAndSavedReopen()
    {
        await Open(); await SelectSummaryPerson("B");
        await Ui.Run(() => {
            Ui.Find<TextBox>("SummaryRemaining").Text = "-";
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.EqualTo("-"), "The existing field buffer owns unfinished Summary input.");
            Ui.Find<CalendarDatePicker>("ActualReportedThrough").Date = DateTimeOffset.Now.AddDays(-1);
            Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("-"));
        });
        await SelectSummaryPerson("A");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("72")));
        await SelectSummaryPerson("B");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("-")));
        await Ui.ClickCommand("SummaryBoards"); await Ui.Ready<TextBox>("GridCell1_0");
        await Open();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("-"));
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
        });
        await Ui.Unmount(grid); Assert.That(await session.FlushAsync(), Is.True);
        var record = (await new DraftStore(root).LoadAsync(session.Workspace.Scope))!;
        session = new(new DraftStore(root), EditingWorkspace.Restore(record), record.Revision);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_0");
        await Open(); await SelectSummaryPerson("B");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("-"));
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    [Test, Category("SummaryContinuity")]
    public async Task SummaryRemainingEnterValidatesAndCommitsWhileEscapeCancelsOnlyItsCandidate()
    {
        await ManualSummaryTask(); await Open(); await SelectSummaryPerson("B");
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("SummaryRemaining"); input.Focus(FocusState.Keyboard); input.Text = "-";
        });
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => Ui.Find<InfoBar>("SummaryOperationStatus").IsOpen);
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("SummaryOperationStatus").IsOpen, Is.True);
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("-"));
            WriteRemainingInputState("invalid Enter");
            Ui.Find<TextBox>("SummaryRemaining").Text = "8";
            WriteRemainingInputState("corrected input");
        });
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Run(() => WriteRemainingInputState("valid Enter dispatched"));
        await Ui.Until(() => session.Workspace.Value(RemainingCell()) == "8");
        await Ui.Run(() => {
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("8"));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            Assert.That(Ui.Find<InfoBar>("SummaryOperationStatus").IsOpen, Is.False);
            Assert.That(Ui.Find<SummaryView>("SummaryView").AdoptedSummary!.People.Single(p => p.Id == "B").Headroom, Is.EqualTo(16));
            AssertManualDates();
        });
        await Ui.ClickCommand("SummaryUndo");
        await Ui.Run(() => {
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            var input = Ui.Find<TextBox>("SummaryRemaining"); input.Focus(FocusState.Keyboard); input.Text = "12";
        });
        await SheetNativeInput.Press(VirtualKey.Escape);
        await Ui.Until(() => session.Workspace.Buffer(RemainingCell()) is null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("40"));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            AssertManualDates(); Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    private void WriteRemainingInputState(string operation)
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject;
        var focusId = focused is null ? "none" : Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(focused);
        TestContext.Out.WriteLine($"{operation}: focus={focusId}; text={Ui.Find<TextBox>("SummaryRemaining").Text}; "
            + $"buffer={session.Workspace.Buffer(RemainingCell())}; value={session.Workspace.Value(RemainingCell())}; "
            + $"problem={Ui.Find<InfoBar>("SummaryOperationStatus").Message}");
    }

    [Test, Category("SummaryIme")]
    public async Task PhysicalJapaneseImeConfirmationKeepsRemainingCandidateWithoutNumericCommit()
    {
        await ManualSummaryTask(); await Open(); await SelectSummaryPerson("B");
        var compositionStarted = false; var compositionEnded = false;
        await SheetNativeInput.Click("SummaryRemaining");
        await Ui.Run(() => {
            var input = Ui.Find<TextBox>("SummaryRemaining");
            input.TextCompositionStarted += (_, _) => compositionStarted = true;
            input.TextCompositionEnded += (_, _) => compositionEnded = true;
        });
        try
        {
            await SheetNativeInput.Press(VirtualKey.A, VirtualKey.Control);
            await SheetNativeInput.Press((VirtualKey)0x16); // VK_IME_ON enters the installed native IME.
            foreach (var key in new[] { VirtualKey.N, VirtualKey.I, VirtualKey.H, VirtualKey.O, VirtualKey.N, VirtualKey.G, VirtualKey.O })
            {
                await SheetNativeInput.Press(key);
                await SheetNativeInput.Rendered();
            }
            await Ui.Until(() => Ui.Find<TextBox>("SummaryRemaining").Text == "にほんご");
            await Ui.Run(() => {
                Assert.That(compositionStarted, Is.True, "Physical keys must start native composition, not insert Unicode text.");
                Assert.That(compositionEnded, Is.False);
                Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
                Assert.That(Ui.Find<InfoBar>("SummaryOperationStatus").IsOpen, Is.False);
            });
            await SheetNativeInput.Press(VirtualKey.Space);
            await Ui.Until(() => Ui.Find<TextBox>("SummaryRemaining").Text == "日本語");
            await SheetNativeInput.Press(VirtualKey.Enter);
            await Ui.Until(() => compositionEnded && session.Workspace.Buffer(RemainingCell()) == "日本語");
            await Ui.Run(() => {
                WriteRemainingInputState("physical IME confirmation Enter");
                Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
                Assert.That(Ui.Find<InfoBar>("SummaryOperationStatus").IsOpen, Is.False,
                    "The IME confirmation key must not also run numeric validation or commit.");
                Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2"));
                AssertManualDates(); Assert.That(session.Workspace.Journal, Is.Empty);
            });
            await SheetNativeInput.Press(VirtualKey.Escape);
            await Ui.Until(() => session.Workspace.Buffer(RemainingCell()) is null);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("40"));
                Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
                Assert.That(Ui.Find<InfoBar>("SummaryOperationStatus").IsOpen, Is.False);
                WriteRemainingInputState("Escape cancels confirmed IME candidate");
            });
        }
        finally { await SheetNativeInput.Press((VirtualKey)0x1A); } // VK_IME_OFF restores direct input.
    }

    [Test, Category("SummaryHiddenEffort")]
    public async Task SummaryDailyUpdateReadsHiddenEffortFieldsAndPreservesTheirPendingInputOnCancel()
    {
        await ManualSummaryTask(); await Ui.Unmount(grid);
        await Ui.Run(() => {
            var work = session.Workspace; var columns = work.PrepareColumns(project);
            work.SaveColumns(columns with { Columns = columns.Columns.Select(c =>
                c.Id.FieldId is "F-Actual" or "F-Remaining" ? c with { Visible = false } : c).ToArray() });
            work.SetBuffer(RemainingCell(), "12");
            grid = new EditingGrid(project, session, () => Task.FromResult(true));
        });
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_0");
        await Open(); await SelectSummaryPerson("B");
        await Ui.ClickCommand("SummaryEdit"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            var actual = Ui.Find<TextBox>("DailyActual", Ui.Dialog("DailyProgressDialog"));
            var remaining = Ui.Find<TextBox>("DailyRemaining", Ui.Dialog("DailyProgressDialog"));
            Assert.That(actual.Text, Is.EqualTo("56")); Assert.That(actual.IsReadOnly, Is.False);
            Assert.That(remaining.Text, Is.EqualTo("12")); Assert.That(remaining.IsReadOnly, Is.False);
            remaining.Text = "8"; Ui.DialogButton("DailyProgressDialog", "CloseButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.EqualTo("12"));
            Assert.That(Ui.Find<TextBox>("SummaryRemaining").Text, Is.EqualTo("12"));
        });
        await Ui.ClickCommand("SummaryEdit"); await Ui.DialogReady("DailyProgressDialog");
        await Ui.Run(() => {
            Ui.Find<TextBox>("DailyActual", Ui.Dialog("DailyProgressDialog")).Text = "60";
            Ui.Find<TextBox>("DailyRemaining", Ui.Dialog("DailyProgressDialog")).Text = "8";
            Ui.DialogButton("DailyProgressDialog", "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog("DailyProgressDialog") is null); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("8"));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            Assert.That(session.Workspace.Columns(project).Hidden("F-Actual"), Is.True);
            Assert.That(session.Workspace.Columns(project).Hidden("F-Remaining"), Is.True);
            Assert.That(Ui.Find<SummaryView>("SummaryView").SelectedRowId, Is.EqualTo("P1T2"));
            AssertManualDates(); Assert.That(session.Workspace.Journal, Is.Empty);
        });
        await Ui.ClickCommand("SummaryUndo");
        await Ui.Run(() => {
            Assert.That(session.Workspace.Value(RemainingCell()), Is.EqualTo("40"));
            Assert.That(session.Workspace.Buffer(RemainingCell()), Is.Null);
            AssertManualDates();
        });
    }
}
