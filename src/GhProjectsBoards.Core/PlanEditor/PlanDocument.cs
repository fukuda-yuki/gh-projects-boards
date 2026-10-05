using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal enum PlanField { Title, Repository, Status, Closed, Assignees, Estimate, Remaining, Actual, Start, End, Predecessors, Parent, Order, StartNoEarlierThan, Fixed, NewTask, SubIssueOrder }
internal enum PlanOperationKind { Cell, Paste, Fill, CtrlD, Clear, Insert, CsvImport, Indent, Outdent, Move, Settings, ResolveConflict }
internal sealed record PlanColumnDefinition(string Id, string Name, string DataType);
internal sealed record PlanColumnMapping(PlanField Role, string FieldId, string Name, string DataType);
internal sealed record PlanResource(string Identity, string Name, decimal Rate, decimal? Allowance, ImmutableArray<DateOnly> DaysOff);
internal sealed record PlanHoliday(DateOnly Date, string Name);
internal sealed record PlanHolidayData(string Version, string Source, string SourceSha256, DateTimeOffset RetrievedAt,
    int FirstYear, int LastYear, ImmutableArray<PlanHoliday> Dates)
{
    public static PlanHolidayData FromPreset(HolidayPreset preset) => new(preset.Version, preset.Source, preset.SourceSha256,
        preset.RetrievedAt, preset.FirstYear, preset.LastYear, preset.Dates.Select(d => new PlanHoliday(d.Date, d.Name)).ToImmutableArray());
    public HolidayPreset ToPreset() => new(Version, Source, SourceSha256, RetrievedAt, FirstYear, LastYear,
        Dates.Select(d => new HolidayDate(d.Date, d.Name)).ToArray());
}
internal sealed record ProjectPlanSettings
{
    public ImmutableArray<PlanColumnMapping> Columns { get; init; } = [];
    public ImmutableArray<DateOnly> CompanyDaysOff { get; init; } = [];
    public PlanHolidayData? ImportedHolidays { get; init; }
    public ImmutableArray<PlanResource> People { get; init; } = [];
    public DateOnly? ProjectStart { get; init; }
    public string? DefaultRepository { get; init; }
    public DateOnly? StatusDate { get; init; }
}
internal sealed record PlanRow(string Identity, string Title, string Repository)
{
    public string? Status { get; init; }
    public bool Closed { get; init; }
    public ImmutableArray<string> Assignees { get; init; } = [];
    public ImmutableArray<string> Predecessors { get; init; } = [];
    public string? Parent { get; init; }
    public decimal? Estimate { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Actual { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public DateOnly? StartNoEarlierThan { get; init; }
    public bool Fixed { get; init; }
    public static PlanRow New(string title, string repository = "") => new("local:" + Guid.NewGuid().ToString("N"), title, repository);
}
internal sealed record PlanBaseline(ImmutableArray<PlanRow> Rows, ImmutableArray<PlanColumnDefinition> Columns);
internal sealed record PlanState(ImmutableArray<PlanRow> Rows, ProjectPlanSettings Settings);
internal sealed record PlanDocument(ScopedId Project, PlanBaseline Baseline, PlanState State) { public PlanSync Sync { get; init; } = new(); }
internal sealed record PlanCellChange(string Identity, PlanField Field, object? Value);
internal abstract record PlanCommand;
internal sealed record EditPlanCells(PlanOperationKind Kind, ImmutableArray<PlanCellChange> Cells) : PlanCommand;
internal sealed record FillPlanCells(PlanOperationKind Kind, string Source, ImmutableArray<string> Targets, ImmutableArray<PlanField> Fields) : PlanCommand;
internal sealed record ClearPlanCells(ImmutableArray<string> Targets, ImmutableArray<PlanField> Fields) : PlanCommand;
internal sealed record InsertPlanRows(ImmutableArray<PlanRow> Rows, string? Before = null, PlanOperationKind Kind = PlanOperationKind.Insert) : PlanCommand;
internal sealed record IndentPlanRows(ImmutableArray<string> Targets, bool Outdent = false) : PlanCommand;
internal sealed record MovePlanRows(ImmutableArray<string> Targets, string? Before = null) : PlanCommand;
internal sealed record ReplacePlanSettings(ProjectPlanSettings Settings) : PlanCommand;
internal sealed record PlanRowChange(string Identity, PlanRow? Before, PlanRow? After);
internal sealed record PlanPatch(PlanOperationKind Kind, ImmutableArray<PlanRowChange> Rows,
    ImmutableArray<string>? BeforeOrder, ImmutableArray<string>? AfterOrder,
    ProjectPlanSettings? BeforeSettings, ProjectPlanSettings? AfterSettings)
{
    public ImmutableArray<PlanRow> DiscardedRows { get; init; } = [];
    public ImmutableArray<string>? BeforeUnavailable { get; init; }
    public ImmutableArray<string>? AfterUnavailable { get; init; }
    public ImmutableArray<PlanConflict>? BeforeConflicts { get; init; }
    public ImmutableArray<PlanConflict>? AfterConflicts { get; init; }
}
internal sealed record PlanCheckpoint(int Version, long Revision, PlanDocument Document, ImmutableArray<PlanPatch> Undo, ImmutableArray<PlanPatch> Redo);
internal enum PlanLoadStatus { Missing, Loaded, Blocked }
internal enum PlanSaveFailure { None, Io, ChangedFile, InvalidFile }
internal sealed record PlanSaveResult(bool Succeeded, PlanSaveFailure Failure, string? Error, string? Fingerprint)
{
    public bool Retryable => Failure == PlanSaveFailure.Io;
}
internal sealed record PlanLoadResult(PlanLoadStatus Status, PlanCheckpoint? Checkpoint, string? Fingerprint, string? Error);
internal sealed record PlanOpenResult(PlanLoadStatus Status, PlanSession? Session, string? Error);
internal sealed record PlanImportResult(bool Applied, ImmutableArray<string> Warnings, string? Error, PlanSaveResult? Save);
internal sealed record PlanUnpublished(ImmutableDictionary<string, ImmutableArray<PlanField>> Fields)
{
    public int TaskCount => Fields.Count;
}
internal sealed record PlanSettingsFile(int Version, ProjectPlanSettings Settings);
