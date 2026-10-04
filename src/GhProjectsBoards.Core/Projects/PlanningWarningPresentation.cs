namespace GhProjectsBoards.Core.Projects;

internal enum PlanningWarningKind { DirectSchedule, DirectEffortBreakdown, Inherited, DirectContributionInconsistency, DirectActualReport }
internal sealed record PlanningWarningDetail(string Message, PlanningWarningKind Kind, string? PredecessorId = null);
internal enum PlanningWarningImpact { Schedule, EffortBreakdown, Unknown, ContributionInconsistency, ActualReport }
internal sealed record PlanningWarningCause(string SourceTaskId, string Message, PlanningWarningImpact Impact, bool Inherited);
internal sealed record PlanningWarningPresentation(long SourceRevision, PlanningWarningCause[] Causes)
{
    public bool HasScheduleAttention => Causes.Any(cause => cause.Impact is PlanningWarningImpact.Schedule or PlanningWarningImpact.Unknown);
    public bool HasDataAttention => Causes.Any(cause => cause.Impact is PlanningWarningImpact.ContributionInconsistency or PlanningWarningImpact.ActualReport);

    public static PlanningWarningPresentation Create(AdoptedPlan plan, string taskId)
    {
        var results = plan.Tasks.GroupBy(task => task.Id).ToDictionary(group => group.Key, group => group.ToArray());
        var complete = new Dictionary<string, PlanningWarningCause[]>();
        var active = new HashSet<string>();
        PlanningWarningCause Cause(string source, string message, PlanningWarningImpact impact)
            => new(source, message, impact, source != taskId);
        PlanningWarningCause[] Resolve(string id, string unavailable)
        {
            if (!results.TryGetValue(id, out var found) || found.Length != 1
                || found[0].SourceRevision != plan.SourceRevision || active.Contains(id))
                return [Cause(id, unavailable, PlanningWarningImpact.Unknown)];
            if (complete.TryGetValue(id, out var prior)) return prior;
            var result = found[0];
            var causes = new List<PlanningWarningCause>();
            active.Add(id);
            if (result.Problem is { } problem) causes.Add(Cause(id, problem, PlanningWarningImpact.Schedule));
            foreach (var warning in result.Warnings)
            {
                var details = result.WarningDetails?.Where(detail => detail.Message == warning).ToArray() ?? [];
                if (details.Length > 1 && details.All(detail => detail.Kind == PlanningWarningKind.Inherited && detail.PredecessorId is not null))
                {
                    // Human labels can coincide; provenance comes from the
                    // typed edges, never from a unique rendered message.
                    foreach (var inheritedDetail in details)
                        causes.AddRange(Resolve(inheritedDetail.PredecessorId!, warning));
                    continue;
                }
                if (details.Length != 1)
                {
                    causes.Add(Cause(id, warning, PlanningWarningImpact.Unknown));
                    continue;
                }
                var detail = details[0];
                if (detail.Kind == PlanningWarningKind.Inherited && detail.PredecessorId is { Length: > 0 } predecessor)
                {
                    var upstream = Resolve(predecessor, warning);
                    // An inherited notice with no available cause is not evidence
                    // that the predecessor is safe. Preserve its recorded caution.
                    causes.AddRange(upstream.Length > 0 ? upstream : [Cause(predecessor, warning, PlanningWarningImpact.Unknown)]);
                }
                else
                {
                    var impact = detail.PredecessorId is null ? detail.Kind switch {
                        PlanningWarningKind.DirectSchedule => PlanningWarningImpact.Schedule,
                        PlanningWarningKind.DirectEffortBreakdown => PlanningWarningImpact.EffortBreakdown,
                        PlanningWarningKind.DirectContributionInconsistency => PlanningWarningImpact.ContributionInconsistency,
                        PlanningWarningKind.DirectActualReport => PlanningWarningImpact.ActualReport,
                        _ => PlanningWarningImpact.Unknown
                    } : PlanningWarningImpact.Unknown;
                    causes.Add(Cause(id, warning, impact));
                }
            }
            active.Remove(id);
            // The cache bounds diamonds to one resolution per task; source/cause
            // identity keeps a shared upstream notice from multiplying at merges.
            return complete[id] = causes.Distinct().ToArray();
        }
        return new(plan.SourceRevision, Resolve(taskId, "採用計画の注意を確認できません。"));
    }
}
