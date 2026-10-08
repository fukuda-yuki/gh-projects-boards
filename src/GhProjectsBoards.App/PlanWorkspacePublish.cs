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
        var open = CommandButton("発行…", "PlanPublish", Symbol.Upload);
        open.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        open.Click += async (_, _) => {
            if (publishing) { Show("publish"); return; }
            await Run(() => { RenderReview(); Show("publish"); return Task.CompletedTask; });
        };
        commandButtons.Children.Add(open);
        AutomationProperties.SetLiveSetting(publishStage, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        publishReview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        publishReview.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmPublish = Button("発行する", "PlanPublishConfirm", Publish);
        var close = Id(new Button { Content = "閉じる" }, "PlanPublishClose");
        close.Click += (_, _) => { if (!closing) Show("tasks"); };
        actions.Children.Add(confirmPublish); actions.Children.Add(close);
        publishReview.Children.Add(actions);
        reviewLines.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanPublishGroupTemplate"];
        ScrollViewer.SetHorizontalScrollBarVisibility(reviewLines, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(reviewLines, ScrollMode.Disabled);
        reviewLines.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        publishReview.Children.Add(reviewLines); Grid.SetRow(reviewLines, 1);
    }

    private void RenderReview()
    {
        if (workspace.Session is not { } session) return;
        var document = session.Document;
        var groups = new Dictionary<string, PlanPublishReviewGroup>();
        PlanPublishReviewGroup Group(string identity)
        {
            if (!groups.TryGetValue(identity, out var group))
                groups.Add(identity, group = new(identity, Caption(document, identity),
                    document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "", () => !publishing && !closing));
            return group;
        }
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var scheduled = session.Schedule(Today).ToDictionary(r => r.Input.Identity);
        foreach (var row in document.State.Rows)
        {
            var result = scheduled[row.Identity];
            var current = row with { Start = result.Start.Value, End = result.End.Value };
            var isNew = !baseline.TryGetValue(row.Identity, out var before);
            before ??= new(row.Identity, "", row.Repository);
            if (isNew) Group(row.Identity).Lines.Add(new($"新規 Issue  {row.Repository}"));
            foreach (var field in PlanValues.RowFields)
            {
                if (PlanOperations.IsSummaryEffort(result.IsSummary, field) || PlanOperations.IsLocalConstraint(field, document.State.Settings)) continue;
                var oldValue = PlanValues.Get(before, field); var newValue = PlanValues.Get(current, field);
                var conflict = document.Sync.Conflicts.SingleOrDefault(c => c.Identity == row.Identity && c.Field == field);
                if (oldValue == newValue && conflict is null || isNew && field is PlanField.Title or PlanField.Repository) continue;
                Group(row.Identity).Lines.Add(ReviewLine(document, row.Identity, field, oldValue, newValue, conflict, isNew));
            }
        }
        foreach (var parent in document.State.Rows.Where(r => r.Parent is not null).Select(r => r.Parent!)
            .Concat(document.Sync.Conflicts.Where(c => c.Field == PlanField.SubIssueOrder).Select(c => c.Identity)).Distinct())
        {
            if (!document.State.Rows.Any(r => r.Identity == parent)) continue;
            var previous = PlanOperations.PreviousSiblingOrder(document, parent);
            var children = document.State.Rows.Where(r => r.Parent == parent).Select(r => r.Identity).ToArray();
            var conflict = document.Sync.Conflicts.SingleOrDefault(c => c.Identity == parent && c.Field == PlanField.SubIssueOrder);
            if (children.Length > 1 && !previous.SequenceEqual(children) || conflict is not null)
                Group(parent).Lines.Add(ReviewLine(document, parent, PlanField.SubIssueOrder, PlanJson.Text(previous), PlanJson.Text(children), conflict, !baseline.ContainsKey(parent)));
        }
        foreach (var conflict in document.Sync.Conflicts.Where(c => c.Field == PlanField.Order))
            Group(conflict.Identity).Lines.Add(ReviewLine(document, conflict.Identity, conflict.Field, conflict.Baseline, conflict.Local, conflict));
        if (session.Changes(Today).Fields.Any(p => p.Value.Contains(PlanField.Order)))
            Group(document.Project.NodeId).Lines.Add(new("表示順  " + string.Join("、", document.Baseline.Rows.Select(r => Caption(document, r.Identity))) + " → " + string.Join("、", document.State.Rows.Select(r => Caption(document, r.Identity)))));
        foreach (var identity in document.Sync.Unavailable)
        {
            var line = new PlanPublishReviewLine("GitHubで取得できません");
            foreach (var copy in new[] { false, true })
                line.Actions.Add(new(copy ? "新規Issueにコピー" : "計画から除く", "PlanUnavailable" + identity + copy, () => Run(async () => {
                    var position = ReviewPosition(identity);
                    Check(await session.ResolveUnavailable(identity, copy, Today)); RenderTasks(); RenderReview();
                    RestoreReviewPosition(identity, position);
                })));
            Group(identity).Lines.Add(line);
        }
        foreach (var failure in document.Sync.Failures)
            Group(failure.Identity).Lines.Add(new((failure.Reason == "NotDispatched" ? "未送信: " : "発行失敗: ") + PlanPublishText.Failure(failure)));
        foreach (var identity in document.Sync.Unverified)
            Group(identity).Lines.Add(new("未検証 — 最新の情報に更新で確認"));
        if (groups.Count == 0) groups.Add("", new("", "", "", () => false) { Lines = [new("未発行の変更はありません")] });
        reviewLines.ItemsSource = groups.Values.ToArray();
        confirmPublish.IsEnabled = !publishing && document.Sync.Conflicts.IsEmpty && document.Sync.Unavailable.IsEmpty;
    }
    private int ReviewPosition(string identity) => reviewLines.Items.Cast<PlanPublishReviewGroup>().TakeWhile(g => g.Identity != identity).Count();
    private void RestoreReviewPosition(string identity, int position)
    {
        if (reviewLines.Items.Count == 0) return;
        var item = reviewLines.Items.Cast<PlanPublishReviewGroup>().FirstOrDefault(g => g.Identity == identity) ??
            reviewLines.Items[Math.Clamp(position, 0, reviewLines.Items.Count - 1)];
        // ItemsSource was replaced; focus/layout must settle before requesting the target group's position.
        reviewLines.UpdateLayout();
        reviewLines.Focus(FocusState.Programmatic);
        reviewLines.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
    }
    private PlanPublishReviewLine ReviewLine(PlanDocument document, string identity, PlanField field, string? before, string? after, PlanConflict? conflict, bool isNew = false)
    {
        var label = document.State.Settings.Columns.FirstOrDefault(c => c.Role == field)?.Name ?? field switch {
            PlanField.Title => "タイトル", PlanField.Assignees => "担当者", PlanField.Parent => "親タスク",
            PlanField.Predecessors => "先行タスク", PlanField.Order => "表示順", PlanField.SubIssueOrder => "子タスクの順序",
            PlanField.Fixed => "日程固定", PlanField.StartNoEarlierThan => "開始日指定", _ => field.ToString() };
        var line = new PlanPublishReviewLine(isNew ? $"{label}  {ReviewValue(document, field, after)}" : $"{label}  {ReviewValue(document, field, before)} → {ReviewValue(document, field, after)}");
        if (field is PlanField.Parent or PlanField.Predecessors &&
            new[] { before, after, conflict?.Remote }.Any(json => ReviewValue(document, field, json).Contains("未取得")))
        {
            if (document.Sync.IssueLinks.TryGetValue(identity, out var link) &&
                Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == document.Project.Scope.Host)
            { line.Link = uri; line.LinkCaption = $"{link.Caption} の関連Issueを確認"; }
        }
        if (conflict is not null)
        {
            line.Text += "  競合  GitHub: " + ReviewValue(document, field, conflict.Remote);
            foreach (var remote in new[] { false, true })
                line.Actions.Add(new(remote ? "GitHubを採用" : "ローカルを採用", $"PlanResolve{conflict.Identity}_{field}_{remote}", () => Run(async () => {
                    var position = ReviewPosition(identity);
                    Check(await workspace.Session!.ResolveConflict(conflict.Identity, field, remote, Today));
                    RenderTasks();
                    RestoreReviewPosition(identity, position);
                })));
        }
        return line;
    }
    private static string Caption(PlanDocument document, string identity)
    {
        var index = document.State.Rows.IndexOf(document.State.Rows.FirstOrDefault(r => r.Identity == identity)!);
        return index >= 0 ? $"{index + 1} {document.State.Rows[index].Title}" : identity == document.Project.NodeId ? "プロジェクト" :
            document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "計画外Issue（未取得）";
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
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await publisher.PublishAsync(session, Today, OperationToken, reporter);
            if (Environment.GetEnvironmentVariable("GHPB_PUBLISH_METRICS") is { Length: > 0 } metricsPath)
            {
                try
                {
                    System.IO.File.AppendAllText(metricsPath, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow,
                        elapsedSeconds = watch.Elapsed.TotalSeconds, result.Succeeded, result.Error,
                        mutationBatchSizes = publisher.EffectiveBatchSizes, creationWaitSeconds = publisher.CreationWait.TotalSeconds }) + Environment.NewLine);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
                { System.Diagnostics.Debug.WriteLine("Publish metrics could not be saved: " + ex.Message); }
            }
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
        projectPicker.IsEnabled = !busy; settingsButton.IsEnabled = !busy;
        tasksTab.IsEnabled = peopleTab.IsEnabled = !busy;
        foreach (var button in commandButtons.Children.OfType<Button>()) button.IsEnabled = !busy || AutomationProperties.GetAutomationId(button) == "PlanPublish";
        if (sheet is not null) sheet.SetRemoteBusy(busy);
        settingsScroll.IsEnabled = !busy;
        if (peopleView is not null) peopleView.IsEnabled = !busy;
        confirmPublish.IsEnabled = !busy;
        if (reviewLines.ItemsPanelRoot is { } panel)
            foreach (var group in panel.Children.OfType<ListViewItem>().Select(i => i.ContentTemplateRoot).OfType<PlanPublishGroupView>()) group.RefreshActions();
    }
}

