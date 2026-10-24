using System.Text.Json.Nodes;
namespace GhProjectsBoards.Core.PlanEditor;
internal static class PlanVerification
{
    internal static string Field(PlanWrite write, ProjectPlanSettings settings) => write.Field ?? (write.Mutation switch
    {
        "createIssue" or "addProjectV2ItemById" => PlanField.NewTask,
        "updateIssue" => PlanField.Title,
        "addAssigneesToAssignable" or "removeAssigneesFromAssignable" => PlanField.Assignees,
        "addSubIssue" or "removeSubIssue" => PlanField.Parent,
        "addBlockedBy" or "removeBlockedBy" => PlanField.Predecessors,
        "updateProjectV2ItemPosition" => PlanField.Order,
        "reprioritizeSubIssue" => PlanField.SubIssueOrder,
        // #231: the row-state writes belong to the closed state and to pending removal.
        "closeIssue" or "reopenIssue" => PlanField.Closed,
        "archiveProjectV2Item" => PlanField.Removed,
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
        // An archived item is not returned by the default item read, so its absence is the observation
        // (#231); the spike confirmed the read never reports isArchived.
        if (write.Mutation == "archiveProjectV2Item") return remote.Baseline.Rows.All(r => r.Identity != identity);
        if (row is null) return false;
        var input = JsonNode.Parse(write.Input)!;
        string? Text(string key) => input[key]?.GetValue<string>();
        switch (write.Mutation)
        {
            // #231: the closed state and its reason are read back from the Issue.
            case "closeIssue": return row.Closed && row.Cancelled == (Text("stateReason") == "NOT_PLANNED");
            case "reopenIssue": return !row.Closed;
            case "updateIssue": return input.AsObject().ContainsKey("milestoneId")
                ? Text("milestoneId") is { } milestoneId ? remote.Baseline.RepositoryMilestones.Any(m => m.Repository == row.Repository && m.Id == milestoneId && m.Name == row.MilestoneTitle) : row.MilestoneTitle is null
                : row.Title == Text("title");
            case "addLabelsToLabelable": case "removeLabelsFromLabelable":
                return input["labelIds"]!.AsArray().All(n => {
                    var label = remote.Baseline.RepositoryLabels.SingleOrDefault(l => l.Repository == row.Repository && l.Id == n!.GetValue<string>());
                    return label is not null && row.Labels.Contains(label.Name) == (write.Mutation == "addLabelsToLabelable");
                });
            case "addAssigneesToAssignable": return input["assigneeIds"]!.AsArray().All(n => row.Assignees.Contains(n!.GetValue<string>()));
            case "removeAssigneesFromAssignable": return input["assigneeIds"]!.AsArray().All(n => !row.Assignees.Contains(n!.GetValue<string>()));
            case "addSubIssue": return row.Parent == Id(Text("issueId"));
            case "removeSubIssue": return row.Parent != Id(Text("issueId"));
            case "addBlockedBy": return row.Predecessors.Contains(Id(Text("blockingIssueId"))!);
            case "removeBlockedBy": return !row.Predecessors.Contains(Id(Text("blockingIssueId"))!);
            case "updateProjectV2ItemPosition":
                // #201: a scoped publication read observes the written items, not the Project's item order,
                // so a position write whose item the read returns is taken as applied. A lost move is
                // re-observed by the next complete read, and a failed move keeps the previous order below.
                if (remote.Scoped) return remote.Items.ContainsKey(identity);
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
                if (write.Mutation.StartsWith("clear", StringComparison.Ordinal)) return PlanValues.Get(row, role) == "null";
                var value = input["value"]!.AsObject().Single();
                if (role is PlanField.Status or PlanField.Phase || PlanSheetColumns.IsExtra(role) && value.Key == "singleSelectOptionId")
                {
                    var definition = remote.Fields.SingleOrDefault(f => f.Id.NodeId == Text("fieldId") && (role != PlanField.Status || f.Name == "Status") && f.DataType == "SINGLE_SELECT");
                    var option = definition?.Options.SingleOrDefault(o => o.Id == value.Value?.GetValue<string>());
                    return option is not null && (PlanSheetColumns.IsExtra(role) ? PlanExtraEditing.Value(row, role) as string : role == PlanField.Phase ? row.Phase : row.Status) == option.Name;
                }
                return JsonNode.DeepEquals(JsonNode.Parse(PlanValues.Get(row, role)), value.Value);
            // #258: an organization Issue Field write is verified by the value the Issue reports back.
            case "setIssueFieldValue":
                var issueRole = Field(write, settings);
                var entry = input["issueFields"]!.AsArray().Single()!.AsObject();
                if (entry["delete"]?.GetValue<bool>() == true) return PlanValues.Get(row, issueRole) == "null";
                var fieldId = entry["fieldId"]!.GetValue<string>();
                var written = entry.Single(p => p.Key != "fieldId");
                if (written.Key == "singleSelectOptionId")
                {
                    var name = remote.Fields.SingleOrDefault(f => f.IssueFieldId == fieldId)?.Options.SingleOrDefault(o => o.Id == written.Value?.GetValue<string>())?.Name;
                    return name is not null && (PlanSheetColumns.IsExtra(issueRole) ? PlanExtraEditing.Value(row, issueRole) as string : row.Phase) == name;
                }
                return JsonNode.DeepEquals(JsonNode.Parse(PlanValues.Get(row, issueRole)), written.Value);
            default: return false;
        }
    }
}
