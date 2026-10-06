using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private readonly Grid publishReview = new() { RowSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly ListView reviewLines = Id(new ListView { SelectionMode = ListViewSelectionMode.None, Padding = new(0) }, "PlanPublishLines");
    private readonly TextBlock publishStage = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanPublishStage");
    private Button confirmPublish = null!;
    private bool publishing;

    private void InitializePublishing()
    {
        var open = Id(new Button { Content = "発行" }, "PlanPublish");
        open.Click += async (_, _) => {
            if (publishing) { Show("publish"); return; }
            await Run(() => { RenderReview(); Show("publish"); return Task.CompletedTask; });
        };
        toolbar.Children.Add(open);
        AutomationProperties.SetLiveSetting(publishStage, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        publishReview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        publishReview.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmPublish = Button("発行する", "PlanPublishConfirm", Publish);
        var close = Id(new Button { Content = "閉じる" }, "PlanPublishClose");
        close.Click += (_, _) => { if (!closing) Show("tasks"); };
        actions.Children.Add(confirmPublish); actions.Children.Add(close);
        publishReview.Children.Add(actions);
        ScrollViewer.SetHorizontalScrollBarVisibility(reviewLines, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(reviewLines, ScrollMode.Enabled);
        reviewLines.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left) } };
        publishReview.Children.Add(reviewLines); Grid.SetRow(reviewLines, 1);
    }

    private void RenderReview()
    {
        if (workspace.Session is not { } session) return;
        reviewLines.Items.Clear();
        var document = session.Document;
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var scheduled = session.Schedule(Today).ToDictionary(r => r.Input.Identity);
        foreach (var row in document.State.Rows)
        {
            var result = scheduled[row.Identity];
            var current = row with { Start = result.Start.Value, End = result.End.Value };
            var isNew = !baseline.TryGetValue(row.Identity, out var before);
            before ??= new(row.Identity, "", row.Repository);
            var caption = Caption(document, row.Identity);
            if (isNew) reviewLines.Items.Add(Label($"{caption}  新規 Issue  {row.Repository}"));
            foreach (var field in PlanValues.RowFields)
            {
                if (PlanOperations.IsSummaryEffort(result.IsSummary, field) || PlanOperations.IsLocalConstraint(field, document.State.Settings)) continue;
                var oldValue = PlanValues.Get(before, field); var newValue = PlanValues.Get(current, field);
                var conflict = document.Sync.Conflicts.SingleOrDefault(c => c.Identity == row.Identity && c.Field == field);
                if (oldValue == newValue && conflict is null || isNew && field is PlanField.Title or PlanField.Repository) continue;
                AddReviewLine(document, caption, field, oldValue, newValue, conflict);
            }
        }
        foreach (var conflict in document.Sync.Conflicts.Where(c => c.Field is PlanField.Order or PlanField.SubIssueOrder))
            AddReviewLine(document, Caption(document, conflict.Identity), conflict.Field, conflict.Baseline, conflict.Local, conflict);
        if (session.Changes(Today).Fields.Any(p => p.Value.Contains(PlanField.Order)))
            reviewLines.Items.Add(Label("表示順  " + string.Join("、", document.Baseline.Rows.Select(r => Caption(document, r.Identity))) + " → " + string.Join("、", document.State.Rows.Select(r => Caption(document, r.Identity)))));
        foreach (var identity in document.Sync.Unavailable)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(Label(Caption(document, identity) + "  GitHubで取得できません"));
            foreach (var copy in new[] { false, true })
                line.Children.Add(Button(copy ? "新規Issueにコピー" : "計画から除く", "PlanUnavailable" + identity + copy, async () => {
                    Check(await session.ResolveUnavailable(identity, copy, Today)); RenderTasks(); RenderReview();
                }));
            reviewLines.Items.Add(line);
        }
        foreach (var failure in document.Sync.Failures)
            reviewLines.Items.Add(Label(Caption(document, failure.Identity) + "  発行失敗: " + failure.Reason));
        foreach (var identity in document.Sync.Unverified)
            reviewLines.Items.Add(Label(Caption(document, identity) + "  未検証 — 最新の情報に更新で確認"));
        if (reviewLines.Items.Count == 0) reviewLines.Items.Add(Label("未発行の変更はありません"));
        confirmPublish.IsEnabled = !publishing && document.Sync.Conflicts.IsEmpty && document.Sync.Unavailable.IsEmpty;
    }
    private void AddReviewLine(PlanDocument document, string caption, PlanField field, string? before, string? after, PlanConflict? conflict)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var label = document.State.Settings.Columns.FirstOrDefault(c => c.Role == field)?.Name ?? field switch {
            PlanField.Title => "タイトル", PlanField.Assignees => "担当者", PlanField.Parent => "親タスク",
            PlanField.Predecessors => "先行タスク", PlanField.Order => "表示順", PlanField.SubIssueOrder => "子タスクの順序",
            PlanField.Fixed => "日程固定", PlanField.StartNoEarlierThan => "開始日指定", _ => field.ToString() };
        line.Children.Add(Label($"{caption}  {label}  {ReviewValue(document, field, before)} → {ReviewValue(document, field, after)}"));
        if (conflict is not null)
        {
            line.Children.Add(Label("競合  GitHub: " + ReviewValue(document, field, conflict.Remote)));
            foreach (var remote in new[] { false, true })
                line.Children.Add(Button(remote ? "GitHubを採用" : "ローカルを採用", $"PlanResolve{conflict.Identity}_{field}_{remote}", async () => {
                    var position = reviewLines.Items.IndexOf(line);
                    Check(await workspace.Session!.ResolveConflict(conflict.Identity, field, remote, Today));
                    RenderTasks();
                    if (reviewLines.Items.Count > 0) reviewLines.ScrollIntoView(reviewLines.Items[Math.Clamp(position, 0, reviewLines.Items.Count - 1)]);
                    reviewLines.Focus(FocusState.Programmatic);
                }));
        }
        reviewLines.Items.Add(line);
    }
    private static string Caption(PlanDocument document, string identity)
    {
        var index = document.State.Rows.IndexOf(document.State.Rows.FirstOrDefault(r => r.Identity == identity)!);
        return index < 0 ? "プロジェクト" : $"{index + 1} {document.State.Rows[index].Title}";
    }
    private static string ReviewValue(PlanDocument document, PlanField field, string? json)
    {
        if (json is null or "null") return "未入力";
        using var value = JsonDocument.Parse(json);
        var element = value.RootElement;
        string Identity(string id) => field == PlanField.Assignees ? document.Sync.PeopleNames.GetValueOrDefault(id,
            document.State.Settings.People.FirstOrDefault(p => p.Identity == id)?.Name ?? "担当者（未確認）") : Caption(document, id);
        return element.ValueKind switch {
            JsonValueKind.Array => element.GetArrayLength() == 0 ? "未入力" : string.Join("、", element.EnumerateArray().Select(e => Identity(e.GetString()!))),
            JsonValueKind.String => field == PlanField.Parent ? Identity(element.GetString()!) : element.GetString() is { Length: > 0 } text ? text : "未入力",
            JsonValueKind.True => field == PlanField.Fixed ? "固定" : "完了",
            JsonValueKind.False => field == PlanField.Fixed ? "指定なし" : "未完了",
            _ => element.ToString() };
    }
    private async Task Publish()
    {
        if (workspace.Session is not { } session || publishing) return;
        publishing = true; SetPublishBusy(true);
        try
        {
            var reporter = new Progress<string>(stage => { if (IsLoaded && !closing && publishing) { publishStage.Text = "発行中: " + stage; publishStage.Visibility = Visibility.Visible; } });
            var result = await new PlanPublisher(workspace.Service!, workspace.Context!).PublishAsync(session, Today, OperationToken, reporter);
            var save = await session.FlushAsync();
            if (!closing)
            {
                error.Text = save.Succeeded ? result.Error ?? "" : save.Error ?? "保存できません。再試行してください。";
                retrySave.Visibility = save.Retryable ? Visibility.Visible : Visibility.Collapsed;
                RenderTasks();
            }
        }
        finally
        {
            publishing = false;
            if (!closing) { publishStage.Text = ""; publishStage.Visibility = Visibility.Collapsed; SetPublishBusy(false); RenderReview(); }
        }
    }
    private void SetPublishBusy(bool busy)
    {
        foreach (var control in sidebar.Children.OfType<Control>()) control.IsEnabled = !busy;
        foreach (var button in toolbar.Children.OfType<Button>()) button.IsEnabled = !busy || AutomationProperties.GetAutomationId(button) == "PlanPublish";
        if (sheet is not null) sheet.SetRemoteBusy(busy);
        settingsScroll.IsEnabled = !busy;
        confirmPublish.IsEnabled = !busy;
        foreach (var button in reviewLines.Items.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>())) button.IsEnabled = !busy;
    }
}
