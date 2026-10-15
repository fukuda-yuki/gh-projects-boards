using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

internal sealed class AssistPlanTools(PlanSession session, DateOnly today, string manualMarkdown)
{
    private readonly object gate = new();
    private readonly Dictionary<string, AssistTaskReference> references = [];
    private AssistProposal? pending;
    public IReadOnlyList<AssistTaskReference> References { get { lock (gate) return references.Values.OrderBy(t => t.Id).ToArray(); } }
    public AssistProposal? PendingProposal { get { lock (gate) return pending; } }
    public bool ProposalIsStale { get { lock (gate) return pending is { } p && session.CaptureRevision().Revision != p.Revision; } }
    public void BeginTurn() { lock (gate) references.Clear(); }
    private void Record(IEnumerable<AssistTaskReference> tasks) { foreach (var t in tasks) references[t.Identity] = t; }
    public AssistPage<AssistTaskReference> FindTasks(AssistFindTasks filter)
    {
        lock (gate) { var result = new AssistPlanReader(session.Document, today).FindTasks(filter); Record(result.Items); return result; }
    }
    public AssistPage<AssistTaskDetails> GetTasks(IReadOnlyList<string> identities)
    {
        lock (gate) {
            var result = new AssistPlanReader(session.Document, today).GetTasks(identities);
            foreach (var t in result.Items) { Record([t.Task]); if (t.Requirement is { } r) Record([r]); Record(t.Predecessors); Record(t.Successors); }
            return result;
        }
    }
    public AssistPlanSummary SummarizePlan()
    {
        lock (gate) { var result = new AssistPlanReader(session.Document, today).SummarizePlan(); Record(result.Tasks); return result; }
    }
    public AssistPage<AssistManualSection> ReadManual(string query, int offset = 0, int limit = 5) => AssistManual.Read(manualMarkdown, query, offset, limit);
    public AssistProposalResult ProposeChanges(IReadOnlyList<AssistEdit> changes)
    {
        lock (gate) {
            var (document, revision) = session.CaptureRevision();
            var result = AssistProposal.Create(document, revision, today, changes);
            if (result.Proposal is { } proposal) pending = proposal;
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
