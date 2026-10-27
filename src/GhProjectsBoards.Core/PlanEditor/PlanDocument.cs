using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanField
{
    public const string Title = "Title";
    public const string Repository = "Repository";
    public const string Status = "Status";
    public const string Phase = "Phase";
    // #319: the organization Issue type, an Issue-level value shown as the standard column 種類.
    public const string IssueType = "IssueType";
    public const string Closed = "Closed";
    public const string Assignees = "Assignees";
    public const string Estimate = "Estimate";
    public const string Remaining = "Remaining";
    public const string Actual = "Actual";
    public const string Start = "Start";
    public const string End = "End";
    public const string Predecessors = "Predecessors";
    public const string Parent = "Parent";
    public const string Order = "Order";
    public const string NewTask = "NewTask";
    public const string SubIssueOrder = "SubIssueOrder";
    public const string Labels = "Labels";
    public const string MilestoneTitle = "MilestoneTitle";
    // #231: a task that is 中止 rather than 完了, and a task the PMO has taken out of this version.
    public const string Cancelled = "Cancelled";
    public const string Removed = "Removed";
    public static readonly string[] All = [Title, Repository, Status, Phase, IssueType, Closed, Assignees, Estimate, Remaining, Actual, Start, End, Predecessors, Parent, Order, NewTask, SubIssueOrder, Labels, MilestoneTitle, Cancelled, Removed];
    public static bool IsDefined(string value) => All.Contains(value) || PlanSheetColumns.IsExtra(value);
}

internal enum PlanOperationKind { Cell, Paste, Fill, CtrlD, Clear, Insert, Delete, CsvImport, Indent, Outdent, Move, Settings, ResolveConflict, Reset, Close, Cancel, Reopen, Remove }
internal sealed record PlanColumnDefinition(string Id, string Name, string DataType)
{ public ImmutableArray<string> Options { get; init; } = [];
  // #258: an organization Issue Field. Its value belongs to the Issue, so every Project holding the Issue shares it.
  public bool IssueField { get; init; } }
