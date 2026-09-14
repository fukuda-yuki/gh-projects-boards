using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class ApplyRemote(GhConnectionService service, ConnectionContext context)
{
    public async Task<(FieldObservation? Observation, ApiResult Result)> ObserveAsync(ApplyBatch batch, ApplyOperation operation, CancellationToken token)
    {
        using var measured = PerformanceTrace.Span("operation-observation");
        if (batch.Project.Scope != ConnectionScope.From(context)) return (null, new(ApiOutcome.Failed, FailureKind.IdentityChanged));
        var check = await service.RecheckAsync(context, token);
        if (!check.IsConnected) return (null, check.Result);
        if (check.Authentication?.Store != CredentialStore.Keyring) return (null, new(ApiOutcome.Failed, FailureKind.UnknownCredentialStore));
        if (check.Authentication.HasScope(operation.Key.Kind == "Title" ? "repo" : "project") != true)
            return (null, new(ApiOutcome.Failed, FailureKind.PermissionDenied));
        var read = await new ProjectReader(service).ReadAsync(context, batch.Project, token);
        if (read.Outcome != ProjectReadOutcome.Complete || read.Project is not { } p)
            return (null, new(ApiOutcome.Failed, read.Problems.FirstOrDefault()?.Failure ?? FailureKind.InvalidResponse, retryAfter: read.Problems.FirstOrDefault()?.RetryAfter));
        var item = p.Items.SingleOrDefault(i => i.Id.NodeId == operation.ItemId);
        if (item is null || item.Kind != ProjectItemKind.Issue || item.ContentId?.NodeId != operation.IssueId || item.IsArchived)
            return (null, new(ApiOutcome.Failed, FailureKind.NotFoundOrInaccessible));
        var registration = new ProjectRegistration(context.Login, "", [], null, DateTimeOffset.UtcNow, p);
        var cell = new EditingWorkspace(batch.Project.Scope).Open(registration).Single(r => r.ItemId == operation.ItemId)
            .Cells.SingleOrDefault(c => c.Key == operation.Key);
        if (cell is null || !cell.Editable || cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)
            || operation.Key.Kind == "Select" && !operation.Intended.Clear && !cell.Options.Any(o => o.Id == operation.Intended.Value))
            return (null, new(ApiOutcome.Failed, FailureKind.PermissionDenied));
        return (new(Guid.NewGuid().ToString("N"), batch.Project, DateTimeOffset.UtcNow, cell.Baseline, cell.Availability, null, cell.Options), new(ApiOutcome.Success));
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
        else if (o.Intended.Clear)
        {
            field = "clearProjectV2ItemFieldValue";
            request = ApiRequest.GraphQl("mutation ApplyClear($input:ClearProjectV2ItemFieldValueInput!){clearProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
                new { input = new { projectId = batch.Project.NodeId, itemId = o.ItemId, fieldId = o.Key.FieldId } });
        }
        else
        {
            field = "updateProjectV2ItemFieldValue";
            request = ApiRequest.GraphQl("mutation ApplySelect($input:UpdateProjectV2ItemFieldValueInput!){updateProjectV2ItemFieldValue(input:$input){projectV2Item{id}}}",
                new { input = new { projectId = batch.Project.NodeId, itemId = o.ItemId, fieldId = o.Key.FieldId, value = new { singleSelectOptionId = o.Intended.Value } } });
        }
        var result = await service.SendAsync(context, request, token, o.Key.Kind == "Title" ? "repo" : "project");
        if (!result.IsSuccess) return result;
        try
        {
            var returned = result.Data!.Value.GetProperty("data").GetProperty(field).GetProperty(o.Key.Kind == "Title" ? "issue" : "projectV2Item");
            if (returned.GetProperty("id").GetString() != o.Key.NodeId || o.Key.Kind == "Title" && returned.GetProperty("title").GetString() != o.Intended.Value)
                return new(ApiOutcome.Unknown, FailureKind.InvalidResponse);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or JsonException)
        { return new(ApiOutcome.Unknown, FailureKind.InvalidResponse); }
    }
}
