using System.Collections.Immutable;

namespace GhProjectsBoards.Core.Projects;

// The persistence snapshot owns its arrays, including arrays held by records.
// Scalars and immutable collections with immutable elements can be shared.
internal static class DraftSnapshot
{
    internal static DraftRecord Copy(DraftRecord record) => record with
    {
        Fields = record.Fields.Select(Field).ToArray(),
        History = record.History.Select(t => t with
        {
            Changes = t.Changes.Select(c => c with { Before = Field(c.Before), After = Field(c.After) }).ToArray(),
            Rows = t.Rows?.ToArray(),
            Plan = t.Plan is { } p ? new(p.Before is null ? null : Plan(p.Before), Plan(p.After)) : null
        }).ToArray(),
        Registrations = record.Registrations?.Select(r => r with
        {
            Repositories = r.Repositories.ToArray(),
            Snapshot = r.Snapshot with
            {
                Fields = r.Snapshot.Fields.Select(f => f with { Options = f.Options.ToArray() }).ToArray(),
                Items = r.Snapshot.Items.Select(i => i with { Values = i.Values.ToArray() }).ToArray(),
                Issues = r.Snapshot.Issues.Select(i => i with { Native = i.Native is { } n
                    ? n with { Assignees = n.Assignees.ToArray(), Predecessors = n.Predecessors.ToArray() } : null }).ToArray()
            }
        }).ToArray(),
        StructuralChanges = record.StructuralChanges?.ToArray(),
        Journal = record.Journal?.Select(b => b with
        {
            Operations = b.Operations.Select(Operation).ToImmutableArray(),
            Creations = b.Creations?.Select(c => c with
            {
                Fields = c.Fields?.Select(Operation).ToArray(), EarlierFields = c.EarlierFields?.Select(Operation).ToArray(),
                SetupIntents = c.SetupIntents?.ToArray(), EarlierSetupIntents = c.EarlierSetupIntents?.Select(v => v.ToArray()).ToArray(),
                PlanningIntents = c.PlanningIntents?.ToArray(), SetupPlanningIntents = c.SetupPlanningIntents?.ToArray(),
                EarlierPlanningIntents = c.EarlierPlanningIntents?.Select(v => v.ToArray()).ToArray()
            }).ToArray()
        }).ToArray(),
        LocalRows = record.LocalRows?.ToArray(),
        ColumnPreferences = record.ColumnPreferences?.Select(p => p with { Columns = p.Columns.ToArray() }).ToArray(),
        RowPreferences = record.RowPreferences?.Select(p => p with { Definition = p.Definition with
        {
            Filters = p.Definition.Filters?.Select(f => f with { OptionIds = f.OptionIds.ToArray(), States = f.States.ToArray() }).ToArray()
        } }).ToArray(),
        Planning = record.Planning?.Select(Plan).ToArray()
    };

    private static FieldObservation? Observation(FieldObservation? value) => value is null ? null : value with { Options = value.Options.ToArray() };
    private static DraftField Field(DraftField field) => field.Observation is null ? field : field with { Observation = Observation(field.Observation) };
    private static ApplyOperation Operation(ApplyOperation operation) => operation.Verification is null ? operation
        : operation with { Verification = Observation(operation.Verification) };
    private static ProjectPlanning Plan(ProjectPlanning plan) => plan with
    {
        Fields = plan.Fields.ToArray(), People = plan.People.ToArray(),
        Tasks = plan.Tasks.Select(t => t with
        { Actuals = t.Actuals?.ToArray(), Contributions = t.Contributions?.ToArray(), LocalLinks = t.LocalLinks?.ToArray(),
            Assignment = t.Assignment is { } a ? a with { Assignees = a.Assignees.ToArray() } : null }).ToArray(),
        Calendar = plan.Calendar with
        {
            Holidays = plan.Calendar.Holidays with { Dates = plan.Calendar.Holidays.Dates.ToArray() },
            Exceptions = plan.Calendar.Exceptions.Select(e => e with { Intervals = e.Intervals.ToArray() }).ToArray()
        }
    };
}
