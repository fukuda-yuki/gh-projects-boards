using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class PlanningHostedTests
{
    [Test, Category("ConfirmedEditUndo")]
    public async Task ConfirmedEstimatesUndoFromTheNextRowWithoutReopeningConsumedInput()
    {
        await EnterEstimate("8", VirtualKey.Number8);
        await AssertConfirmedEstimate("8", "2026-10-05 18:00");
        await EnterEstimate("10", VirtualKey.Number1, VirtualKey.Number0);
        await AssertConfirmedEstimate("10", "2026-10-06 11:00");

        await AssertNextEstimateSelected();
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => CellText("GridCell0_2") == "8");
        await AssertConfirmedEstimate("8", "2026-10-05 18:00");
        await AssertNextEstimateSelected();

        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => CellText("GridCell0_2") == "");
        await Ui.Run(() => {
            var work = session.Workspace;
            var estimate = work.Open(project)[0].Cells[2];
            Assert.That(work.Value(estimate), Is.Null);
            Assert.That(work.Buffer(estimate), Is.Null);
            Assert.That(CellText("GridCell0_6"), Is.Empty);
            Assert.That(work.PlanFor(project).Tasks.Single(t => t.Id == "I1").Finish, Is.Null);
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain("未確定入力"));
            Assert.That(work.Journal, Is.Empty);
        });
        await AssertNextEstimateSelected();
    }

    [Test, Category("ConfirmedEditUndo")]
    public async Task NativeTextUndoKeepsTheEarlierConfirmedEstimateAndItsPlan()
    {
        await EnterEstimate("8", VirtualKey.Number8);
        await FocusEstimateInput("GridCell0_2");
        await SheetNativeInput.Press(VirtualKey.F2);
        await SheetNativeInput.Press(VirtualKey.Number9);
        await Ui.Until(() => CellText("GridCell0_2") == "9");
        await Ui.Run(() => Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("9")));

        await AssertEstimateFocus("GridCell0_2");
        await SheetNativeInput.Press(VirtualKey.Z, VirtualKey.Control);
        await Ui.Until(() => CellText("GridCell0_2") == "8");
        await Ui.Run(() => {
            var work = session.Workspace;
            var estimate = work.Open(project)[0].Cells[2];
            Assert.That(work.Value(estimate), Is.EqualTo("8"), "Native text Undo must not undo the preceding confirmed cell operation.");
            Assert.That(work.Buffer(estimate), Is.EqualTo("8"), "Native text Undo leaves the current editor unconfirmed.");
            Assert.That(work.PlanFor(project).Tasks.Single(t => t.Id == "I1").Finish,
                Is.EqualTo(PlanningContractTests.At("2026-10-05 18:00")));
            Assert.That(CellText("GridCell0_6"), Is.EqualTo("2026-10-05"));
            Assert.That(work.Journal, Is.Empty);
        });
        await AssertEstimateFocus("GridCell0_2");
    }

    [TestCase(false), TestCase(true), Category("ConfirmedEditUndo")]
    public async Task SuccessfulEstimateCorrectionRetiresOperationFailureButPreservesIndependentSaveFailure(bool holdWriterLock)
    {
        string? root = null;
        DraftStore? store = null;
        if (holdWriterLock)
        {
            await Ui.Unmount(grid);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            root = Path.Combine(Path.GetTempPath(), "ghpb-confirmed-undo-ui-" + Guid.NewGuid().ToString("N"));
            store = new DraftStore(root);
            session = new DraftSession(store, session.Workspace, 0);
            await Ui.Run(() => grid = new EditingGrid(project, session, () => Task.FromResult(true), readClipboard: () => Task.FromResult(clipboard)));
            await Ui.Mount(grid);
        }
        await EnterEstimate("8", VirtualKey.Number8);
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));

        FileStream? writerLock = null;
        try
        {
            if (holdWriterLock) writerLock = new FileStream(Path.Combine(root!, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await FocusEstimateInput("GridCell0_2");
            // These setters exercise TextChanging and commit/status wiring; native text Undo is covered above.
            await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "9");
            if (holdWriterLock) await Ui.Until(() => session.Status.StartsWith("ローカル保存失敗"));
            await Ui.ClickCommand("GridUndo");
            await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("元に戻せません"));
            await Ui.Run(() => {
                var estimate = session.Workspace.Open(project)[0].Cells[2];
                Assert.That(session.Workspace.Value(estimate), Is.EqualTo("8"));
                Assert.That(session.Workspace.Buffer(estimate), Is.EqualTo("9"));
                Assert.That(CellText("GridCell0_2"), Is.EqualTo("9"));
                if (holdWriterLock) Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus").Text, Does.Contain("ローカル保存失敗"));
            });

            await FocusEstimateInput("GridCell0_2");
            if (holdWriterLock)
            {
                await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "8x");
                await SheetNativeInput.Press(VirtualKey.Enter);
                await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("工数") && session.Status.StartsWith("ローカル保存失敗"));
                await Ui.Run(() => {
                    Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain("工数").And.Not.Contain("元に戻せません"));
                    Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus").Text, Does.Contain("ローカル保存失敗"));
                    Assert.That(CellText("GridCell0_2"), Is.EqualTo("8x"));
                    Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("8"));
                });
                await AssertEstimateFocus("GridCell0_2");
            }
            await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "8");
            await SheetNativeInput.Press(VirtualKey.Enter);
            await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2");
            if (holdWriterLock) await Ui.Until(() => session.Status.StartsWith("ローカル保存失敗"));
            await AssertConfirmedEstimate("8", "2026-10-05 18:00");
            await Ui.Run(() => {
                var status = Ui.Find<TextBlock>("DraftStatus").Text;
                Assert.That(status, Does.Not.Contain("元に戻せません").And.Not.Contain("工数"));
                if (holdWriterLock) Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus").Text, Does.Contain("ローカル保存失敗"));
                Assert.That(session.Workspace.Journal, Is.Empty);
            });
            await AssertNextEstimateSelected();
        }
        finally { writerLock?.Dispose(); }

        if (holdWriterLock)
        {
            await Ui.ClickCommand("GridSave");
            await Ui.Until(() => session.Status.StartsWith("ローカル保存済み") && session.DurableRevision == session.Workspace.Revision);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus").Text, Does.Contain("ローカル保存済み").And.Not.Contain("失敗"));
                Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain("元に戻せません"));
            });
            var saved = await store!.LoadAsync(project.Snapshot.Id.Scope);
            Assert.That(saved, Is.Not.Null);
            var estimate = saved!.Fields.Single(f => f.Key == new FieldKey("Number", "P1T1", "P1", "F-Estimate"));
            Assert.That(estimate.Change?.Value, Is.EqualTo("8"));
            Assert.That(estimate.Buffer, Is.Null);
        }
    }

    [TestCase(false), TestCase(true), Category("ScopedOperationProblem")]
    public async Task ConfirmingAnotherCellPreservesTheFirstCellsProblemUntilThatInputIsCorrected(bool rejectedUndo)
    {
        await EnterEstimate("8", VirtualKey.Number8);
        await FocusEstimateInput("GridCell0_2");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = rejectedUndo ? "9" : "8x");
        var problem = rejectedUndo ? "元に戻せません" : "工数";
        if (rejectedUndo) await Ui.ClickCommand("GridUndo");
        else await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains(problem));

        await FocusEstimateInput("GridCell1_2");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell1_2").Text = "2");
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => {
            var other = session.Workspace.Open(project)[1].Cells[2];
            return session.Workspace.Value(other) == "2" && session.Workspace.Buffer(other) is null;
        });
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain(problem));
            var first = session.Workspace.Open(project)[0].Cells[2];
            Assert.That(session.Workspace.Value(first), Is.EqualTo("8"));
            Assert.That(session.Workspace.Buffer(first), Is.EqualTo(rejectedUndo ? "9" : "8x"));
        });

        await FocusEstimateInput("GridCell0_2");
        await Ui.Run(() => Ui.Find<TextBox>("GridCell0_2").Text = "8");
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => session.Workspace.Buffer(session.Workspace.Open(project)[0].Cells[2]) is null);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain(problem));
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[0].Cells[2]), Is.EqualTo("8"));
            Assert.That(session.Workspace.Value(session.Workspace.Open(project)[1].Cells[2]), Is.EqualTo("2"));
            Assert.That(session.Workspace.Journal, Is.Empty);
        });
    }

    private async Task EnterEstimate(string text, params VirtualKey[] digits)
    {
        await FocusEstimateInput("GridCell0_2");
        await SheetNativeInput.Press(VirtualKey.F2);
        foreach (var digit in digits) await SheetNativeInput.Press(digit);
        await Ui.Until(() => CellText("GridCell0_2") == text);
        await AssertEstimateFocus("GridCell0_2");
        await SheetNativeInput.Press(VirtualKey.Enter);
        await Ui.Until(() => grid.SelectionIdentity?.Item == "P1T2" && grid.SelectionIdentity?.Field?.FieldId == "F-Estimate");
        await Ui.Ready<TextBox>("GridCell1_2");
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<TextBox>("GridCell1_2")));
    }

    private async Task FocusEstimateInput(string id)
    {
        await Ui.Ready<Microsoft.UI.Xaml.FrameworkElement>(id);
        await SheetNativeInput.ActivateWindow();
        await Ui.Run(() => FocusCell(id));
        await Ui.Ready<TextBox>(id);
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<TextBox>(id)));
    }

    private Task AssertEstimateFocus(string id) => Ui.Run(() =>
        Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(Ui.Find<TextBox>(id))));

    private async Task AssertNextEstimateSelected()
    {
        await AssertEstimateFocus("GridCell1_2");
        await Ui.Run(() => {
            Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T2"));
            Assert.That(grid.SelectionIdentity?.Field?.FieldId, Is.EqualTo("F-Estimate"));
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(project)[1].Cells[2]), Is.Null);
        });
    }

    private Task AssertConfirmedEstimate(string estimate, string finish) => Ui.Run(() => {
        var work = session.Workspace;
        var cell = work.Open(project)[0].Cells[2];
        Assert.That(CellText("GridCell0_2"), Is.EqualTo(estimate));
        Assert.That(work.Value(cell), Is.EqualTo(estimate));
        Assert.That(work.Buffer(cell), Is.Null);
        Assert.That(CellText("GridCell0_6"), Is.EqualTo(finish[..10]));
        Assert.That(work.PlanFor(project).Tasks.Single(t => t.Id == "I1").Finish, Is.EqualTo(PlanningContractTests.At(finish)));
        Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Not.Contain("未確定入力"));
    });
}
