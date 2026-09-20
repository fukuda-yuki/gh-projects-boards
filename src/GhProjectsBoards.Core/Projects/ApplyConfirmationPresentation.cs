namespace GhProjectsBoards.Core.Projects;

internal enum ConfirmationValueKind { Value, Unset, NotLoaded, Unavailable, Unsupported, Unchanged, Clear, NotCreated, Unspecified }
internal sealed record ConfirmationValue(ConfirmationValueKind Kind, string Text, string? Id = null);
internal sealed record ConfirmationColumn(ColumnIdentity Id, string Name, bool Hidden);
internal sealed record ConfirmationCell(ConfirmationColumn Column, ConfirmationValue Before, ConfirmationValue After,
    DraftField? Draft, string? Pending, string? Problem, bool Latest, DateTimeOffset RetrievedAt)
{
    public string Difference => Before.Text + " → " + After.Text;
    public bool CanResolve => Latest && Draft is { Conflict: true, Buffer: null, Observation: { Reason: null,
        Availability: ValueAvailability.Present or ValueAvailability.Empty } };
}
internal sealed record ConfirmationIssue(ApplyCandidate Candidate, string Repository, string Number, string Title,
    string IdentityDetails, ConfirmationCell[] Cells, string[] RowProblems, bool Hidden, string? RepositoryBuffer)
{
    public string Id => Candidate.Id;
    public bool IsCreation => Candidate.IsCreation;
    public string Identity => Repository + " " + Number + " / " + Title;
    public string[] Problems => RowProblems.Concat(Cells.Where(c => c.Problem is not null).Select(c => c.Column.Name + ": " + c.Problem)).ToArray();
}
internal sealed record ApplyConfirmationPresentation(ConfirmationColumn[] Columns, ConfirmationIssue[] Rows,
    int HiddenCount, int HiddenSelected, int SelectedCount, int? EffectiveCount, string Summary, string HiddenSummary)
{
    public static ApplyConfirmationPresentation Create(EditingWorkspace workspace, ProjectRegistration project,
        IReadOnlyList<ApplyCandidate> candidates, IReadOnlyList<string> visible, bool includeHidden,
        IReadOnlySet<string> selected, ApplyReview? review, bool latest, ConfirmationColumn[]? retainedColumns = null)
    {
        var p = project.Snapshot;
        var definitions = p.Fields.ToDictionary(f => f.Id.NodeId);
        var columnLayout = workspace.Columns(project);
        var displayed = candidates.Where(c => includeHidden || visible.Contains(c.Id)).ToArray();
        var ids = displayed.SelectMany(c => c.IsCreation
            ? new[] { ColumnIdentity.Title }.Concat(workspace.LocalRows.Single(r => r.Id == c.Id).Selects
                .Where(s => s.Intent != "Unspecified").Select(s => new ColumnIdentity("Field", s.FieldId)))
                .Concat(workspace.CreationPlanningFor(project, c.Id).Select(s => new ColumnIdentity(s.Kind == "Dependency" ? "Dependency" : "Field", s.FieldId)))
                .Concat(c.Fields.Where(f => f.Buffer is not null).Select(f => new ColumnIdentity("Field", f.Key.FieldId)))
            : c.Fields.Where(f => f.Change is not null || f.Buffer is not null || f.Conflict || f.Observation?.Reason == EditingWorkspace.ProjectionDecisionReason)
                .Select(f => f.Key.Kind == "Title" ? ColumnIdentity.Title : new ColumnIdentity(f.Key.Kind == "Dependency" ? "Dependency" : "Field", f.Key.FieldId)))
            .Concat((retainedColumns ?? []).Select(c => c.Id)).Distinct().ToArray();
        string Name(ColumnIdentity id)
        {
            if (id == ColumnIdentity.Title) return "タイトル（Issue共通）";
            if (id.Role == "Dependency")
            {
                var predecessor = p.Issues.GetValueOrDefault(new(p.Id.Scope, id.FieldId!));
                return "先行: " + (predecessor is not null ? $"{predecessor.Repository.NameWithOwner} #{predecessor.Number}"
                    : workspace.LocalRows.SingleOrDefault(r => r.Id == id.FieldId)?.Title ?? id.FieldId);
            }
            var name = definitions.GetValueOrDefault(id.FieldId!)?.Name
                ?? workspace.LocalRows.SelectMany(r => r.Selects).FirstOrDefault(s => s.FieldId == id.FieldId)?.FieldName
                ?? "未確認フィールド";
            var duplicate = ids.Count(other => other.Role == "Field" && definitions.GetValueOrDefault(other.FieldId!)?.Name == name) > 1;
            return name + (duplicate || !definitions.ContainsKey(id.FieldId!) ? " [" + id.FieldId + "]" : "");
        }
        var fieldOrder = p.Fields.Select((f, index) => (f.Id.NodeId, index)).ToDictionary(v => v.NodeId, v => v.index);
        var columns = ids.OrderBy(id => id == ColumnIdentity.Title ? -1 : fieldOrder.GetValueOrDefault(id.FieldId!, int.MaxValue))
            .Select(id => new ConfirmationColumn(id, Name(id), columnLayout.Hidden(id.FieldId))).ToArray();

        ConfirmationIssue Row(ApplyCandidate candidate)
        {
            var local = workspace.LocalRows.SingleOrDefault(r => r.Id == candidate.Id);
            var item = p.Items.SingleOrDefault(i => i.Id.NodeId == candidate.Id);
            var issue = item?.ContentId is { } issueId ? p.Issues.GetValueOrDefault(issueId) : null;
            var repository = local?.Repository ?? issue?.Repository.NameWithOwner ?? "Repository未確認";
            if (string.IsNullOrWhiteSpace(repository)) repository = "宛先未指定";
            var number = local is not null ? "新規作成" : issue is not null ? "#" + issue.Number : "取得結果で確認できない変更";
            var title = local?.Title ?? issue?.Title.Value ?? "タイトル未取得";
            var rowProblems = review?.Problems.Where(b => b.RowId == candidate.Id).ToArray() ?? [];
            var problems = rowProblems.Where(b => b.Field is null).Select(b => b.Message)
                .Concat(local is null ? [] : workspace.LocalProblems(project, local.Id))
                .Concat(candidate.Missing ? new[] { "取得結果で確認できません。変更を保持しています。" } : []).Distinct().ToList();
            var cells = columns.Select(column =>
            {
                var id = column.Id;
                var field = candidate.Fields.SingleOrDefault(f => id == ColumnIdentity.Title ? f.Key.Kind == "Title" : f.Key.FieldId == id.FieldId);
                var cell = candidate.Row.Cells.SingleOrDefault(c => c.Key is not null && (id == ColumnIdentity.Title
                    ? c.Key.Kind is "Title" or "LocalTitle" : c.Key.FieldId == id.FieldId));
                var definition = id.FieldId is null ? null : definitions.GetValueOrDefault(id.FieldId);
                ConfirmationValue Value(string? value, ValueAvailability availability) => id.Role == "Dependency" && value == "present"
                    ? new(ConfirmationValueKind.Value, "あり") : Format(value, availability, definition);
                ConfirmationValue before, after;
                string? pending = field?.Buffer, problem = null;
                if (local is not null)
                {
                    before = new(ConfirmationValueKind.NotCreated, "未作成");
                    var select = local.Selects.SingleOrDefault(s => s.FieldId == id.FieldId);
                    var intent = workspace.CreationPlanningFor(project, local.Id).SingleOrDefault(s => s.FieldId == id.FieldId && (s.Kind == "Dependency") == (id.Role == "Dependency"));
                    after = id == ColumnIdentity.Title ? new(ConfirmationValueKind.Value, local.Title)
                        : intent is not null ? intent.Value.Clear ? new(ConfirmationValueKind.Clear, "クリア") : Value(intent.Value.Value, ValueAvailability.Present)
                        : select?.ExplicitClear == true ? new(ConfirmationValueKind.Clear, "クリア")
                        : select?.OptionId is { } option ? Value(option, ValueAvailability.Present)
                        : new(ConfirmationValueKind.Unspecified, "指定なし");
                    pending = id == ColumnIdentity.Title ? local.TitleBuffer : field?.Buffer;
                }
                else
                {
                    var observation = field?.Observation;
                    before = Value(observation?.Value ?? (observation is null ? field?.Baseline ?? cell?.Baseline : null),
                        observation?.Availability ?? cell?.Availability ?? ValueAvailability.Unavailable);
                    after = field?.Change is not { } change ? new(ConfirmationValueKind.Unchanged, "変更なし")
                        : change.Clear ? new(ConfirmationValueKind.Clear, "クリア")
                        : Value(change.Value, change.Value is null ? ValueAvailability.Empty : ValueAvailability.Present);
                    problem = field?.Conflict == true ? "競合: GitHubと端末内の両方で変更されています。"
                        : rowProblems.FirstOrDefault(b => b.Field == field?.Key && b.Field is not null)?.Message
                            ?? observation?.Reason;
                }
                return new ConfirmationCell(column, before, after, field, pending, problem, latest,
                    field?.Observation?.At ?? field?.RetrievedAt ?? project.RetrievedAt);
            }).ToArray();
            var details = local is not null ? "ローカル行 " + local.Id : $"{issue?.Url}\nIssue ID {issue?.Id.NodeId ?? "未確認"}\n項目 ID {candidate.Id}";
            return new(candidate, repository, number, title, details, cells, problems.Distinct().ToArray(), !visible.Contains(candidate.Id), local?.RepositoryBuffer);
        }
        var order = visible.Select((id, index) => (id, index)).ToDictionary(v => v.id, v => v.index);
        var rows = displayed.OrderBy(c => order.GetValueOrDefault(c.Id, int.MaxValue)).Select(Row).ToArray();
        var selectedCount = rows.Count(r => selected.Contains(r.Id));
        var hidden = candidates.Count(c => !visible.Contains(c.Id));
        var hiddenSelected = rows.Count(r => r.Hidden && selected.Contains(r.Id));
        int? effective = latest ? review?.IssueCount : null;
        var summary = $"{rows.Length}件中{selectedCount}件を選択";
        if (effective is { } count && count != selectedCount)
        {
            var targets = review!.Batch.Operations.Select(o => o.ItemId).Concat((review.Batch.Creations ?? []).Select(c => c.LocalId)).ToHashSet();
            var excluded = rows.Where(r => selected.Contains(r.Id) && !targets.Contains(r.Id)).ToArray();
            var problems = excluded.Count(r => r.Problems.Length > 0);
            var unchanged = excluded.Length - problems;
            var reasons = new List<string>();
            if (problems > 0) reasons.Add($"要対応{problems}件");
            if (unchanged > 0) reasons.Add($"変更なし{unchanged}件");
            if (reasons.Count == 0) reasons.Add("同一Issueの変更を含む");
            summary += $" · 反映対象{count}件（{string.Join("・", reasons)}）";
        }
        if (latest && review is { CreatedIssues: > 0 }) summary += review.UpdatedIssues == 0 ? $" · 新規作成{review.CreatedIssues}件"
            : $" · 更新{review.UpdatedIssues}件・新規作成{review.CreatedIssues}件";
        var hiddenSummary = hidden == 0 ? "" : includeHidden ? $"非表示の変更{hidden}件を候補に追加済み（{hiddenSelected}件を選択）"
            : $"非表示の変更{hidden}件は候補から除外中";
        return new(columns, rows, hidden, hiddenSelected, selectedCount, effective, summary, hiddenSummary);
    }

    internal static ConfirmationValue Format(string? value, ValueAvailability availability, ProjectFieldDefinition? field)
    {
        var option = field?.Options.SingleOrDefault(o => o.Id == value);
        var label = option?.Name ?? value ?? "空文字";
        if (option is not null && field!.Options.Count(o => o.Name == option.Name) > 1) label += " [" + option.Id + "]";
        return availability switch {
        ValueAvailability.Empty => new(ConfirmationValueKind.Unset, "未設定"),
        ValueAvailability.NotLoaded => new(ConfirmationValueKind.NotLoaded, "未取得"),
        ValueAvailability.Unavailable => new(ConfirmationValueKind.Unavailable, "取得不可"),
        ValueAvailability.Unsupported => new(ConfirmationValueKind.Unsupported, "非対応"),
        _ => new(ConfirmationValueKind.Value, label, field is null ? null : value)
        };
    }
}
