using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #260 / #227 / #238 on the hosted desktop: the attention strip above the command row, its one filter model,
// the chase list copy and the summary verdict. Counts and text are asserted, not #264's mark glyphs.
internal sealed partial class PlanWorkspaceHostedTests
{
    private const string FixtureDay = "2026-10-06";
    // I2 is a red late task that pushes I3 (a late successor) and is 22 days without a progress change;
    // I4 is the over-estimate; I1 is the requirement I2 and I3 belong to.
    private void SeedAttentionFixture()
    {
        var day = new DateOnly(2026, 10, 6);
        readDay = day;
        FakePlanEditor.Save(root, new([
            new(new("I1", "R01 要求事項", "acme/repo"), "", true),
            new(new("I2", "R01 遅れている設計", "acme/repo") {
                Parent = "I1", Phase = "SS", Assignees = ["U1"],
                Start = day.AddDays(-4), End = day.AddDays(-3), Estimate = 24, Actual = 8, Remaining = 16 }, "", true) {
                ProgressUpdatedAt = new(day.AddDays(-22).ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(9)) },
            new(new("I3", "R01 後続の実装", "acme/repo") {
                Parent = "I1", Phase = "SS", Assignees = ["U2"], Predecessors = ["I2"],
                Start = day.AddDays(-1), End = day.AddDays(-1), Estimate = 8, Remaining = 8 }, "", true),
            new(new("I4", "R02 見積超過の試験", "acme/repo") {
                Phase = "PS", Assignees = ["U1"], Start = day, End = day.AddDays(5), Estimate = 8, Actual = 6, Remaining = 6 }, "", true)
        ], 7) { Phases = ["SS", "PS"] });
    }
    private static PlanSheetView Sheet() => Ui.Tree(Ui.Root).OfType<PlanSheetView>().Single();
    private static string Text(string id) => Ui.Find<TextBlock>(id).Text;

