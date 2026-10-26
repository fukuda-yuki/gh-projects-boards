using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

internal sealed class AssistPlanTools(PlanSession session, DateOnly today, string manualMarkdown)
{
    private readonly object gate = new();
    private readonly Dictionary<string, AssistTaskReference> references = [];
    private readonly Dictionary<string, int> referencePriority = [];
    private AssistProposal? pending;
    private AssistProposal? turnProposal;
    // Edits already accepted into this turn's proposal. A later call in the same turn adds to them;
    // BeginTurn clears the list so the next turn replaces the pending proposal instead of extending it.
    private readonly List<AssistEdit> turnEdits = [];
    public IReadOnlyList<AssistTaskReference> References { get { lock (gate) return references.Values.OrderBy(t => referencePriority[t.Identity]).ThenBy(t => t.Position).ToArray(); } }
    public AssistProposal? PendingProposal { get { lock (gate) return pending; } }
    public AssistProposal? TurnProposal { get { lock (gate) return turnProposal; } }
    public bool ProposalIsStale { get { lock (gate) return pending is { } p && session.CaptureRevision().Revision != p.Revision; } }
    public void BeginTurn() { lock (gate) { references.Clear(); referencePriority.Clear(); turnProposal = null; turnEdits.Clear(); } }
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
            // One selected row per call used to replace the card and leave only the last row. Calls in this
            // turn combine; the same task and field keep the later value. A failed call leaves the card as it was.
            var combined = turnProposal is null ? changes : Combine(turnEdits, changes);
            var result = AssistProposal.Create(document, revision, today, combined);
            if (result.Proposal is { } proposal) {
                pending = turnProposal = proposal;
                turnEdits.Clear();
                turnEdits.AddRange(combined);
                Record(proposal.Changes.Select(c => c.Task), 0);
                Record(proposal.ScheduleEffect.Items.Select(c => c.Task));
            }
            return result;
        }
    }
    private static List<AssistEdit> Combine(IReadOnlyList<AssistEdit> prior, IReadOnlyList<AssistEdit> next)
    {
        var chosen = new Dictionary<(string Identity, string Field), AssistEdit>();
        var order = new List<(string Identity, string Field)>();
        foreach (var edit in prior.Concat(next))
        {
            var key = (edit.Identity, edit.Field);
            if (chosen.TryAdd(key, edit)) order.Add(key);
            else chosen[key] = edit;
        }
        return order.Select(key => chosen[key]).ToList();
    }
    public void DiscardProposal() { lock (gate) ClearProposal(); }
    public Task<PlanSaveResult> ApplyProposal(string id)
    {
        lock (gate) {
            if (pending is not { } proposal || proposal.Id != id) throw new InvalidOperationException("この提案は破棄されたか、別の提案に置き換えられました。");
            var save = session.ExecuteAtRevision(proposal.Command, today, proposal.Revision, AssistProposal.StaleMessage);
            ClearProposal(); return save;
        }
    }
    // Dropping the card ends this turn's proposal. The next call starts a new one instead of
    // putting the discarded or applied edits back.
    private void ClearProposal() { pending = null; turnProposal = null; turnEdits.Clear(); }
    internal void ValidateSelectedIdentities(IReadOnlyList<string> identities)
    {
        // The App sends only task rows from the open plan; a stale or foreign identity must fail before any answer.
        if (identities.Any(identity => !session.Document.State.Rows.Any(row => row.Identity == identity)))
            throw new ArgumentException("開いている計画にないタスクです。");
    }
}
