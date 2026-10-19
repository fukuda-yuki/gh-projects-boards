using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    // #240: a whole-row selection must be distinguishable from a rectangle that covers every column.
    // Delete removes the rows for the former and clears for the latter, and never blanks タスク名.
    [Test]
    public async Task WholeRowDeleteRemovesTheRowsWhileARangeDeleteKeepsTheTaskName()
    {
        // #236/#241: the plan must stay acyclic. I2 is blocked by I1 and I1 is free, so the range delete still
        // has a predecessor to clear without asking any operation to reschedule a pre-existing cycle.
        var first = new PlanRow("I1", "Published", "acme/repo") { Fixed = true, Assignees = ["U1"] };
        var second = new PlanRow("I2", "Other", "acme/repo") { Fixed = true, Assignees = ["U1"], Predecessors = ["I1"] };
        await MountPresentation([first, second], [first, second], [new("U1", "alice", 100, null)]);
        await Select(2, PlanField.Title);
        // A rectangle that happens to span every column is still a cell range.
        await Ui.Run(() => {
            var fields = sheet.VisibleColumns.Where(c => c.Field is not null).Select(c => c.Field!).ToArray();
            sheet.Select("I2", fields[0], false);
            sheet.Select("I2", fields[^1], true);
        });
        var undo = session.UndoCount;
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Delete));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Length > 0);
        await Ui.Run(() => {
            var row = session.Document.State.Rows.Single(r => r.Identity == "I2");
            Assert.That(row.Title, Is.EqualTo("Other"), "A range Delete never blanks a task name.");
            Assert.That(row.Assignees, Is.Empty, "The other selected cells still clear.");
            Assert.That(row.Predecessors, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.EqualTo("タスク名は消せません。行ごと消すときは 行を削除"));
        });
        Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
        // Choosing another row dismisses the sentence, like every other refusal in the sheet.
        await Select(1, PlanField.Title);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Is.Empty));

        // The ID cell sets the explicit whole-row selection: Delete removes the row and its subtree.
        var inserted = PlanRow.New("誤って挿入");
        var child = PlanRow.New("誤って挿入の子");
        await Ui.Run(async () => { await session.Execute(new InsertPlanRows([inserted, child]), Today); sheet.Refresh(); });
        await Ui.Until(() => sheet.RowIds.Contains(inserted.Identity));
        await Ui.Run(async () => { await session.Execute(new IndentPlanRows([child.Identity]), Today); sheet.Refresh(); });
        await Ui.Until(() => session.Document.State.Rows.Single(r => r.Identity == child.Identity).Parent == inserted.Identity);
        var number = sheet.PlanIds[inserted.Identity];
        await Ui.Run(() => Ui.Click("PlanRowHandle" + number));
        await Ui.Run(() => Assert.That(sheet.IsSelected(inserted.Identity, PlanField.Title), Is.True));
        undo = session.UndoCount;
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Delete));
        await Ui.Until(() => !session.Document.State.Rows.Any(r => r.Identity == inserted.Identity || r.Identity == child.Identity));
        Assert.That(session.UndoCount, Is.EqualTo(undo + 1), "A whole-row delete is one Undo step.");
        await Ui.Run(async () => { await session.Undo(Today); sheet.Refresh(); });
        await Ui.Until(() => session.Document.State.Rows.Any(r => r.Identity == inserted.Identity));

        // A published Issue in the whole-row selection deletes nothing and names the three commands.
        await Ui.Run(() => Ui.Click("PlanRowHandle1"));
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Delete));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Contains("GitHub の Issue は削除できません"));
        await Ui.Run(() => {
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1", "I2", inserted.Identity, child.Identity }));
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Text, Does.Contain("完了・中止・この版から外す"));
        });
    }

    // The keyboard gesture itself is native input, so it runs with the other PlanSheetNative cases.
    [Test, Category("PlanSheetNative")]
    public async Task ShiftSpaceTakesTheWholeRowAndDeleteRemovesIt()
    {
        var first = new PlanRow("I1", "Published", "acme/repo") { Fixed = true };
        var second = new PlanRow("I2", "Other", "acme/repo") { Fixed = true };
        await MountPresentation([first, second], [first, second]);
        await SheetNativeInput.Click("PlanCell2_Title");
        await SheetNativeInput.Press(VirtualKey.Space, VirtualKey.Shift);
        await Ui.Run(() => {
            foreach (var field in sheet.VisibleColumns.Where(c => c.Field is not null).Select(c => c.Field!))
                Assert.That(sheet.IsSelected("I2", field), Is.True, "Shift+Space takes the whole row.");
        });
        await SheetNativeInput.Press(VirtualKey.Delete);
        await Ui.Until(() => !session.Document.State.Rows.Any(r => r.Identity == "I2"));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        await SheetNativeInput.Click("PlanCell1_Title");
        await SheetNativeInput.Press(VirtualKey.Space, VirtualKey.Shift);
        await SheetNativeInput.Press(VirtualKey.Delete);
        await Ui.Until(() => Ui.Find<TextBlock>("PlanSheetError").Text.Contains("GitHub の Issue は削除できません"));
        Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "I1" }));
    }
}
