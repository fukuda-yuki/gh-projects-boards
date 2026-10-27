using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed record ConnectionScope(string Host, long ViewerId)
{
    public static ConnectionScope From(ConnectionContext context) => new(context.Host, context.ViewerId);
}
internal sealed record ScopedId(ConnectionScope Scope, string NodeId);
internal enum ValueAvailability { Present, Empty, Unsupported, Unavailable, NotLoaded }
internal enum FieldOwner { Issue, ProjectItem, Unknown }
internal enum IssueState { Open, Closed }
// #231: the read enum has four values; the write enum has no REOPENED.
internal enum IssueStateReason { Completed, NotPlanned, Duplicate, Reopened }
internal enum ProjectItemKind { Issue, PullRequest, Draft, Unavailable, Unsupported }
internal enum ProjectReadOutcome { Complete, Partial, Failed, Cancelled, TimedOut }
internal enum ReadProblemKind { Api, InvalidResponse, DuplicateIdentity, RepeatedCursor, IncompleteTraversal, ConcurrentChange, ScopeMismatch }
internal sealed record RemoteProgress(string Stage, int? Completed = null, int? Total = null);

internal sealed record ReadValue<T>(ValueAvailability Availability, T? Value = default);
internal sealed record RepositoryReadModel(ScopedId Id, ScopedId OwnerId, string NameWithOwner);
internal sealed record IssueReadModel(ScopedId Id, RepositoryReadModel Repository, int Number, string Url,
    ReadValue<string> Title, ReadValue<IssueState?> State, CapabilityObservation? Capability = null,
    IssuePlanningObservation? Native = null)
{ public ReadValue<NativeLabel[]> Labels { get; init; } = new(ValueAvailability.NotLoaded);
  public ReadValue<MilestoneReadModel> Milestone { get; init; } = new(ValueAvailability.NotLoaded);
  public ReadValue<DateTimeOffset?> ClosedAt { get; init; } = new(ValueAvailability.NotLoaded);
  public ReadValue<IssueStateReason?> StateReason { get; init; } = new(ValueAvailability.NotLoaded);
  // #258: organization Issue Field values are written on the Issue; this is GitHub's permission for that.
  public bool? CanSetFields { get; init; }
  // #308: the other half of the history change observation (see ProjectItemReadModel.UpdatedAt).
  public DateTimeOffset? UpdatedAt { get; init; } }
internal sealed record MilestoneReadModel(ScopedId Id, ScopedId RepositoryId, string Title, DateOnly? DueDate, bool Closed);
internal sealed record NativeLabel(string Id, string Name);
internal sealed record RepositoryLabel(ScopedId RepositoryId, string Id, string Name);
internal sealed record NativePerson(ScopedId Id, string Login);
internal sealed record IssuePlanningObservation(NativePerson[] Assignees, ScopedId[] Predecessors,
    ReadValue<ScopedId> Parent, bool Complete) { public ScopedId[] SubIssues { get; init; } = []; }
internal sealed record CapabilityObservation(bool? CanUpdate, DateTimeOffset ObservedAt);
internal sealed record SelectOption(string Id, string Name);
internal sealed record ProjectFieldDefinition(ScopedId Id, ScopedId ProjectId, string Name,
    string TypeName, string DataType, FieldOwner ValueOwner, IReadOnlyList<SelectOption> Options,
    ValueAvailability Availability)
{
    // #258: an organization Issue Field added to the Project. Its value belongs to the Issue and is written
    // with this organization field ID, not the Project field ID.
    public string? IssueFieldId { get; init; }
}
internal sealed record ProjectFieldValue(ScopedId? FieldId, string? ValueId, string TypeName,
    ValueAvailability Availability, string? OptionId = null, string? Scalar = null)
{
    // When GitHub last changed a Project field value (#238). Organization Issue Field values report none.
    public DateTimeOffset? UpdatedAt { get; init; }
}
internal sealed record ProjectItemReadModel(ScopedId Id, ProjectItemKind Kind, string TypeName,
    ScopedId? ContentId, bool IsArchived, IReadOnlyList<ProjectFieldValue> Values, bool ValuesComplete)
{
    // #308: GitHub's change time of the item itself. A Status write changes it while the Issue's own
    // updatedAt does not, so a refresh compares both to decide which rows need their history re-read.
    public DateTimeOffset? UpdatedAt { get; init; }
}
internal sealed record ProjectReadModel(ScopedId Id, ScopedId OwnerId, string OwnerType, int Number,
    string Url, string Title, IReadOnlyList<ProjectFieldDefinition> Fields,
    IReadOnlyDictionary<ScopedId, IssueReadModel> Issues, IReadOnlyList<ProjectItemReadModel> Items,
    bool FieldsComplete, bool ItemsComplete, CapabilityObservation? Capability = null)
{ public IReadOnlyList<RepositoryLabel> Labels { get; init; } = [];
  public IReadOnlyList<MilestoneReadModel> Milestones { get; init; } = [];
  public bool MilestonesComplete { get; init; }
  public int UndeliveredItemCount { get; init; } }
internal sealed record ReadProblem(ReadProblemKind Kind, string Stage, FailureKind Failure = FailureKind.None,
    ApiOutcome? ApiOutcome = null, int? HttpStatus = null, TimeSpan? RetryAfter = null);
internal sealed record ProjectReadResult(ProjectReadOutcome Outcome, ProjectReadModel? Project,
    IReadOnlyList<ReadProblem> Problems)
{
    // Snapshots contain private source values; diagnostics expose classifications only.
    public override string ToString() => $"Project read: {Outcome}; problems: {Problems.Count}";
}
