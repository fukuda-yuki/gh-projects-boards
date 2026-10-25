using GhProjectsBoards.Core.Assist;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private void RenderAssistSettings()
    {
        var section = SettingsSection("assist", "AI アシスト", "AssistSettingsHeading", out var heading);
        assistSettingsHeading = heading;
        var mode = Id(new ComboBox { Header = "AI の接続先", ItemsSource = new[] { "使わない", "GitHub Copilot", "API キー（BYOK）" }, SelectedIndex = (int)assistSettings.Mode }, "AssistMode");
        var provider = Id(new ComboBox { Header = "種類", ItemsSource = new[] { "OpenAI 互換", "Anthropic", "Azure OpenAI" }, SelectedIndex = (int)assistSettings.Provider }, "AssistProvider");
        var wire = Id(new ComboBox { Header = "API 形式", ItemsSource = new[] { "Chat Completions", "Responses" }, SelectedIndex = (int)assistSettings.WireApi }, "AssistWireApi");
        var endpoint = Id(new TextBox { Header = "エンドポイント URL", Text = assistSettings.Endpoint }, "AssistEndpoint");
        var model = Id(new TextBox { Header = "モデル", Text = assistSettings.Model }, "AssistModel");
        var suggestions = Id(new ComboBox { Header = "利用できるモデル", Visibility = Visibility.Collapsed }, "AssistModels");
        var header = Id(new TextBox { Header = "会話 ID ヘッダー（省略可）", Text = assistSettings.ConversationIdHeader ?? "" }, "AssistHeader");
        var password = Id(new PasswordBox { Header = "API キー", PasswordRevealMode = PasswordRevealMode.Hidden }, "AssistKey");
        var keyState = Id(Label(""), "AssistKeyState");
        var feedback = Id(Label(assistSettingsError ?? ""), "AssistSettingsResult");
        AutomationProperties.SetLiveSetting(feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var enabled = new StackPanel { Spacing = 8 };
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var register = Id(new Button { Content = "登録" }, "AssistRegisterKey");
        var delete = Id(new Button { Content = "削除" }, "AssistDeleteKey");
        keyButtons.Children.Add(register); keyButtons.Children.Add(delete);
        FrameworkElement[] byok = [provider, endpoint, wire, header, password, keyState, keyButtons];
        foreach (var control in new FrameworkElement[] { provider, endpoint, model, suggestions, wire, header, password, keyState, keyButtons })
            enabled.Children.Add(control);
        var check = Id(new Button { Content = "接続を確認" }, "AssistCheck"); enabled.Children.Add(check);
        section.Children.Add(mode); section.Children.Add(enabled); section.Children.Add(feedback);
        var generation = settingsGeneration;
        var composing = new HashSet<TextBox>(); var ended = new HashSet<TextBox>();
        var accepted = assistSettings;
        var invalidDraft = false;
        AssistSettings Draft() => mode.SelectedIndex == 0 ? accepted with { Mode = AssistMode.None } : new() {
            Mode = (AssistMode)mode.SelectedIndex, Provider = (AssistProvider)provider.SelectedIndex,
            WireApi = (AssistWireApi)wire.SelectedIndex, Endpoint = mode.SelectedIndex == 2 ? endpoint.Text.Trim() : accepted.Endpoint, Model = model.Text.Trim(),
            ConversationIdHeader = mode.SelectedIndex != 2 ? accepted.ConversationIdHeader : string.IsNullOrWhiteSpace(header.Text) ? null : header.Text.Trim() };
        void ShowModes() {
            enabled.Visibility = mode.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var control in byok) control.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            wire.Visibility = mode.SelectedIndex == 2 && provider.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (mode.SelectedIndex != 1) suggestions.Visibility = Visibility.Collapsed;
        }
        void Registered() {
            try { var registered = AssistSecrets.IsRegistered; keyState.Text = registered ? "登録済み" : "未登録"; delete.IsEnabled = registered; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { feedback.Text = "API キーの登録状態を確認できませんでした。資格情報へのアクセスを確認してください。"; }
        }
        async Task Save(bool navigation = false) {
            if (generation != settingsGeneration) return;
            if (composing.Count > 0 || ended.Count > 0) {
                if (navigation) throw new InvalidOperationException("入力中の変換を確定してください。");
                return;
            }
            var draft = Draft();
            if (draft == accepted) {
                if (invalidDraft) { feedback.Text = assistSettingsError ?? ""; invalidDraft = false; AutomationProperties.SetHelpText(model, ""); AutomationProperties.SetHelpText(endpoint, ""); }
                return;
            }
            try {
                draft.Validate(); await assistStore.SaveAsync(draft); accepted = assistSettings = draft;
                assistSettingsError = null; feedback.Text = ""; invalidDraft = false;
                AutomationProperties.SetHelpText(model, ""); AutomationProperties.SetHelpText(endpoint, "");
                await EndAssist(); ClearAssistMessages(); RenderAssist();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) {
                invalidDraft = true;
                feedback.Text = ex is ArgumentException ? ex.Message : "AI アシストの設定を保存できませんでした。assist.json を確認してください。";
                AutomationProperties.SetHelpText(model, feedback.Text); AutomationProperties.SetHelpText(endpoint, feedback.Text);
                if (navigation) throw new InvalidOperationException(feedback.Text);
            }
        }
        pendingSettings.Add(() => Save(navigation: true));
        foreach (var input in new[] { model, endpoint, header }) {
            input.TextCompositionStarted += (_, _) => composing.Add(input);
            input.TextCompositionEnded += (_, _) => {
                composing.Remove(input); ended.Add(input);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ended.Remove(input));
            };
            input.KeyDown += async (_, e) => {
                if (composing.Contains(input) || ended.Contains(input)) return;
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Run(() => Save(), commitPending: false); }
            };
            input.LostFocus += async (_, _) => { if (!rendering) await Run(() => Save(), commitPending: false); };
        }
        foreach (var combo in new[] { mode, provider, wire }) combo.SelectionChanged += async (sender, _) => {
            if (ReferenceEquals(sender, mode) && mode.SelectedIndex == 1 && string.IsNullOrWhiteSpace(model.Text)) model.Text = "auto";
            ShowModes(); await Run(() => Save(), commitPending: false);
        };
        suggestions.SelectionChanged += async (_, _) => {
            if (suggestions.SelectedItem is string suggestion) { model.Text = suggestion; await Run(() => Save(), commitPending: false); }
        };
        register.Click += async (_, _) => {
            if (password.Password.Length == 0) return;
            try { AssistSecrets.Store(password.Password); password.Password = ""; await EndAssist(); ClearAssistMessages(); Registered(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { password.Password = ""; feedback.Text = "API キーを登録できませんでした。資格情報へのアクセスを確認してください。"; }
        };
        delete.Click += async (_, _) => {
            try { AssistSecrets.Delete(); password.Password = ""; await EndAssist(); ClearAssistMessages(); Registered(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { feedback.Text = "API キーを削除できませんでした。資格情報へのアクセスを確認してください。"; }
        };
        check.Click += async (_, _) => {
            await Run(() => Save(), commitPending: false);
            if (closing || generation != settingsGeneration || Draft() != accepted || assistSettings.Mode == AssistMode.None || workspace.Context is not { } context) return;
            check.IsEnabled = false; feedback.Text = "接続を確認しています…";
            assistCheckCancellation?.Cancel(); var cancellation = assistCheckCancellation = new();
            try {
                var checkTask = AssistRuntimeEdge.Check(assistSettings, assistDataRoot, context.Host, context.Login,
                    assistSettings.Mode == AssistMode.Byok ? AssistSecrets.Read() : null, cancellation.Token);
                assistCheckTask = checkTask;
                var result = await checkTask;
                if (generation != settingsGeneration || closing || cancellation.IsCancellationRequested) return;
                feedback.Text = result.Succeeded ? $"接続できました: {result.Model}" : result.Failure?.Message ?? "接続できませんでした。";
                suggestions.ItemsSource = result.Models; suggestions.Visibility = mode.SelectedIndex == 1 && result.Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { if (!closing && generation == settingsGeneration) feedback.Text = "接続確認を停止しました。"; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { if (!closing && generation == settingsGeneration) feedback.Text = "接続を確認できませんでした。設定と API キーを確認してください。"; }
            finally { if (!closing && generation == settingsGeneration) check.IsEnabled = true; if (ReferenceEquals(assistCheckCancellation, cancellation)) assistCheckCancellation = null; cancellation.Dispose(); }
        };
        ShowModes(); if (assistSettings.Mode == AssistMode.Byok) Registered();
        mode.SelectionChanged += (_, _) => { if (mode.SelectedIndex == 2) Registered(); };
    }
}
