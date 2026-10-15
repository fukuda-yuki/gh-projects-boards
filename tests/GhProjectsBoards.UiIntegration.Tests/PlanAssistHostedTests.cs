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
            Ui.Find<ComboBox>("AssistMode").SelectedIndex = byok ? 2 : 1;
            Ui.Find<TextBox>("AssistModel").Text = "test-model";
            if (byok) Ui.Find<TextBox>("AssistEndpoint").Text = "https://provider.example/v1";
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
            await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
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
        internal string? ProposalId, CheckKey;
        internal readonly List<string> Prompts = [];
        internal readonly List<string?> Selected = [];
        public IAssistTurn Create(AssistSettings settings, string root, string host, string login, AssistPlanTools tools, string? key) => new Turn(this, tools);
        public Task<AssistConnectionCheck> Check(AssistSettings settings, string root, string host, string login, string? key, CancellationToken cancellation) {
            CheckKey = key; return Task.FromResult(new AssistConnectionCheck(true, settings.Model, null, ["test-model"]));
        }
        private sealed class Turn(ScriptedAssistRuntime owner, AssistPlanTools tools) : IAssistTurn
        {
            public async IAsyncEnumerable<AssistEvent> Send(string text, string? selected, [EnumeratorCancellation] CancellationToken cancellation)
            {
                owner.Prompts.Add(text); owner.Selected.Add(selected); tools.BeginTurn();
                if (owner.Hold) await Task.Delay(Timeout.Infinite, cancellation);
                if (owner.Fail) { yield return new(AssistEventKind.Error, Failure: new(AssistFailureKind.Unreachable, "接続できませんでした。", "再試行")); yield break; }
                var tasks = tools.GetTasks(["I1"]);
                var proposal = tools.ProposeChanges([new("I1", PlanField.Title, JsonSerializer.SerializeToElement("提案したタイトル"))]).Proposal!;
                owner.ProposalId = proposal.Id;
                yield return new(AssistEventKind.Delta, "計画を");
                await Task.Yield();
                yield return new(AssistEventKind.Delta, "確認しました");
                yield return new(AssistEventKind.TurnEnd, References: tools.References, Proposal: proposal);
            }
            public ValueTask DisposeAsync() { owner.Disposed++; return ValueTask.CompletedTask; }
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