internal sealed record PlanColumnMapping(string Role, string FieldId, string Name, string DataType);
internal sealed record PlanResource(string Identity, string Name, decimal Rate, decimal? Allowance);
internal sealed record PlanHoliday(DateOnly Date, string Name);
internal sealed record PlanHolidayData(string Version, string Source, string SourceSha256, DateTimeOffset RetrievedAt,
    int FirstYear, int LastYear, ImmutableArray<PlanHoliday> Dates)
{
    public static PlanHolidayData FromPreset(HolidayPreset preset) => new(preset.Version, preset.Source, preset.SourceSha256,
        preset.RetrievedAt, preset.FirstYear, preset.LastYear, preset.Dates.Select(d => new PlanHoliday(d.Date, d.Name)).ToImmutableArray());
    public HolidayPreset ToPreset() => new(Version, Source, SourceSha256, RetrievedAt, FirstYear, LastYear,
        Dates.Select(d => new HolidayDate(d.Date, d.Name)).ToArray());
}
internal sealed record PlanSheetColumnChoice(string Key, bool Visible, string? DisplayName, double? Width = null);
internal sealed record ProjectPlanSettings
{
    public ImmutableArray<PlanSheetColumnChoice> SheetColumns { get; init; } = [];
    public ImmutableDictionary<string, string> PhaseMilestones { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<PlanColumnMapping> Columns { get; init; } = [];
    public ImmutableArray<DateOnly> CompanyDaysOff { get; init; } = [];
    public PlanHolidayData? ImportedHolidays { get; init; }
    public ImmutableArray<PlanResource> People { get; init; } = [];
    public DateOnly? ProjectStart { get; init; }
    public DateOnly? ProjectEnd { get; init; }
    public string? DefaultRepository { get; init; }
    public DateOnly? StatusDate { get; init; }
    // #308: the Status option names that mean not started when 着手日 is read from the Issue history. Null
    // means the defaults (no Status, Backlog, Todo, Ready); an explicit empty list means every option starts.
    public string[]? NotStartedStatuses { get; init; }
}
internal sealed record PlanExtraValue(ValueAvailability Availability, string? Json);
internal sealed record PlanRow(string Identity, string Title, string Repository)
{
    public string? MilestoneTitle { get; init; }
    public ImmutableArray<string> Labels { get; init; } = [];
    public ImmutableDictionary<string, PlanExtraValue> ExtraValues { get; init; } = ImmutableDictionary<string, PlanExtraValue>.Empty;
    public string? CsvSourceHash { get; init; }
    public string? Status { get; init; }
    public string? Phase { get; init; }
    // #319: the name of the Issue's organization Issue type; null when it has none or its repository has none.
    public string? IssueType { get; init; }
    public bool Closed { get; init; }
    public DateOnly? CloseDate { get; init; }
    // #231: 中止 (closed as not planned) and この版から外す (pending archive). Both leave the schedule,
    // the totals and the load; the row keeps its values until the publish confirms it.
    public bool Cancelled { get; init; }
    public bool Removed { get; init; }
    public ImmutableArray<string> Assignees { get; init; } = [];
    public ImmutableArray<string> Predecessors { get; init; } = [];
    public string? Parent { get; init; }
    public decimal? Estimate { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Actual { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public static PlanRow New(string title, string repository = "") => new("local:" + Guid.NewGuid().ToString("N"), title, repository);
}
internal sealed record PlanMilestone(string Title, DateOnly? DueDate, bool Closed, bool ConflictingDates);
internal sealed record PlanRepositoryOption(string Repository, string Id, string Name);
// #319: one organization Issue type this Project can use. The name is the plan value; the ID is the write target.
internal sealed record PlanIssueType(string Id, string Name);
internal sealed record PlanBaseline(ImmutableArray<PlanRow> Rows, ImmutableArray<PlanColumnDefinition> Columns)
{ public ImmutableArray<PlanMilestone> Milestones { get; init; } = [];
  public ImmutableArray<PlanRepositoryOption> RepositoryMilestones { get; init; } = [];
  public ImmutableArray<PlanRepositoryOption> RepositoryLabels { get; init; } = [];
  public ImmutableArray<PlanIssueType> IssueTypes { get; init; } = [];
  // The repositories whose Issues can carry a type: their owner organization has Issue types.
  public ImmutableArray<string> IssueTypeRepositories { get; init; } = []; }
internal sealed record PlanState(ImmutableArray<PlanRow> Rows, ProjectPlanSettings Settings)
{
    // #231: verified Project-item archives this plan performed. A later read that returns one of these
    // Issues is re-adopting an Issue this plan already knew, not meeting a new one, so it keeps the
    // position the read reports instead of the #245 tail placement. Persisted with the plan and pruned
    // when the Issue comes back.
    public ImmutableArray<string> ArchivedIdentities { get; init; } = [];
}
internal sealed record PlanDocument(ScopedId Project, PlanBaseline Baseline, PlanState State)
{
    public PlanSync Sync { get; init; } = new();
}
internal sealed record PlanCellChange(string Identity, string Field, object? Value);
internal abstract record PlanCommand;
internal sealed record EditPlanCells(PlanOperationKind Kind, ImmutableArray<PlanCellChange> Cells) : PlanCommand
{
    // #253: people one on-demand lookup verified ride in the command that assigns them, so the assignment and
    // their registration are one Undo step and one save, exactly as InsertPlanRows.CsvPeople does for CSV import.
    public ImmutableArray<PlanResource> NewPeople { get; init; } = [];
}
internal sealed record FillPlanCells(PlanOperationKind Kind, string Source, ImmutableArray<string> Targets, ImmutableArray<string> Fields) : PlanCommand;
internal sealed record ClearPlanCells(ImmutableArray<string> Targets, ImmutableArray<string> Fields) : PlanCommand;
internal sealed record InsertPlanRows(ImmutableArray<PlanRow> Rows, string? Before = null, PlanOperationKind Kind = PlanOperationKind.Insert) : PlanCommand
{
    public ImmutableArray<PlanResource> CsvPeople { get; init; } = [];
    // #253: a person resolved while editing a new row's 担当者 is registered with the insert, for the same
    // one-operation reason as CsvPeople.
    public ImmutableArray<PlanResource> NewPeople { get; init; } = [];
    public bool AllowDuplicateCsv { get; init; }
}
internal sealed record IndentPlanRows(ImmutableArray<string> Targets, bool Outdent = false) : PlanCommand;
internal sealed record MovePlanSubtrees(ImmutableArray<string> Targets, string? Before = null) : PlanCommand;
internal sealed record ArrangePlanOutline() : PlanCommand;
internal sealed record MovePlanRows(ImmutableArray<string> Targets, string? Before = null) : PlanCommand;
internal sealed record DeletePlanRows(ImmutableArray<string> Targets) : PlanCommand;
// #231: the four row-state commands. A requirement row stands for its whole subtree, and each command
// applies to the rows of the selection it fits.
internal sealed record ClosePlanRows(ImmutableArray<string> Targets, bool Cancelled = false) : PlanCommand;
internal sealed record ReopenPlanRows(ImmutableArray<string> Targets) : PlanCommand;
internal sealed record RemovePlanRows(ImmutableArray<string> Targets) : PlanCommand;
internal enum PlanRowAction { Complete, Cancel, Reopen, Remove }
internal sealed record PlanRowEligibility(ImmutableArray<string> Applies, ImmutableArray<(string Identity, string Reason)> Skipped);
internal sealed record ReplacePlanSettings(ProjectPlanSettings Settings) : PlanCommand;
internal sealed record PlanRowChange(string Identity, PlanRow? Before, PlanRow? After);
internal sealed record PlanPatch(PlanOperationKind Kind, ImmutableArray<PlanRowChange> Rows,
    ImmutableArray<string>? BeforeOrder, ImmutableArray<string>? AfterOrder,
    ProjectPlanSettings? BeforeSettings, ProjectPlanSettings? AfterSettings)
{
    public ImmutableArray<PlanRow> DiscardedRows { get; init; } = [];
    public ImmutableArray<string>? BeforeUnavailable { get; init; }
    public ImmutableArray<string>? AfterUnavailable { get; init; }
    public ImmutableDictionary<string, ImmutableArray<string>>? BeforeLocalOrderChoices { get; init; }
    public ImmutableDictionary<string, ImmutableArray<string>>? AfterLocalOrderChoices { get; init; }
    public ImmutableArray<PlanConflict>? BeforeConflicts { get; init; }
    public ImmutableArray<PlanConflict>? AfterConflicts { get; init; }
}
internal sealed record PlanCheckpoint(int Version, long Revision, PlanDocument Document, ImmutableArray<PlanPatch> Undo, ImmutableArray<PlanPatch> Redo);
internal enum PlanLoadStatus { Missing, Loaded, Blocked, Unreadable }
internal enum PlanSaveFailure { None, Io, ChangedFile, InvalidFile }
internal sealed record PlanSaveResult(bool Succeeded, PlanSaveFailure Failure, string? Error, string? Fingerprint)
{
    public PlanRefreshResult? Recalculation { get; init; }
    public bool Retryable => Failure == PlanSaveFailure.Io;
}internal sealed record PlanLoadResult(PlanLoadStatus Status, PlanCheckpoint? Checkpoint, string? Fingerprint, string? Error);
internal sealed record PlanOpenResult(PlanLoadStatus Status, PlanSession? Session, string? Error);
internal sealed record PlanReconcileResult(bool Succeeded, string? Error);
// Carries the failure kind to the UI boundary, so a changed stored plan is not presented as a retry (#248).
internal sealed class PlanSaveFailedException(PlanSaveResult result) : IOException(result.Error ?? "保存できませんでした。")
{
    public PlanSaveResult Result { get; } = result;
}
internal sealed record PlanImportResult(bool Applied, ImmutableArray<string> Warnings, string? Error, PlanSaveResult? Save);
internal sealed record PlanSiblingOrderChange(ImmutableArray<string> Before, ImmutableArray<string> After);
internal enum PlanUnpublishedInputKind { Entered, Recalculated }
internal sealed record PlanUnpublished(ImmutableDictionary<string, ImmutableArray<string>> Fields)
{
    public int TaskCount => Fields.Count;
    public int NewTaskCount => Fields.Count(pair => pair.Value.Contains(PlanField.NewTask));
    public int EnteredTaskCount => Fields.Count(pair => pair.Value.Contains(PlanField.NewTask)
        || pair.Value.Any(value => InputKind(pair.Key, value) == PlanUnpublishedInputKind.Entered));
    public int RecalculatedTaskCount => TaskCount - EnteredTaskCount;
    public ImmutableHashSet<(string Identity, string Field)> RecalculatedFields { get; init; } = [];
    public PlanUnpublishedInputKind? InputKind(string identity, string field)
        => !Fields.TryGetValue(identity, out var fields) || !fields.Contains(field) ? null
            : RecalculatedFields.Contains((identity, field)) ? PlanUnpublishedInputKind.Recalculated : PlanUnpublishedInputKind.Entered;
}
internal sealed record PlanSettingsFile(int Version, ProjectPlanSettings Settings);
