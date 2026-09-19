using System.Collections.ObjectModel;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed class ProjectReader(GhConnectionService service)
{
    public Task<ProjectReadResult> ReadAsync(ConnectionContext context, ScopedId project,
        CancellationToken cancellationToken = default, Action<ProjectReadProgress>? progress = null)
        => new ReadSession(service, context, project, cancellationToken, progress).RunAsync();

    public Task<(FieldObservation? Observation, ApiResult Result)> ObserveFieldAsync(ConnectionContext context,
        ApplyBatch batch, ApplyOperation operation, CancellationToken token)
        => new ReadSession(service, context, batch.Project, token, null).ObserveFieldAsync(operation);

    private sealed class ReadSession(GhConnectionService service, ConnectionContext context,
        ScopedId projectId, CancellationToken cancellationToken, Action<ProjectReadProgress>? progress)
    {
        private readonly List<ReadProblem> problems = [];
        private readonly Dictionary<ScopedId, ProjectFieldDefinition> fields = [];
        private readonly Dictionary<ScopedId, IssueReadModel> issues = [];
        private readonly Dictionary<ScopedId, ItemBuilder> items = [];
        private readonly HashSet<string> valueIds = new(StringComparer.Ordinal);
        private ProjectReadModel? project;
        private bool fieldsComplete, itemsComplete, stopped;
        private ApiOutcome? interruption;
        private string? observationScope;
        private bool firstObservationRead;

        private async Task<ApiResult> SendAsync(string query, object variables)
        {
            if (observationScope is null)
                return await service.SendAsync(context, ApiRequest.GraphQl(query, variables), cancellationToken);
            // Each contributing response identifies its own authenticated principal.
            // Preflight alone cannot bind a later subprocess to the same gh account.
            var request = ApiRequest.GraphQl(query.Insert(query.LastIndexOf('}'), " viewer { databaseId } "), variables);
            var first = firstObservationRead; firstObservationRead = false;
            var result = first
                ? await service.RecheckAndReadAsync(context, request, observationScope, cancellationToken)
                : await service.SendAsync(context, request, cancellationToken);
            if (!result.IsSuccess) return result;
            if (result.Data is not { ValueKind: JsonValueKind.Object } body
                || !body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("viewer", out var viewer) || viewer.ValueKind != JsonValueKind.Object
                || !viewer.TryGetProperty("databaseId", out var id) || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out var viewerId) || viewerId <= 0)
                return new(ApiOutcome.Failed, FailureKind.InvalidResponse);
            if (viewerId != context.ViewerId || context.IsInvalidated)
            {
                context.Invalidate();
                return new(ApiOutcome.Failed, FailureKind.IdentityChanged);
            }
            return result;
        }

        public async Task<(FieldObservation? Observation, ApiResult Result)> ObserveFieldAsync(ApplyOperation operation)
        {
            using var measured = PerformanceTrace.Span("scoped-item-observation");
            var key = operation.Key;
            if (projectId.Scope != ConnectionScope.From(context)
                || (key.Kind == "Title" ? key.NodeId != operation.IssueId || key.ProjectId is not null || key.FieldId is not null
                    : key.Kind is not ("Select" or "Number" or "Date") || key.NodeId != operation.ItemId || key.ProjectId != projectId.NodeId || string.IsNullOrWhiteSpace(key.FieldId)))
                return (null, new(ApiOutcome.Failed, FailureKind.IdentityChanged));
            try
            {
                observationScope = key.Kind == "Title" ? "repo" : "project";
                firstObservationRead = true;
                // Complete definitions retain the reader's field ownership and unknown-value guards.
                // Their cost depends on fields/options, never unrelated Project item count.
                var result = await SendAsync(ProjectQueries.ApplyObservation,
                    new { id = projectId.NodeId, item = operation.ItemId, after = (string?)null });
                if (!result.IsSuccess) return (null, result);
                var data = Property(result.Data ?? default, "data");
                var initialFields = ReadProjectFields(Property(data, "project"));
                var node = Property(data, "item");
                fieldsComplete = await WalkAsync("fields", ProjectQueries.Fields, projectId.NodeId,
                    ReadProjectFields, value => { AddField(value); return Task.CompletedTask; }, initialFields);
                if (!fieldsComplete || project is null) return Failure();
                MatchNode(node, operation.ItemId, "ProjectV2Item");
                await AddItemAsync(node);
                if (problems.Count != 0 || !items.TryGetValue(new(projectId.Scope, operation.ItemId), out var builder) || !builder.Complete)
                    return Failure();
                var item = BuildItem(builder, true);
                if (item.Kind != ProjectItemKind.Issue || item.ContentId?.NodeId != operation.IssueId || item.IsArchived)
                    return (null, new(ApiOutcome.Failed, FailureKind.NotFoundOrInaccessible));
                string? value;
                ValueAvailability availability;
                IReadOnlyList<SelectOption> options = [];
                if (key.Kind == "Title")
                {
                    var issue = issues[item.ContentId];
                    if (issue.Capability?.CanUpdate != true) return (null, new(ApiOutcome.Failed, FailureKind.PermissionDenied));
                    value = issue.Title.Value; availability = issue.Title.Availability;
                }
                else
                {
                    if (project.Capability?.CanUpdate != true || !fields.TryGetValue(new(projectId.Scope, key.FieldId!), out var field)
                        || field.ValueOwner != FieldOwner.ProjectItem || field.Availability != ValueAvailability.Present)
                        return (null, new(ApiOutcome.Failed, FailureKind.PermissionDenied));
                    options = field.Options;
                    if (field.DataType != PlanningScalars.DataType(key.Kind)
                        || key.Kind == "Select" && !operation.Intended.Clear && !options.Any(o => o.Id == operation.Intended.Value)
                        || !PlanningScalars.Publishable(key.Kind, operation.Intended))
                        return (null, new(ApiOutcome.Failed, FailureKind.PermissionDenied));
                    var observed = item.Values.Single(v => v.FieldId == field.Id);
                    value = key.Kind == "Select" ? observed.OptionId : observed.Scalar; availability = observed.Availability;
                }
                if (availability is not (ValueAvailability.Present or ValueAvailability.Empty)) return Failure();
                // This is evidence for one field only. No complete Project snapshot is published.
                return (new(Guid.NewGuid().ToString("N"), projectId, DateTimeOffset.UtcNow, value, availability, null, options.ToArray()), new(ApiOutcome.Success));
            }
            catch (ReadException) { return Failure(); }
            catch (OperationCanceledException) { return (null, new(ApiOutcome.Failed, FailureKind.Cancelled)); }

            (FieldObservation?, ApiResult) Failure() => (null, new(ApiOutcome.Failed,
                problems.FirstOrDefault()?.Failure ?? FailureKind.InvalidResponse, retryAfter: problems.FirstOrDefault()?.RetryAfter));
        }

        public async Task<ProjectReadResult> RunAsync()
        {
            using var measured = PerformanceTrace.Span("full-project-traversal");
            if (projectId.Scope != ConnectionScope.From(context) || string.IsNullOrWhiteSpace(projectId.NodeId))
                return new(ProjectReadOutcome.Failed, null,
                    [new(ReadProblemKind.ScopeMismatch, "project", FailureKind.IdentityChanged)]);
            try
            {
                fieldsComplete = await WalkAsync("fields", ProjectQueries.Fields, projectId.NodeId,
                    ReadProjectFields, value => { AddField(value); return Task.CompletedTask; });
                if (!stopped)
                    itemsComplete = await WalkAsync("items", ProjectQueries.Items, projectId.NodeId,
                        node => { MatchNode(node, projectId.NodeId, "ProjectV2"); return Property(node, "items"); },
                        AddItemAsync);
            }
            catch (OperationCanceledException)
            {
                interruption = ApiOutcome.Cancelled;
                problems.Add(new(ReadProblemKind.Api, "read", FailureKind.Cancelled, ApiOutcome.Cancelled));
            }
            var complete = fieldsComplete && itemsComplete && items.Values.All(item => item.Complete) && problems.Count == 0;
            var outcome = interruption switch
            {
                ApiOutcome.Cancelled => ProjectReadOutcome.Cancelled,
                ApiOutcome.TimedOut => ProjectReadOutcome.TimedOut,
                _ => complete ? ProjectReadOutcome.Complete : project is null ? ProjectReadOutcome.Failed : ProjectReadOutcome.Partial
            };
            if (project is not null)
                project = project with
                {
                    Fields = fields.Values.ToArray(),
                    Issues = new ReadOnlyDictionary<ScopedId, IssueReadModel>(issues),
                    Items = items.Values.Select(item => BuildItem(item, complete)).ToArray(),
                    FieldsComplete = fieldsComplete, ItemsComplete = itemsComplete
                };
            return new(outcome, project, problems.ToArray());
        }

        private JsonElement ReadProjectFields(JsonElement node)
        {
            MatchNode(node, projectId.NodeId, "ProjectV2");
            var owner = Property(node, "owner");
            var metadata = new ProjectReadModel(projectId, Id(owner), Text(owner, "__typename"),
                PositiveInt(node, "number"), SameHostUrl(node, "url"), Text(node, "title", allowEmpty: true),
                [], new Dictionary<ScopedId, IssueReadModel>(), [], false, false, Capability(node));
            if (project is not null && (metadata.OwnerId != project.OwnerId || metadata.Number != project.Number))
                throw new ReadException(ReadProblemKind.IncompleteTraversal);
            project ??= metadata;
            return Property(node, "fields");
        }

        private void AddField(JsonElement node)
        {
            var id = Id(node);
            if (fields.ContainsKey(id)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
            MatchProject(node);
            var type = Text(node, "__typename");
            var dataType = Text(node, "dataType");
            var owner = Boolean(node, "isIssueField") || dataType is "TITLE" or "ASSIGNEES" or "LABELS"
                or "MILESTONE" or "REPOSITORY" or "LINKED_PULL_REQUESTS" or "REVIEWERS" or "ISSUE_TYPE"
                or "CREATED" or "CLOSED" or "UPDATED" or "PARENT_ISSUE" or "SUB_ISSUES_PROGRESS" or "TRACKED_BY" or "TRACKS"
                ? FieldOwner.Issue
                : dataType is "SINGLE_SELECT" or "MULTI_SELECT" or "ITERATION" or "TEXT" or "DATE" or "NUMBER"
                    ? FieldOwner.ProjectItem : FieldOwner.Unknown;
            var options = new List<SelectOption>();
            if (type == "ProjectV2SingleSelectField")
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in Array(node, "options").EnumerateArray())
                {
                    var optionId = Text(option, "id");
                    if (!ids.Add(optionId)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                    options.Add(new(optionId, Text(option, "name", allowEmpty: true)));
                }
            }
            var availability = owner == FieldOwner.ProjectItem && (dataType == "SINGLE_SELECT"
                && type == "ProjectV2SingleSelectField" || dataType is "NUMBER" or "DATE" && type == "ProjectV2Field")
                ? ValueAvailability.Present : ValueAvailability.Unsupported;
            fields.Add(id, new(id, projectId, Text(node, "name", allowEmpty: true), type, dataType, owner,
                options.AsReadOnly(), availability));
        }

        private async Task AddItemAsync(JsonElement node)
        {
            PerformanceTrace.Count("returned-items");
            MatchProject(node);
            var id = Id(node);
            if (items.ContainsKey(id)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
            var item = new ItemBuilder(id, Text(node, "type"), Boolean(node, "isArchived"));
            items.Add(id, item);
            if (item.Type is not ("ISSUE" or "PULL_REQUEST" or "DRAFT_ISSUE" or "REDACTED"))
                item.Kind = ProjectItemKind.Unsupported;
            var content = Optional(node, "content");
            if (item.Type != "REDACTED" && content.ValueKind == JsonValueKind.Object)
            {
                var contentType = Text(content, "__typename");
                item.ContentId = Id(content);
                item.Kind = (item.Type, contentType) switch
                {
                    ("ISSUE", "Issue") => ProjectItemKind.Issue,
                    ("PULL_REQUEST", "PullRequest") => ProjectItemKind.PullRequest,
                    ("DRAFT_ISSUE", "DraftIssue") => ProjectItemKind.Draft,
                    _ => ProjectItemKind.Unsupported
                };
                if (item.Kind == ProjectItemKind.Issue)
                {
                    var repository = Property(content, "repository");
                    var issue = new IssueReadModel(item.ContentId, new(Id(repository), Id(Property(repository, "owner")),
                        Text(repository, "nameWithOwner")), PositiveInt(content, "number"), SameHostUrl(content, "url"),
                        ReadTitle(content), ReadState(content), Capability(content));
                    if (!issues.TryAdd(issue.Id, issue)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                    if (issue.Title.Availability != ValueAvailability.Present || issue.State.Availability != ValueAvailability.Present)
                        problems.Add(new(ReadProblemKind.IncompleteTraversal, "issue"));
                }
            }
            else if (content.ValueKind == JsonValueKind.Undefined)
                problems.Add(new(ReadProblemKind.InvalidResponse, "content"));
            item.Complete = await WalkAsync("values", ProjectQueries.ItemValues, id.NodeId,
                value => { MatchNode(value, id.NodeId, "ProjectV2Item"); MatchProject(value); return Property(value, "fieldValues"); },
                value => { AddValue(item, value); return Task.CompletedTask; }, Optional(node, "fieldValues"));
        }

        private void AddValue(ItemBuilder item, JsonElement node)
        {
            PerformanceTrace.Count("returned-values");
            var type = Text(node, "__typename");
            var valueId = OptionalText(node, "id");
            if (valueId is not null && !valueIds.Add(valueId)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
            var field = Optional(node, "field");
            if (field.ValueKind != JsonValueKind.Object)
            {
                item.Values.Add(new(null, valueId, type,
                    field.ValueKind == JsonValueKind.Undefined ? ValueAvailability.Unsupported : ValueAvailability.Unavailable));
                // Without field identity this observation cannot prove any known field empty.
                problems.Add(new(ReadProblemKind.IncompleteTraversal, "value-field"));
                return;
            }
            MatchProject(field);
            var fieldId = Id(field);
            if (!item.FieldIds.Add(fieldId)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
            if (!fields.TryGetValue(fieldId, out var definition))
            {
                item.Values.Add(new(fieldId, valueId, type, ValueAvailability.NotLoaded));
                problems.Add(new(ReadProblemKind.IncompleteTraversal, "value-definition"));
                return;
            }
            if (definition.Availability == ValueAvailability.Unsupported)
            {
                item.Values.Add(new(fieldId, valueId, type, ValueAvailability.Unsupported));
                return;
            }
            if (definition.DataType is "NUMBER" or "DATE")
            {
                var expected = definition.DataType == "NUMBER" ? "ProjectV2ItemFieldNumberValue" : "ProjectV2ItemFieldDateValue";
                var raw = Optional(node, definition.DataType == "NUMBER" ? "number" : "date");
                string? scalar = null;
                var known = type == expected && raw.ValueKind == JsonValueKind.Null;
                try
                {
                    if (type == expected && (definition.DataType == "NUMBER" ? raw.ValueKind == JsonValueKind.Number : raw.ValueKind == JsonValueKind.String))
                    {
                        scalar = definition.DataType == "NUMBER" ? PlanningScalars.RemoteNumber(raw)
                            : PlanningScalars.Normalize("Date", raw.GetString()!);
                        known = true;
                    }
                }
                catch (Exception e) when (e is FormatException or OverflowException or InvalidOperationException) { known = false; }
                var state = !known ? ValueAvailability.Unavailable : scalar is null ? ValueAvailability.Empty : ValueAvailability.Present;
                if (!known) problems.Add(new(ReadProblemKind.IncompleteTraversal, "scalar"));
                item.Values.Add(new(fieldId, valueId, type, state, Scalar: scalar)); return;
            }
            if (type != "ProjectV2ItemFieldSingleSelectValue")
            {
                item.Values.Add(new(fieldId, valueId, type, ValueAvailability.Unsupported));
                problems.Add(new(ReadProblemKind.IncompleteTraversal, "value-type"));
                return;
            }
            var option = Optional(node, "optionId");
            var availability = option.ValueKind switch
            {
                JsonValueKind.String => ValueAvailability.Present,
                JsonValueKind.Null => ValueAvailability.Empty,
                _ => ValueAvailability.Unavailable
            };
            var optionId = option.ValueKind == JsonValueKind.String ? option.GetString() : null;
            if (optionId is not null && !definition.Options.Any(candidate => candidate.Id == optionId))
                availability = ValueAvailability.Unavailable;
            if (availability == ValueAvailability.Unavailable)
                problems.Add(new(ReadProblemKind.IncompleteTraversal, "option"));
            item.Values.Add(new(fieldId, valueId, type, availability, optionId));
        }

        private ProjectItemReadModel BuildItem(ItemBuilder item, bool complete)
        {
            var kind = item.Kind == ProjectItemKind.Issue && (item.ContentId is null || !issues.ContainsKey(item.ContentId))
                ? ProjectItemKind.Unavailable : item.Kind;
            // Nulls or absent fields from any incomplete read cannot become clear instructions.
            var values = item.Values.Select(value => !complete && value.Availability == ValueAvailability.Empty
                ? value with { Availability = ValueAvailability.Unavailable } : value).ToList();
            foreach (var field in fields.Values.Where(field => !item.FieldIds.Contains(field.Id)))
            {
                var availability = field.Availability == ValueAvailability.Unsupported ? ValueAvailability.Unsupported
                    : kind == ProjectItemKind.Unavailable ? ValueAvailability.Unavailable
                    : kind == ProjectItemKind.Unsupported ? ValueAvailability.NotLoaded
                    : complete && item.Complete ? ValueAvailability.Empty : ValueAvailability.NotLoaded;
                values.Add(new(field.Id, null, field.TypeName, availability));
            }
            return new(item.Id, kind, item.Type, item.ContentId, item.IsArchived, values.AsReadOnly(), item.Complete);
        }

        private async Task<bool> WalkAsync(string stage, string query, string id,
            Func<JsonElement, JsonElement> connection, Func<JsonElement, Task> accept, JsonElement initial = default)
        {
            string? after = null;
            int? total = null;
            var count = 0;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var page = initial;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var trusted = !stopped;
                    if (page.ValueKind == JsonValueKind.Undefined)
                    {
                        if (stopped) return false;
                        progress?.Invoke(new(stage, fields.Count, items.Count, issues.Count));
                        var result = await SendAsync(query, new { id, after });
                        trusted = result.IsSuccess;
                        if (!trusted)
                        {
                            stopped = true;
                            interruption = result.Outcome;
                            problems.Add(new(ReadProblemKind.Api, stage, result.Failure, result.Outcome, result.HttpStatus, result.RetryAfter));
                            // Only GraphQL partial data is a usable failed response, never an HTTP error body.
                            if (result.Failure != FailureKind.GraphQl) return false;
                        }
                        var node = Property(Property(result.Data ?? default, "data"), "node");
                        if (node.ValueKind == JsonValueKind.Null)
                        {
                            problems.Add(new(ReadProblemKind.Api, stage, FailureKind.NotFoundOrInaccessible));
                            stopped = true;
                            return false;
                        }
                        page = connection(node);
                    }
                    var nodes = Array(page, "nodes");
                    foreach (var node in nodes.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await accept(node);
                        count++;
                    }
                    var expected = NonnegativeInt(page, "totalCount");
                    if (total is not null && total != expected) throw new ReadException(ReadProblemKind.IncompleteTraversal);
                    total = expected;
                    var info = Property(page, "pageInfo");
                    var next = Boolean(info, "hasNextPage");
                    var cursor = OptionalText(info, "endCursor");
                    if (count > total || (!next && count != total) || (next && (nodes.GetArrayLength() == 0 || count >= total)))
                        throw new ReadException(ReadProblemKind.IncompleteTraversal);
                    if (next && cursor is null) throw new ReadException(ReadProblemKind.IncompleteTraversal);
                    if (cursor is not null && !cursors.Add(cursor)) throw new ReadException(ReadProblemKind.RepeatedCursor);
                    if (!trusted || stopped) return false;
                    if (!next) return true;
                    after = cursor;
                    page = default;
                }
            }
            catch (ReadException error)
            {
                stopped = true;
                problems.Add(new(error.Kind, stage, FailureKind.InvalidResponse));
                return false;
            }
        }

        private static CapabilityObservation Capability(JsonElement node) => new(Optional(node, "viewerCanUpdate").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }, DateTimeOffset.UtcNow);
        private ScopedId Id(JsonElement node) => new(projectId.Scope, Text(node, "id"));
        private void MatchProject(JsonElement node)
        {
            if (Id(Property(node, "project")) != projectId) throw new ReadException(ReadProblemKind.ScopeMismatch);
        }
        private static void MatchNode(JsonElement node, string id, string type)
        {
            if (Text(node, "id") != id || Text(node, "__typename") != type)
                throw new ReadException(ReadProblemKind.ScopeMismatch);
        }
        private string SameHostUrl(JsonElement node, string name)
        {
            var text = Text(node, name);
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host != projectId.Scope.Host)
                throw new ReadException(ReadProblemKind.ScopeMismatch);
            return text;
        }
        private static ReadValue<string> ReadTitle(JsonElement node)
        {
            var value = Optional(node, "title");
            return value.ValueKind switch
            {
                JsonValueKind.String => new(ValueAvailability.Present, value.GetString()),
                JsonValueKind.Undefined => new(ValueAvailability.NotLoaded),
                _ => new(ValueAvailability.Unavailable)
            };
        }
        private static ReadValue<IssueState?> ReadState(JsonElement node)
        {
            var value = Optional(node, "state");
            return value.ValueKind switch
            {
                JsonValueKind.String when value.GetString() == "OPEN" => new(ValueAvailability.Present, IssueState.Open),
                JsonValueKind.String when value.GetString() == "CLOSED" => new(ValueAvailability.Present, IssueState.Closed),
                JsonValueKind.String => new(ValueAvailability.Unsupported),
                JsonValueKind.Undefined => new(ValueAvailability.NotLoaded),
                _ => new(ValueAvailability.Unavailable)
            };
        }
        private sealed class ItemBuilder(ScopedId id, string type, bool archived)
        {
            public ScopedId Id { get; } = id;
            public string Type { get; } = type;
            public bool IsArchived { get; } = archived;
            public ProjectItemKind Kind { get; set; } = ProjectItemKind.Unavailable;
            public ScopedId? ContentId { get; set; }
            public List<ProjectFieldValue> Values { get; } = [];
            public HashSet<ScopedId> FieldIds { get; } = [];
            public bool Complete { get; set; }
        }
    }

    private sealed class ReadException(ReadProblemKind kind = ReadProblemKind.InvalidResponse) : Exception
    {
        public ReadProblemKind Kind { get; } = kind;
    }
    private static JsonElement Optional(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;
    private static JsonElement Property(JsonElement node, string name)
    {
        var value = Optional(node, name);
        return value.ValueKind != JsonValueKind.Undefined ? value : throw new ReadException();
    }
    private static JsonElement Array(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind == JsonValueKind.Array ? value : throw new ReadException();
    }
    private static string Text(JsonElement node, string name, bool allowEmpty = false)
    {
        var value = OptionalText(node, name);
        return value is not null && (allowEmpty || !string.IsNullOrWhiteSpace(value)) ? value : throw new ReadException();
    }
    private static string? OptionalText(JsonElement node, string name)
    {
        var value = Optional(node, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static bool Boolean(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new ReadException();
    }
    private static int NonnegativeInt(JsonElement node, string name)
    {
        var value = Property(node, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0
            ? number : throw new ReadException();
    }
    private static int PositiveInt(JsonElement node, string name)
    {
        var value = NonnegativeInt(node, name);
        return value > 0 ? value : throw new ReadException();
    }
}
