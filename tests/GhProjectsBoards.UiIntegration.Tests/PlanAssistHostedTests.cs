using GhProjectsBoards.App;
using GhProjectsBoards.Core.Assist;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    private static void ToggleAssist()
    {
        var button = Ui.Find<ToggleButton>("PlanAssist");
        Assert.That(button.IsLoaded && button.IsEnabled, Is.True);
        ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Toggle)).Toggle();
    }
    private async Task<ScriptedAssistRuntime> EnableAssist(bool byok = false)
    {
        await Open();
        var runtime = new ScriptedAssistRuntime();
        await Ui.Run(() => { view.AssistRuntimeEdge = runtime; view.AssistSecrets = new TestAssistSecrets(); });
        await Settings();
        await Ui.Run(() => {
            Ui.Find<TextBox>("AssistModel").Text = "test-model";
            if (byok) Ui.Find<TextBox>("AssistEndpoint").Text = "https://provider.example/v1";
            Ui.Find<ComboBox>("AssistMode").SelectedIndex = byok ? 2 : 1;
            Ui.Click("PlanSettingsBack");
        });
        await Ui.Until(() => Ui.Find<ScrollViewer>("PlanSettingsScroll").Visibility == Visibility.Collapsed);
        await Ui.Run(ToggleAssist);
        await Ui.Ready<TextBox>("AssistInput");
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsLoaded && Ui.Find<Button>("AssistSend").IsEnabled);
        return runtime;
    }
    private async Task AskAssist(string text = "遅れの理由と変更を提案してください")
    {
        var answers = 0;
        await Ui.Run(() => answers = Ui.Tree(view).OfType<TextBlock>().Count(t => t.Text.Contains("計画を確認しました")));
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = text; Ui.Click("AssistSend"); });
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Count(t => t.Text.Contains("計画を確認しました")) > answers);
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsLoaded && Ui.Find<Button>("AssistSend").IsEnabled);
        await Ui.Until(() => Ui.Tree(view).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith("AssistApply") && b.IsLoaded));
    }

    [TestCase("", "auto"), TestCase("chosen-model", "chosen-model"), Category("Assist")]
    public async Task AssistCopilotModelIsSavedAndCanCheckBeforeDiscoveringSuggestions(string initial, string expected)
    {
        await Open(); var runtime = new ScriptedAssistRuntime();
        await Ui.Run(() => view.AssistRuntimeEdge = runtime);
        await Settings();
        await Ui.Run(() => {
            Ui.Find<TextBox>("AssistModel").Text = initial;
            Ui.Find<ComboBox>("AssistMode").SelectedIndex = 1;
        });
        await Ui.Until(() => Ui.Find<TextBox>("AssistModel").Text == expected);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("AssistModel").IsReadOnly, Is.False);
            Ui.Click("AssistCheck");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistSettingsResult").Text == "接続できました: " + expected);
        Assert.That(runtime.CheckModel, Is.EqualTo(expected));
        var stored = await new AssistSettingsStore(root).LoadAsync();
        Assert.That(stored.Error, Is.Null);
        Assert.That(stored.Settings.Mode, Is.EqualTo(AssistMode.Copilot));
        Assert.That(stored.Settings.Model, Is.EqualTo(expected));
        await Ui.Run(() => {
            Assert.That(Ui.Find<ComboBox>("AssistModels").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Ui.Find<ComboBox>("AssistModels").Items.Cast<string>(), Is.EqualTo(new[] { "test-model" }));
        });
    }

    [Test, Category("Assist")]
    public async Task AssistByokEmptyModelBlocksCheckWithoutSavingOrStartingRuntime()
    {
        await Open(); var runtime = new ScriptedAssistRuntime();
        await Ui.Run(() => view.AssistRuntimeEdge = runtime);
        await Settings();
        await Ui.Run(() => {
            Ui.Find<TextBox>("AssistEndpoint").Text = "https://provider.example/v1";
            Ui.Find<ComboBox>("AssistMode").SelectedIndex = 2;
            Ui.Click("AssistCheck");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistSettingsResult").Text == "モデルを入力してください。");
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("AssistModel").Text, Is.Empty));
        Assert.That(runtime.CheckModel, Is.Null);
        Assert.That(File.Exists(new AssistSettingsStore(root).FilePath), Is.False);
        await Ui.Run(() => Ui.Find<ComboBox>("AssistMode").SelectedIndex = 0);
    }

    [TestCase(1032), TestCase(640), Category("Assist")]
    public async Task AssistTurnEndShowsProposalActionsAfterReferencesInAnAlreadyLongConversation(int height)
    {
        var fake = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, fake with { Issues = [.. Enumerable.Range(1, 12).Select(i =>
            new PlanFakeIssue(new($"I{i}", i == 1 ? "設計" : $"R24 PS-{i:000} 詳細設計 データ", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true))], NextId = 13 });
        var runtime = await EnableAssist(); runtime.Propose = false;
        await Ui.Run(() => ResizeWorkspace(1920, height));
        await Ui.Until(() => Math.Abs(view.ActualHeight - height) < 1);
        runtime.Answer = "前の回答\n" + string.Join('\n', Enumerable.Repeat("担当者と日程を確認しました。", 60));
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "先に現状を確認"; Ui.Click("AssistSend"); });
        await Ui.Until(() => runtime.Prompts.Count == 1 && Ui.Find<Button>("AssistSend").IsEnabled && AssistScroll().ScrollableHeight > 500);
        runtime.Propose = true; runtime.ManyReferences = true; runtime.PauseBeforeEnd = true;
        runtime.Answer = "変更案を下に示しました。";
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "変更を提案"; Ui.Click("AssistSend"); });
        await Ui.Until(() => runtime.WaitingForEnd && AssistScroll().VerticalOffset > 500);
        runtime.EndAnswer.TrySetResult();
        await Ui.Until(() => runtime.Prompts.Count == 2 && Ui.Find<Button>("AssistSend").IsEnabled);
        await Ui.Until(() => new[] { "AssistApply", "AssistDiscard" }.All(prefix =>
            Ui.Tree(view).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == prefix + runtime.ProposalId)
                is { } button && AssistVisible(button)), () => $"offset={AssistScroll().VerticalOffset}, extent={AssistScroll().ExtentHeight}, viewport={AssistScroll().ViewportHeight}");
        await Ui.Run(async () => {
            var items = Ui.Find<ListView>("AssistMessages").Items.OfType<FrameworkElement>().ToArray();
            Assert.That(items.OfType<HyperlinkButton>().Count(), Is.EqualTo(6), "The first turn has one reference, the second has five.");
            Assert.That(items.OfType<Button>().Any(b => Equals(b.Content, "参照した 12 件を計画で表示")), Is.True);
            await RenderedEvidence.Capture(view, "assist-long-conversation-turn-end-" + height);
        });
        Assert.That(workspace.Session!.UndoCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("Assist")]
    public async Task AssistAnswerStripsSplitMarkdown()
    {
        var runtime = await EnableAssist();
        runtime.Answer = "## 計画を確認しました\n**遅れがあります**\n| 担当者 | 対応 |\n| --- | --- |\n| person-U1 | 日程を確認 |";
        await AskAssist();
        await Ui.Run(() => {
            var answer = Ui.Tree(view).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t).StartsWith("AssistAnswer"));
            Assert.That(answer.Text, Is.EqualTo("計画を確認しました\n遅れがあります\n担当者 / 対応\nperson-U1 / 日程を確認"));
        });
    }

    [Test, Category("Assist")]
    public async Task AssistFollowsTheNewProposalAfterALongAnswer()
    {
        var runtime = await EnableAssist();
        runtime.Answer = "計画を確認しました\n" + string.Join('\n', Enumerable.Repeat("担当者と日程を確認しています。", 35));
        await AskAssist("追加の提案をお願いします");
        await Ui.Until(() => AssistVisible(Ui.Find<Button>("AssistApply" + runtime.ProposalId)));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "assist-follow-proposal"));
    }

    private bool AssistVisible(FrameworkElement element)
    {
        var list = Ui.Tree(Ui.Find<Border>("PlanAssistPane")).OfType<ListView>().First();
        var rect = element.TransformToVisual(list).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
        return element.IsLoaded && element.ActualHeight > 0 && element.ActualWidth > 0
            && rect.Top >= 0 && rect.Bottom <= list.ActualHeight && rect.Right <= list.ActualWidth;
    }

    [Test, Category("Assist")]
    public async Task AssistRespectsScrollingUpUntilTheNextSend()
    {
        var runtime = await EnableAssist(); runtime.PauseBeforeEnd = true;
        runtime.Answer = "計画を確認しました\n" + string.Join('\n', Enumerable.Repeat("日程と担当者を確認しています。", 40));
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "日程を確認"; Ui.Click("AssistSend"); });
        await Ui.Until(() => runtime.WaitingForEnd && AssistScroll().VerticalOffset > 100);
        await Ui.Run(() => AssistScroll().ChangeView(null, 0, null, true));
        await Ui.Until(() => AssistScroll().VerticalOffset < 1);
        runtime.EndAnswer.TrySetResult();
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsEnabled);
        await Ui.Run(() => Assert.That(AssistScroll().VerticalOffset, Is.LessThan(1), "A new card must not pull the PMO away from earlier text."));
        runtime.PauseBeforeEnd = false; runtime.Hold = true;
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "続きを確認"; Ui.Click("AssistSend"); });
        await Ui.Until(() => runtime.Prompts.Count == 2 && AssistScroll().VerticalOffset > 100);
        await Ui.Run(() => Ui.Click("AssistStop"));
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsEnabled);
    }

    private ScrollViewer AssistScroll() => Ui.Tree(Ui.Tree(Ui.Find<Border>("PlanAssistPane")).OfType<ListView>().First()).OfType<ScrollViewer>().First();

    [TestCase(false), TestCase(true), Category("Assist")]
    public async Task AssistToolProgressKeepsStopAvailableAndClearsOnEnd(bool cancel)
    {
        var runtime = await EnableAssist(); runtime.ToolProgress = true;
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "調査してください"; Ui.Click("AssistSend"); });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistProgress").Text == "計画を調べています（3 回目）…");
        await Ui.Run(() => Assert.That(Ui.Find<Button>("AssistStop").IsEnabled && Ui.Find<Button>("AssistStop").Visibility == Visibility.Visible, Is.True));
        runtime.ContinueTools.TrySetResult();
        await Ui.Until(() => Ui.Find<TextBlock>("AssistProgress").Text == "変更案を検証しています…");
        if (cancel) await Ui.Run(() => Ui.Click("AssistStop"));
        else runtime.FinishTools.TrySetResult();
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsEnabled);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("AssistProgress").Text, Is.Empty);
            Assert.That(Ui.Find<Button>("AssistStop").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    [Test, Category("Assist")]
    public async Task AssistByokSettingsFollowTheConnectionOrderAndUseASectionHeading()
    {
        await EnableAssist(byok: true); await Settings();
        await Ui.Until(() => Ui.Find<TextBox>("AssistModel").ActualHeight > 0 && Ui.Find<PasswordBox>("AssistKey").ActualHeight > 0);
        await Ui.Run(() => {
            var ids = new[] { "AssistProvider", "AssistEndpoint", "AssistModel", "AssistWireApi", "AssistHeader", "AssistKey", "AssistCheck", "AssistSettingsResult" };
            var positions = ids.Select(id => Ui.Find<FrameworkElement>(id).TransformToVisual(view).TransformPoint(new()).Y).ToArray();
            Assert.That(positions.Zip(positions.Skip(1), (a, b) => b > a), Is.All.True, "Each connection field must occupy a later rendered row.");
            Assert.That(Ui.Find<TextBlock>("AssistSettingsHeading").Text, Is.EqualTo("AI アシスト（この PC の全プロジェクト共通）"));
            Assert.That(Ui.Find<TextBlock>("AssistSettingsHeading").FontWeight.Weight, Is.GreaterThan(Ui.Find<TextBlock>("AssistSettingsResult").FontWeight.Weight));
            Ui.Find<TextBlock>("AssistSettingsHeading").StartBringIntoView(new() { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
        await Ui.Idle();
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "assist-settings-order"));
    }

    [Test, Category("Assist")]
    public async Task AssistShowsFiveReferencesAndFiltersAllReferencedTasks()
    {
        var fake = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, fake with { Issues = [.. Enumerable.Range(1, 13).Select(i =>
            new PlanFakeIssue(new($"I{i}", i == 1 ? "設計" : i == 13 ? "検索対象外" : $"R24 PS-{i:000} 詳細設計 データ", "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U1"] }, "", true))], NextId = 14 });
        var runtime = await EnableAssist(); runtime.ManyReferences = true;
        await Ui.Run(() => ResizeWorkspace(1920, 1032));
        await Ui.Until(() => Math.Abs(view.ActualWidth - 1920) < 1);
        await Ui.Run(() => Ui.Click("PlanShowSummary"));
        await AskAssist();
        await Ui.Run(() => {
            var links = Ui.Tree(view).OfType<HyperlinkButton>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("AssistTask")).ToArray();
            Assert.That(links.Length, Is.EqualTo(5));
            Assert.That(AutomationProperties.GetName(links[0]), Does.StartWith("1 "));
            Assert.That(AutomationProperties.GetName(links[1]), Does.StartWith("12 "));
            var all = Ui.Tree(view).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b).StartsWith("AssistAllReferences"));
            Assert.That(all.Content, Is.EqualTo("参照した 12 件を計画で表示"));
            Ui.Click(AutomationProperties.GetAutomationId(all));
        });
        await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected);
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Where(id => id.Length > 0), Is.EquivalentTo(Enumerable.Range(1, 12).Select(i => $"I{i}")));
            Assert.That(Ui.Find<Border>("PlanAssistPane").Visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(Ui.Tree(view).OfType<TextBox>().Any(t => t.Text == "アシストの参照タスク"), Is.True);
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("12 件"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await Ui.Until(() => AssistVisible(Ui.Find<Button>("AssistApply" + runtime.ProposalId)));
        await Ui.Until(() => AssistVisible(Ui.Find<Border>("AssistProposal" + runtime.ProposalId)));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "assist-references-proposal"));
    }

    [Test, Category("Assist")]
    public async Task AssistReferencesNavigateToPlanAndProposalIsOneUndoWithNoRemoteWrites()
    {
        var runtime = await EnableAssist();
        var before = workspace.Session!.Document.State.Rows[0].Title;
        var undo = workspace.Session.UndoCount;
        await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Idle();
        await AskAssist();
        Assert.That(workspace.Session.Document.State.Rows[0].Title, Is.EqualTo(before));
        await Ui.Run(() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Tree(view).OfType<HyperlinkButton>().Single(b => AutomationProperties.GetAutomationId(b).StartsWith("AssistTaskI1-"))).GetPattern(PatternInterface.Invoke)).Invoke());
        await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowTasks").IsSelected);
        await Ui.Run(() => Assert.That(Ui.Tree(view).OfType<PlanSheetView>().Single().IsSelectedRow("I1"), Is.True));
        await Ui.Run(() => Ui.Click("AssistApply" + runtime.ProposalId));
        await Ui.Until(() => workspace.Session.Document.State.Rows[0].Title == "提案したタイトル");
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo + 1));
        await Ui.Until(() => Ui.Find<Button>("PlanUndo").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanUndo"));
        await Ui.Until(() => workspace.Session.Document.State.Rows[0].Title == before);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("Assist")]
    public async Task AssistCardStatesItsOwnScheduleEffectAndApplyingATitleMoveShowsNoRecalculation()
    {
        var runtime = await EnableAssist();
        // The first take-in takes GitHub's dates as the plan dates; there is no hold to release (#236).
        var readDay = workspace.Session!.Document.State.Settings.StatusDate!.Value;
        Assert.That(workspace.Session.Schedule(readDay).All(t => t.Start.Value == t.Input.Start && t.End.Value == t.Input.End), Is.True);
        await AskAssist();
        var effect = "";
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.StartsWith("適用すると、この変更で")), Is.False,
                "A title proposal moves no dates, so the card states no moved-task count.");
            effect = Ui.Tree(Ui.Find<Border>("AssistProposal" + runtime.ProposalId)).OfType<TextBlock>().Single(t => t.Text.StartsWith("日程が変わるタスク")).Text;
        });
        Assert.That(effect, Is.EqualTo("日程が変わるタスク 0 件"), "A title proposal moves no dates of its own.");
        await Ui.Run(() => Ui.Click("AssistApply" + runtime.ProposalId));
        await Ui.Until(() => workspace.Session.Document.State.Rows[0].Title == "提案したタイトル");
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanRefreshResult").IsOpen, Is.False,
            "An ordinary edit publishes unpublished dates instead of a whole-plan recalculation (#236)."));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true), Category("Assist")]
    public async Task AssistProposalDiscardOrSubsequentEditPreventsApplication(bool stale)
    {
        var runtime = await EnableAssist(); await AskAssist();
        if (!stale) {
            await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text == "提案したタイトル" && t.IsLoaded));
            await Ui.Run(async () => await RenderedEvidence.Capture(view, "assist-proposal"));
        }
        if (stale) await CommitText("PlanCell1_Title", "通常の編集");
        else await Ui.Run(() => Ui.Click("AssistDiscard" + runtime.ProposalId));
        await Ui.Until(() => !Ui.Find<Button>("AssistApply" + runtime.ProposalId).IsEnabled);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("AssistProposalState" + runtime.ProposalId).Text,
            stale ? Is.EqualTo(AssistProposal.StaleMessage) : Is.EqualTo("破棄しました。")));
        Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.Not.EqualTo("提案したタイトル"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(false), TestCase(true), Category("Assist")]
    public async Task AssistAcceptedProposalRefreshesTheScreenWhenSavingFails(bool onSummary)
    {
        var runtime = await EnableAssist(); runtime.Remaining = 16; await AskAssist();
        if (onSummary) {
            await Ui.Run(() => Ui.Click("PlanShowSummary"));
            await Ui.Until(() => Ui.Tree(view).OfType<Border>().Any(t => AutomationProperties.GetAutomationId(t) == "SummaryRequiredLoadCard" && ToolTipService.GetToolTip(t) is string tip && tip.Contains("残 8h")));
        }
        var session = workspace.Session!;
        var undo = session.UndoCount;
        using (var writer = new FileStream(new PlanStore(root).FileFor(session.Document.Project) + ".writer.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            await Ui.Run(() => Ui.Click("AssistApply" + runtime.ProposalId));
            await Ui.Until(() => Ui.Find<InfoBar>("PlanSaveFailure").IsOpen);
            if (onSummary) await Ui.Until(() => Ui.Tree(view).OfType<Border>().Any(t => AutomationProperties.GetAutomationId(t) == "SummaryRequiredLoadCard" && ToolTipService.GetToolTip(t) is string tip && tip.Contains("残 16h")));
            await Ui.Run(() => {
                Ui.Trace($"Accepted proposal screen: title={Ui.Find<TextBox>("PlanCell1_Title").Text}; apply={Ui.Find<Button>("AssistApply" + runtime.ProposalId).IsEnabled}; state={Ui.Find<TextBlock>("AssistProposalState" + runtime.ProposalId).Text}");
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Text, Is.EqualTo("提案したタイトル"));
                Assert.That(Ui.Find<Button>("AssistApply" + runtime.ProposalId).IsEnabled, Is.False);
                Assert.That(Ui.Find<Button>("AssistDiscard" + runtime.ProposalId).IsEnabled, Is.False);
                Assert.That(Ui.Find<TextBlock>("AssistProposalState" + runtime.ProposalId).Text, Is.EqualTo("適用しました。"));
                AssertFailure("PlanSaveFailure", "保存できませんでした", "保存を再試行");
                Assert.That(Ui.Find<Button>("PlanUndo").IsEnabled, Is.True);
            });
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("提案したタイトル"));
            Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            Ui.Trace("Accepted edit, visible values, disabled proposal and save-failure feedback assertions completed.");
            await Ui.Run(async () => await RenderedEvidence.Capture(view, onSummary ? "assist-summary-save-failed" : "assist-plan-save-failed"));
        }
        await Ui.Run(() => Ui.Click("PlanRetrySave"));
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanSaveFailure").IsOpen, () => Ui.Find<InfoBar>("PlanSaveFailure").Message);
        Assert.That(session.UndoCount, Is.EqualTo(undo + 1));
    }

    [Test, Category("Assist")]
    public async Task AssistLaterAnswerWithoutProposalKeepsExactlyOneEarlierCard()
    {
        var runtime = await EnableAssist(); await AskAssist(); var first = runtime.ProposalId;
        runtime.Propose = false;
        await AskAssist("説明を続けてください");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b) == "AssistApply" + first), Is.EqualTo(1));
            Assert.That(Ui.Tree(view).OfType<TextBlock>().Count(t => t.Text == "変更の提案 1 件"), Is.EqualTo(1));
            Assert.That(Ui.Find<Button>("AssistApply" + first).IsEnabled, Is.True);
        });
    }

    [TestCase(false), TestCase(true), Category("Assist")]
    public async Task AssistCloseForcesHungRuntimeAndDeletesItsStateBeforeReturning(bool alreadyEnding)
    {
        var runtime = await EnableAssist(); runtime.Hold = !alreadyEnding; runtime.HungShutdown = true;
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "終了まで待機"; Ui.Click("AssistSend"); });
        await Ui.Until(() => runtime.Prompts.Count == 1);
        Assert.That(Directory.Exists(runtime.StatePath!), Is.True);
        if (alreadyEnding) {
            await Ui.Until(() => Ui.Find<Button>("AssistSend").IsEnabled);
            await Ui.Run(() => Ui.Click("AssistNewConversation"));
            await Ui.Until(() => runtime.Disposed == 1);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        Ui.Trace($"Close returned after {watch.Elapsed.TotalMilliseconds:F0} ms; force issued={runtime.Forced}; session state exists={Directory.Exists(runtime.StatePath!)}");
        Assert.That(runtime.Forced, Is.True);
        Assert.That(Directory.Exists(runtime.StatePath!), Is.False);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3.3)));
    }

    [Test, Category("Assist")]
    public async Task AssistCompositionAndShiftEnterDoNotSendAndStopLeavesPlanEditable()
    {
        var runtime = await EnableAssist(); runtime.Hold = true;
        await Ui.Run(async () => {
            Ui.Find<TextBox>("AssistInput").Text = "変換中";
            view.AssistCompositionStarted(); await view.AssistEnter(false);
            view.AssistCompositionEnded(); await view.AssistEnter(false);
            await view.AssistEnter(true);
            Assert.That(runtime.Prompts, Is.Empty);
        });
        await Ui.LowPriorityIdle();
        await Ui.Run(() => view.AssistEnter(false));
        await Ui.Until(() => Ui.Find<Button>("AssistStop").Visibility == Visibility.Visible);
        await CommitText("PlanCell1_Title", "応答中の編集");
        await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Title == "応答中の編集");
        await Ui.Run(() => Ui.Click("AssistStop"));
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsEnabled);
        Assert.That(runtime.Prompts, Is.EqualTo(new[] { "変換中" }));
        Assert.That(workspace.Session!.Document.State.Rows[0].Title, Is.EqualTo("応答中の編集"));
    }

    [Test, Category("Assist")]
    public async Task AssistFailureHasSettingsAndRetryAndNewConversationEndsTheRuntime()
    {
        var runtime = await EnableAssist(); runtime.Fail = true;
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "質問"; Ui.Click("AssistSend"); });
        await Ui.Until(() => Ui.Find<InfoBar>("AssistFailure").IsOpen);
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("AssistFailure").Message, Is.EqualTo("接続できませんでした。")));
        runtime.Fail = false;
        await Ui.Ready<Button>("AssistRetry");
        await Ui.Run(() => Ui.Click("AssistRetry"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("計画を確認しました")));
        await Ui.Until(() => Ui.Find<Button>("AssistSend").IsEnabled);
        await Ui.Run(() => Ui.Click("AssistNewConversation"));
        await Ui.Until(() => !Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("計画を確認しました")));
        Assert.That(runtime.Disposed, Is.GreaterThanOrEqualTo(2));
    }

    [Test, Category("Assist")]
    public async Task AssistSettingsPersistOnlyValidNonsecretValuesOutsidePlanHistory()
    {
        var runtime = await EnableAssist(byok: true);
        var undo = workspace.Session!.UndoCount;
        await Settings();
        var path = Path.Combine(root, "PlanningEditor", "v1", "assist.json");
        var before = File.ReadAllText(path);
        await Ui.Run(() => {
            var endpoint = Ui.Find<TextBox>("AssistEndpoint"); endpoint.Focus(FocusState.Programmatic); endpoint.Text = "invalid";
            Ui.Find<TextBox>("AssistModel").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistSettingsResult").Text.Contains("URL"));
        Assert.That(File.ReadAllText(path), Is.EqualTo(before));
        await Ui.Run(() => {
            Ui.Find<TextBox>("AssistEndpoint").Text = "https://provider.example/v2";
            Ui.Find<PasswordBox>("AssistKey").Password = "sentinel-assist-secret";
            Ui.Click("AssistRegisterKey");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistKeyState").Text == "登録済み");
        await Ui.Run(() => {
            Assert.That(Ui.Find<PasswordBox>("AssistKey").Password, Is.Empty);
            Assert.That(Ui.Tree(view).Select(AutomationProperties.GetName), Has.None.Contains("sentinel-assist-secret"));
            Ui.Click("AssistCheck");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistSettingsResult").Text.StartsWith("接続できました"),
            () => Ui.Find<TextBlock>("AssistSettingsResult").Text);
        Assert.That(runtime.CheckKey, Is.EqualTo("sentinel-assist-secret"));
        Assert.That(File.ReadAllText(path), Does.Not.Contain("sentinel-assist-secret").And.Contain("/v2"));
        await Ui.Run(() => Ui.Click("AssistDeleteKey"));
        await Ui.Until(() => Ui.Find<TextBlock>("AssistKeyState").Text == "未登録");
        Assert.That(workspace.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("Assist")]
    public async Task AssistKeyRegistrationReplacementAndDeletionNeverExposeTheKey()
    {
        var runtime = await EnableAssist(byok: true);
        await Settings();
        var original = File.ReadAllText(Path.Combine(root, "PlanningEditor", "v1", "assist.json"));
        await Ui.Run(() => {
            var model = Ui.Find<TextBox>("AssistModel"); model.Focus(FocusState.Programmatic); model.Text = "";
            Ui.Find<TextBox>("AssistEndpoint").Focus(FocusState.Programmatic);
        });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistSettingsResult").Text.Contains("モデル"));
        Assert.That(File.ReadAllText(Path.Combine(root, "PlanningEditor", "v1", "assist.json")), Is.EqualTo(original));
        await Ui.Run(() => { Ui.Find<TextBox>("AssistModel").Text = "test-model"; Ui.Click("AssistCheck"); });
        await Ui.Until(() => Ui.Find<TextBlock>("AssistSettingsResult").Text.StartsWith("接続できました"));
        foreach (var key in new[] { "first-sentinel", "replacement-sentinel" }) {
            await Ui.Ready<PasswordBox>("AssistKey");
            await Ui.Run(() => { Ui.Find<PasswordBox>("AssistKey").Password = key; Ui.Click("AssistRegisterKey"); });
            await Ui.Until(() => Ui.Find<TextBlock>("AssistKeyState").Text == "登録済み");
            await Ui.Run(() => Ui.Click("AssistCheck"));
            await Ui.Until(() => runtime.CheckKey == key && Ui.Find<Button>("AssistCheck").IsEnabled);
            await Ui.Run(() => {
                Assert.That(Ui.Find<PasswordBox>("AssistKey").Password, Is.Empty);
                Assert.That(Ui.Tree(view).Select(AutomationProperties.GetName), Has.None.Contains(key));
            });
            Assert.That(File.ReadAllText(Path.Combine(root, "PlanningEditor", "v1", "assist.json")), Does.Not.Contain(key));
        }
        await Ui.Run(() => Ui.Click("AssistDeleteKey"));
        await Ui.Until(() => Ui.Find<TextBlock>("AssistKeyState").Text == "未登録");
        await Ui.Run(() => Assert.That(view.AssistSecrets.Read(), Is.Null));
        Assert.That(workspace.Session!.UndoCount, Is.Zero);
    }

    [TestCase("project"), TestCase("connection"), TestCase("close"), Category("Assist")]
    public async Task AssistConversationEndsWhenItsWorkspaceLifetimeEnds(string change)
    {
        var runtime = await EnableAssist(); runtime.Hold = true;
        await Ui.Run(() => { Ui.Find<TextBox>("AssistInput").Text = "待機中"; Ui.Click("AssistSend"); });
        await Ui.Until(() => runtime.Prompts.Count == 1);
        if (change == "close") await Ui.Run(async () => Assert.That(await view.StopAsync(), Is.True));
        else if (change == "connection") {
            await PickerCommand("PlanConnection"); await Ui.Run(() => Ui.Click("PlanConnect"));
        } else {
            await PickerCommand("PlanChooseProject");
            await ProjectChooserTestActions.SelectOwner("acme");
            await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0);
        }
        await Ui.Until(() => runtime.Disposed == 1);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("Assist")]
    public async Task AssistSelectedTaskCanBeRemovedBeforeSending()
    {
        var runtime = await EnableAssist();
        await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().Select("I1", PlanField.Title, false));
        await Ui.Ready<Button>("AssistSelectedTask");
        await Ui.Run(() => Ui.Click("AssistSelectedTask"));
        await AskAssist();
        Assert.That(runtime.Selected, Is.EqualTo(new string?[] { null }));
    }

    [Test, Category("Assist")]
    public async Task AssistSendsNoSelectedTaskWhenTheSheetInputRowIsSelected()
    {
        var runtime = await EnableAssist();
        await Ui.Run(() => Ui.Tree(view).OfType<PlanSheetView>().Single().Select("", PlanField.Title, false));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<Button>("AssistSelectedTask").Visibility, Is.EqualTo(Visibility.Collapsed)));
        await AskAssist();
        Assert.That(runtime.Selected, Is.EqualTo(new string?[] { null }));
    }

    [Test, Category("Assist")]
    public async Task AssistDockKeepsDefaultSheetColumnsVisibleAt1920AndAlsoOpensOnSummary()
    {
        await EnableAssist();
        await Ui.Run(() => ResizeWorkspace(1920, 1032));
        await Ui.Until(() => Math.Abs(view.ActualWidth - 1920) < 1);
        await Ui.Run(() => {
            var pane = Ui.Find<Border>("PlanAssistPane");
            Assert.That(pane.ActualWidth, Is.EqualTo(380).Within(1));
            var horizontal = Ui.Find<ScrollViewer>("PlanSheetHorizontal");
            Assert.That(horizontal.ScrollableWidth, Is.Zero);
            Assert.That(Ui.Find<ScrollViewer>("PlanGanttHorizontal").ViewportWidth, Is.GreaterThanOrEqualTo(320));
            Assert.That(AutomationProperties.GetName(Ui.Find<ToggleButton>("PlanAssist")), Is.EqualTo("アシスト"));
        });
        await AskAssist();
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "assist-plan-1920"));
        await Ui.Run(() => Ui.Click("PlanShowSummary"));
        await Ui.Until(() => Ui.Find<SelectorBarItem>("PlanShowSummary").IsSelected);
        await Ui.Run(async () => {
            Assert.That(Ui.Find<Border>("PlanAssistPane").Visibility, Is.EqualTo(Visibility.Visible));
            await RenderedEvidence.Capture(view, "assist-summary-1920");
        });
    }

    [Test, Category("Assist")]
    public async Task AssistNewProposalReplacesTheEarlierCardWithoutEditingThePlan()
    {
        var runtime = await EnableAssist(); await AskAssist(); var first = runtime.ProposalId;
        await AskAssist("別の案をください"); var second = runtime.ProposalId;
        await Ui.Ready<Button>("AssistApply" + second);
        await Ui.Run(() => {
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(Ui.Find<Button>("AssistApply" + first).IsEnabled, Is.False);
            Assert.That(Ui.Find<Button>("AssistApply" + second).IsEnabled, Is.True);
            var links = Ui.Tree(view).OfType<HyperlinkButton>().Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith("AssistTaskI1-")).ToArray();
            Assert.That(links, Has.Length.EqualTo(2)); Assert.That(links.Distinct().Count(), Is.EqualTo(2));
        });
        Assert.That(workspace.Session!.UndoCount, Is.Zero);
    }

    private sealed class TestAssistSecrets : IAssistSecretStore
    {
        private string? key;
        public bool IsRegistered => key is not null;
        public string? Read() => key;
        public void Store(string value) => key = value;
        public void Delete() => key = null;
    }
    private sealed class ScriptedAssistRuntime : IAssistRuntime
    {
        internal bool Hold, Fail;
        internal int Disposed;
        internal bool Propose = true;
        internal string? Answer;
        internal bool ManyReferences;
        internal bool ToolProgress;
        internal bool PauseBeforeEnd, WaitingForEnd;
        internal readonly TaskCompletionSource EndAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ContinueTools = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinishTools = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal decimal? Remaining;
        internal bool HungShutdown, Forced;
        internal string? StatePath;
        internal string? ProposalId, CheckKey, CheckModel;
        internal readonly List<string> Prompts = [];
        internal readonly List<string?> Selected = [];
        public IAssistTurn Create(AssistSettings settings, string root, string host, string login, AssistPlanTools tools, string? key) => new Turn(this, tools, root);
        public Task<AssistConnectionCheck> Check(AssistSettings settings, string root, string host, string login, string? key, CancellationToken cancellation) {
            CheckKey = key; CheckModel = settings.Model; return Task.FromResult(new AssistConnectionCheck(true, settings.Model, null, ["test-model"]));
        }
        private sealed class Turn(ScriptedAssistRuntime owner, AssistPlanTools tools, string root) : IAssistTurn
        {
            private readonly TaskCompletionSource killed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private AssistRuntimeDirectory? state;
            public async IAsyncEnumerable<AssistEvent> Send(string text, string? selected, [EnumeratorCancellation] CancellationToken cancellation)
            {
                if (owner.HungShutdown) {
                    state = new AssistRuntimeDirectory(root, Guid.NewGuid().ToString("N"));
                    owner.StatePath = state.Path;
                    File.WriteAllText(Path.Combine(state.Path, "session.json"), "temporary conversation");
                }
                owner.Prompts.Add(text); owner.Selected.Add(selected); tools.BeginTurn();
                if (owner.Hold) {
                    if (owner.HungShutdown) await killed.Task;
                    else await Task.Delay(Timeout.Infinite, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                }
                if (owner.Fail) { yield return new(AssistEventKind.Error, Failure: new(AssistFailureKind.Unreachable, "接続できませんでした。", "再試行")); yield break; }
                if (owner.ToolProgress) {
                    yield return new(AssistEventKind.Progress, "計画を調べています（3 回目）…");
                    await owner.ContinueTools.Task.WaitAsync(cancellation);
                    yield return new(AssistEventKind.Progress, "変更案を検証しています…");
                    await owner.FinishTools.Task.WaitAsync(cancellation);
                }
                if (owner.ManyReferences) { tools.FindTasks(new() { Title = "詳細設計", Limit = 100 }); tools.GetTasks(["I12"]); }
                else tools.GetTasks(["I1"]);
                if (owner.Propose) {
                    var changes = new List<AssistEdit> { new("I1", PlanField.Title, JsonSerializer.SerializeToElement("提案したタイトル")) };
                    if (owner.ManyReferences) changes.Add(new("I12", PlanField.Remaining, JsonSerializer.SerializeToElement(16m)));
                    if (owner.Remaining is { } remaining) changes.Add(new("I1", PlanField.Remaining, JsonSerializer.SerializeToElement(remaining)));
                    owner.ProposalId = tools.ProposeChanges(changes).Proposal!.Id;
                }
                var answer = owner.Answer ?? "計画を確認しました";
                foreach (var character in answer) {
                    yield return new(AssistEventKind.Delta, character.ToString());
                    await Task.Yield();
                }
                if (owner.PauseBeforeEnd) { owner.WaitingForEnd = true; await owner.EndAnswer.Task.WaitAsync(cancellation); }
                yield return new(AssistEventKind.TurnEnd, References: tools.References, Proposal: tools.TurnProposal);
            }
            public async ValueTask DisposeAsync() {
                owner.Disposed++;
                if (!owner.HungShutdown) return;
                using var ownedState = state;
                var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await AssistConversation.StopOwnedRuntime(() => hung.Task,
                    () => { owner.Forced = true; killed.TrySetResult(); hung.TrySetResult(); return Task.CompletedTask; },
                    () => ValueTask.CompletedTask, ownedState);
            }
        }
    }

    [Test, Category("Assist")]
    public async Task DisabledAssistOpensSettingsAndReturnsToTheOpenPane()
    {
        await Open();
        await Ui.Run(() => {
            Assert.That(Ui.Find<Border>("PlanAssistPane").Visibility, Is.EqualTo(Visibility.Collapsed));
            ToggleAssist();
        });
        await Ui.Ready<Button>("AssistOpenSettings");
        await Ui.Run(() => Ui.Click("AssistOpenSettings"));
        await Ui.Ready<ComboBox>("AssistMode");
        await Ui.Run(() => Ui.Click("PlanSettingsBack"));
        await Ui.Until(() => Ui.Find<Border>("PlanAssistPane").Visibility == Visibility.Visible);
        await Ui.Run(ToggleAssist);
        await Ui.Until(() => Ui.Find<Border>("PlanAssistPane").Visibility == Visibility.Collapsed);
        Assert.That(workspace.Session!.UndoCount, Is.Zero);
    }
}
