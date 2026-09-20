using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly StackPanel dateInputPane = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock dateInputState = new() { VerticalAlignment = VerticalAlignment.Center };
    private bool TypedDate(EditCell cell) => session.Workspace.PlanningInputRole(cell) is "Start" or "Finish";
    private bool TypedPlanning(EditCell cell) => TypedDate(cell) || TypedActual(cell);

    private void InitializeDateInput(StackPanel footer)
    {
        AutomationProperties.SetAutomationId(dateInputState, "DateInputState");
        dateInputPane.Children.Add(dateInputState);
        var edit = new Button { Content = "日時を編集" }; AutomationProperties.SetAutomationId(edit, "DateInputEditor");
        edit.Click += async (_, _) => await ShowSchedulingEditorAsync(edit); dateInputPane.Children.Add(edit);
        var commit = new Button { Content = "適用" }; AutomationProperties.SetAutomationId(commit, "DateInputCommit");
        commit.Click += (_, _) => CommitDateCell(); dateInputPane.Children.Add(commit);
        footer.Children.Add(dateInputPane);
    }
    private void UpdateDateInput()
    {
        if (!active || ShowingGantt || !TypedDate(rows[currentRow].Cells[currentColumn])) { dateInputPane.Visibility = Visibility.Collapsed; return; }
        dateInputPane.Visibility = Visibility.Visible;
        var cell = rows[currentRow].Cells[currentColumn]; var role = session.Workspace.PlanningInputRole(cell);
        var current = session.Workspace.PlanFor(registration).Tasks.Single(t => t.Id == session.Workspace.TaskId(registration, rows[currentRow].ItemId));
        dateInputState.Text = (session.Workspace.Buffer(cell) is not null ? "日時を指定（入力中）" : current.Mode switch
            { PlanningMode.Manual => "日時を指定", PlanningMode.Auto => "自動計算", _ => "日程未設定" })
            + " · " + (role == "Start" ? "開始日時" : "終了日時") + " · 日本時間・分単位";
    }
    private string ExactDateText(EditCell cell, int row)
    {
        var current = session.Workspace.PlanFor(registration).Tasks.Single(t => t.Id == session.Workspace.TaskId(registration, rows[row].ItemId));
        return DateText(session.Workspace.PlanningInputRole(cell) == "Start" ? current.Start : current.Finish);
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
    private async Task ShowSchedulingEditorAsync(FrameworkElement anchor)
    {
        if (!CanRefresh || !active) { ShowOperationProblem("日程を変更するタスクを選択してください。"); return; }
        var rowId = rows[currentRow].ItemId; var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
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
        string Initial(string role, DateTime? value) => dateCells.SingleOrDefault(c => work.PlanningInputRole(c) == role) is { } c
            ? work.Buffer(c) ?? DateText(value) : DateText(value);
        var panel = new StackPanel { Spacing = 8, Width = 360 };
        AutomationProperties.SetAutomationId(panel, "SchedulingEditor");
        panel.Children.Add(new TextBlock { Text = RowIdentity(row), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var method = new RadioButtons { Header = "日程の決め方", MaxColumns = 2 };
        method.Items.Add("自動計算"); method.Items.Add("日時を指定");
        method.SelectedIndex = dateCells.Any(c => work.Buffer(c) is not null) || current.Mode == PlanningMode.Manual ? 1 : current.Mode == PlanningMode.Auto ? 0 : -1;
        AutomationProperties.SetAutomationId(method, "ScheduleMethod"); panel.Children.Add(method);
        if (current.Mode == PlanningMode.Unplanned) panel.Children.Add(new TextBlock { Text = "日程はまだ設定されていません。" });
        var start = new MinuteEditor("開始日時", "ScheduleStart", Initial("Start", current.Start));
        var finish = new MinuteEditor("終了日時", "ScheduleFinish", Initial("Finish", current.Finish));
        TrackContextInput(start.Input); TrackContextInput(finish.Input);
        panel.Children.Add(start); panel.Children.Add(finish);
        var native = registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, id))?.Native;
        var person = native is { Complete: true, Assignees.Length: 1 } ? native.Assignees[0] : null;
        var weight = person is null ? null : plan.People.SingleOrDefault(p => p.Id == person.Id.NodeId);
        panel.Children.Add(new TextBlock { Text = person is null ? native?.Complete != true ? "GitHub担当者: 未取得" : native.Assignees.Length == 0 ? "GitHub担当者: 未設定" : "GitHub担当者: 複数（日時を指定できます）"
            : $"GitHub担当者: {person.Login} · Project配賦: {(weight is null ? "未設定" : weight.WeightPercent + "%")}", TextWrapping = TextWrapping.Wrap });
        if (oldTask is { Mode: not PlanningMode.Unplanned } && (oldTask.Assignment is null || oldTask.Assignment.Legacy)) panel.Children.Add(new TextBlock
        { Text = "以前の計画担当者・日時を保持中。自動計算を選ぶと現在のGitHub担当者との差分を比較します。", TextWrapping = TextWrapping.Wrap });
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(preview, "SchedulePreview");
        panel.Children.Add(preview);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var apply = new Button { Content = "適用" }; var close = new Button { Content = "閉じる" }; var settings = new Button { Content = "配賦・設定" };
        AutomationProperties.SetAutomationId(apply, "ScheduleApply"); AutomationProperties.SetAutomationId(close, "ScheduleClose");
        AutomationProperties.SetAutomationId(settings, "ScheduleSettings");
        actions.Children.Add(apply); actions.Children.Add(close); actions.Children.Add(settings); panel.Children.Add(actions);
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(180, XamlRoot.Size.Height - 100), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        var expected = work.Revision; var editorGeneration = generation; var explicitAuto = method.SelectedIndex == 0;
        ProjectPlanning Candidate() => work.SchedulingCandidate(registration, rowId,
            method.SelectedIndex == 0 ? PlanningMode.Auto : method.SelectedIndex == 1 ? PlanningMode.Manual : PlanningMode.Unplanned,
            method.SelectedIndex == 1 ? PlanningDate(start.Text) : current.Start,
            method.SelectedIndex == 1 ? PlanningDate(finish.Text) : current.Finish, explicitAuto);
        void Preview()
        {
            try
            {
                var staged = EditingWorkspace.Restore(work.Snapshot());
                staged.CommitPlanning(registration, Candidate(), expected, consumeBuffers: dateCells.Select(c => c.Key!).ToArray());
                var previous = work.PlanFor(registration).Tasks.ToDictionary(t => t.Id);
                var next = staged.PlanFor(registration).Tasks;
                preview.Text = string.Join("\n", next.Where(t => t.Id == id || !previous.TryGetValue(t.Id, out var old) || old.Start != t.Start || old.Finish != t.Finish)
                    .Select(t => $"{(t.Id == id ? "このタスク" : registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, t.Id))?.Title.Value ?? t.Id)}: {DateText(previous.GetValueOrDefault(t.Id)?.Start)} → {DateText(t.Start)} / 終了 {DateText(previous.GetValueOrDefault(t.Id)?.Finish)} → {DateText(t.Finish)}\n{t.Problem ?? t.Controller}"));
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { preview.Text = e.Message; }
        }
        void Edited(string role, string value)
        {
            if (work != session.Workspace || expected != work.Revision || editorGeneration != generation) { preview.Text = "作業が変わりました。閉じて開き直してください。"; return; }
            method.SelectedIndex = 1; explicitAuto = false;
            var cell = dateCells.SingleOrDefault(c => work.PlanningInputRole(c) == role);
            if (cell is not null) { work.SetPlanningBuffer(cell, value); expected = work.Revision; _ = FlushDraftsAsync("date-editor-input"); }
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
        settings.Click += async (_, _) => { flyout.Hide(); await PlanningDialogAsync(true); if (IsLoaded) await ShowSchedulingEditorAsync(anchor); };
        Preview(); flyout.ShowAt(anchor);
    }
}
