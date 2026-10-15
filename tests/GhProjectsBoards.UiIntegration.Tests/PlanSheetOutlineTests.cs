using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test]
    public async Task FoldedOutlineMoveUndoRedoKeepsViewportOffsetAndFirstVisibleIdentity()
    {
        var rows = Enumerable.Range(1, 80).SelectMany(i => new[] {
            new PlanRow($"R{i:00}", $"Requirement {i}", "acme/repo"),
            new PlanRow($"C{i:00}", $"Child {i}", "acme/repo") { Parent = $"R{i:00}" }
        }).ToImmutableArray();
        await MountPresentation(rows, rows);
        await Ui.ClickCommand("PlanSheetCollapseAll");
        await Ui.Until(() => sheet.RowIds.Count == 81);
        await Ui.Run(() => sheet.SelectWholeRow("R21", false));
        // Let the selection's own bring-into-view settle before fixing the measured offset.
        await SheetNativeInput.Rendered();
        await Ui.Run(() => ScrollMeasurementViewport().ChangeView(null, 20 * sheet.RowHeight, null, true));
        await Ui.Until(() => Math.Abs(ScrollMeasurementViewport().VerticalOffset - 20 * sheet.RowHeight) < 1);
        await AssertViewport("R21");
        await Ui.ClickCommand("PlanSheetMoveDown");
        await Ui.Until(() => sheet.RowIds[20] == "R22");
        await AssertViewport("R22");
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Z));
        await Ui.Until(() => sheet.RowIds[20] == "R21");
        await AssertViewport("R21");
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Y));
        await Ui.Until(() => sheet.RowIds[20] == "R22");
        await AssertViewport("R22");

        async Task AssertViewport(string first) {
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                Assert.That(ScrollMeasurementViewport().VerticalOffset, Is.EqualTo(20 * sheet.RowHeight).Within(1));
                var visible = sheet.Realized.Where(r => r.IsLoaded && r.Identity.Length > 0)
                    .Select(r => (r.Identity, Bounds: r.TransformToVisual(sheet.List).TransformBounds(new(0, 0, r.ActualWidth, r.ActualHeight))))
                    .Where(r => r.Bounds.Bottom > 1 && r.Bounds.Top < sheet.List.ActualHeight).OrderBy(r => r.Bounds.Top).ToArray();
                Assert.That(visible[0].Identity, Is.EqualTo(first));
                Assert.That(visible.Any(r => r.Identity == "R21"), Is.True);
            });
        }
    }
    private async Task MountOutline()
    {
        ImmutableArray<PlanRow> rows = [new("P", "Parent", "acme/repo"),
            new("A", "First", "acme/repo") { Parent = "P" },
            new("C", "Folded child", "acme/repo") { Parent = "A" },
            new("B", "Second", "acme/repo") { Parent = "P" }, new("Q", "Other", "acme/repo")];
        rows = rows.Select(r => r with { Start = Today, End = Today, Fixed = true }).ToImmutableArray();
        await MountPresentation(rows, rows);
        await Ui.Run(() => sheet.SetFold("A", true));
        await Ui.Until(() => sheet.RowIds.SequenceEqual(new[] { "P", "A", "B", "Q", "" }));
        await Ui.Ready<TextBox>("PlanCell2_Title");
        await Ui.Ready<TextBox>("PlanCell4_Title");
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell2_Title").Text == "First"
            && Ui.Find<TextBox>("PlanCell4_Title").Text == "Second");
    }
    [Test]
    public async Task IdInvocationSelectsWholeRowAndOverflowMoveKeepsIdentityAndGanttOrder()
    {
        await MountOutline();
        await Ui.Run(() => Ui.Click("PlanRowHandle2"));
        await Ui.Run(() => {
            foreach (var field in sheet.VisibleColumns.Where(c => c.Field is not null).Select(c => c.Field!))
                Assert.That(sheet.IsSelected("A", field), Is.True);
            Assert.That(Ui.CommandButton("PlanSheetMoveUp").IsEnabled, Is.False);
            Assert.That(AutomationProperties.GetHelpText(Ui.CommandButton("PlanSheetMoveUp")), Does.Contain("先頭"));
            Assert.That(Ui.CommandButton("PlanSheetMoveDown").IsEnabled, Is.True);
        });
        await Ui.ClickCommand("PlanSheetMoveDown");
        await Ui.Until(() => sheet.RowIds.SequenceEqual(new[] { "P", "B", "A", "Q", "" }));
        await Ui.Ready<TextBox>("PlanCell3_Title");
        await Ui.Until(() => Ui.Find<TextBox>("PlanCell3_Title").Text == "First"
            && Ui.Find<TextBox>("PlanCell2_Title").Text == "Second");
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "B", "A", "C", "Q" }));
            Assert.That(sheet.IsSelected("A", PlanField.Title), Is.True);
            Assert.That(sheet.IsSelected("B", PlanField.Title), Is.False);
            foreach (var row in sheet.Realized.Where(r => r.IsLoaded && r.Identity.Length > 0 && sheet.RowIds.Contains(r.Identity))) {
                var number = sheet.PlanIds[row.Identity];
                var bar = Ui.Find<FrameworkElement>("PlanBar" + number, row);
                Assert.That(AutomationProperties.GetName(bar), Does.StartWith($"ID {number} "));
                var barY = bar.TransformToVisual(row).TransformPoint(new()).Y;
                Assert.That(barY, Is.InRange(0, row.ActualHeight));
                Assert.That(Ui.Find<TextBlock>("PlanRowId" + number, row).Text, Is.EqualTo(number.ToString()));
            }
        });
        await Ui.Until(() => sheet.Realized.Single(r => r.Identity == "A").SelectionTarget(PlanField.Title)!.FocusState != FocusState.Unfocused);
        Assert.That(session.UndoCount, Is.EqualTo(1));
    }
    [Test]
    public async Task MoveCommandsExplainMixedParentsFilterAndAlreadyArranged()
    {
        await MountOutline();
        await Ui.Run(() => {
            sheet.Select("A", PlanField.Title, false); sheet.Select("Q", PlanField.Title, true);
            foreach (var id in new[] { "PlanSheetMoveUp", "PlanSheetMoveDown" }) {
                Assert.That(Ui.CommandButton(id).IsEnabled, Is.False);
                Assert.That(AutomationProperties.GetHelpText(Ui.CommandButton(id)), Does.Contain("同じ親"));
            }
            Assert.That(AutomationProperties.GetHelpText(Ui.CommandButton("PlanSheetArrangeOutline")), Does.Contain("すでに"));
            Ui.Find<TextBox>("PlanSheetFilter").Text = "First";
        });
        await Ui.Until(() => sheet.RowIds.SequenceEqual(new[] { "A", "" }));
        await Ui.Run(() => {
            Ui.Click("PlanSheetMore");
        });
        await Ui.Ready<MenuFlyoutItem>("PlanSheetMoveUp");
        await Ui.Run(() => {
            foreach (var id in new[] { "PlanSheetMoveUp", "PlanSheetMoveDown", "PlanSheetArrangeOutline" }) {
                var command = Ui.Find<MenuFlyoutItem>(id);
                Assert.That(command.IsEnabled, Is.False);
                Assert.That(command.AllowFocusWhenDisabled, Is.True);
                Assert.That(AutomationProperties.GetHelpText(command), Does.Contain("絞り込み中"));
            }
            Ui.Find<AppBarButton>("PlanSheetMore").Flyout.Hide();
        });
        Assert.That(session.UndoCount, Is.Zero);
    }
    [Test]
    public async Task OutlineMoveCommitsPendingInputAndInvalidInputBlocksTheMove()
    {
        await MountOutline();
        await Ui.Run(() => {
            Ui.Click("PlanRowHandle4");
            Ui.Find<TextBox>("PlanCell4_Title").Text = "Renamed second";
        });
        await Ui.ClickCommand("PlanSheetMoveUp");
        await Ui.Until(() => session.Document.State.Rows[1].Identity == "B");
        Assert.That(session.Document.State.Rows[1].Title, Is.EqualTo("Renamed second"));
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell2_Estimate").Text = "invalid");
        var before = session.Document.State;
        var undo = session.UndoCount;
        await Ui.ClickCommand("PlanSheetMoveDown");
        await Ui.Until(() => sheet.Problems.ContainsKey(("B", PlanField.Estimate)));
        Assert.That(session.Document.State, Is.SameAs(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }
    [Test]
    public async Task ArrangeOverflowRepairsScatteredRowsAsOneUndoStep()
    {
        var rows = ImmutableArray.Create(new PlanRow("P", "Parent", "acme/repo"),
            new PlanRow("Q", "Other", "acme/repo"), new PlanRow("A", "Child", "acme/repo") { Parent = "P" });
        await MountPresentation(rows, rows);
        await Ui.Run(() => {
            Ui.Click("PlanRowHandle3");
            Assert.That(Ui.CommandButton("PlanSheetArrangeOutline").IsEnabled, Is.True);
        });
        await Ui.ClickCommand("PlanSheetArrangeOutline");
        await Ui.Until(() => sheet.RowIds.SequenceEqual(new[] { "P", "A", "Q", "" }));
        await Ui.Run(() => Assert.That(sheet.IsSelected("A", PlanField.Title), Is.True));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Z));
        await Ui.Until(() => sheet.RowIds.SequenceEqual(new[] { "P", "Q", "A", "" }));
    }
    [Test, Category("PlanSheetNative")]
    public async Task IdShiftClickAndAltShiftKeysMoveSelectedSiblingSubtrees()
    {
        await MountOutline();
        await SheetNativeInput.Click("PlanRowHandle2");
        await SheetNativeInput.Click("PlanRowHandle4", VirtualKey.Shift);
        await Ui.Run(() => {
            foreach (var id in new[] { "A", "B" })
                foreach (var field in sheet.VisibleColumns.Where(c => c.Field is not null).Select(c => c.Field!))
                    Assert.That(sheet.IsSelected(id, field), Is.True);
        });
        // A plain click inside the selected rows must narrow to that row, or a later move or Delete hits the old range.
        await SheetNativeInput.Click("PlanRowHandle4");
        await Ui.Run(() => {
            Assert.That(sheet.IsSelected("B", PlanField.Title), Is.True);
            Assert.That(sheet.IsSelected("A", PlanField.Title), Is.False);
        });
        await SheetNativeInput.Click("PlanRowHandle5");
        await SheetNativeInput.Press(VirtualKey.Up, VirtualKey.Menu, VirtualKey.Shift);
        await Ui.Until(() => session.Document.State.Rows[0].Identity == "Q");
        await Ui.Run(() => Assert.That(sheet.IsSelected("Q", PlanField.Title), Is.True));
        await SheetNativeInput.Press(VirtualKey.Down, VirtualKey.Menu, VirtualKey.Shift);
        await Ui.Until(() => session.Document.State.Rows[^1].Identity == "Q");
        Assert.That(session.UndoCount, Is.EqualTo(2));
    }
    [TestCase("drop"), TestCase("outside"), TestCase("escape"), TestCase("capture"), Category("PlanSheetNative")]
    public async Task IdDragShowsBoundaryAndEitherMovesFoldedSubtreeOrCancels(string outcome)
    {
        await MountOutline();
        var before = session.Document.State;
        var from = await SheetNativeInput.PointFor("PlanRowHandle2");
        // Target just inside the end of P's group, with DPI-aware coordinates.
        var to = outcome == "outside" ? await SheetNativeInput.PointFor("PlanRowHandle1")
            : await SheetNativeInput.PointFor("PlanRowHandle4", y: .95);
        await SheetNativeInput.Drag(from, to, async () => {
            await Ui.Run(() => {
                Assert.That(Ui.Find<Border>("PlanSheetMoveIndicator").Visibility,
                    Is.EqualTo(outcome == "outside" ? Visibility.Collapsed : Visibility.Visible));
                Assert.That(session.Document.State, Is.SameAs(before));
                if (outcome == "outside") Assert.That(Ui.Find<TextBlock>("PlanSheetSelection").Text, Is.EqualTo("「Parent」の中でだけ移動できます（親を変えるにはインデント／アウトデント）"));
                else Assert.That(Ui.Find<TextBlock>("PlanSheetSelection").Text, Is.EqualTo("2行を移動"));
            });
            if (outcome == "escape") await SheetNativeInput.Press(VirtualKey.Escape);
            if (outcome == "capture") await Ui.Run(() => Ui.Find<Button>("PlanRowHandle2").ReleasePointerCaptures());
        });
        if (outcome == "drop") {
            await Ui.Until(() => session.Document.State.Rows[1].Identity == "B");
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "B", "A", "C", "Q" }));
            await Ui.Run(() => Assert.That(sheet.IsSelected("A", PlanField.Title), Is.True));
        } else Assert.That(session.Document.State, Is.SameAs(before));
        Assert.That(session.UndoCount, Is.EqualTo(outcome == "drop" ? 1 : 0));
        await Ui.Run(() => Assert.That(Ui.Find<Border>("PlanSheetMoveIndicator").Visibility, Is.EqualTo(Visibility.Collapsed)));
    }
}
