using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

internal sealed class AssistPlanTools(PlanSession session, DateOnly today, string manualMarkdown)
{
    private readonly object gate = new();
    private readonly Dictionary<string, AssistTaskReference> references = [];
    private readonly Dictionary<string, int> referencePriority = [];
    private AssistProposal? pending;
    private AssistProposal? turnProposal;
    public IReadOnlyList<AssistTaskReference> References { get { lock (gate) return references.Values.OrderBy(t => referencePriority[t.Identity]).ThenBy(t => t.Position).ToArray(); } }
    public AssistProposal? PendingProposal { get { lock (gate) return pending; } }
    public AssistProposal? TurnProposal { get { lock (gate) return turnProposal; } }
    public bool ProposalIsStale { get { lock (gate) return pending is { } p && session.CaptureRevision().Revision != p.Revision; } }
    public void BeginTurn() { lock (gate) { references.Clear(); referencePriority.Clear(); turnProposal = null; } }
    private void Record(IEnumerable<AssistTaskReference> tasks, int priority = 2)
    {
        foreach (var t in tasks) {
            references[t.Identity] = t;
            referencePriority[t.Identity] = Math.Min(referencePriority.GetValueOrDefault(t.Identity, priority), priority);
        }
    }
    public AssistPage<AssistTaskReference> FindTasks(AssistFindTasks filter)
    {
        lock (gate) { var result = new AssistPlanReader(session.Document, today).FindTasks(filter); Record(result.Items); return result; }
    }
    public AssistPage<AssistTaskDetails> GetTasks(IReadOnlyList<string> identities)
    {
        lock (gate) {
            var result = new AssistPlanReader(session.Document, today).GetTasks(identities);
            foreach (var t in result.Items) { Record([t.Task], 1); if (t.Requirement is { } r) Record([r]); Record(t.Predecessors); Record(t.Successors); }
            return result;
        }
    }
    public AssistPlanSummary SummarizePlan()
    {
        // The summary spans the whole plan, so listing its tasks as references would name every row.
        lock (gate) return new AssistPlanReader(session.Document, today).SummarizePlan();
    }
    public AssistPage<AssistManualSection> ReadManual(string query, int offset = 0, int limit = 5) => AssistManual.Read(manualMarkdown, query, offset, limit);
    public AssistProposalResult ProposeChanges(IReadOnlyList<AssistEdit> changes)
    {
        lock (gate) {
            var (document, revision) = session.CaptureRevision();
            var result = AssistProposal.Create(document, revision, today, changes);
            if (result.Proposal is { } proposal) {
                pending = turnProposal = proposal;
                Record(proposal.Changes.Select(c => c.Task), 0);
                Record(proposal.ScheduleEffect.Items.Select(c => c.Task));
            }
            return result;
        }
    }
    public void DiscardProposal() { lock (gate) pending = null; }
    public Task<PlanSaveResult> ApplyProposal(string id)
    {
        lock (gate) {
            if (pending is not { } proposal || proposal.Id != id) throw new InvalidOperationException("この提案は破棄されたか、別の提案に置き換えられました。");
            var save = session.ExecuteAtRevision(proposal.Command, today, proposal.Revision, AssistProposal.StaleMessage);
            pending = null; return save;
        }
    }
    internal void ValidateSelectedIdentity(string? identity)
    { if (identity is not null && !session.Document.State.Rows.Any(r => r.Identity == identity)) throw new ArgumentException("開いている計画にないタスクです。"); }
}