    [Test, Category("PlanAttention")]
    public async Task AttentionStripSitsAboveTheCommandRowAndAReadResultMovesNoRows()
    {
        SeedAttentionFixture();
        await Open("R01 要求事項");
        double commandRowY = 0, rowsY = 0;
        await Ui.Run(() => {
            var sheet = Sheet();
            Assert.That(AutomationProperties.GetAutomationId(Ui.Find<Grid>("PlanAttentionStrip")), Is.EqualTo("PlanAttentionStrip"));
            Assert.That(Ui.Find<Grid>("PlanAttentionStrip").ActualHeight, Is.EqualTo(40).Within(1), "The strip has a fixed height.");
            commandRowY = Ui.Find<Grid>("PlanAttentionStrip").TransformToVisual(sheet).TransformPoint(new()).Y;
            rowsY = Ui.Find<ListView>("PlanTasks").TransformToVisual(sheet).TransformPoint(new()).Y;
            Assert.That(rowsY, Is.GreaterThan(commandRowY), "The strip is above the command row and the rows.");
            Assert.That(Text("PlanReadResult"), Does.EndWith(" に GitHub から読み込み"), "The read result is at the strip's right end.");
        });
        await RefreshFromToolbar();
        await Ui.Run(() => {
            var sheet = Sheet();
            Assert.That(Ui.Find<Grid>("PlanAttentionStrip").ActualHeight, Is.EqualTo(40).Within(1));
            Assert.That(Ui.Find<ListView>("PlanTasks").TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(rowsY).Within(.1),
                "A completed read moves no row (#233 / #238).");
            Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").IsOpen, Is.False, "No result bar opens.");
            Assert.That(Text("PlanReadResult"), Does.EndWith(" に GitHub から読み込み"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PlanAttention")]
    public async Task TogglesShowTheirCountsFilterAndClearWithoutBecomingTitleText()
    {
        SeedAttentionFixture();
        await Open("R01 要求事項");
        await Ui.Run(() => {
            // #260 D0: one 遅れ count even though I2 is red and I3 is ochre.
            Assert.That(AttentionLabel("PlanAttentionLate"), Is.EqualTo("遅れ 2"));
            Assert.That(AttentionLabel("PlanAttentionCause"), Is.EqualTo("遅れの原因 1"), "I2 delays its successor.");
            Assert.That(AttentionLabel("PlanAttentionOverEstimate"), Is.EqualTo("見積超過 1"));
            Assert.That(AttentionLabel("PlanAttentionNotUpdated"), Is.EqualTo("未更新 1"));
            Assert.That(Text("PlanStatusCounts"), Is.EqualTo("要求事項 1 · タスク 3"));
        });
        await PressAttentionToggle("PlanAttentionLate");
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 4);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I1", "I2", "I3", "" }));
            Assert.That(Ui.Find<ToggleButton>("PlanAttentionLate").IsChecked, Is.True);
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("2 件"));
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Text, Is.Empty, "A count never becomes title text.");
            Assert.That(Text("PlanStatusCounts"), Is.EqualTo("person-U1 1 · person-U2 1"));
            Assert.That(Ui.Find<Button>("PlanCopyByAssignee").Visibility, Is.EqualTo(Visibility.Visible));
            // The first match is selected and in view (#227).
            Assert.That(Sheet().Selected, Is.EqualTo("I2"));
            Assert.That(Sheet().Realized.Any(row => row.Identity == "I2" && row.IsLoaded), Is.True);
        });
        await PressAttentionToggle("PlanAttentionLate");
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 5);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ToggleButton>("PlanAttentionLate").IsChecked, Is.False);
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<Button>("PlanCopyByAssignee").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Text("PlanStatusCounts"), Is.EqualTo("要求事項 1 · タスク 3"));
        });
    }

    [Test, Category("PlanAttention")]
    public async Task PersonAndPhaseFiltersAndTheTitleBoxCombineWithAnd()
    {
        SeedAttentionFixture();
        await Open("R01 要求事項");
        // 担当者 ▾: a search box over the Project's people; checking one value filters with OR inside it.
        await Ui.Run(() => Ui.Click("PlanAttentionAssignees"));
        await Ui.Ready<TextBox>("PlanAttentionAssigneeSearch");
        await Ui.Run(() => Ui.Find<TextBox>("PlanAttentionAssigneeSearch").Text = "person-U1");
        await Ui.Until(() => Ui.Tree(Ui.Root).Concat(OpenPopups()).Any(e => AutomationProperties.GetAutomationId(e) == "PlanAttentionAssignee_U1"));
        await Ui.Run(() => Ui.ToggleToggleButton("PlanAttentionAssignee_U1"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 4);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I1", "I2", "I4", "" }), "The requirement row stays as context.");
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanAttentionAssignees")), Is.EqualTo("担当者: person-U1"));
        });
        // 工程 ▾ narrows the same match set; the parts combine with AND.
        await Ui.Run(() => Ui.Click("PlanAttentionPhases"));
        await Ui.Until(() => Ui.Tree(Ui.Root).Concat(OpenPopups()).Any(e => AutomationProperties.GetAutomationId(e) == "PlanAttentionPhase_PS"));
        await Ui.Run(() => Ui.ToggleToggleButton("PlanAttentionPhase_PS"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 2);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I4", "" }));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanAttentionPhases")), Is.EqualTo("工程: PS"));
            Assert.That(Sheet().Selected, Is.EqualTo("I4"));
        });
        // The title search box is text on the same model; it ANDs with the filters.
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "設計");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 1);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "" }), "No row satisfies all three parts.");
            Assert.That(Text("PlanStatusCounts"), Is.EqualTo("条件に一致するタスクはありません"));
        });
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "");
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 2);
        await Ui.Run(() => {
            Ui.Click("PlanAttentionClearAssignees");
            Ui.Click("PlanAttentionClearPhases");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 5);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanSheetFilterCount").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Text("PlanStatusCounts"), Is.EqualTo("要求事項 1 · タスク 3"));
        });
    }

    [Test, Category("PlanAttention")]
    public async Task CopyByAssigneeCopiesTheChaseListWithIssueLinks()
    {
        SeedAttentionFixture();
        await Open("R01 要求事項");
        await PressAttentionToggle("PlanAttentionLate");
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 4);
        await Ui.Run(() => Ui.Click("PlanCopyByAssignee"));
        await Ui.Until(() => Text("PlanStatusCounts") == "担当者ごとにコピーしました");
        string text = "";
        await Ui.Run(async () => {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            Assert.That(content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text), Is.True);
            text = await content.GetTextAsync();
        });
        TestContext.Out.WriteLine("chase list: " + text.Replace("\r\n", " / "));
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.StartWith("person-U1（1）"));
            Assert.That(text, Does.Contain("#2 R01 遅れている設計 https://github.com/acme/repo/issues/2"));
            Assert.That(text, Does.Contain("person-U2（1）").And.Contain("#3 R01 後続の実装 https://github.com/acme/repo/issues/3"));
            Assert.That(text, Does.Not.Contain("I4"), "Only the matching tasks are copied.");
        });
        Assert.That(workspace.Session!.Document.State.Settings.StatusDate, Is.EqualTo(new DateOnly(2026, 10, 6)));
    }

    [Test, Category("PlanAttention")]
    public async Task SelectionBandAnswersWhyWithProgressCauseAndEstimate()
    {
        SeedAttentionFixture();
        await Open("R01 要求事項");
        await Ui.Run(() => { var sheet = Sheet(); sheet.Select("I2", PlanField.Title, false); sheet.FocusSelected(); });
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Text("PlanSheetSlip"), Does.Contain("終了日 10/3 を過ぎて未完了"), "The band names the slip.");
            Assert.That(Text("PlanSelectionProgress"), Is.EqualTo("実績と残の更新 9/14（22 日前）"));
            Assert.That(Text("PlanSheetLatenessDetail"), Is.Empty);
            var successor = Ui.Find<HyperlinkButton>("PlanSelectionSuccessorFilter");
            Assert.That(successor.Visibility, Is.EqualTo(Visibility.Visible), "I2's lateness pushes its successor.");
            Assert.That(successor.Content?.ToString(), Does.Contain("後続 1 タスクが遅れ"));
            var issue = Ui.Find<HyperlinkButton>("PlanSheetIssue");
            Assert.That(issue.Content, Is.EqualTo("acme/repo#2"));
            Assert.That(issue.TransformToVisual(Sheet()).TransformPoint(new()).X,
                Is.LessThan(Ui.Find<TextBlock>("PlanStartReason").TransformToVisual(Sheet()).TransformPoint(new()).X),
                "The Issue reference sits next to the task name.");
        });
        // 後続を表示 applies the delayed successors as a named set on the strip.
        await Ui.Run(() => Ui.Invoke("PlanSelectionSuccessorFilter"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 3);
        await Ui.Run(() => {
            Assert.That(Ui.Find<ListView>("PlanTasks").Items.Cast<string>(), Is.EqualTo(new[] { "I1", "I3", "" }));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PlanAttentionNamedSet")), Is.EqualTo("後続 1"));
        });
        await Ui.Run(() => Ui.Click("PlanAttentionClearNamed"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 5);
        // 見積超過 shows the forecast against the estimate.
        await Ui.Run(() => { var sheet = Sheet(); sheet.Select("I4", PlanField.Title, false); sheet.FocusSelected(); });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Text("PlanSelectionOverEstimate"), Is.EqualTo("見積超過 見込 12h / 見積 8h")));
    }

    [Test, Category("PlanAttention")]
    public async Task SummaryVerdictComesFirstAndItsCountButtonsOpenThePlanFiltered()
    {
        SeedAttentionFixture();
        // A Project end before the forecast makes the verdict state the worst slip in words.
        await Open("R01 要求事項");
        await Ui.Run(async () => {
            var session = workspace.Session!;
            await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {
                ProjectStart = new(2026, 9, 1), ProjectEnd = new(2026, 10, 5) }), new DateOnly(2026, 10, 6));
        });
        await Ui.Run(() => Ui.Click("PlanShowSummary"));
        await Ui.Ready<TextBlock>("SummaryVerdictHeadline");
        await Ui.Run(() => {
            var headline = Ui.Find<TextBlock>("SummaryVerdictHeadline");
            Assert.That(headline.Text, Does.Contain("稼働日遅れ"));
            Assert.That(headline.Text, Does.Contain("完了見込"), "The verdict names the worst slip first.");
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryVerdictLate")), Is.EqualTo("遅れ 2"));
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("SummaryVerdictCause")), Is.EqualTo("遅れの原因 1"));
            // The verdict is the first content line of the summary page.
            Assert.That(headline.TransformToVisual(Ui.Root).TransformPoint(new()).Y,
                Is.LessThan(Ui.Find<TextBlock>("SummaryRemainingDays").TransformToVisual(Ui.Root).TransformPoint(new()).Y));
        });
        await Ui.Run(() => Ui.Click("SummaryVerdictLate"));
        await Ui.Until(() => Ui.Find<ListView>("PlanTasks").Items.Count == 4);
        await Ui.Run(() => {
            Assert.That(Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected, Is.True);
            Assert.That(Ui.Find<ToggleButton>("PlanAttentionLate").IsChecked, Is.True, "The verdict's button presses the strip's toggle.");
            Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Text, Is.Empty);
        });
    }

    [Test, Category("PlanAttention")]
    public async Task NotUpdatedExplainsWhyItCannotBeJudged()
    {
        SeedAttentionFixture();
        await Open("R01 要求事項");
        await Ui.Run(async () => {
            var session = workspace.Session!;
            // Drop the 実績/残 mapping: without both Project fields GitHub reports no change time (#238).
            await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {
                Columns = session.Document.State.Settings.Columns.Where(c => c.Role is not (PlanField.Actual or PlanField.Remaining)).ToImmutableArray()
            }), new DateOnly(2026, 10, 6));
            Sheet().Refresh();
        });
        await Ui.Idle();
        await Ui.Run(() => {
            var toggle = Ui.Find<ToggleButton>("PlanAttentionNotUpdated");
            Assert.That(toggle.IsEnabled, Is.False);
            Assert.That(AutomationProperties.GetName(toggle), Is.EqualTo("未更新 0"));
            Assert.That(ToolTipService.GetToolTip(toggle), Is.EqualTo("実績と残のフィールドが未設定です"));
        });
    }

    private static IEnumerable<DependencyObject> OpenPopups() =>
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot).SelectMany(p => Ui.Tree(p.Child));
}
