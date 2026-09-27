using System.Globalization;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private TextBox PlanningText(StackPanel panel, string label, string id, string? value)
    {
        var input = new TextBox { Header = label, Text = value ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        TrackContextInput(input);
        AutomationProperties.SetAutomationId(input, id); panel.Children.Add(input); return input;
    }
    private static string DateText(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
    private static DateTime? PlanningDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException("日時は yyyy-MM-dd HH:mm（日本時間）で入力してください。");
        return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
    }
    private async Task PlanningDialogAsync(bool settings, bool progressCorrection = false)
    {
        if (settings || session.Workspace.Planning(projectId) is null) { await ShowPlanningSettingsAsync(); return; }
        if (!CanRefresh) { ShowOperationProblem("IME入力を確定・取消してから計画を開いてください。"); return; }
        var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var work = session.Workspace;
        var saved = work.Planning(projectId);
        if (!active || currentRow >= rows.Length) { ShowOperationProblem("計画する行を選択してください。"); return; }
        if (!rows[currentRow].IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == rows[currentRow].ItemId
            && i.Kind == ProjectItemKind.Issue && i.ContentId is not null)) { ShowOperationProblem("計画はIssueまたは新規行で設定してください。"); return; }
        var plan = saved ?? new(3, projectId, 0, null, null, [], new("official-2025-2027", PlanningContract.BundledHolidays(), false, []), [], []);
        var expected = work.Revision;
        var content = new StackPanel { Spacing = 12 };
        var surface = new Grid { RowSpacing = 8, Width = Math.Min(480, XamlRoot.Size.Width - 96), MaxHeight = Math.Max(240, XamlRoot.Size.Height - 220) };
        surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new());
        surface.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var title = work.Value(rows[currentRow].Cells[0]) ?? rows[currentRow].Cells[0].Display;
        var identity = new TextBlock { Text = $"{RowIdentity(rows[currentRow])}  {title}", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(identity, "PlanTaskIdentity");
        surface.Children.Add(identity);
        var feedback = new StackPanel { Spacing = 4 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(status, "PlanningStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var correction = new HyperlinkButton { Content = "入力へ移動", Padding = new(0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(correction, "PlanningCorrection");
        feedback.Children.Add(status); feedback.Children.Add(correction); SetRow(feedback, 1); surface.Children.Add(feedback);
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        SetRow(scroll, 2); surface.Children.Add(scroll);
        var leaving = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        leaving.Children.Add(new TextBlock { Text = "詳細の未保存の変更を破棄して、表の入力へ戻りますか。表の入力途中の内容は保持します。", TextWrapping = TextWrapping.Wrap });
        var leaveActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var discard = new Button { Content = "破棄して表へ" }; AutomationProperties.SetAutomationId(discard, "PlanDiscardToCell");
        var keep = new Button { Content = "編集を続ける" }; AutomationProperties.SetAutomationId(keep, "PlanKeepEditing");
        leaveActions.Children.Add(discard); leaveActions.Children.Add(keep); leaving.Children.Add(leaveActions);
        SetRow(leaving, 3); surface.Children.Add(leaving);
        var problems = Array.Empty<PlanningInputException>();
        PlanningDetails? details = null;
        void Describe(Control target, string? message)
        {
            if (target is TextBox text) text.Description = message;
            if (target is ComboBox box) box.Description = message;
            AutomationProperties.SetHelpText(target, message ?? "");
        }
        void ShowProblems(PlanningInputException[] next)
        {
            foreach (var problem in problems) Describe(problem.Target, null);
            problems = next;
            foreach (var group in problems.GroupBy(p => p.Target)) Describe(group.Key, string.Join("\n", group.Select(p => p.Message)));
            status.Text = problems.Length == 0 ? "" : $"入力を確認：{problems.Select(p => p.Target).Distinct().Count()}項目\n{problems[0].Message}";
            status.Visibility = correction.Visibility = problems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void FocusInput(Control target)
        {
            // Collapsed controls do not yet expose a visual parent. The form
            // retains their section owners to reveal only the correction path.
            details?.Reveal(target);
            if (target.Parent is MinuteEditor minute) minute.RevealInput();
            void FocusReady()
            {
                if (!target.IsLoaded || !surface.IsLoaded) return;
                target.Focus(FocusState.Programmatic);
                target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
            surface.UpdateLayout();
            if (target.IsLoaded) FocusReady();
            else
            {
                RoutedEventHandler? loaded = null;
                loaded = (_, _) => { target.Loaded -= loaded; FocusReady(); };
                target.Loaded += loaded;
            }
        }
        correction.Click += (_, _) => { if (problems.Length > 0) FocusInput(problems[0].Target); };
        void Changed() { if (problems.Length > 0) ShowProblems(problems.Where(p => !p.Corrected()).ToArray()); }
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "タスクの詳細", PrimaryButtonText = "保存", CloseButtonText = "キャンセル", Content = surface };
        FieldKey? destination = null, requestedCell = null;
        Func<ProjectPlanning> candidate;
        Func<PlanningValueEdit[]> values = () => [];
        Func<PlanningDependencyEdit[]> dependencies = () => [];
        Func<PlanningProjectionDecision[]> decisions = () => [];
        {
            var id = work.TaskId(registration, rows[currentRow].ItemId);
            var task = plan.Tasks.SingleOrDefault(t => t.Id == id) ?? (plan.Version >= 3 ? EditingWorkspace.WithObservedAssignment(registration, new(id)) : new PlanningTask(id));
            PlanningDetails? form = null;
            details = form = PlanningTaskDetails(content, plan, task, rows[currentRow], Changed, key => {
                if (!CanRefresh) { status.Text = "IME入力を確定・取消してから移動してください。"; status.Visibility = Visibility.Visible; return; }
                requestedCell = key;
                if (form!.Dirty()) { leaving.Visibility = Visibility.Visible; dialog.IsPrimaryButtonEnabled = false; keep.Focus(FocusState.Programmatic); }
                else { destination = key; dialog.Hide(); }
            });
            values = details.Values; dependencies = details.Dependencies; decisions = details.Decisions;
            candidate = () => plan with { Tasks = plan.Tasks.Where(t => t.Id != id).Append(details.Task()).ToArray() };
        }
        AutomationProperties.SetAutomationId(dialog, "PlanningDialog");
        discard.Click += (_, _) => { if (CanRefresh) { destination = requestedCell; dialog.Hide(); } };
        keep.Click += (_, _) => { leaving.Visibility = Visibility.Collapsed; dialog.IsPrimaryButtonEnabled = true; FocusInput(details.Progress); };
        if (progressCorrection)
        {
            details.Reveal(details.Progress);
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => {
                details.Progress.Loaded -= loaded;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusInput(details.Progress));
            };
            details.Progress.Loaded += loaded;
        }
        dialog.PrimaryButtonClick += (_, args) =>
        {
            using var operation = diagnostics?.Span("planning-save-handler");
            if (!CanRefresh) { status.Text = "IME変換を確定または取消してから保存してください。"; status.Visibility = Visibility.Visible; args.Cancel = true; return; }
            try {
                ShowProblems(details.Problems());
                if (problems.Length > 0) { args.Cancel = true; FocusInput(problems[0].Target); return; }
                ProjectPlanning next; PlanningValueEdit[] edits; PlanningDependencyEdit[] links; PlanningProjectionDecision[] choices;
                using (diagnostics?.Span("planning-candidate")) { next = candidate(); edits = values(); links = dependencies(); choices = decisions(); }
                using var coreTrace = diagnostics is null ? null : new PerformanceTrace();
                using (diagnostics?.Span("planning-commit")) work.CommitPlanning(registration, next, expected, edits, links, choices);
                if (coreTrace is not null) diagnostics!.Record("planning-core", new { samples = coreTrace.Samples.ToArray() });
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { status.Text = e.Message; status.Visibility = Visibility.Visible; args.Cancel = true; return; }
            // A task-value edit preserves the existing controls and pending native input.
            using (diagnostics?.Span("planning-view-refresh"))
            {
                Update();
            }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await FlushDraftsAsync("planning");
        }
        if (destination is { } cellKey)
        {
            ShowProjectView(ProjectView.Boards, cellKey.NodeId);
            var r = Array.FindIndex(rows, row => row.Cells.Any(c => c.Key == cellKey));
            if (r >= 0) Select(r, Array.FindIndex(rows[r].Cells, c => c.Key == cellKey), false);
        }
    }
    private string PlanningSummary(EditRow row)
    {
        if (session.Workspace.Planning(projectId) is null) return "";
        if (!row.IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == row.ItemId && i.Kind == ProjectItemKind.Issue && i.ContentId is not null)) return "";
        var id = session.Workspace.TaskId(registration, row.ItemId);
        var result = session.Workspace.PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == id);
        return result is null ? "" : $"\n{result.Mode}: {DateText(result.Start)} → {DateText(result.Finish)}\n{result.Problem ?? result.Controller}"
            + (result.Mode == PlanningMode.Auto && !result.Resolved ? "\n表の以前の日付は今回の計算結果ではありません。" : "")
            + $"\n{string.Join(" / ", result.Warnings)}";
    }
}
