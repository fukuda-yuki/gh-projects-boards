using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

internal sealed record AssistEdit(string Identity, string Field, JsonElement Value);
internal sealed record AssistProposedChange(AssistTaskReference Task, string Field, string Current, string Proposed);
internal sealed record AssistScheduleChange(AssistTaskReference Task, DateOnly? PreviousStart, DateOnly? PreviousEnd, DateOnly? Start, DateOnly? End);
internal sealed record AssistProposalResult(AssistProposal? Proposal, IReadOnlyList<string> Errors);
internal sealed record AssistProposal(string Id, long Revision, IReadOnlyList<AssistProposedChange> Changes, AssistPage<AssistScheduleChange> ScheduleEffect)
{
    internal EditPlanCells Command { get; init; } = null!;
    // The tasks this proposal's own edit reschedules (#236). Applying it moves nothing else.
    public int MovedTasks { get; init; }
    public DateOnly StatusDate { get; init; }
    public const string StaleMessage = "計画が変わったため、この提案は適用できません。もう一度依頼してください。";
    internal static readonly string[] EditableFields = [PlanField.Assignees, PlanField.Estimate, PlanField.Actual, PlanField.Remaining,
        PlanField.Phase, PlanField.Start, PlanField.End, PlanField.Predecessors, PlanField.Title];
    public static AssistProposalResult Create(PlanDocument document, long revision, DateOnly today, IReadOnlyList<AssistEdit> edits)
    {
        try {
            if (edits.Count is < 1 or > 1000) throw new ArgumentException("変更は1〜1000セルで指定してください。");
            var reader = new AssistPlanReader(document, today);
            var command = new EditPlanCells(PlanOperationKind.Paste, edits.Select(e => {
                _ = reader.Reference(e.Identity);
                if (!EditableFields.Contains(e.Field)) throw new ArgumentException("アシストではこの項目を変更できません。");
                return new PlanCellChange(e.Identity, e.Field, TypedValue(document, e, today));
            }).ToImmutableArray());
            var (state, _) = PlanOperations.Apply(document, command, today);
            // The effect is the tasks the PMO's own edit reschedules, measured on the candidate document.
            var candidate = document with { State = state };
            var displayed = PlanOperations.Schedule(document, today).ToDictionary(t => t.Input.Identity);
            var beforeSchedule = displayed;
            var afterSchedule = PlanOperations.Schedule(candidate, today);
            var afterById = afterSchedule.ToDictionary(t => t.Input.Identity);
            var effect = afterSchedule.Where(t => !t.IsSummary &&
                (t.Start.Value != beforeSchedule[t.Input.Identity].Start.Value || t.End.Value != beforeSchedule[t.Input.Identity].End.Value))
                .Select(t => new AssistScheduleChange(reader.Reference(t.Input.Identity), beforeSchedule[t.Input.Identity].Start.Value,
                    beforeSchedule[t.Input.Identity].End.Value, t.Start.Value, t.End.Value)).ToArray();
            var changes = edits.Select(e => new AssistProposedChange(reader.Reference(e.Identity), e.Field,
                DisplayScheduled(document, document.State.Rows.Single(r => r.Identity == e.Identity), displayed[e.Identity], e.Field),
                DisplayScheduled(candidate, state.Rows.Single(r => r.Identity == e.Identity), afterById[e.Identity], e.Field))).ToArray();
            return new(new(Guid.NewGuid().ToString("N"), revision, changes, AssistPlanReader.Page(effect, 0, 10)) {
                Command = command, MovedTasks = effect.Length, StatusDate = document.State.Settings.StatusDate ?? today }, []);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or JsonException or OverflowException) {
            return new(null, [ex is JsonException or FormatException or OverflowException ? "変更する値の型または範囲が不正です。" : ex.Message]);
        }
    }
    private static object? TypedValue(PlanDocument document, AssistEdit edit, DateOnly today)
    {
        var value = edit.Value;
        if (value.ValueKind == JsonValueKind.Null) return null;
        switch (edit.Field) {
            case PlanField.Estimate: case PlanField.Actual: case PlanField.Remaining: return value.GetDecimal();
            case PlanField.Start: case PlanField.End:
                return DateOnly.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            case PlanField.Assignees: case PlanField.Predecessors:
                var ids = value.EnumerateArray().Select(v => v.GetString() ?? throw new ArgumentException("空の識別子は指定できません。")).ToImmutableArray();
                var allowed = edit.Field == PlanField.Predecessors ? document.State.Rows.Select(r => r.Identity).ToHashSet()
                    : document.State.Settings.People.Select(p => p.Identity).Concat(document.Sync.PeopleNames.Keys).ToHashSet();
                if (ids.Any(id => !allowed.Contains(id))) throw new ArgumentException(edit.Field == PlanField.Predecessors ? "開いている計画にない先行タスクです。" : "担当者を確認してください。");
                return ids;
            default: return PlanSheetEditing.Parse(document, edit.Field, value.GetString()!, today, edit.Identity);
        }
    }
    internal static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    internal static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string DisplayScheduled(PlanDocument document, PlanRow row, ScheduledTask task, string field) => field switch {
        PlanField.Start => Date(task.Start.Value), PlanField.End => Date(task.End.Value),
        PlanField.Estimate => Number(task.Estimate), PlanField.Actual => Number(task.Actual), PlanField.Remaining => Number(task.Remaining),
        _ => Display(document, row, field)
    };
    internal static string Display(PlanDocument document, PlanRow row, string field) => field switch {
        PlanField.Assignees => string.Join(", ", row.Assignees.Select(id => AssistPlanReader.PersonName(document, id))),
        PlanField.Predecessors => string.Join(", ", row.Predecessors.Select(id => document.State.Rows.FirstOrDefault(r => r.Identity == id) is { } predecessor
            ? (document.State.Rows.IndexOf(predecessor) + 1).ToString(CultureInfo.InvariantCulture) : "計画外")),
        PlanField.Estimate => Number(row.Estimate), PlanField.Actual => Number(row.Actual), PlanField.Remaining => Number(row.Remaining),
        PlanField.Start => Date(row.Start), PlanField.End => Date(row.End),
        PlanField.Phase => row.Phase ?? "", PlanField.Title => row.Title,
        _ => throw new ArgumentException("アシストではこの項目を変更できません。")
    };
}
