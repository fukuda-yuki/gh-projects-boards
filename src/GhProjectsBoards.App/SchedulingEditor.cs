using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly StackPanel dateInputPane = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock dateInputState = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock planningHint = new() { TextWrapping = TextWrapping.Wrap };
    private Button dateCommit = null!, dateEdit = null!;
    private bool TypedDate(EditCell cell) => session.Workspace.PlanningInputRole(cell) is "Start" or "Finish";
    private bool TypedPlanning(EditCell cell) => TypedDate(cell) || TypedActual(cell);
    private static string MissingManualDate(TaskPlan task) => task.Start is null
        ? task.Finish is null ? "開始・終了日時が未設定です。" : "開始日時が未設定です。"
        : "終了日時が未設定です。";

    private void InitializeDateInput(StackPanel footer)
    {
        AutomationProperties.SetAutomationId(dateInputState, "DateInputState");
        AutomationProperties.SetAutomationId(planningHint, "FirstPlanningHint"); footer.Children.Add(planningHint);
        dateInputState.TextWrapping = TextWrapping.Wrap;
        dateInputPane.Children.Add(dateInputState);
        var edit = dateEdit = new Button { Content = "日時を編集" }; AutomationProperties.SetAutomationId(edit, "DateInputEditor");
        edit.Click += async (_, _) => await ShowSchedulingEditorAsync(edit); dateInputPane.Children.Add(edit);
        var commit = dateCommit = new Button { Content = "適用" }; AutomationProperties.SetAutomationId(commit, "DateInputCommit");
        commit.Click += (_, _) => CommitDateCell(); dateInputPane.Children.Add(commit);
        footer.Children.Add(dateInputPane);
    }
    private void UpdateDateInput()
    {
        var work = session.Workspace; var config = work.Planning(projectId);
        var plan = config is null ? null : work.PlanFor(registration);
        var estimate = config?.Fields.SingleOrDefault(f => f.Role == "Estimate");
        planningHint.Text = estimate is null ? "" : $"タスクの「{registration.Snapshot.Fields.FirstOrDefault(f => f.Id.NodeId == estimate.FieldId)?.Name ?? "見積"}」に見積（人時）を入力。確定すると日程を自動計算します。";
        planningHint.Visibility = !ShowingGantt && config is not null && estimate is not null && plan?.Tasks.Any(t => t.Resolved) != true ? Visibility.Visible : Visibility.Collapsed;
        if (!active || currentRow >= rows.Length || ShowingGantt || plan is null || TypedActual(rows[currentRow].Cells[currentColumn])) { dateInputPane.Visibility = Visibility.Collapsed; return; }
        dateInputState.MaxWidth = Math.Max(120, ActualWidth - 240);
        var item = registration.Snapshot.Items.FirstOrDefault(i => i.Id.NodeId == rows[currentRow].ItemId);
        dateEdit.IsEnabled = rows[currentRow].IsLocal || item is { Kind: ProjectItemKind.Issue, ContentId: not null };
        if (!dateEdit.IsEnabled)
        {
            dateInputPane.Visibility = Visibility.Visible; dateCommit.Visibility = Visibility.Collapsed;
            dateInputState.Text = RowIdentity(rows[currentRow]) + " · " + (item?.Kind is ProjectItemKind.Issue or ProjectItemKind.Unavailable
                ? "Issue情報を確認できません。取得状態を確認してください。" : "この行は計画対象外です。Issueまたは新規行で計画できます。");
            return;
        }
        var current = plan.Tasks.SingleOrDefault(t => t.Id == work.TaskId(registration, rows[currentRow].ItemId));
        if (current is null) { dateInputPane.Visibility = Visibility.Collapsed; return; }
        dateInputPane.Visibility = Visibility.Visible;
        var cell = rows[currentRow].Cells[currentColumn]; var role = session.Workspace.PlanningInputRole(cell);
        dateCommit.Visibility = TypedDate(cell) && work.Buffer(cell) is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!TypedDate(cell))
        {
            dateInputState.Text = RowIdentity(rows[currentRow]) + " · " + (current.Resolved ? (current.Mode == PlanningMode.Manual ? "日時を指定" : "自動計算")
                + $"（採用済み） {DateText(current.Start)} → {DateText(current.Finish)}" : current.Mode == PlanningMode.Unplanned ? "未計画 · 見積を入力するか、日時を指定"
                : current.Mode == PlanningMode.Manual ? MissingManualDate(current) : current.Problem ?? "日程を確認してください。");
            return;
        }
        dateInputState.Text = (session.Workspace.Buffer(cell) is not null ? "日時を指定（入力中）" : current.Mode switch
            { PlanningMode.Manual => "日時を指定", PlanningMode.Auto => "自動計算", _ => "日程未設定" })
            + " · " + (role == "Start" ? "開始日時" : "終了日時") + " · 日本時間・分単位";
    }
    private string ExactDateText(EditCell cell, int row)
    {
        var exact = session.Workspace.SchedulingEndpoint(registration, rows[row].ItemId, session.Workspace.PlanningInputRole(cell)!);
        return exact is null ? session.Workspace.Value(cell) ?? "" : DateText(exact);
    }
    private bool CommitDateCell()
    {
        if (!active || !CanRefresh || !TypedDate(rows[currentRow].Cells[currentColumn])) return false;
        var cell = rows[currentRow].Cells[currentColumn];
        if (session.Workspace.Buffer(cell) is not { } text) return false;
        Run(() => session.Workspace.CommitDateInput(registration, rows[currentRow].ItemId,
            session.Workspace.PlanningInputRole(cell)!, text, session.Workspace.Revision));
        return session.Workspace.Buffer(cell) is null;
    }
    private sealed record SchedulingDraft(string RowId, string Start, string Finish, int Method, bool ExplicitAuto);
    private async Task ShowSchedulingEditorAsync(FrameworkElement anchor, SchedulingDraft? retained = null)
    {
        if (!CanRefresh) { ShowOperationProblem("IME入力を確定・取消してから日程を編集してください。"); return; }
        if (!active) { ShowOperationProblem(SelectSchedulingTask); return; }
        if (!rows[currentRow].IsLocal && !registration.Snapshot.Items.Any(i => i.Id.NodeId == rows[currentRow].ItemId
            && i.Kind == ProjectItemKind.Issue && i.ContentId is not null))
        { ShowOperationProblem("計画はIssueまたは新規行で設定してください。"); return; }
        var rowId = rows[currentRow].ItemId; var request = generation; var requestedView = ShowingGantt;
        if (retained?.RowId != rowId) retained = null;
        if (!await prepareLocalRows() || request != generation || !IsLoaded || requestedView != ShowingGantt
            || anchor.Visibility != Visibility.Visible) return;
        if (session.Workspace.Planning(projectId) is null)
        {
            await PlanningDialogAsync(true);
            if (!IsLoaded || session.Workspace.Planning(projectId) is null) return;
            var r = Array.FindIndex(rows, row => row.ItemId == rowId); if (r < 0) return; Select(r, 0, false, false);
        }
        var work = session.Workspace; var plan = work.Planning(projectId)!;
        var row = canonicalRows.Single(r => r.ItemId == rowId); var id = work.TaskId(registration, rowId);
        var current = work.PlanFor(registration).Tasks.Single(t => t.Id == id);
        var oldTask = plan.Tasks.SingleOrDefault(t => t.Id == id);
        var dateCells = row.Cells.Where(c => work.PlanningInputRole(c) is "Start" or "Finish").ToArray();
        string Initial(string role)
        {
            if (retained is not null) return role == "Start" ? retained.Start : retained.Finish;
            var value = work.SchedulingEndpoint(registration, rowId, role);
            var cell = dateCells.SingleOrDefault(c => work.PlanningInputRole(c) == role);
            return cell is null ? DateText(value) : work.Buffer(cell) ?? (value is null ? work.Value(cell) ?? "" : DateText(value));
        }
        var panel = new StackPanel { Spacing = 8, Width = 420 };
        AutomationProperties.SetAutomationId(panel, "SchedulingEditor");
        panel.Children.Add(new TextBlock { Text = RowIdentity(row), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var method = new RadioButtons { Header = "日程の決め方", MaxColumns = 2 };
        method.Items.Add("自動計算"); method.Items.Add("日時を指定");
        method.SelectedIndex = retained?.Method ?? (dateCells.Any(c => work.Buffer(c) is not null) || current.Mode == PlanningMode.Manual ? 1 : current.Mode == PlanningMode.Auto ? 0 : -1);
        AutomationProperties.SetAutomationId(method, "ScheduleMethod"); panel.Children.Add(method);
        if (current.Mode == PlanningMode.Unplanned) panel.Children.Add(new TextBlock { Text = "日程はまだ設定されていません。" });
        var start = new MinuteEditor("開始日時", "ScheduleStart", Initial("Start"));
        var finish = new MinuteEditor("終了日時", "ScheduleFinish", Initial("Finish"));
        TrackContextInput(start.Input); TrackContextInput(finish.Input);
        panel.Children.Add(start); panel.Children.Add(finish);
        var native = registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, id))?.Native;
        var person = native is { Complete: true, Assignees.Length: 1 } ? native.Assignees[0] : null;
        var weight = person is null ? null : plan.People.SingleOrDefault(p => p.Id == person.Id.NodeId);
        panel.Children.Add(new TextBlock { Text = person is null ? native?.Complete != true ? "GitHub担当者: 未取得" : native.Assignees.Length == 0 ? "GitHub担当者: 未設定" : "GitHub担当者: 複数（日時を指定できます）"
            : $"GitHub担当者: {person.Login} · Project配賦: {(weight is null ? "未設定" : weight.WeightPercent + "%")}", TextWrapping = TextWrapping.Wrap });
        if (oldTask is not null && (oldTask.Assignment is null || oldTask.Assignment.Legacy)) panel.Children.Add(new TextBlock
        { Text = $"以前の計画担当者: {plan.People.SingleOrDefault(p => p.Id == oldTask.OwnerId)?.Name ?? oldTask.OwnerId ?? "未割当"}。日時とともに保持中。自動計算を選ぶと現在のGitHub担当者との差分を比較します。", TextWrapping = TextWrapping.Wrap });
        if (dateCells.Any(c => work.Value(c) is not null && work.SchedulingEndpoint(registration, rowId, work.PlanningInputRole(c)!) is null))
            panel.Children.Add(new TextBlock { Text = "日付だけの項目は時刻が未確認です。正確な日時を入力するか、消去してください。", TextWrapping = TextWrapping.Wrap });
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(preview, "SchedulePreview");
        panel.Children.Add(preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "適用" }; var close = new Button { Content = "閉じる" }; var settings = new Button { Content = "計画の前提" };
        AutomationProperties.SetAutomationId(apply, "ScheduleApply"); AutomationProperties.SetAutomationId(close, "ScheduleClose");
        AutomationProperties.SetAutomationId(settings, "ScheduleSettings");
        actions.Children.Add(apply); actions.Children.Add(close); actions.Children.Add(settings); panel.Children.Add(actions);
        // Anchor to a stable toolbar or contextual control, never an overflow
        // item that disappears or the full-height workspace.
        // The closing animation must not intercept the next task's first click.
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            AreOpenCloseAnimationsEnabled = false,
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(180, XamlRoot.Size.Height - 240), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        var expected = work.Revision; var editorGeneration = generation; var explicitAuto = retained?.ExplicitAuto ?? false;
        ProjectPlanning Candidate() => work.SchedulingCandidate(registration, rowId,
            method.SelectedIndex == 0 ? PlanningMode.Auto : method.SelectedIndex == 1 ? PlanningMode.Manual : PlanningMode.Unplanned,
            method.SelectedIndex == 1 ? PlanningDate(start.Text) : current.Start,
            method.SelectedIndex == 1 ? PlanningDate(finish.Text) : current.Finish, explicitAuto,
            dateCells.Where(c => work.Buffer(c) == "").Select(c => work.PlanningInputRole(c)!).ToArray());
        void Preview()
        {
            try
            {
                var staged = EditingWorkspace.Restore(work.Snapshot());
                var candidate = Candidate();
                staged.CommitPlanning(registration, candidate, expected, consumeBuffers: dateCells.Select(c => c.Key!).ToArray());
                var previous = work.PlanFor(registration).Tasks.ToDictionary(t => t.Id);
                var next = staged.PlanFor(registration).Tasks;
                var changed = next.Where(t => !previous.TryGetValue(t.Id, out var old) || old.Start != t.Start || old.Finish != t.Finish || old.Mode != t.Mode).ToArray();
                if (changed.Length == 0)
                {
                    preview.Text = !current.Resolved ? current.Mode == PlanningMode.Manual ? MissingManualDate(current) : current.Problem ?? "日程はまだ確定していません。"
                        : explicitAuto ? "現在のGitHub担当者で自動計算を採用します。日程の変更はありません。"
                        : dateCells.Any(c => work.Buffer(c) is not null) || retained is not null ? "入力を適用しても採用日程は変わりません。"
                        : "採用済みの日程です。確認だけなら適用は不要です。";
                    return;
                }
                preview.Text = string.Join("\n", changed
                    .Select(t => $"{(t.Id == id ? "このタスク" : registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, t.Id))?.Title.Value ?? t.Id)}: 開始 {Endpoint(previous.GetValueOrDefault(t.Id)?.Start)} → {Endpoint(t.Start)} / 終了 {Endpoint(previous.GetValueOrDefault(t.Id)?.Finish)} → {Endpoint(t.Finish)}\n{(t.Mode == PlanningMode.Manual ? t.Resolved ? "指定した日時を採用します。" : MissingManualDate(t) + "片側のみ保存できます。" : t.Problem ?? t.Controller)}"));
                static string Endpoint(DateTime? value) => value is null ? "未設定" : DateText(value);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { preview.Text = e.Message; }
        }
        void Edited(string role, string value)
        {
            if (work != session.Workspace || expected != work.Revision || editorGeneration != generation) { preview.Text = "作業が変わりました。閉じて開き直してください。"; return; }
            method.SelectedIndex = 1; explicitAuto = false;
            var cell = dateCells.SingleOrDefault(c => work.PlanningInputRole(c) == role);
            if (cell is not null)
            {
                work.SetPlanningBuffer(cell, value); expected = work.Revision;
                // The contextual TextBox is another editor for the same field.
                // Pending-only revisions must also refresh its rendered peers.
                for (var r = 0; r < controls.Count; r++)
                    for (var c = 0; c < controls[r].Length; c++)
                        if (rows[r].Cells[c].Key == cell.Key) UpdateCell(r, c);
                _ = FlushDraftsAsync("date-editor-input");
            }
            Preview();
        }
        start.Edited += () => Edited("Start", start.Text); finish.Edited += () => Edited("Finish", finish.Text);
        method.SelectionChanged += (_, _) => { explicitAuto = method.SelectedIndex == 0; Preview(); };
        apply.Click += async (_, _) =>
        {
            try
            {
                if (!IsLoaded || work != session.Workspace || editorGeneration != generation || !CanRefresh) throw new InvalidOperationException("作業が変わりました。閉じて開き直してください。");
                work.CommitPlanning(registration, Candidate(), expected, consumeBuffers: dateCells.Select(c => c.Key!).ToArray());
                flyout.Hide(); Update(); await FlushDraftsAsync("schedule-editor");
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { preview.Text = e.Message; }
        };
        close.Click += (_, _) => flyout.Hide();
        settings.Click += async (_, _) => {
            var input = new SchedulingDraft(rowId, start.Text, finish.Text, method.SelectedIndex, explicitAuto);
            flyout.Hide(); await ShowPlanningSettingsAsync();
            if (IsLoaded && !leavingPlanningSettings && SelectGanttRow(rowId)) await ShowSchedulingEditorAsync(anchor, input);
        };
        Preview(); flyout.ShowAt(anchor);
    }
}
