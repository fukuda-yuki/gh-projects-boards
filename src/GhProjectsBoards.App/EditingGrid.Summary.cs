using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly bool summaryEnabled;
    private static bool SummaryEvaluationEnabled()
    {
        // The existing isolated fixture launcher is the temporary development
        // boundary. Ordinary saved profiles keep the unfinished report contained.
        var root = Environment.GetEnvironmentVariable("GHPB_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root)) return false;
        try
        {
            using var marker = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(
                System.IO.Path.Combine(root, "diagnostics", "summary-fixture.json")));
            var value = marker.RootElement;
            return value.GetProperty("kind").GetString() == "synthetic-summary-v2"
                && value.GetProperty("validatedReadback").GetBoolean()
                && string.Equals(value.GetProperty("dataRoot").GetString(), System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    private long summaryRevision = -1;
    private EditingWorkspace? summaryWorkspace;
    private DateOnly summaryDay;
    private void EnsureSummary()
    {
        if (summaryView is not null) return;
        summaryView = new SummaryView { Visibility = Visibility.Collapsed };
        SetRow(summaryView, 1); SetRowSpan(summaryView, RowDefinitions.Count - 1); Children.Add(summaryView);
        summaryView.AllowanceRequested += async id => await AllowanceDialogAsync(id);
        summaryView.BaselineRequested += async replace => await BaselineDialogAsync(replace);
        summaryView.TaskRequested += (id, view) => { if (SelectGanttRow(id)) ShowProjectView(view, id); };
        summaryView.EditRequested += async id => { if (SelectGanttRow(id)) { await PlanningDialogAsync(false); UpdateSummary(true); } };
        summaryView.UndoRequested += () => { Run(Undo); UpdateSummary(true); };
        summaryView.SaveRequested += async () => { await FlushDraftsAsync("summary-retry"); Update(); };
        summaryView.SettingsRequested += async () => { await PlanningDialogAsync(true); UpdateSummary(true); };
    }
    private void UpdateSummary(bool force = false, string? person = null, string? row = null)
    {
        if (!ShowingSummary) return;
        summaryView!.ShowOperationStatus(operationProblem, session.Status);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(9));
        if (!force && summaryWorkspace == session.Workspace && summaryRevision == session.Workspace.Revision && summaryDay == today) return;
        summaryView.Present(SummaryProjection.Create(session.Workspace, registration, today), person, row);
        summaryWorkspace = session.Workspace; summaryRevision = session.Workspace.Revision; summaryDay = today;
    }
    private async Task AllowanceDialogAsync(string personId)
    {
        if (!CanRefresh) return;
        var person = summaryView?.AdoptedSummary?.People.SingleOrDefault(p => p.Id == personId); if (person is null) return;
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = person.Name + " / " + personId, TextWrapping = TextWrapping.Wrap });
        var input = PlanningText(panel, "Project全体の投入可能工数（人時、空欄は未設定）", "SummaryAllowanceHours",
            person.Allowance is { } h ? PlanningContract.CanonicalHours(h) : "");
        var days = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(days);
        input.TextChanged += (_, _) => { try { days.Text = input.Text.Length == 0 ? "未設定" : SummaryText.Number(PlanningContract.ParseHours(input.Text) / 8) + " 人日（8人時/人日）"; } catch (InvalidOperationException) { days.Text = "人時で入力してください。"; } };
        await SummarySaveDialogAsync("投入可能工数", "SummaryAllowanceDialog", panel, work => {
            var hours = input.Text.Length == 0 ? (decimal?)null : PlanningContract.ParseHours(input.Text);
            work.SetAllowance(registration, personId, hours, work.Revision);
        });
    }
    private async Task BaselineDialogAsync(bool replace)
    {
        if (!CanRefresh) return;
        var p = session.Workspace.Planning(projectId);
        if (p is null) { ShowOperationProblem("計画設定を保存してから基準を確立してください。"); return; }
        var baseline = p.Summary?.Baseline;
        if (replace != (baseline is not null)) return;
        var rows = GanttProjection.Create(session.Workspace, registration, []).Rows.DistinctBy(r => r.TaskId).ToArray();
        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = $"{registration.Snapshot.Title} · {rows.Length}タスク\n見積・採用日時・Auto/Manual・カレンダー・配賦を記録します。未確定文字は含みません。"
                + (baseline is null ? "" : $"\n{baseline.CapturedAt.LocalDateTime:g} の基準（{baseline.Tasks.Length}タスク）を置き換えます。") });
        panel.Children.Add(new ListView { Height = 180, ItemsSource = rows.Select(r => r.Identity + " · " + r.Title).ToArray() });
        await SummarySaveDialogAsync(replace ? "基準計画を置き換える" : "基準計画を確立する", "SummaryBaselineDialog", panel,
            work => work.CaptureBaseline(registration, work.Revision, baseline?.Id, DateTimeOffset.UtcNow), replace ? "置き換える" : "確立する");
    }
    private async Task SummarySaveDialogAsync(string title, string id, StackPanel panel, Action<EditingWorkspace> change, string primary = "保存")
    {
        var source = session.Workspace; var expected = source.Revision;
        // A pending view rebuild changes cell generation without changing this Project or its data.
        bool Current() => IsLoaded && session.Workspace == source && source.Revision == expected && CanRefresh;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(error, "SummaryDialogError"); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, PrimaryButtonText = primary, CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = panel, MaxHeight = 480, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        AutomationProperties.SetAutomationId(dialog, id);
        dialog.PrimaryButtonClick += async (_, args) => {
            var deferral = args.GetDeferral(); args.Cancel = true;
            try {
                if (!Current()) { error.Text = "比較後に変更がありました。閉じて現在の値を確認してください。"; return; }
                // Validate before the durable transaction so precise input errors remain beside their input.
                var probe = EditingWorkspace.Restore(session.Workspace.Snapshot()); change(probe);
                var saved = await session.CommitAsync(work => { change(work); return work; }, Current);
                if (saved) args.Cancel = false;
                else { error.Text = session.Status; dialog.PrimaryButtonText = "保存を再試行"; }
            } catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { error.Text = e.Message; }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync(); UpdateSummary(true); Update();
    }
}
