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
internal enum ProjectItemKind { Issue, PullRequest, Draft, Unavailable, Unsupported }
internal enum ProjectReadOutcome { Complete, Partial, Failed, Cancelled, TimedOut }
internal enum ReadProblemKind { Api, InvalidResponse, DuplicateIdentity, RepeatedCursor, IncompleteTraversal, ScopeMismatch }
internal sealed record ProjectReadProgress(string Stage, int Fields, int Items, int Issues);

internal sealed record ReadValue<T>(ValueAvailability Availability, T? Value = default);
internal sealed record RepositoryReadModel(ScopedId Id, ScopedId OwnerId, string NameWithOwner);
internal sealed record IssueReadModel(ScopedId Id, RepositoryReadModel Repository, int Number, string Url,
    ReadValue<string> Title, ReadValue<IssueState?> State, CapabilityObservation? Capability = null);
internal sealed record CapabilityObservation(bool? CanUpdate, DateTimeOffset ObservedAt);
internal sealed record SelectOption(string Id, string Name);
internal sealed record ProjectFieldDefinition(ScopedId Id, ScopedId ProjectId, string Name,
    string TypeName, string DataType, FieldOwner ValueOwner, IReadOnlyList<SelectOption> Options,
    ValueAvailability Availability);
internal sealed record ProjectFieldValue(ScopedId? FieldId, string? ValueId, string TypeName,
    ValueAvailability Availability, string? OptionId = null, string? Scalar = null);
internal sealed record ProjectItemReadModel(ScopedId Id, ProjectItemKind Kind, string TypeName,
    ScopedId? ContentId, bool IsArchived, IReadOnlyList<ProjectFieldValue> Values, bool ValuesComplete);
internal sealed record ProjectReadModel(ScopedId Id, ScopedId OwnerId, string OwnerType, int Number,
    string Url, string Title, IReadOnlyList<ProjectFieldDefinition> Fields,
    IReadOnlyDictionary<ScopedId, IssueReadModel> Issues, IReadOnlyList<ProjectItemReadModel> Items,
    bool FieldsComplete, bool ItemsComplete, CapabilityObservation? Capability = null);
internal sealed record ReadProblem(ReadProblemKind Kind, string Stage, FailureKind Failure = FailureKind.None,
    ApiOutcome? ApiOutcome = null, int? HttpStatus = null, TimeSpan? RetryAfter = null);
internal sealed record ProjectReadResult(ProjectReadOutcome Outcome, ProjectReadModel? Project,
    IReadOnlyList<ReadProblem> Problems)
{
    // Snapshots contain private source values; diagnostics expose classifications only.
    public override string ToString() => $"Project read: {Outcome}; problems: {Problems.Count}";
}
