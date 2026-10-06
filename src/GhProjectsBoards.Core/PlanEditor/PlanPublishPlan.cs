using System.Globalization;
using System.Collections.Immutable;
namespace GhProjectsBoards.Core.PlanEditor;
internal enum PlanPublishStage { Create, Add, Fields, Hierarchy, Dependencies, Order }
internal enum PlanWriteState { Pending, Dispatched, Succeeded, Failed }
internal sealed record PlanReviewChange(string Identity, PlanField Field, string? Before, string? After);
internal sealed record PlanWrite(string Key, string Identity, PlanPublishStage Stage, string Mutation, string InputType, string Input, string Selection)
{
    public PlanField? Field { get; init; }
    public PlanWriteState State { get; init; }
    public string? Error { get; init; }
    public string? ResultId { get; init; }
}
internal sealed record PlanPublishProgress(string RunId, ImmutableArray<PlanWrite> Writes)
{ public bool DispatchStarted { get; init; } }
internal sealed record PlanPublishReview(ImmutableArray<PlanReviewChange> Changes, ImmutableArray<PlanWrite> Writes);
internal static class PlanPublishPlan
{
    internal static bool IsIdentifierField(string key) => key is "id" or "repositoryId" or "projectId" or "itemId" or "contentId" or "issueId" or "subIssueId" or "blockingIssueId" or "assignableId" or "assigneeIds" or "afterId" or "beforeId";
    internal static void Validate(PlanPublishProgress progress, PlanDocument document)
    {
        PlanOperations.Require(Guid.TryParseExact(progress.RunId, "N", out _) && !progress.Writes.IsDefault &&
            progress.Writes.Select(w => w.Key).Distinct().Count() == progress.Writes.Length, "発行記録が不正です。");
        foreach (var write in progress.Writes)
        {
            var contract = write.Mutation switch
            {
                "createIssue" => (PlanPublishStage.Create, "issue { id }"),
                "addProjectV2ItemById" => (PlanPublishStage.Add, "item { id }"),
                "updateIssue" => (PlanPublishStage.Fields, "issue { id }"),
                "updateProjectV2ItemFieldValue" or "clearProjectV2ItemFieldValue" => (PlanPublishStage.Fields, "projectV2Item { id }"),
                "addAssigneesToAssignable" or "removeAssigneesFromAssignable" => (PlanPublishStage.Fields, "assignable { __typename }"),
                "addSubIssue" or "removeSubIssue" or "reprioritizeSubIssue" => (PlanPublishStage.Hierarchy, "issue { id }"),
                "addBlockedBy" or "removeBlockedBy" => (PlanPublishStage.Dependencies, "issue { id }"),
                "updateProjectV2ItemPosition" => (PlanPublishStage.Order, "items { totalCount }"),
                _ => throw new ArgumentException("未対応の発行操作です。")
            };
            PlanOperations.Require(Enum.IsDefined(write.State) && !(write.Stage == PlanPublishStage.Create && write.ResultId is not null && write.State != PlanWriteState.Succeeded) && write.Stage == contract.Item1 && write.Selection == contract.Item2 &&
                write.InputType == char.ToUpperInvariant(write.Mutation[0]) + write.Mutation[1..] + "Input" &&
                document.State.Rows.Any(r => r.Identity == write.Identity), "発行操作の対象または形式が不正です。");
            PlanOperations.Require(write.Field is null || write.Field == PlanField.Status && write.Mutation is "updateProjectV2ItemFieldValue" or "clearProjectV2ItemFieldValue", "発行列の指定が不正です。");
            using var input = System.Text.Json.JsonDocument.Parse(write.Input);
            PlanOperations.Require(input.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object, "発行入力が不正です。");
            var target = write.Mutation switch
            {
                "updateIssue" => "id", "addProjectV2ItemById" => "contentId",
                "addAssigneesToAssignable" or "removeAssigneesFromAssignable" => "assignableId",
                "addBlockedBy" or "removeBlockedBy" => "issueId",
                "addSubIssue" or "removeSubIssue" or "reprioritizeSubIssue" => "subIssueId",
                "updateProjectV2ItemFieldValue" or "clearProjectV2ItemFieldValue" or "updateProjectV2ItemPosition" => "itemId", _ => null
            };
            if (target is not null) PlanOperations.Require(input.RootElement.GetProperty(target).GetString() == (target == "itemId" ? "item:" : "") + write.Identity, "発行対象が行と一致しません。");
            if (input.RootElement.TryGetProperty("projectId", out var project))
                PlanOperations.Require(project.GetString() == document.Project.NodeId, "発行先のProjectが一致しません。");
        }
    }
    internal static PlanPublishReview Build(PlanDocument document, PlanRemoteSnapshot remote, DateOnly today, string runId)
    {
        if (!document.Sync.Conflicts.IsEmpty || !document.Sync.Unavailable.IsEmpty) throw new InvalidOperationException("競合を解決してください。");
        var changes = ImmutableArray.CreateBuilder<PlanReviewChange>(); var writes = ImmutableArray.CreateBuilder<PlanWrite>();
        var baseline = document.Baseline.Rows.ToDictionary(r => r.Identity);
        void Add(string id, PlanPublishStage stage, string mutation, string type, object input, string selection)
            => writes.Add(new(writes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), id, stage, mutation, type, PlanJson.Text(input), selection));
        var rows = document.State.Rows;
        foreach (var scheduled in PlanOperations.Schedule(document, today))
        {
            var row = rows[scheduled.Input.RowId - 1] with { Start = scheduled.Start.Value, End = scheduled.End.Value };
            var isNew = !baseline.TryGetValue(row.Identity, out var before);
            before ??= new(row.Identity, "", row.Repository);
            if (isNew)
            {
                if (string.IsNullOrWhiteSpace(row.Title)) throw new InvalidOperationException("タイトルを入力してください。");
                if (!PlanOperations.Repository(row.Repository)) throw new InvalidOperationException("リポジトリを設定してください。");
                changes.Add(new(row.Identity, PlanField.NewTask, null, row.Title));
                Add(row.Identity, PlanPublishStage.Create, "createIssue", "CreateIssueInput", new
                { repositoryId = "repository:" + row.Repository, title = row.Title,
                    body = "<!-- ghpb-plan:" + runId + ":" + row.Identity + " -->" }, "issue { id }");
                Add(row.Identity, PlanPublishStage.Add, "addProjectV2ItemById", "AddProjectV2ItemByIdInput",
                    new { projectId = document.Project.NodeId, contentId = row.Identity }, "item { id }");
            }
            foreach (var field in PlanValues.RowFields)
            {
                if (PlanOperations.IsLocalConstraint(field, document.State.Settings) || PlanOperations.IsSummaryEffort(scheduled.IsSummary, field)) continue;
                var bv = PlanValues.Get(before, field); var av = PlanValues.Get(row, field);
                if (bv == av) continue;
                if (isNew && field is PlanField.Repository or PlanField.Title) continue;
                changes.Add(new(row.Identity, field, isNew ? null : bv, av));
                switch (field)
                {
                    case PlanField.Repository: case PlanField.Closed:
                        throw new InvalidOperationException("この変更は発行できません: " + field);
                    case PlanField.Status:
                        var statusField = remote.Fields.SingleOrDefault(f => f.Name == "Status" && f.DataType == "SINGLE_SELECT" && f.ValueOwner == Projects.FieldOwner.ProjectItem && f.Availability == Projects.ValueAvailability.Present)
                            ?? throw new InvalidOperationException("Status の列を確認してください。");
                        var statusInput = new Dictionary<string, object?> { ["projectId"] = document.Project.NodeId, ["itemId"] = "item:" + row.Identity, ["fieldId"] = statusField.Id.NodeId };
                        if (row.Status is not null)
                        {
                            var option = statusField.Options.SingleOrDefault(o => o.Name == row.Status)
                                ?? throw new InvalidOperationException("Status の選択肢を確認してください。");
                            statusInput["value"] = new { singleSelectOptionId = option.Id };
                        }
                        Add(row.Identity, PlanPublishStage.Fields, row.Status is null ? "clearProjectV2ItemFieldValue" : "updateProjectV2ItemFieldValue",
                            row.Status is null ? "ClearProjectV2ItemFieldValueInput" : "UpdateProjectV2ItemFieldValueInput", statusInput, "projectV2Item { id }");
                        writes[^1] = writes[^1] with { Field = PlanField.Status };
                        break;
                    case PlanField.Title:
                        if (string.IsNullOrWhiteSpace(row.Title)) throw new InvalidOperationException("タイトルを入力してください。");
                        Add(row.Identity, PlanPublishStage.Fields, "updateIssue", "UpdateIssueInput", new { id = row.Identity, title = row.Title }, "issue { id }"); break;
                    case PlanField.Assignees:
                        foreach (var (mutation, ids) in new[] { ("removeAssigneesFromAssignable", before.Assignees.Except(row.Assignees).ToArray()),
                            ("addAssigneesToAssignable", row.Assignees.Except(before.Assignees).ToArray()) })
                            if (ids.Length > 0) Add(row.Identity, PlanPublishStage.Fields, mutation, char.ToUpperInvariant(mutation[0]) + mutation[1..] + "Input",
                                new { assignableId = row.Identity, assigneeIds = ids }, "assignable { __typename }");
                        break;
                    case PlanField.Parent:
                        if (before.Parent is not null) Add(row.Identity, PlanPublishStage.Hierarchy, "removeSubIssue", "RemoveSubIssueInput",
                            new { issueId = before.Parent, subIssueId = row.Identity }, "issue { id }");
                        if (row.Parent is not null) Add(row.Identity, PlanPublishStage.Hierarchy, "addSubIssue", "AddSubIssueInput",
                            new { issueId = row.Parent, subIssueId = row.Identity }, "issue { id }");
                        break;
                    case PlanField.Predecessors:
                        foreach (var (mutation, ids) in new[] { ("removeBlockedBy", before.Predecessors.Except(row.Predecessors)), ("addBlockedBy", row.Predecessors.Except(before.Predecessors)) })
                            foreach (var id in ids) Add(row.Identity, PlanPublishStage.Dependencies, mutation, char.ToUpperInvariant(mutation[0]) + mutation[1..] + "Input",
                                new { issueId = row.Identity, blockingIssueId = id }, "issue { id }");
                        break;
                    default:
                        var mapping = document.State.Settings.Columns.SingleOrDefault(m => m.Role == field)
                            ?? throw new InvalidOperationException("列を割り当ててください: " + field);
                        var definition = remote.Fields.Single(f => f.Id.NodeId == mapping.FieldId);
                        var clear = av == "null" || field == PlanField.Fixed && !row.Fixed;
                        var input = new Dictionary<string, object?> { ["projectId"] = document.Project.NodeId, ["itemId"] = "item:" + row.Identity, ["fieldId"] = mapping.FieldId };
                        if (!clear) input["value"] = field switch
                        {
                            PlanField.Estimate => new { number = row.Estimate }, PlanField.Remaining => new { number = row.Remaining }, PlanField.Actual => new { number = row.Actual },
                            PlanField.Start => new { date = row.Start!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, PlanField.End => new { date = row.End!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                            PlanField.StartNoEarlierThan => new { date = row.StartNoEarlierThan!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                            PlanField.Fixed => (object)new { singleSelectOptionId = definition.Options.Single(o => o.Name == "固定").Id },
                            _ => throw new InvalidOperationException("未対応の列です。")
                        };
                        Add(row.Identity, PlanPublishStage.Fields, clear ? "clearProjectV2ItemFieldValue" : "updateProjectV2ItemFieldValue",
                            clear ? "ClearProjectV2ItemFieldValueInput" : "UpdateProjectV2ItemFieldValueInput", input, "projectV2Item { id }");
                        break;
                }
            }
        }
        var oldOrder = document.Baseline.Rows.Select(r => r.Identity).ToArray();
        var currentOrder = oldOrder.Concat(rows.Where(r => !baseline.ContainsKey(r.Identity)).Select(r => r.Identity)).ToList();
        foreach (var parent in rows.Where(r => r.Parent is not null).Select(r => r.Parent!).Distinct())
        {
            if (!rows.Any(r => r.Identity == parent)) continue;
            var children = rows.Where(r => r.Parent == parent).Select(r => r.Identity).ToArray();
            var previous = remote.SubIssueOrders.GetValueOrDefault(parent, []).Where(id => rows.Any(r => r.Identity == id)).ToImmutableArray();
            if (previous.SequenceEqual(children)) continue;
            changes.Add(new(parent, PlanField.SubIssueOrder, PlanJson.Text(previous), PlanJson.Text(children)));
            for (var i = 0; i < children.Length; i++)
            {
                var input = new Dictionary<string, object?> { ["issueId"] = parent, ["subIssueId"] = children[i] };
                if (i > 0) input["afterId"] = children[i - 1];
                else if (children.Length > 1) input["beforeId"] = children[1];
                if (input.Count > 2) Add(children[i], PlanPublishStage.Hierarchy, "reprioritizeSubIssue", "ReprioritizeSubIssueInput", input, "issue { id }");
            }
        }
        for (var i = 0; i < rows.Length; i++)
        {
            var id = rows[i].Identity;
            if (currentOrder[i] == id && baseline.ContainsKey(id)) continue;
            changes.Add(new(id, PlanField.Order, baseline.ContainsKey(id) ? currentOrder.IndexOf(id).ToString(CultureInfo.InvariantCulture) : null, i.ToString(CultureInfo.InvariantCulture)));
            currentOrder.Remove(id); currentOrder.Insert(i, id);
            Add(id, PlanPublishStage.Order, "updateProjectV2ItemPosition", "UpdateProjectV2ItemPositionInput",
                new { projectId = document.Project.NodeId, itemId = "item:" + id, afterId = i == 0 ? null : "item:" + rows[i - 1].Identity }, "items { totalCount }");
        }
        return new(changes.ToImmutable(), writes.OrderBy(w => w.Stage).ThenBy(RelationshipPhase).ToImmutableArray());
    }
    private static int RelationshipPhase(PlanWrite write) => write.Mutation switch { "addSubIssue" or "addBlockedBy" => 1, "reprioritizeSubIssue" => 2, _ => 0 };
    internal static IEnumerable<ImmutableArray<PlanWrite>> Batches(IEnumerable<PlanWrite> writes)
    {
        foreach (var group in writes.GroupBy(w => (w.Stage, Phase: RelationshipPhase(w))).OrderBy(g => g.Key.Stage).ThenBy(g => g.Key.Phase))
        {
            var numericDatesOnly = group.Key.Stage == PlanPublishStage.Fields && group.All(w =>
                w.Mutation == "updateProjectV2ItemFieldValue" && System.Text.Json.Nodes.JsonNode.Parse(w.Input)?["value"] is System.Text.Json.Nodes.JsonObject value &&
                value.Any(p => p.Key is "number" or "date"));
            var size = group.Key.Stage == PlanPublishStage.Order ? 1 : numericDatesOnly ? 50 : 10;
            foreach (var batch in group.Chunk(size)) yield return batch.ToImmutableArray();
        }
    }
}
