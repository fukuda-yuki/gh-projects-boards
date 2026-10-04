using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    private async Task MountWeeklyBulk(bool sourceEdited = false, bool missingWorker = false, bool nativeClipboard = false)
    {
        await Ui.Unmount(grid);
        var fixture = WeeklyBulkEditingTests.Work(3); project = fixture.Project;
        if (missingWorker)
        {
            project = project with { Snapshot = project.Snapshot with { Issues = project.Snapshot.Issues.ToDictionary(pair => pair.Key,
                pair => pair.Key.NodeId == "I2" ? pair.Value with { Native = pair.Value.Native! with { Assignees = [] } } : pair.Value) } };
            fixture.Work.SetRegistrations([project]);
        }
        if (sourceEdited) fixture.Work.CommitActualInput(project, "P1T1", "7", new(2026, 10, 13), "U1", fixture.Work.Revision);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-weekly-bulk-ui-" + Guid.NewGuid().ToString("N"))), fixture.Work, 0);
        await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true),
            readClipboard: nativeClipboard ? null : () => Task.FromResult(clipboard)));
        await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell0_4");
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("ActualReportedThrough").Date = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(9)));
    }

    [Test, Category("WeeklyBulk")]
    public async Task NativeWeeklyRectanglePasteUsesTheSharedReportingDateAndOneUndo()
    {
        GhProjectsBoards.E2E.Tests.NativeClipboardScope saved = null!;
        await Ui.Run(() => saved = new());
        try
        {
            await MountWeeklyBulk(nativeClipboard: true);
            var history = session.Workspace.Snapshot().History.Length;
            await SheetNativeInput.Click("GridCell0_3");
            await SheetNativeInput.Click("GridCell2_4", VirtualKey.Shift);
            await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("3行・6セル"));
            await Ui.Run(() => GhProjectsBoards.E2E.Tests.NativeClipboardScope.WriteTestFormats("3\t7\n2\t9\n1\t8"));

            await Ui.ClickCommand("GridPaste");

            await Ui.Until(() => CellText("GridCell2_4") == "8");
            await Ui.Run(() => {
                Assert.That(new[] { CellText("GridCell0_3"), CellText("GridCell1_3"), CellText("GridCell2_3") }, Is.EqualTo(new[] { "3", "2", "1" }));
                Assert.That(new[] { CellText("GridCell0_4"), CellText("GridCell1_4"), CellText("GridCell2_4") }, Is.EqualTo(new[] { "7", "9", "8" }));
                Assert.That(session.Workspace.Planning("P1")!.Tasks.Select(task => task.Actuals!.Single().ReportedThrough), Is.All.EqualTo(new DateOnly(2026, 10, 13)));
                Assert.That(session.Workspace.Snapshot().History, Has.Length.EqualTo(history + 1));
                Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・6セル"));
            });
            await Ui.ClickCommand("GridUndo");
            await Ui.Until(() => CellText("GridCell0_4") == "5" && CellText("GridCell2_4") == "2");
            await Ui.Run(() => Assert.That(new[] { CellText("GridCell0_3"), CellText("GridCell1_3"), CellText("GridCell2_3") }, Is.All.EqualTo("4")));
        }
        finally { await Ui.Run(() => saved.Dispose()); }
    }

    [TestCase("down"), TestCase("fill"), Category("WeeklyBulk")]
    public async Task ActualCopyDownAndFillKeepEachWorkerAndRestoreDestinationsBeforeTheSource(string route)
    {
        await MountWeeklyBulk(sourceEdited: true);
        await SheetNativeInput.Click("GridCell0_4");
        if (route == "fill")
        {
            await Ui.Ready<Button>("GridFillHandle0_4");
            await SheetNativeInput.Drag("GridFillHandle0_4", "GridCell2_4", async () => {
                await Ui.Until(() => Ui.Find<TextBlock>("GridSelection").Text.Contains("3行へコピー予定"));
                await Ui.Run(() => Assert.That(CellText("GridCell2_4"), Is.EqualTo("2")));
            });
        }
        else
        {
            await SheetNativeInput.Click("GridCell2_4", VirtualKey.Shift);
            await SheetNativeInput.Press(VirtualKey.D, VirtualKey.Control);
        }
        await Ui.Until(() => CellText("GridCell1_4") == "7" && CellText("GridCell2_4") == "7");
        await Ui.Run(() => Assert.That(session.Workspace.Planning("P1")!.Tasks.Select(task => (task.Id, task.Actuals!.Single().PersonId)),
            Is.EquivalentTo(new (string, string?)[] { ("I1", "U1"), ("I2", "U2"), ("I3", null) })));
        await Ui.ClickCommand("GridUndo");
        await Ui.Until(() => CellText("GridCell2_4") == "2");
        await Ui.Run(() => Assert.That(CellText("GridCell0_4"), Is.EqualTo("7")));
        await Ui.ClickCommand("GridUndo");
        await Ui.Until(() => CellText("GridCell0_4") == "5");
    }

    [Test, Category("WeeklyBulk")]
    public async Task WeeklyPasteWithAnAmbiguousWorkerKeepsTheRangeAndAllOriginalValues()
    {
        await MountWeeklyBulk(missingWorker: true);
        await SheetNativeInput.Click("GridCell0_3");
        await SheetNativeInput.Click("GridCell2_4", VirtualKey.Shift);
        clipboard = "3\t7\n2\t9\n1\t8";
        var before = JsonSerializer.Serialize(session.Workspace.Snapshot());

        await Ui.ClickCommand("GridPaste");

        await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("P1T2"));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain("実績担当者"));
            Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("3行・6セル"));
            Assert.That(new[] { CellText("GridCell0_3"), CellText("GridCell1_3"), CellText("GridCell2_3") }, Is.All.EqualTo("4"));
            Assert.That(CellText("GridCell0_4"), Is.EqualTo("5"));
            Assert.That(JsonSerializer.Serialize(session.Workspace.Snapshot()), Is.EqualTo(before));
        });
    }
}
