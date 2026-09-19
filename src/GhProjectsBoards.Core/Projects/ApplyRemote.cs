using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class ApplyRemote(GhConnectionService service, ConnectionContext context)
{
    public async Task<(FieldObservation? Observation, ApiResult Result)> ObserveAsync(ApplyBatch batch, ApplyOperation operation, CancellationToken token)
    {
        using var measured = PerformanceTrace.Span("operation-observation");
        if (batch.Project.Scope != ConnectionScope.From(context)) return (null, new(ApiOutcome.Failed, FailureKind.IdentityChanged));
        return await new ProjectReader(service).ObserveFieldAsync(context, batch, operation, token);
    }
    public async Task<ApiResult> MutateAsync(ApplyBatch batch, ApplyOperation o, CancellationToken token)
    {
        ApiRequest request;
        string field;
        if (o.Key.Kind == "Title")
        {
            field = "updateIssue";
            request = ApiRequest.GraphQl("mutation ApplyTitle($input:UpdateIssueInput!){updateIssue(input:$input){issue{id title}}}",
                new { input = new { id = o.IssueId, title = o.Intended.Value } });
        }
        else if (o.Key.Kind == "Dependency")
        {
            field = o.Intended.Clear ? "removeBlockedBy" : "addBlockedBy";
            var type = o.Intended.Clear ? "RemoveBlockedByInput" : "AddBlockedByInput";
            request = ApiRequest.GraphQl($"mutation ApplyDependency($input:{type}!){{{field}(input:$input){{issue{{id}}}}}}",
                new { input = new { issueId = o.IssueId, blockingIssueId = o.Key.FieldId } });
        }
        else if (o.Intended.Clear)
        {
            field = "clearProjectV2ItemFieldValue";
            request = ApiRequest.GraphQl("mutation ApplyClear($input:ClearProjectV2ItemFieldValueInput!){clearProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
                new { input = new { projectId = batch.Project.NodeId, itemId = o.ItemId, fieldId = o.Key.FieldId } });
        }
        else
        {
            field = "updateProjectV2ItemFieldValue";
            if (!PlanningScalars.Publishable(o.Key.Kind, o.Intended)) return new(ApiOutcome.Failed, FailureKind.InvalidResponse);
            object value = o.Key.Kind switch {
                "Number" => new { number = PlanningContract.ParseHours(o.Intended.Value!) },
                "Date" => new { date = PlanningScalars.Normalize("Date", o.Intended.Value!) },
                _ => (object)new { singleSelectOptionId = o.Intended.Value }
            };
            request = ApiRequest.GraphQl("mutation ApplySelect($input:UpdateProjectV2ItemFieldValueInput!){updateProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
                new { input = new { projectId = batch.Project.NodeId, itemId = o.ItemId, fieldId = o.Key.FieldId, value } });
        }
        var result = await service.SendAsync(context, request, token, o.Key.Kind is "Title" or "Dependency" ? "repo" : "project");
        if (!result.IsSuccess) return result;
        try
        {
            var returned = result.Data!.Value.GetProperty("data").GetProperty(field).GetProperty(o.Key.Kind is "Title" or "Dependency" ? "issue" : "projectV2Item");
            if (returned.GetProperty("id").GetString() != o.Key.NodeId || o.Key.Kind == "Title" && returned.GetProperty("title").GetString() != o.Intended.Value)
                return new(ApiOutcome.Unknown, FailureKind.InvalidResponse);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException)
        { return new(ApiOutcome.Unknown, FailureKind.InvalidResponse); }
    }
}
