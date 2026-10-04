namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    internal bool IsWeeklyApplyField(ProjectRegistration project, FieldKey key) =>
        key.Kind == "Number" && key.ProjectId == project.Snapshot.Id.NodeId
        && Planning(project.Snapshot.Id.NodeId)?.Fields.Any(binding =>
            binding.Role is "Actual" or "Remaining" && binding.FieldId == key.FieldId) == true;

    internal ApplyCandidate[] WeeklyApplyCandidates(ProjectRegistration project) => ReadApplyCandidates(project)
        .Where(candidate => !candidate.IsCreation)
        .Select(candidate => candidate with { Fields = candidate.Fields.Where(field => IsWeeklyApplyField(project, field.Key)).ToArray() })
        .Where(candidate => candidate.Fields.Any(field => field.Change is not null || field.Buffer is not null || field.Conflict
            || field.Observation?.Reason == ProjectionDecisionReason))
        .ToArray();
}
