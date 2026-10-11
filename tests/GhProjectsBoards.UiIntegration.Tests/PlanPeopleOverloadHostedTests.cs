using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanPeopleOverload")]
internal sealed class PlanPeopleOverloadHostedTests
{
    private string root = null!;
    private PlanSession session = null!;
    private PlanPeopleView view = null!;
    private static readonly DateOnly Day = new(2026, 10, 5);

    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghpb-people-" + Guid.NewGuid().ToString("N"));
        var rows = Enumerable.Range(1, 3).Select(i => new PlanRow("I" + i, "作業" + i, "acme/repo") {
            Assignees = ["U1"], Estimate = 8, Remaining = 8, Actual = 0, StartNoEarlierThan = i == 3 ? Day.AddDays(1) : Day
        }).ToImmutableArray();
        var document = new PlanDocument(new(new("github.com", 1), "P1"), new(rows, []), new(rows, new() {
            StatusDate = Day, People = [new("U1", "alice", 100, null, []), new("U2", "bob", 100, null, [])]
        }));
        session = await PlanSession.CreateAsync(new(root), document, Day);
        await Ui.Run(() => view = new(session) { Width = 1280, Height = 720 });
        await Ui.Mount(view);
        await Ui.Ready<ComboBox>("PeopleScale");
    }

    [TearDown]
    public async Task Cleanup()
    {
        try {
            if (view is not null) await Ui.Unmount(view, check: false);
            if (session is not null) await session.FlushAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        } finally { Ui.EndTest(); }
    }

    [Test, Category("PlanSheetNative")]
    public async Task EmptyCalculatedRemainingCommitEndsEditingBeforeRefreshAndFlush()
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [
            new("I1", PlanField.Estimate, 40m), new("I1", PlanField.Actual, 8m),
            new("I1", PlanField.Remaining, null)]), Day);
        var undo = session.UndoCount;
        await Ui.Run(() => { view.Refresh(); Ui.Click("PeopleLoadOpen_U1_0"); });
        await Ui.Ready<TextBox>("PeopleTask_I1_Remaining");
        await SheetNativeInput.Click("PeopleTask_I1_Remaining");
        await Ui.Run(() => Ui.Find<TextBox>("PeopleTask_I1_Remaining").Text = "");
        await SheetNativeInput.Press(Windows.System.VirtualKey.Enter);
        await Ui.Idle();
        await Ui.Run(() => view.FlushInput());
        await Ui.Run(() => view.Refresh());
        await Ui.Idle();
        await Ui.Run(() => view.FlushInput());
        Assert.That(session.Document.State.Rows[0].Remaining, Is.Null);
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PeopleTask_I1_Remaining").Text, Is.EqualTo("32")));
    }

    [Test]
    public async Task ClosedTaskRetainedInDetailsShowsPlainRemainingWithSharedReadOnlyReason()
    {
        await Ui.Run(() => Ui.Click("PeopleLoadOpen_U1_0"));
        await Ui.Ready<TextBox>("PeopleTask_I1_Actual");
        await Ui.Run(() => Ui.Find<TextBox>("PeopleTask_I1_Actual").Text = "1");
        var baseline = session.Document.Baseline;
        var closed = baseline.Rows[0] with { Closed = true, CloseDate = Day, Remaining = -5 };
        await session.AcceptRefresh(new(baseline with { Rows = baseline.Rows.SetItem(0, closed) },
            ImmutableDictionary<string, string>.Empty, [], 0, 0), Day);
        await Ui.Run(() => view.Refresh());
        await Ui.Ready<TextBlock>("PeopleTask_I1_Remaining");
        await Ui.Run(() => {
            var value = Ui.Find<TextBlock>("PeopleTask_I1_Remaining");
            Assert.That(value.Text, Is.EqualTo("0"));
            const string reason = "完了したタスクは Issue のクローズで確定します（編集不可）";
            Assert.That(PlanOperations.ReadOnlyReason(closed, false, PlanField.Remaining), Is.EqualTo(reason));
            Assert.That(ToolTipService.GetToolTip(value), Is.EqualTo(reason));
            Assert.That(AutomationProperties.GetHelpText(value), Is.EqualTo(reason));
            Assert.That(AutomationProperties.GetName(value), Does.Contain("残").And.Contain("0"));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Select(AutomationProperties.GetAutomationId),
                Does.Not.Contain("PeopleTask_I1_Remaining"));
        });
        await Ui.Run(() => view.FlushInput());
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(-5));
    }

    [TestCase("Enter", true), TestCase("Tab", true), TestCase("Leave", true)]
    [TestCase("Escape", false), TestCase("Open", false)]
    [Category("PlanSheetNative")]
    public async Task CalculatedDetailRemainingUsesStoredBaselineOnlyDuringEditing(string exit, bool explicitValue)
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [
            new("I1", PlanField.Estimate, 40m), new("I1", PlanField.Actual, 8m),
            new("I1", PlanField.Remaining, null)]), Day);
        var undo = session.UndoCount;
        await Ui.Run(() => view.Refresh());
        await Ui.Ready<Button>("PeopleLoadOpen_U1_0");
        await Ui.Run(() => Ui.Click("PeopleLoadOpen_U1_0"));
        await Ui.Ready<TextBox>("PeopleTask_I1_Remaining");
        if (exit != "Open") await SheetNativeInput.Click("PeopleTask_I1_Remaining");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PeopleTask_I1_Remaining").Text, Is.EqualTo("32")));
        if (exit == "Enter") await SheetNativeInput.Press(Windows.System.VirtualKey.Enter);
        else if (exit == "Tab") await SheetNativeInput.Press(Windows.System.VirtualKey.Tab);
        else if (exit == "Escape") {
            await SheetNativeInput.Press(Windows.System.VirtualKey.Escape);
            await Ui.Idle();
        }
        if (exit is "Leave" or "Escape")
            await Ui.Run(() => Ui.Find<ComboBox>("PeopleScale").Focus(FocusState.Programmatic));
        await Ui.Idle();
        await Ui.Run(() => view.FlushInput());
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(explicitValue ? 32m : (decimal?)null));
            Assert.That(session.UndoCount, Is.EqualTo(undo + (explicitValue ? 1 : 0)));
            var cell = Ui.Find<TextBox>("PeopleTask_I1_Remaining");
            Assert.That(cell.Text, Is.EqualTo("32"));
            Assert.That(((SolidColorBrush)cell.Foreground).Color, Is.EqualTo(((SolidColorBrush)Application.Current.Resources[
                explicitValue ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"]).Color));
        });
    }

    [TestCase(false), TestCase(true)]
    public async Task DetailsShowEffectiveRemainingWithCalculatedOrEnteredText(bool entered)
    {
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [
            new("I1", PlanField.Estimate, 40m), new("I1", PlanField.Actual, 8m),
            new("I1", PlanField.Remaining, entered ? 32m : null)]), Day);
        await Ui.Run(() => view.Refresh());
        await Ui.Ready<Button>("PeopleLoadOpen_U1_0");
        await Ui.Run(() => Ui.Click("PeopleLoadOpen_U1_0"));
        await Ui.Ready<TextBox>("PeopleTask_I1_Remaining");
        await Ui.Run(() => {
            var cell = Ui.Find<TextBox>("PeopleTask_I1_Remaining");
            Assert.That(cell.Text, Is.EqualTo("32"));
            Assert.That(((SolidColorBrush)cell.Foreground).Color, Is.EqualTo(((SolidColorBrush)Application.Current.Resources[
                entered ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"]).Color));
        });
        Assert.That(session.Document.State.Rows[0].Remaining, Is.EqualTo(entered ? 32m : (decimal?)null));
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task AverageShowsDailyWarningAndDateDrillDownCorrectsOnlyTheSelectedTask(int scale)
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PeopleScale").SelectedIndex = scale);
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PeopleLoad_U1_0");
        await Ui.Run(() => {
            var label = Ui.Find<TextBlock>("PeopleLoad_U1_0");
            Assert.That(label.Text, Does.Contain("日超過1日").And.Contain("最大200%"));
            Assert.That(label.IsTextTrimmed, Is.False);
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PeopleLoadOpen_U1_0")), Does.Contain(label.Text));
            Ui.Click("PeopleLoadOpen_U1_0");
        });
        await Ui.Ready<Button>("PeopleOverloadDay_U1_20261005");
        await Ui.Run(() => Ui.Click("PeopleOverloadDay_U1_20261005"));
        await Ui.Idle();
        await Ui.Ready<ComboBox>("PeopleTask_I1_Assignees");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<ComboBox>().Select(AutomationProperties.GetAutomationId),
                Does.Contain("PeopleTask_I1_Assignees").And.Contain("PeopleTask_I2_Assignees").And.Not.Contain("PeopleTask_I3_Assignees"));
            var choice = Ui.Find<ComboBox>("PeopleTask_I2_Assignees");
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Tag == "U2");
        });
        await Ui.Until(() => session.Document.State.Rows[1].Assignees.SequenceEqual(["U2"]));
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PeopleLoad_U1_0");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PeopleLoad_U1_0").Text, Does.Not.Contain("日超過")));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        Assert.That(session.Document.State.Rows[0].Assignees, Is.EqualTo(new[] { "U1" }));
        await session.Undo(Day);
        await Ui.Run(() => view.Refresh());
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PeopleLoad_U1_0");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PeopleLoad_U1_0").Text, Does.Contain("日超過1日")));
    }
}
