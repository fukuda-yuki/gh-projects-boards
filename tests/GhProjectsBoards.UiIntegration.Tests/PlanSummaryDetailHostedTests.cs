using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    [TestCase(1920, 1032), TestCase(1280, 720)]
    public async Task SummaryDetailHeadersAndValuesShareHorizontalBounds(int width, int height)
    {
        await SummaryPlan();
        await Ui.Run(() => { ResizeWorkspace(width, height); Ui.Click("SummaryCell_U1_0"); });
        await Ui.Ready<TextBlock>("SummaryTask_I2"); await Ui.Idle();
        await Ui.Run(() => {
            var summary = Ui.Find<PlanSummaryView>("PlanSummary");
            foreach (var identity in new[] { "I1", "I2" }) {
                for (var column = 0; column < 8; column++) {
                    var header = Ui.Find<TextBlock>("SummaryTaskHeader_" + column);
                    var cell = Ui.Find<TextBlock>(column == 0 ? "SummaryTaskId_" + identity : column == 7 ? "SummaryTask_" + identity : $"SummaryTaskValue_{identity}_{column}");
                    var headerX = header.TransformToVisual(summary).TransformPoint(new()).X;
                    var cellX = cell.TransformToVisual(summary).TransformPoint(new()).X;
                    Assert.That(cell.ActualWidth, Is.GreaterThan(0));
                    Assert.That(cellX, Is.EqualTo(headerX).Within(1), $"{identity}, {header.Text}: left");
                    Assert.That(cellX + cell.ActualWidth, Is.EqualTo(headerX + header.ActualWidth).Within(1), $"{identity}, {header.Text}: right");
                    if (column is >= 2 and <= 6) {
                        Assert.That(header.TextAlignment, Is.EqualTo(TextAlignment.Right));
                        Assert.That(cell.TextAlignment, Is.EqualTo(TextAlignment.Right));
                    }
                }
            }
        });
    }

    [Test]
    public async Task SummaryDetailShowsOnlyApplicableSheetStatesWithFullTooltips()
    {
        readDay = new(2026, 10, 5);
        FakePlanEditor.Save(root, new([
            new(new("I1", "Ordinary", "acme/repo") { Estimate = 8, Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I2", "Closed", "acme/repo") { Estimate = 8, Closed = true, CloseDate = new(2026, 10, 2), Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I3", "Over estimate", "acme/repo") { Estimate = 8, Actual = 4, Remaining = 8, Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I4", "Zero open", "acme/repo") { Estimate = 8, Remaining = 0, Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I5", "Overdue", "acme/repo") { Estimate = 24, Actual = 8, Remaining = 16, End = new(2026, 10, 2), Assignees = ["U1"] }, "", true) { Phase = "SA" },
            new(new("I6", "Later", "acme/repo") { Estimate = 40, End = new(2026, 10, 6), Assignees = ["U1"] }, "", true) { Phase = "SA" }], 7) { Phases = ["SA"] });
        await Open("Ordinary");
        await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = readDay, ProjectStart = readDay }), readDay);
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<Button>("SummaryCell_U1_0");
        await Ui.Run(() => Ui.Click("SummaryCell_U1_0")); await Ui.Ready<TextBlock>("SummaryTask_I6");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("SummaryTask_I1").Text, Is.Empty);
            Assert.That(Ui.Find<TextBlock>("SummaryTask_I2").Text, Is.EqualTo("完了"));
            Assert.That(Ui.Find<TextBlock>("SummaryTask_I3").Text, Is.EqualTo("見積超過: 見込工数 12h（見積工数 8h、差異 +4h）"));
            Assert.That(Ui.Find<TextBlock>("SummaryTask_I4").Text, Is.EqualTo("残 0・未完了: 見込み残存工数が 0 ですが Issue は閉じていません"));
            Assert.That(Ui.Find<TextBlock>("SummaryTask_I5").Text, Does.StartWith("期限超過: 完了予定 10/2 を過ぎて未完了（見込み残存工数 16h）").And.Contain("予定より遅れ: 発行済み 10/2 から +2 日"));
            Assert.That(Ui.Find<TextBlock>("SummaryTask_I6").Text, Is.EqualTo("予定より遅れ: 発行済み 10/6 から +3 日"));
            foreach (var id in Enumerable.Range(1, 6)) {
                var state = Ui.Find<TextBlock>("SummaryTask_I" + id);
                Assert.That(ToolTipService.GetToolTip(state), Is.EqualTo(state.Text));
                Assert.That(state.TextTrimming, Is.EqualTo(TextTrimming.CharacterEllipsis));
            }
        });
    }

    [TestCase(200), TestCase(201)]
    public async Task SummaryDetailUsesPageHeightAndCapsPreviewWithoutTruncatingFilter(int count)
    {
        readDay = new(2026, 10, 5);
        FakePlanEditor.Save(root, new(Enumerable.Range(1, count).Select(i => new PlanFakeIssue(
            new("I" + i, "Detail task " + i, "acme/repo") { Estimate = 8, Assignees = ["U1"] }, "", true) { Phase = "SA" }).ToImmutableArray(), count + 1) { Phases = ["SA"] });
        await Open("Detail task 1");
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Ready<Button>("SummaryCell_U1_0");
        var before = PlanJson.Text(workspace.Session!.Document); var undo = workspace.Session.UndoCount;
        var calls = File.ReadAllText(Path.Combine(root, "calls.jsonl"));
        await Ui.Run(() => Ui.Click("SummaryCell_U1_0")); await Ui.Ready<TextBlock>("SummaryTaskId_I200"); await Ui.Idle();
        await Ui.Run(() => {
            var list = Ui.Find<FrameworkElement>("SummaryTasks");
            var ids = Ui.Tree(list).OfType<TextBlock>().Where(t => AutomationProperties.GetAutomationId(t).StartsWith("SummaryTaskId_", StringComparison.Ordinal)).ToArray();
            Assert.That(ids.Select(t => t.Text), Is.EqualTo(Enumerable.Range(1, 200).Select(i => i.ToString())));
            var last = ids[^1];
            Assert.That(list.ActualHeight, Is.GreaterThan(320));
            Assert.That(last.TransformToVisual(list).TransformPoint(new()).Y + last.ActualHeight, Is.LessThanOrEqualTo(list.ActualHeight));
            var outer = Ui.Find<ScrollViewer>("SummaryScroll");
            for (DependencyObject? element = list; element != outer; element = VisualTreeHelper.GetParent(element)) {
                Assert.That(element, Is.Not.Null, "Details must remain inside the page viewport.");
                if (element is ScrollViewer viewer) Assert.That(viewer.VerticalScrollMode, Is.EqualTo(ScrollMode.Disabled));
                if (element is ScrollView viewport) Assert.That(viewport.VerticalScrollMode, Is.EqualTo(ScrollingScrollMode.Disabled));
            }
            Assert.That(Ui.Tree(list).OfType<ScrollViewer>(), Is.Empty);
            var notice = Ui.Tree(view).OfType<TextBlock>().SingleOrDefault(t => AutomationProperties.GetAutomationId(t) == "SummaryTasksMore");
            if (count == 200) Assert.That(notice, Is.Null);
            else Assert.That(notice?.Text, Is.EqualTo("ほか 1 タスク。計画で絞り込むとすべて表示します"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryFilter")), Is.EqualTo($"この {count} タスクで計画を絞り込む"));
            Ui.Click("SummaryFilter");
        });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Where(id => id.Length > 0), Is.EqualTo(Enumerable.Range(1, count).Select(i => "I" + i)));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo($"{count} 件"));
        });
        Assert.That(PlanJson.Text(workspace.Session.Document), Is.EqualTo(before));
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(File.ReadAllText(Path.Combine(root, "calls.jsonl")), Is.EqualTo(calls));
    }
}