internal sealed record PlanPublishReviewGroup(string Identity, string Caption, string IssueCaption, Func<bool> CanAct)
{
    internal List<PlanPublishReviewLine> Lines { get; init; } = [];
}
internal sealed class PlanPublishReviewLine(string text)
{
    internal string Text { get; set; } = text;
    internal Uri? Link { get; set; }
    internal string? LinkCaption { get; set; }
    internal List<PlanPublishReviewAction> Actions { get; } = [];
}
internal sealed record PlanPublishReviewAction(string Caption, string AutomationId, Func<Task> Invoke);

// Only realized ListView items own native controls; the review source contains plain presentation data.
public sealed class PlanPublishGroupView : Grid
{
    private readonly List<Button> actions = [];
    public PlanPublishGroupView()
    {
        ColumnSpacing = 16;
        ColumnDefinitions.Add(new() { Width = new(240) });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        HorizontalAlignment = HorizontalAlignment.Stretch;
        DataContextChanged += (_, _) => Render();
        Loaded += (_, _) => RefreshActions();
    }
    internal void RefreshActions()
    {
        var enabled = DataContext is PlanPublishReviewGroup group && group.CanAct();
        foreach (var button in actions) button.IsEnabled = enabled;
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private void Render()
    {
        Children.Clear(); actions.Clear();
        if (DataContext is not PlanPublishReviewGroup group) return;
        AutomationProperties.SetAutomationId(this, "PlanPublishGroup" + group.Identity);
        var caption = new StackPanel { Spacing = 2 };
        var title = Text(group.Caption); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        caption.Children.Add(title);
        if (group.IssueCaption.Length > 0) caption.Children.Add(Text(group.IssueCaption));
        Children.Add(caption);
        var differences = new StackPanel { Spacing = 4 };
        Children.Add(differences); SetColumn(differences, 1);
        var ordinary = group.Lines.Where(l => l.Actions.Count == 0 && l.Link is null).ToArray();
        if (ordinary.Length > 0) differences.Children.Add(Text(string.Join("  /  ", ordinary.Select(l => l.Text))));
        foreach (var line in group.Lines.Where(l => l.Actions.Count > 0 || l.Link is not null))
        {
            differences.Children.Add(Text(line.Text));
            if (line.Link is not null) differences.Children.Add(new HyperlinkButton { Content = line.LinkCaption, NavigateUri = line.Link });
            if (line.Actions.Count == 0) continue;
            var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var action in line.Actions)
            {
                var button = new Button { Content = action.Caption };
                AutomationProperties.SetAutomationId(button, action.AutomationId);
                button.Click += async (_, _) => { if (button.IsLoaded && group.CanAct()) await action.Invoke(); };
                actions.Add(button); choices.Children.Add(button);
            }
            differences.Children.Add(choices);
        }
        RefreshActions();
    }
}

internal static class PlanPublishText
{
    internal static string Failure(PlanPublishFailure failure) => failure.Reason switch
    {
        "VerificationMismatch" when failure.Field == PlanField.NewTask => "Project への追加を確認できません",
        "VerificationMismatch" => "GitHubへの反映を確認できません",
        "NotDispatched" => "まだGitHubへ送信されていません",
        _ when failure.Reason.IndexOf(": ", StringComparison.Ordinal) is var separator && separator >= 0 => failure.Reason[(separator + 2)..],
        _ when failure.Reason.Any(c => c > 127 || char.IsWhiteSpace(c)) => failure.Reason,
        _ => "GitHubへの発行に失敗しました。再発行してください。"
    };
}
