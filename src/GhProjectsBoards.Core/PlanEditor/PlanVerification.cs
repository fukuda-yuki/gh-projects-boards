using System.Text.Json.Nodes;
namespace GhProjectsBoards.Core.PlanEditor;
internal static class PlanVerification
{
    internal static PlanField Field(PlanWrite write, ProjectPlanSettings settings) => write.Field ?? (write.Mutation switch
    {
        "createIssue" or "addProjectV2ItemById" => PlanField.NewTask,
        "updateIssue" => PlanField.Title,
        "addAssigneesToAssignable" or "removeAssigneesFromAssignable" => PlanField.Assignees,
        "addSubIssue" or "removeSubIssue" => PlanField.Parent,
        "addBlockedBy" or "removeBlockedBy" => PlanField.Predecessors,
        "updateProjectV2ItemPosition" => PlanField.Order,
        "reprioritizeSubIssue" => PlanField.SubIssueOrder,
        _ => settings.Columns.Single(c => c.FieldId == JsonNode.Parse(write.Input)!["fieldId"]!.GetValue<string>()).Role
    });
    internal static bool Verify(PlanWrite write, PlanPublishProgress progress, PlanRemoteSnapshot remote, ProjectPlanSettings settings)
    {
        var bindings = progress.Writes.Where(w => w.Stage == PlanPublishStage.Create && w.ResultId is not null).ToDictionary(w => w.Identity, w => w.ResultId!);
        string? Id(string? id) => id is null ? null : bindings.GetValueOrDefault(id) ?? id;
        var identity = Id(write.Identity)!;
        var row = remote.Baseline.Rows.SingleOrDefault(r => r.Identity == identity);
        if (write.Stage == PlanPublishStage.Create) return write.ResultId is not null;
        // An Auto-add membership still leaves this publisher's unsent addition pending.
        if (write.Stage == PlanPublishStage.Add) return write.State != PlanWriteState.Pending && remote.Items.ContainsKey(identity);
        if (row is null) return false;
        var input = JsonNode.Parse(write.Input)!;
        string? Text(string key) => input[key]?.GetValue<string>();
        switch (write.Mutation)
        {
            case "updateIssue": return row.Title == Text("title");
            case "addAssigneesToAssignable": return input["assigneeIds"]!.AsArray().All(n => row.Assignees.Contains(n!.GetValue<string>()));
            case "removeAssigneesFromAssignable": return input["assigneeIds"]!.AsArray().All(n => !row.Assignees.Contains(n!.GetValue<string>()));
            case "addSubIssue": return row.Parent == Id(Text("issueId"));
            case "removeSubIssue": return row.Parent != Id(Text("issueId"));
            case "addBlockedBy": return row.Predecessors.Contains(Id(Text("blockingIssueId"))!);
            case "removeBlockedBy": return !row.Predecessors.Contains(Id(Text("blockingIssueId"))!);
            case "updateProjectV2ItemPosition":
                var order = remote.Baseline.Rows.Select(r => r.Identity).ToArray();
                var index = Array.IndexOf(order, identity); var after = Text("afterId");
                return after is null ? index == 0 : index > 0 && order[index - 1] == Id(after[5..]);
            case "reprioritizeSubIssue":
                if (!remote.SubIssueOrders.TryGetValue(Id(Text("issueId"))!, out var siblings)) return false;
                var position = siblings.IndexOf(identity);
                if (Text("afterId") is { } prior) return position > 0 && siblings[position - 1] == Id(prior);
                if (Text("beforeId") is { } next) return position >= 0 && position + 1 < siblings.Length && siblings[position + 1] == Id(next);
                return false;
            case "clearProjectV2ItemFieldValue": case "updateProjectV2ItemFieldValue":
                var role = Field(write, settings);
                if (write.Mutation.StartsWith("clear", StringComparison.Ordinal)) return PlanValues.Get(row, role) == (role == PlanField.Fixed ? "false" : "null");
                var value = input["value"]!.AsObject().Single();
                if (role == PlanField.Fixed) return row.Fixed;
                if (role == PlanField.Status)
                {
                    var definition = remote.Fields.SingleOrDefault(f => f.Id.NodeId == Text("fieldId") && f.Name == "Status" && f.DataType == "SINGLE_SELECT");
                    var option = definition?.Options.SingleOrDefault(o => o.Id == value.Value?.GetValue<string>());
                    return option is not null && row.Status == option.Name;
                }
                return JsonNode.DeepEquals(JsonNode.Parse(PlanValues.Get(row, role)), value.Value);
            default: return false;
        }
    }
}
