using System.Globalization;
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
    private readonly TextBlock publishBlockedReason = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanPublishBlockedReason");
    private readonly TextBlock publishStatusDate = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanPublishStatusDate");
    private readonly TextBlock publishSummary = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanPublishSummary");
    private readonly TextBlock publishDestination = Id(new TextBlock { TextWrapping = TextWrapping.Wrap }, "PlanPublishDestination");
    private readonly TextBlock publishConflictCount = Id(new TextBlock(), "PlanPublishConflictTag");
    private readonly Border publishConflictTag = new() { CornerRadius = new(4), Padding = new(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock publishCounts = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, "PlanPublishCounts");
    // #258 D2: an Issue Field write changes the Issue itself, so every Project holding it shows the new value.
    private readonly TextBlock publishIssueFields = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanPublishIssueFields");
    private readonly TextBlock publishResult = Id(new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, "PlanPublishResult");
    private PlanDocument? publishedDocument;
    private Button openPublish = null!;
    private int reviewTaskCount;
    private bool reviewHasWritableChanges, reviewEmpty;
    private Button confirmPublish = null!;
    private bool publishing;
    // #224: the review in its three sections; each recalculation group's entries follow its header and
    // are listed only while that group is open. Groups start closed each time the review opens.
    private List<PlanPublishReviewGroup> reviewItems = [];
    private readonly HashSet<string> openCauses = new(StringComparer.Ordinal);

    private void InitializePublishing()
    {
        var open = openPublish = CommandButton("発行…", "PlanPublish", Symbol.Upload);
        open.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        open.Click += async (_, _) => {
            if (publishing) return;
            await Run(() => { openCauses.Clear(); RenderReview(); Show("publish"); return Task.CompletedTask; });
        };
        commandButtons.Children.Add(open);
        publishReview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        publishReview.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        publishReview.RowDefinitions.Add(new() { Height = GridLength.Auto });
        publishSummary.Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"];
        publishDestination.Foreground = PlanSheetView.Brush("TextFillColorSecondaryBrush");
        publishConflictTag.Background = PlanSheetView.Brush("SystemFillColorCriticalBackgroundBrush");
        publishConflictTag.BorderBrush = publishConflictCount.Foreground = PlanSheetView.Brush("SystemFillColorCriticalBrush");
        publishConflictTag.BorderThickness = new(1);
        publishConflictTag.Child = publishConflictCount;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        title.Children.Add(publishSummary); title.Children.Add(publishConflictTag);
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(title); header.Children.Add(publishDestination); header.Children.Add(publishIssueFields); header.Children.Add(publishResult); header.Children.Add(publishStatusDate);
        AutomationProperties.SetLiveSetting(publishBlockedReason, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(publishResult, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        publishReview.Children.Add(header);
        reviewLines.ContainerContentChanging += (_, args) => {
            AutomationProperties.SetName(args.ItemContainer, !args.InRecycleQueue && args.Item is PlanPublishReviewGroup group
                ? string.Join("\n", new[] { group.Caption }.Concat(group.Lines.Select(line => line.Text))) : "");
        };
        reviewLines.ItemTemplate = (DataTemplate)Application.Current.Resources["PlanPublishGroupTemplate"];
        ScrollViewer.SetHorizontalScrollBarVisibility(reviewLines, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(reviewLines, ScrollMode.Disabled);
        reviewLines.ItemContainerStyle = new Style(typeof(ListViewItem)) { BasedOn = (Style)Application.Current.Resources["PlanListViewItemStyle"], Setters = {
            new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } };
        publishReview.Children.Add(reviewLines); Grid.SetRow(reviewLines, 1);
        // The decision comes after the content: counts on the left, 閉じる and the primary action at the bottom right (#224).
        var footer = new Grid { ColumnSpacing = 16 };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var facts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        facts.Children.Add(publishCounts); facts.Children.Add(publishBlockedReason);
        footer.Children.Add(facts);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        confirmPublish = Button("発行する", "PlanPublishConfirm", Publish);
        confirmPublish.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var close = Id(new Button { Content = "閉じる" }, "PlanPublishClose");
        close.Click += (_, _) => { if (!closing && !publishing) Show("tasks"); };
        actions.Children.Add(close); actions.Children.Add(confirmPublish);
        footer.Children.Add(actions); Grid.SetColumn(actions, 1);
        publishReview.Children.Add(footer); Grid.SetRow(footer, 2);
    }

    private void RenderReview()
    {
        if (workspace.Session is not { } session) return;
        ClearObsoletePublishResult();
        var document = session.Document;
        var today = Today;
        var schedule = session.Schedule(today);
        var changes = PlanOperations.Changes(document, today, schedule);
        var names = PlanTaskNames.Of(document);
        var statusDate = document.State.Settings.StatusDate ?? today;
        var pastStatusDate = statusDate < today;
        var dateText = statusDate.ToString("yyyy/MM/dd (ddd)", CultureInfo.GetCultureInfo("ja-JP"));
        // Only a stale 基準日 is worth a line: the review's dates are the plan's own and need no caption.
        publishStatusDate.Text = pastStatusDate ? "基準日 " + dateText + " は今日より前です。" : "";
        publishStatusDate.Visibility = pastStatusDate ? Visibility.Visible : Visibility.Collapsed;
        publishStatusDate.Foreground = PlanSheetView.Brush("SystemFillColorCautionBrush");
        AutomationProperties.SetName(publishStatusDate, publishStatusDate.Text);
        var conflicts = PlanOperations.BlockingConflicts(document);
        Func<bool> canAct = () => !publishing && !closing;
        // 競合 items in their own section; あなたの変更 grouped per task; recalculated lines per task, grouped by cause below.
        var conflictItems = new List<PlanPublishReviewGroup>();
        var groups = new Dictionary<string, PlanPublishReviewGroup>();
        var recalculated = new Dictionary<string, PlanPublishReviewGroup>();
        var writable = new HashSet<string>(StringComparer.Ordinal);
        var values = 0;
        PlanPublishReviewGroup Group(string identity)
        {
            if (!groups.TryGetValue(identity, out var group))
                groups.Add(identity, group = new(identity, Caption(document, identity),
                    document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "", canAct));
            return group;
        }
        PlanPublishReviewGroup Recalculated(string identity)
        {
            if (!recalculated.TryGetValue(identity, out var group))
                recalculated.Add(identity, group = new(identity, Caption(document, identity),
                    document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "", canAct) { Kind = PlanReviewItemKind.Recalculated });
            return group;
        }
        void Write(string identity) { writable.Add(identity); values++; }
        void Conflict(string identity, string field, string? local, PlanConflict conflict)
        {
            var label = PlanSheetColumns.ReviewName(field, PlanSheetColumns.All(document).FirstOrDefault(c => c.Key == field));
            var item = new PlanPublishReviewGroup(identity, Caption(document, identity), document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "", canAct)
                { Kind = PlanReviewItemKind.Conflict };
            var line = new PlanPublishReviewLine(label) { Local = ReviewValue(document, field, local), Remote = ReviewValue(document, field, conflict.Remote) };
            ResolveActions(line, identity, field, conflict);
            item.Lines.Add(line);
            conflictItems.Add(item);
        }
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        var scheduled = schedule.ToDictionary(r => r.Input.Identity);
        // Rows this publish cannot create because they have no title, and every new row that would be
        // created inside one (#247). Their writes are held, so the review must not present them as writes.
        var held = PlanOperations.HeldRows(document);
        var untitled = PlanOperations.UntitledNewRows(document).ToHashSet(StringComparer.Ordinal);
        var issueFieldTasks = new HashSet<string>(StringComparer.Ordinal);
        // #319: 種類 is likewise the Issue's own value, so the review states the shared scope for it too.
        var issueTypeTasks = new HashSet<string>(StringComparer.Ordinal);
        var newIssues = 0;
        void MoveToRow(string identity) { Show("tasks"); sheet?.RevealAddedRow(identity); }
        void RowActions(PlanPublishReviewLine line, string identity)
        {
            line.Actions.Add(new("行へ移動", $"PlanReviewMoveTo{identity}", () => { MoveToRow(identity); return Task.CompletedTask; }));
            line.Actions.Add(new("行を削除", $"PlanReviewDelete{identity}", () => Run(async () => {
                var position = ReviewPosition(identity);
                Check(await session.Execute(new DeletePlanRows([identity]), Today));
                RenderTasks();
                RestoreReviewPosition(identity, position);
            })));
        }
        foreach (var row in document.State.Rows)
        {
            if (held.Contains(row.Identity))
            {
                var line = new PlanPublishReviewLine(untitled.Contains(row.Identity)
                    ? $"タイトルのない新しい行 {untitled.Count:N0} 件は発行しません"
                    : "タイトルのない新しい行の中にあるため発行しません");
                RowActions(line, row.Identity);
                Group(row.Identity).Lines.Add(line);
                continue;
            }
            var result = scheduled[row.Identity];
            var current = row with { Start = result.Start.Value, End = result.End.Value };
            // Same rule as PlanOperations.Changes: a closed task publishes its Remaining as 0.
            if (row.Closed && !result.IsSummary) current = current with { Remaining = 0 };
            var isNew = !baseline.TryGetValue(row.Identity, out var before);
            before ??= new(row.Identity, "", row.Repository);
            if (isNew) { Group(row.Identity).Lines.Add(new($"新規 Issue  {row.Repository} に作成")); writable.Add(row.Identity); newIssues++; }
            // #231: the review names what the publish sends and nothing else. この版から外す archives the item
            // alone; a close or reopen sends its state write, and its dates and remaining work follow the read.
            var stateChange = !isNew && before.Closed != row.Closed;
            if (!isNew && !before.Removed && row.Removed)
            {
                Group(row.Identity).Lines.Add(new("GitHub: プロジェクトからアーカイブします（Issue とフィールドの値は残ります）"));
                Write(row.Identity);
                continue;
            }
            if (stateChange)
            {
                Group(row.Identity).Lines.Add(new(row.Closed
                    ? row.Cancelled ? "GitHub: Issue を完了としてではなく閉じます（中止）" : "GitHub: Issue を完了として閉じます"
                    : "GitHub: Issue を再オープンします"));
                Write(row.Identity);
            }
            foreach (var field in PlanValues.Fields(before, current))
            {
                if (stateChange && field is PlanField.Start or PlanField.End or PlanField.Remaining or PlanField.Cancelled) continue;
                if (PlanSheetColumns.IsMappedExtra(document, field)) continue;
                var conflict = conflicts.SingleOrDefault(c => c.Identity == row.Identity && c.Field == field);
                if (conflict is null && PlanOperations.IsSummaryPublishExcluded(result.IsSummary, field)) continue;
                var oldValue = PlanValues.Get(before, field); var newValue = PlanValues.Get(current, field);
                if (oldValue == newValue && conflict is null || isNew && field is PlanField.Title or PlanField.Repository) continue;
                if (conflict is not null) { Conflict(row.Identity, field, newValue, conflict); continue; }
                // A relationship that would name a row this publish does not create is held, not written.
                // The same Core predicate decides this line and the publish's completeness (#247).
                var holds = PlanOperations.HoldsRemainingChange(document, row.Identity, field, held);
                var kind = changes.InputKind(row.Identity, field);
                if (!holds && kind is not null) Write(row.Identity);
                var line = ReviewLine(document, field, oldValue, newValue, isNew, row.Identity);
                if (holds) line.Text += "（タイトルのない新しい行のため保留）";
                else if (oldValue != newValue && PlanSheetColumns.IsIssueField(document, field))
                {
                    line.Text += "（Issue フィールド）";
                    issueFieldTasks.Add(row.Identity);
                }
                else if (oldValue != newValue && field == PlanField.IssueType) issueTypeTasks.Add(row.Identity);
                if (kind == PlanUnpublishedInputKind.Recalculated && !isNew) Recalculated(row.Identity).Lines.Add(line);
                else { line.Typed = kind == PlanUnpublishedInputKind.Entered || isNew; Group(row.Identity).Lines.Add(line); }
            }
        }
        foreach (var parent in document.State.Rows.Where(r => r.Parent is not null).Select(r => r.Parent!)
            .Concat(conflicts.Where(c => c.Field == PlanField.SubIssueOrder).Select(c => c.Identity)).Distinct())
        {
            if (!document.State.Rows.Any(r => r.Identity == parent) || held.Contains(parent)) continue;
            var conflict = conflicts.SingleOrDefault(c => c.Identity == parent && c.Field == PlanField.SubIssueOrder);
            // The same comparison decides the unpublished count, this line and the publish writes.
            var sibling = PlanOperations.SiblingOrderChange(document, parent);
            if (sibling is null && conflict is null) continue;
            var previous = sibling?.Before ?? PlanOperations.PreviousSiblingOrder(document, parent);
            var children = sibling?.After ?? PlanOperations.PlanChildren(document, parent);
            if (conflict is not null) { Conflict(parent, PlanField.SubIssueOrder, PlanJson.Text(children), conflict); continue; }
            // An order that names a row this publish does not create is held whole (#247). The same Core
            // predicate decides this line and the publish's completeness.
            var holdsOrder = PlanOperations.HoldsRemainingChange(document, parent, PlanField.SubIssueOrder, held);
            if (!holdsOrder) Write(parent);
            var orderLine = OrderLine(document, names, previous, children, !baseline.ContainsKey(parent));
            orderLine.Typed = true;
            if (holdsOrder) orderLine.Text += "（タイトルのない新しい行のため保留）";
            Group(parent).Lines.Add(orderLine);
        }
        foreach (var conflict in conflicts.Where(c => c.Field == PlanField.Order))
            Conflict(conflict.Identity, conflict.Field, conflict.Local, conflict);
        foreach (var conflict in document.Sync.Conflicts.Where(c => c.DeletedFieldName is not null))
        {
            var line = new PlanPublishReviewLine($"{conflict.DeletedFieldName}  {ReviewValue(document, conflict.Field, conflict.Local)}  GitHub で削除されたフィールド（発行対象外）");
            line.Actions.Add(new("編集を破棄", $"PlanDiscardDeleted{conflict.Identity}_{conflict.Field}", () => Run(async () => {
                var position = ReviewPosition(conflict.Identity);
                Check(await session.ResolveConflict(conflict.Identity, conflict.Field, true, Today));
                RenderTasks(); RenderReview(); RestoreReviewPosition(conflict.Identity, position);
            })));
            Group(conflict.Identity).Lines.Add(line);
        }
        var baselinePositions = document.Baseline.Rows.Select((row, index) => (row.Identity, Position: index + 1))
            .ToDictionary(p => p.Identity, p => p.Position);
        foreach (var (row, index) in document.State.Rows.Select((row, index) => (row, index)))
        {
            if (held.Contains(row.Identity)) continue;
            if (!changes.Fields.TryGetValue(row.Identity, out var fields) ||
                !fields.Contains(PlanField.Order) && !fields.Contains(PlanField.NewTask)) continue;
            var before = baselinePositions.TryGetValue(row.Identity, out var position) ? position.ToString(CultureInfo.InvariantCulture) : "—";
            Write(row.Identity);
            Group(row.Identity).Lines.Add(new($"表示順  {before} → {index + 1}") { Typed = true });
        }
        foreach (var identity in document.Sync.Unavailable)
        {
            var line = new PlanPublishReviewLine("GitHubで取得できません");
            foreach (var copy in new[] { false, true })
                line.Actions.Add(new(copy ? "新規Issueにコピー" : "計画から除く", "PlanUnavailable" + identity + copy, () => Run(async () => {
                    var position = ReviewPosition(identity);
                    Check(await session.ResolveUnavailable(identity, copy, Today)); RenderTasks(); RenderReview();
                    RestoreReviewPosition(identity, position);
                })));
            var item = new PlanPublishReviewGroup(identity, Caption(document, identity), document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "", canAct);
            item.Lines.Add(line);
            conflictItems.Add(item);
        }
        foreach (var failure in document.Sync.Failures)
            Group(failure.Identity).Lines.Add(new((failure.Reason == "NotDispatched" ? "未送信: " : "発行失敗: ") + PlanPublishText.Failure(failure)));
        foreach (var identity in document.Sync.Unverified)
            Group(identity).Lines.Add(new("GitHub で未確認 — 最新の情報に更新で確認"));

        var items = new List<PlanPublishReviewGroup>();
        PlanPublishReviewGroup Section(string key, string caption) => new("§" + key, caption, "", canAct) { Kind = PlanReviewItemKind.Section };
        if (conflictItems.Count > 0)
        {
            items.Add(Section("Conflicts", $"競合  {conflictItems.Count} 件"));
            items.AddRange(conflictItems);
        }
        if (groups.Count > 0)
        {
            items.Add(Section("Own", $"あなたの変更  {groups.Count} タスク"));
            items.AddRange(groups.Values);
        }
        var causes = PlanPublishGrouping.RecalculationGroups(document, changes, schedule)
            .Select(g => (Group: g, Entries: g.Tasks.Where(recalculated.ContainsKey).Select(id => recalculated[id]).ToArray()))
            .Where(g => g.Entries.Length > 0).ToArray();
        if (causes.Length > 0)
        {
            var section = Section("Recalculated", $"再計算で動いた日程  {causes.SelectMany(c => c.Entries).Select(e => e.Identity).Distinct().Count()} タスク");
            section.Actions.Add(new("すべて開く", "PlanPublishExpandAll", () => {
                var all = causes.All(c => openCauses.Contains(c.Group.Cause ?? ""));
                foreach (var (group, _) in causes) { if (all) openCauses.Remove(group.Cause ?? ""); else openCauses.Add(group.Cause ?? ""); }
                ShowReviewItems();
                return Task.CompletedTask;
            }));
            items.Add(section);
            foreach (var (group, entries) in causes)
            {
                var key = group.Cause ?? "";
                var header = new PlanPublishReviewGroup("§Cause" + key, CauseCaption(document, names, group, entries.Length), "", canAct)
                    { Kind = PlanReviewItemKind.Cause, CauseKey = key };
                header.Actions.Add(new("", "PlanPublishCause" + (key.Length == 0 ? "Other" : key), () => {
                    if (!openCauses.Remove(key)) openCauses.Add(key);
                    ShowReviewItems();
                    return Task.CompletedTask;
                }));
                items.Add(header);
                foreach (var entry in entries) { entry.CauseKey = key; items.Add(entry); }
            }
        }
        // Unattributed recalculated lines (none expected) must never disappear from the review.
        foreach (var orphan in recalculated.Values.Where(g => g.CauseKey is null)) { orphan.Kind = PlanReviewItemKind.Task; items.Add(orphan); }
        reviewEmpty = items.Count == 0;
        if (reviewEmpty) items.Add(new("", "", "", () => false) { Lines = [new("未発行の変更はありません")] });
        reviewTaskCount = writable.Count(id => id != document.Project.NodeId);
        // An interrupted or partly failed publication stays resumable through explicit confirmation.
        reviewHasWritableChanges = writable.Count > 0 || document.Sync.Publish is not null || !document.Sync.Failures.IsEmpty;
        publishSummary.Text = $"{reviewTaskCount} タスクを発行";
        publishSummary.Visibility = reviewTaskCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        var repositories = document.State.Rows.Where(r => writable.Contains(r.Identity)).Select(r => r.Repository)
            .Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        publishDestination.Text = workspace.Selected is { } project
            ? $"{project.Title} → {project.OwnerLogin} / プロジェクト {project.Number}" + (repositories.Length == 0 ? "" : "、" + string.Join("、", repositories)) : "";
        publishConflictCount.Text = conflictItems.Count == 0 ? "" : $"競合 {conflictItems.Count} 件を選んでください";
        publishConflictTag.Visibility = conflictItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(publishConflictCount, publishConflictCount.Text);
        publishCounts.Text = reviewTaskCount == 0 ? "" : $"書き込む値 {values:N0} · 新しい Issue {newIssues:N0}";
        // #258/#319: a value written on the Issue is shared by every Project holding it; the review states that
        // once below the destination line for each such column whose changes this publish sends.
        var issueWrites = new List<string>();
        if (issueFieldTasks.Count > 0) issueWrites.Add(PlanPublishText.IssueDestinationNote("Issue フィールド", issueFieldTasks.Count));
        if (issueTypeTasks.Count > 0) issueWrites.Add(PlanPublishText.IssueDestinationNote("種類", issueTypeTasks.Count));
        publishIssueFields.Text = string.Join("\n", issueWrites);
        publishIssueFields.Visibility = issueWrites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(publishIssueFields, publishIssueFields.Text);
        confirmPublish.Content = reviewTaskCount > 0 ? $"{reviewTaskCount} タスクを発行" : "発行する";
        reviewItems = items;
        ShowReviewItems();
        UpdatePublishAvailability();
    }
    private void ShowReviewItems()
    {
        foreach (var header in reviewItems.Where(i => i.Kind == PlanReviewItemKind.Cause)) header.Open = openCauses.Contains(header.CauseKey!);
        var causes = reviewItems.Where(i => i.Kind == PlanReviewItemKind.Cause).ToArray();
        if (reviewItems.FirstOrDefault(i => i.Identity == "§Recalculated") is { } section)
            section.Actions[0] = section.Actions[0] with { Caption = causes.All(c => c.Open) ? "すべて閉じる" : "すべて開く" };
        reviewLines.ItemsSource = reviewItems.Where(i => i.Kind != PlanReviewItemKind.Recalculated || openCauses.Contains(i.CauseKey!)).ToArray();
    }
    private static string CauseCaption(PlanDocument document, PlanTaskNames names, PlanRecalculationGroup group, int count)
    {
        if (group.Cause is null) return $"入力した変更によらない再計算で {count} タスク";
        var name = names[group.Cause];
        if (group.CauseFields.Contains(PlanField.NewTask)) return $"{name} の追加で {count} タスク";
        var columns = PlanSheetColumns.All(document);
        return $"{name} の{string.Join("・", group.CauseFields.Select(f => PlanSheetColumns.ReviewName(f, columns.FirstOrDefault(c => c.Key == f))))}の変更で {count} タスク";
    }
    // #224: a parent's rewritten child order is shown around the rows that changed it, not as the whole list.
    private static PlanPublishReviewLine OrderLine(PlanDocument document, PlanTaskNames names, System.Collections.Immutable.ImmutableArray<string> before,
        System.Collections.Immutable.ImmutableArray<string> after, bool isNew)
    {
        var label = PlanSheetColumns.ReviewName(PlanField.SubIssueOrder);
        var excerpt = PlanPublishGrouping.ChildOrderExcerpt(document, before, after);
        if (isNew || excerpt.Causes.IsEmpty)
            return ReviewLine(document, PlanField.SubIssueOrder, PlanJson.Text(before), PlanJson.Text(after), isNew);
        var added = excerpt.Causes.Where(c => c.Added).Select(c => names[c.Identity]).ToArray();
        var moved = excerpt.Causes.Where(c => !c.Added).Select(c => names[c.Identity]).ToArray();
        var reasons = new List<string>();
        if (added.Length > 0) reasons.Add(string.Join("、", added) + " の追加");
        if (moved.Length > 0) reasons.Add(string.Join("、", moved) + " の移動");
        var windows = string.Join("  …  ", excerpt.Windows.Select(w => string.Join(", ", w.Select(id => names[id]))));
        return new($"{label}  {string.Join("、", reasons)}による" + (windows.Length == 0 ? "" : $"  {windows}"));
    }
    private void UpdatePublishAvailability()
    {
        var reasons = new List<string>();
        // An empty review already says 未発行の変更はありません in the list.
        if (!reviewHasWritableChanges && !reviewEmpty) reasons.Add("書き込む未発行の変更はありません。");
        if (publishing) reasons.Add("発行中です。完了までお待ちください。");
        if (busyKind == "refresh") reasons.Add("最新の情報に更新しています。");
        if (busyKind == "reset") reasons.Add("GitHub の状態に戻しています。");
        if (closing) reasons.Add("終了処理中です。");
        if (workspace.Session is not { } session) reasons.Add("プロジェクトを開いてください。");
        else {
            var document = session.Document;
            var count = PlanOperations.BlockingConflicts(document).Length;
            if (count > 0)
                reasons.Add($"競合を解決してください（{count}件）。");
            if (!document.Sync.Unavailable.IsEmpty) reasons.Add("GitHubで取得できないタスクを確認してください。");
        }
        confirmPublish.IsEnabled = reasons.Count == 0 && reviewHasWritableChanges;
        publishBlockedReason.Text = string.Join(" ", reasons);
        publishBlockedReason.Visibility = reasons.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ClearPublishResult()
    {
        publishedDocument = null;
        publishResult.Text = "";
        publishResult.Visibility = Visibility.Collapsed;
    }
    private void ClearObsoletePublishResult()
    {
        if (publishedDocument is not null && !ReferenceEquals(publishedDocument, workspace.Session?.Document)) ClearPublishResult();
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
    private static PlanPublishReviewLine ReviewLine(PlanDocument document, string field, string? before, string? after, bool isNew = false, string? identity = null)
    {
        var label = PlanSheetColumns.ReviewName(field, PlanSheetColumns.All(document).FirstOrDefault(c => c.Key == field));
        var line = new PlanPublishReviewLine(isNew ? $"{label}  {ReviewValue(document, field, after)}" : $"{label}  {ReviewValue(document, field, before)} → {ReviewValue(document, field, after)}");
        if (identity is not null) RelationshipLink(document, line, identity, field, before, after);
        return line;
    }
    // A relationship outside the plan that the read could not name links to the source Issue's relationships.
    private static void RelationshipLink(PlanDocument document, PlanPublishReviewLine line, string identity, string field, params string?[] values)
    {
        if (field is PlanField.Parent or PlanField.Predecessors && values.Any(json => ReviewValue(document, field, json).Contains("未取得")) &&
            document.Sync.IssueLinks.TryGetValue(identity, out var link) &&
            Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == document.Project.Scope.Host)
        { line.Link = uri; line.LinkCaption = $"{link.Caption} の関連Issueを確認"; }
    }
    private void ResolveActions(PlanPublishReviewLine line, string identity, string field, PlanConflict conflict)
    {
        RelationshipLink(workspace.Session!.Document, line, identity, field, conflict.Local, conflict.Remote);
        foreach (var remote in new[] { false, true })
            line.Actions.Add(new(remote ? "GitHub の値にする" : "この PC の値にする", $"PlanResolve{conflict.Identity}_{field}_{remote}", () => Run(async () => {
                var position = ReviewPosition(identity);
                Check(await workspace.Session!.ResolveConflict(conflict.Identity, field, remote, Today));
                RenderTasks();
                RestoreReviewPosition(identity, position);
            })));
    }
    private static string Caption(PlanDocument document, string identity)
    {
        var index = document.State.Rows.IndexOf(document.State.Rows.FirstOrDefault(r => r.Identity == identity)!);
        if (index < 0) return identity == document.Project.NodeId ? "プロジェクト" :
            document.Sync.IssueLinks.GetValueOrDefault(identity)?.Caption ?? "計画外Issue（未取得）";
        // A task is named by its Issue number or 新N, never its row position (#263); an untitled new row has only that.
        var name = PlanTaskNames.Of(document)[identity];
        return document.State.Rows[index].Title.Length == 0 ? $"{name} タイトルのない新しい行" : $"{name} {document.State.Rows[index].Title}";
    }
    internal static string ReviewValue(PlanDocument document, string field, string? json)
    {
        if (json is null or "null") return "未入力";
        using var value = JsonDocument.Parse(json);
        var element = value.RootElement;
        string Identity(string id) => field == PlanField.Labels ? id : field == PlanField.Assignees ? document.Sync.PeopleNames.GetValueOrDefault(id,
            document.State.Settings.People.FirstOrDefault(p => p.Identity == id)?.Name ?? "担当者（未確認）") : Caption(document, id);
        if (field is PlanField.Start or PlanField.End && element.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            // The plan's own date form; the year appears only when it is not the 基準日's year.
            return (day.Year == (document.State.Settings.StatusDate ?? PlanClock.Today).Year ? "" : day.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/")
                + day.ToString("M/d", System.Globalization.CultureInfo.InvariantCulture) + " (" + "日月火水木金土"[(int)day.DayOfWeek] + ")";
        if (field is PlanField.Estimate or PlanField.Actual or PlanField.Remaining && element.ValueKind == JsonValueKind.Number) return element.ToString() + "h";
        return element.ValueKind switch {
            JsonValueKind.Array => element.GetArrayLength() == 0 ? "未入力" : string.Join("、", element.EnumerateArray().Select(e => Identity(e.GetString()!))),
            JsonValueKind.String => field == PlanField.Parent ? Identity(element.GetString()!) : element.GetString() is { Length: > 0 } text ? text : "未入力",
            // #231: the row-state fields read as the command's own words, never as a bare 完了.
            JsonValueKind.True => field switch { PlanField.Cancelled => "中止にする", PlanField.Removed => "この版から外す", _ => "完了" },
            JsonValueKind.False => field switch { PlanField.Cancelled => "中止にしない", PlanField.Removed => "この版に残す", _ => "未完了" },
            _ => element.ToString() };
    }
    private async Task Publish()
    {
        if (workspace.Session is not { } session || publishing || !confirmPublish.IsEnabled) return;
        var taskCount = reviewTaskCount;
        ClearPublishResult();
        publishing = true;
        Show("tasks");
        var reporter = BeginBusy("publish", "発行しています");
        SetPublishBusy(true);
        var outcome = "発行を中断しました";
        try
        {
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
            outcome = result.Succeeded ? "発行しました" : "発行できませんでした。" + result.Error;
            var save = await session.FlushAsync();
            if (!save.Succeeded) outcome = "保存できませんでした。" + save.Error;
            if (!closing)
            {
                if (!save.Succeeded) ShowSaveFailure(save.Error ?? "保存できません。再試行してください。");
                else {
                    ClearSaveFailures();
                    publishFailure.Message = result.Error ?? "";
                    publishFailure.IsOpen = !result.Succeeded && !OperationToken.IsCancellationRequested;
                }
                RenderTasks();
                if (result.Succeeded)
                {
                    publishedDocument = session.Document;
                    publishResult.Text = $"{taskCount} タスクを発行しました（{DateTimeOffset.Now.ToString("H:mm", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"))}）";
                    publishResult.Visibility = Visibility.Visible;
                    (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(publishResult)
                        ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(publishResult))
                        ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
                }
            }
        }
        catch (OperationCanceledException) when (OperationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = "発行できませんでした。" + ex.Message;
            if (ex is IOException or UnauthorizedAccessException) ShowSaveFailure(ex.Message);
            else { publishFailure.Message = ex.Message; publishFailure.IsOpen = true; }
        }
        finally
        {
            publishing = false;
            EndBusy(outcome);
            SetPublishBusy(false);
            if (!closing) { RenderReview(); Show("publish"); }
        }
    }
    private void SetPublishBusy(bool busy)
    {
        SetRemotePresentation();
        UpdatePublishAvailability();
        if (reviewLines.ItemsPanelRoot is { } panel)
            foreach (var group in panel.Children.OfType<ListViewItem>().Select(i => i.ContentTemplateRoot).OfType<PlanPublishGroupView>()) group.RefreshActions();
    }
}

internal enum PlanReviewItemKind { Task, Section, Conflict, Cause, Recalculated }
internal sealed record PlanPublishReviewGroup(string Identity, string Caption, string IssueCaption, Func<bool> CanAct)
{
    internal PlanReviewItemKind Kind { get; set; }
    // The recalculation group a cause header or a recalculated entry belongs to; "" is the group without an edit.
    internal string? CauseKey { get; set; }
    internal bool Open { get; set; }
    internal List<PlanPublishReviewLine> Lines { get; init; } = [];
    internal List<PlanPublishReviewAction> Actions { get; } = [];
}
internal sealed class PlanPublishReviewLine(string text)
{
    internal string Text { get; set; } = text;
    // A value the PMO typed carries the sheet's typed tint (#224).
    internal bool Typed { get; set; }
    // A conflict's two values, shown side by side with their choices.
    internal string? Local { get; init; }
    internal string? Remote { get; init; }
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
    private Button Action(PlanPublishReviewGroup group, PlanPublishReviewAction action, object? content = null)
    {
        var button = new Button { Content = content ?? action.Caption };
        AutomationProperties.SetAutomationId(button, action.AutomationId);
        button.Click += async (_, _) => { if (button.IsLoaded && group.CanAct()) await action.Invoke(); };
        actions.Add(button);
        return button;
    }
    private static StackPanel TaskCaption(PlanPublishReviewGroup group)
    {
        var caption = new StackPanel { Spacing = 2 };
        var title = Text(group.Caption); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        caption.Children.Add(title);
        if (group.IssueCaption.Length > 0) caption.Children.Add(Text(group.IssueCaption));
        return caption;
    }
    private void Render()
    {
        Children.Clear(); actions.Clear(); ColumnDefinitions.Clear();
        Margin = new(0); Background = null; ColumnSpacing = 16;
        if (DataContext is not PlanPublishReviewGroup group) return;
        AutomationProperties.SetAutomationId(this, group.Kind switch {
            PlanReviewItemKind.Section => "PlanPublishSection" + group.Identity[1..],
            PlanReviewItemKind.Cause => "PlanPublishCauseGroup" + (group.CauseKey is { Length: > 0 } key ? key : "Other"),
            PlanReviewItemKind.Recalculated => "PlanPublishRecalculated" + group.Identity,
            PlanReviewItemKind.Conflict => "PlanPublishConflict" + group.Identity,
            _ => "PlanPublishGroup" + group.Identity });
        switch (group.Kind)
        {
            case PlanReviewItemKind.Section: RenderSection(group); break;
            case PlanReviewItemKind.Cause: RenderCause(group); break;
            case PlanReviewItemKind.Conflict: RenderConflict(group); break;
            default: RenderTask(group); break;
        }
        RefreshActions();
    }
    private void RenderSection(PlanPublishReviewGroup group)
    {
        Margin = new(0, 12, 0, 0);
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var heading = Text(group.Caption);
        heading.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        heading.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        Children.Add(heading);
        foreach (var action in group.Actions)
        {
            var button = Action(group, action);
            Children.Add(button); SetColumn(button, 1);
        }
    }
    private void RenderCause(PlanPublishReviewGroup group)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = group.Open ? "" : "", FontSize = 12 });
        content.Children.Add(new TextBlock { Text = group.Caption, TextWrapping = TextWrapping.Wrap });
        var button = Action(group, group.Actions[0], content);
        button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderThickness = new(0);
        AutomationProperties.SetName(button, group.Caption);
        AutomationProperties.SetHelpText(button, group.Open ? "閉じる" : "開く");
        Children.Add(button);
    }
    private void RenderConflict(PlanPublishReviewGroup group)
    {
        // Task, field, この PC value, GitHub value and the two choices on one row (#224).
        foreach (var width in new[] { new GridLength(2, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(2, GridUnitType.Star),
            new GridLength(2, GridUnitType.Star), GridLength.Auto })
            ColumnDefinitions.Add(new() { Width = width });
        var line = group.Lines[0];
        var caption = TaskCaption(group);
        if (line.Link is not null) caption.Children.Add(new HyperlinkButton { Content = line.LinkCaption, NavigateUri = line.Link });
        Children.Add(caption);
        if (line.Local is null)
        {
            // An unavailable task has no field to compare: its reason fills the value columns.
            var reason = Text(line.Text);
            Children.Add(reason); SetColumn(reason, 1); SetColumnSpan(reason, 3);
        }
        else
        {
            var cells = new[] { Text(line.Text), Text("この PC  " + line.Local), Text("GitHub  " + line.Remote) };
            for (var i = 0; i < cells.Length; i++) { Children.Add(cells[i]); SetColumn(cells[i], i + 1); }
        }
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        foreach (var action in line.Actions) choices.Children.Add(Action(group, action));
        Children.Add(choices); SetColumn(choices, 4);
    }
    private void RenderTask(PlanPublishReviewGroup group)
    {
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MaxWidth = 480 });
        ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        if (group.Kind == PlanReviewItemKind.Recalculated) Margin = new(28, 0, 0, 0);
        Children.Add(TaskCaption(group));
        var differences = new StackPanel { Spacing = 4 };
        Children.Add(differences); SetColumn(differences, 1);
        var ordinary = group.Lines.Where(l => l.Actions.Count == 0 && l.Link is null).ToArray();
        // Typed values first with the sheet's typed tint, then the values that follow from them.
        foreach (var typed in new[] { true, false })
        {
            var lines = ordinary.Where(l => l.Typed == typed).ToArray();
            if (lines.Length == 0) continue;
            var text = Text(string.Join("  /  ", lines.Select(l => l.Text)));
            if (!typed) { differences.Children.Add(text); continue; }
            differences.Children.Add(new Border { Child = text, Background = PlanSheetView.Brush("SheetChangedBrush"), Padding = new(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Left });
        }
        foreach (var line in group.Lines.Where(l => l.Actions.Count > 0 || l.Link is not null))
        {
            differences.Children.Add(Text(line.Text));
            if (line.Link is not null) differences.Children.Add(new HyperlinkButton { Content = line.LinkCaption, NavigateUri = line.Link });
            if (line.Actions.Count == 0) continue;
            var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var action in line.Actions) choices.Children.Add(Action(group, action));
            differences.Children.Add(choices);
        }
    }
}
