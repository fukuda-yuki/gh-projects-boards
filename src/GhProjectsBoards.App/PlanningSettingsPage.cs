using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private Grid? planningSettingsPage;
    private TaskCompletionSource<bool>? planningSettingsCompletion;
    private Action<bool>? closePlanningSettings;
    private bool planningSettingsDirty, planningSettingsSaving, leavingPlanningSettings;
    private ProjectView planningReturnView;
    internal bool PlanningSettingsOpen => planningSettingsPage is not null;
    internal event Action? PlanningSettingsChanged;
    private sealed class PlanningInputException(Control target, string message, Func<bool> corrected, Expander? section = null) : InvalidOperationException(message)
    {
        internal Control Target { get; } = target;
        internal bool Corrected() => corrected();
        internal Expander? Section { get; } = section;
    }
    private static bool ValidPlanningInput(Action read)
    {
        try { read(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal async Task<bool> ConfirmLeavePlanningSettingsAsync()
    {
        if (!PlanningSettingsOpen) return true;
        if (planningSettingsSaving || !CanRefresh) return false;
        if (planningSettingsDirty)
        {
            var dialog = new ContentDialog {
                XamlRoot = XamlRoot, Title = "計画の前提は未保存です",
                Content = $"{registration.Snapshot.Title} の設定候補を破棄して移動しますか。表の入力は保持します。",
                PrimaryButtonText = "破棄して移動", CloseButtonText = "編集を続ける", DefaultButton = ContentDialogButton.Close
            };
            AutomationProperties.SetAutomationId(dialog, "LeavePlanningSettings");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        leavingPlanningSettings = true; closePlanningSettings?.Invoke(false);
        return true;
    }

    internal async Task<bool> ShowPlanningSettingsAsync()
    {
        if (planningSettingsCompletion is { } existing) return await existing.Task;
        if (!CanRefresh) { ShowOperationProblem("IME入力を確定・取消してから計画の前提を開いてください。"); return false; }
        var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return false;
        var work = session.Workspace;
        var plan = work.Planning(projectId) ?? new(3, projectId, 0, null, null, [],
            new("official-2025-2027", PlanningContract.BundledHolidays(), false, []), [], []);
        var expected = work.Revision;
        var completion = planningSettingsCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        planningSettingsDirty = leavingPlanningSettings = false;
        planningReturnView = CurrentProjectView;
        var returnSelection = ViewSelection;
        var returnFocus = FocusManager.GetFocusedElement(XamlRoot) as Control;
        var hidden = Children.ToDictionary(c => c, c => c.Visibility);
        foreach (var child in hidden.Keys) child.Visibility = Visibility.Collapsed;
        var page = planningSettingsPage = new Grid { Padding = new(20, 12, 20, 12), RowSpacing = 12 };
        AutomationProperties.SetAutomationId(page, "PlanningSettingsPage");
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new());
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock { Text = "計画の前提", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        heading.Children.Add(new TextBlock { Text = $"Project「{registration.Snapshot.Title}」全体に適用します。保存すると自動計算の日程を見直します。", TextWrapping = TextWrapping.Wrap });
        page.Children.Add(heading);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        PlanningInputException? inputProblem = null;
        void ClearInputProblem()
        {
            if (inputProblem is null) return;
            if (inputProblem.Target is TextBox text) text.Description = null;
            if (inputProblem.Target is ComboBox box) box.Description = null;
            inputProblem = null; status.Text = ""; status.Visibility = Visibility.Collapsed;
        }
        AutomationProperties.SetAutomationId(status, "PlanningStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        SetRow(status, 1); page.Children.Add(status);
        var content = new StackPanel { Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        var scroll = new ScrollViewer { Content = content, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        SetRow(scroll, 2); page.Children.Add(scroll);
        void Changed()
        {
            planningSettingsDirty = true;
            if (inputProblem?.Corrected() == true) ClearInputProblem();
        }
        var start = new MinuteEditor("Project開始（日本時間）", "PlanProjectStart", DateText(plan.Start));
        var cutoff = new MinuteEditor("再計画の基準日時（日本時間）", "PlanCutoff", DateText(plan.Cutoff));
        TrackContextInput(start.Input); TrackContextInput(cutoff.Input);
        start.Edited += Changed; cutoff.Edited += Changed;
        content.Children.Add(start);
        content.Children.Add(new TextBlock { Text = "自動計算の開始点です。日時を指定するタスクは、タスクの日程から編集できます。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(cutoff);
        content.Children.Add(new TextBlock { Text = "進行中・再開したタスクの残作業を、いつ以降に計画するか。実績の報告日とは別です。", TextWrapping = TextWrapping.Wrap });
        var fields = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "GitHubの列との対応", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "見積時間を対応づけると表で入力できます。既存の列を確認して選んでください。その他の列は必要になった時に設定できます。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(fields);
        var mappings = new Dictionary<string, ComboBox>();
        foreach (var role in PlanningContract.Roles)
        {
            var name = role switch { "Estimate" => "見積時間", "Remaining" => "残時間", "Actual" => "実績合計", "Start" => "開始日", _ => "終了日" };
            var box = new FormComboBox { Header = name, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(box, "PlanField-" + role);
            box.Items.Add(new ComboBoxItem { Content = "未設定", Tag = "" });
            foreach (var f in registration.Snapshot.Fields.Where(f => f.ValueOwner == FieldOwner.ProjectItem
                && f.DataType == (role is "Start" or "Finish" ? "DATE" : "NUMBER") && f.Availability == ValueAvailability.Present))
                box.Items.Add(new ComboBoxItem { Content = f.Name + (registration.Snapshot.Fields.Count(other => other.Name == f.Name) > 1 ? " [" + f.Id.NodeId + "]" : ""), Tag = f.Id.NodeId });
            var id = plan.Fields.SingleOrDefault(f => f.Role == role)?.FieldId ?? "";
            // Retain a missing mapping visibly; opening settings must not silently clear it.
            if (id.Length > 0 && !box.Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag == id))
                box.Items.Add(new ComboBoxItem { Content = id + "（未確認の列）", Tag = id });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == id);
            box.SelectionChanged += (_, _) => Changed();
            fields.Children.Add(box); mappings.Add(role, box);
        }
        content.Children.Add(new TextBlock { Text = $"通常は平日9–13時・14–18時。採用祝日 {plan.Calendar.Holidays.FirstYear}–{plan.Calendar.Holidays.LastYear}。", TextWrapping = TextWrapping.Wrap });
        var extras = PlanningSettings(content, plan, Changed);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "保存して戻る", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "取消して戻る" };
        AutomationProperties.SetAutomationId(save, "PlanSettingsSave"); AutomationProperties.SetAutomationId(cancel, "PlanSettingsCancel");
        actions.Children.Add(save); actions.Children.Add(cancel); SetRow(actions, 3); page.Children.Add(actions);
        closePlanningSettings = saved => {
            if (!ReferenceEquals(planningSettingsPage, page)) return;
            ClearInputProblem();
            Children.Remove(page); planningSettingsPage = null; planningSettingsCompletion = null; closePlanningSettings = null;
            foreach (var (child, visibility) in hidden) child.Visibility = visibility;
            if (saved) { layout = session.Workspace.Columns(registration); RebuildRows(); RestoreSelection(returnSelection); }
            Update(); PlanningSettingsChanged?.Invoke();
            if (returnFocus is { IsLoaded: true, IsEnabled: true }) returnFocus.Focus(FocusState.Programmatic);
            completion.TrySetResult(saved);
        };
        cancel.Click += (_, _) => { if (!planningSettingsSaving) closePlanningSettings?.Invoke(false); };
        save.Click += async (_, _) => {
            if (planningSettingsSaving) return;
            ClearInputProblem();
            status.Visibility = Visibility.Visible;
            if (!CanRefresh) { status.Text = "IME入力を確定・取消してから保存してください。"; return; }
            try
            {
                foreach (var input in Descendants(content).OfType<Control>())
                {
                    if (input is TextBox text) text.Description = null;
                    if (input is ComboBox box) box.Description = null;
                }
                DateTime? Date(MinuteEditor editor, string label)
                {
                    try { return PlanningDate(editor.Text); }
                    catch (InvalidOperationException) { throw new PlanningInputException(editor.Input,
                        label + "：日付と時刻を選択するか、yyyy-MM-dd HH:mmで入力してください。", () => ValidPlanningInput(() => PlanningDate(editor.Text))); }
                }
                var first = Date(start, "Project開始"); var cut = Date(cutoff, "再計画の基準日時");
                var bindings = new List<PlanningFieldBinding>();
                foreach (var (role, box) in mappings)
                {
                    var id = (string)((ComboBoxItem)box.SelectedItem).Tag;
                    if (id.Length == 0) continue;
                    if (bindings.Any(b => b.FieldId == id)) throw new PlanningInputException(box, box.Header + "：同じGitHub列を複数の用途には使えません。",
                        () => (string)((ComboBoxItem)box.SelectedItem).Tag == "" || mappings.Values.Count(other =>
                            (string)((ComboBoxItem)other.SelectedItem).Tag == (string)((ComboBoxItem)box.SelectedItem).Tag) == 1);
                    bindings.Add(new(role, id, role is "Start" or "Finish" ? "DATE" : "NUMBER"));
                }
                var extra = extras();
                var candidate = EditingWorkspace.UpgradeAssignmentContract(plan) with { Start = first, Cutoff = cut, Fields = bindings.ToArray(), People = extra.People, Calendar = extra.Calendar };
                planningSettingsSaving = true; scroll.IsEnabled = save.IsEnabled = cancel.IsEnabled = false;
                status.Text = "ローカルに保存中…";
                string? candidateProblem = null;
                var ok = await session.CommitAsync(w => {
                    try { w.CommitPlanning(registration, candidate, expected); }
                    catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { candidateProblem = error.Message; throw; }
                    return w;
                },
                    () => IsLoaded && ReferenceEquals(planningSettingsPage, page) && CanRefresh);
                if (!ok) { status.Text = candidateProblem ?? "保存できませんでした。設定候補を保持しています。保存先と作業状態を確認して再試行してください。"; return; }
                closePlanningSettings?.Invoke(true);
            }
            catch (PlanningInputException error)
            {
                status.Text = "⚠ 入力を確認してください\n" + error.Message;
                if (error.Section is { } section) section.IsExpanded = true;
                if (error.Target.Parent is MinuteEditor editor) editor.RevealInput();
                inputProblem = error;
                if (error.Target is TextBox text) text.Description = "入力を確認：" + error.Message;
                if (error.Target is ComboBox box) box.Description = "入力を確認：" + error.Message;
                page.UpdateLayout(); error.Target.Focus(FocusState.Programmatic);
                error.Target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { status.Text = error.Message; }
            finally { planningSettingsSaving = false; scroll.IsEnabled = save.IsEnabled = cancel.IsEnabled = true; }
        };
        SetRowSpan(page, RowDefinitions.Count); Children.Add(page);
        page.Loaded += (_, _) => start.DefaultInput.Focus(FocusState.Programmatic);
        page.Unloaded += (_, _) => { if (!IsLoaded) { planningSettingsCompletion?.TrySetResult(false); planningSettingsCompletion = null; } };
        PlanningSettingsChanged?.Invoke();
        return await completion.Task;
    }
}
