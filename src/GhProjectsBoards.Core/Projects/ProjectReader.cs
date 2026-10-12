using System.Collections.ObjectModel;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed class ProjectReader(GhConnectionService service, GhConnectionService.OperationLease? lease = null)
{
    public Task<ProjectReadResult> ReadAsync(ConnectionContext context, ScopedId project,
        CancellationToken cancellationToken = default, IProgress<RemoteProgress>? progress = null)
        => new ReadSession(service, context, project, cancellationToken, progress, lease).RunAsync();

    private sealed class ReadSession(GhConnectionService service, ConnectionContext context,
        ScopedId projectId, CancellationToken cancellationToken, IProgress<RemoteProgress>? progress, GhConnectionService.OperationLease? lease = null)
    {
        private readonly List<ReadProblem> problems = [];
        private readonly Dictionary<ScopedId, ProjectFieldDefinition> fields = [];
        private readonly Dictionary<ScopedId, IssueReadModel> issues = [];
        private readonly Dictionary<ScopedId, ItemBuilder> items = [];
        private readonly HashSet<string> valueIds = new(StringComparer.Ordinal);
        private ProjectReadModel? project;
        private bool fieldsComplete, itemsComplete, stopped;
        private bool milestonesComplete = true;
        private readonly Dictionary<ScopedId, MilestoneReadModel> milestones = [];
        private int undeliveredItems;
        private readonly List<RepositoryLabel> labels = [];
        private readonly Dictionary<ScopedId, (string Cursor, int Total)> labelPages = [];
        private readonly Dictionary<ScopedId, HashSet<string>> labelCursors = [];
        private ApiOutcome? interruption;

        private async Task<ApiResult> SendAsync(string query, object variables)
        {
            if (lease is not null)
            {
                var leaserequest = ApiRequest.GraphQl(query.Insert(query.LastIndexOf('}'), " viewer { databaseId } "), variables);
                var response = await lease.SendAsync(leaserequest, cancellationToken);
                if (!response.IsSuccess) return response;
                if (response.Data is not { } leasebody || !leasebody.TryGetProperty("data", out var leasedata) ||
                    !leasedata.TryGetProperty("viewer", out var leaseviewer) || !leaseviewer.TryGetProperty("databaseId", out var leaseid) ||
                    !leaseid.TryGetInt64(out var actual) || actual != context.ViewerId)
                {
                    context.Invalidate();
                    return new(ApiOutcome.Failed, FailureKind.IdentityChanged);
                }
                return response;
            }
            return await service.SendAsync(context, ApiRequest.GraphQl(query, variables), cancellationToken);
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
                    itemsComplete = await WalkAsync("items", (lease is null ? ProjectQueries.Items : ProjectQueries.Items.Replace("parent { id }", "parent { id } subIssues(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{id}}")), projectId.NodeId,
                        node => { MatchNode(node, projectId.NodeId, "ProjectV2"); return Property(node, "items"); },
                        AddItemAsync);
                await ReadValuePages();
                await ReadLabelPages();
                foreach (var repository in issues.Values.Select(i => i.Repository.Id).Distinct())
                {
                    if (stopped) { milestonesComplete = false; break; }
                    milestonesComplete &= await WalkAsync("milestones", ProjectQueries.Milestones, repository.NodeId,
                        node => { MatchNode(node, repository.NodeId, "Repository"); return Property(node, "milestones"); },
                        node => { var milestone = ReadMilestone(node, repository);
                            if (!milestones.TryAdd(milestone.Id, milestone)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                            return Task.CompletedTask; });
                }
                foreach (var repository in issues.Values.Select(i => i.Repository.Id).Distinct())
                    milestonesComplete &= await WalkAsync("labels", ProjectQueries.Labels, repository.NodeId,
                        node => { MatchNode(node, repository.NodeId, "Repository"); return Property(node, "labels"); },
                        node => { labels.Add(new(repository, Text(node, "id"), Text(node, "name"))); return Task.CompletedTask; });
                if (milestonesComplete && !stopped)
                    foreach (var issue in issues.Values)
                        if (issue.Milestone.Value is { } member && (!milestones.TryGetValue(member.Id, out var catalog) || member != catalog))
                        { milestonesComplete = false; problems.Add(new(ReadProblemKind.ConcurrentChange, "milestones")); }
            }
            catch (ReadException error)
            {
                stopped = true;
                problems.Add(new(error.Kind, "read", FailureKind.InvalidResponse));
            }
            catch (OperationCanceledException)
            {
                interruption = ApiOutcome.Cancelled;
                problems.Add(new(ReadProblemKind.Api, "read", FailureKind.Cancelled, ApiOutcome.Cancelled));
            }
            var complete = fieldsComplete && itemsComplete && milestonesComplete && items.Values.All(item => item.Complete) && problems.Count == 0;
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
                    FieldsComplete = fieldsComplete, ItemsComplete = itemsComplete, UndeliveredItemCount = undeliveredItems,
                    Labels = labels.ToArray(), MilestonesComplete = milestonesComplete && !stopped, Milestones = complete ? milestones.Values.ToArray() : []
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
                && type == "ProjectV2SingleSelectField" || dataType is "NUMBER" or "DATE" or "TEXT" && type == "ProjectV2Field" || dataType == "ITERATION" && type == "ProjectV2IterationField")
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
                        ReadTitle(content), ReadState(content), Capability(content), await ReadNativeAsync(content)) { ClosedAt = ReadClosedAt(content),
                            Labels = ReadIssueLabels(content),
                            Milestone = Property(content, "milestone").ValueKind == JsonValueKind.Null ? new(ValueAvailability.Empty)
                                : new(ValueAvailability.Present, ReadMilestone(Property(content, "milestone"), Id(repository))) };
                    if (!issues.TryAdd(issue.Id, issue)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                    if (issue.Title.Availability != ValueAvailability.Present || issue.State.Availability != ValueAvailability.Present
                        || issue.State.Value == IssueState.Closed && (issue.ClosedAt.Availability != ValueAvailability.Present || issue.ClosedAt.Value is null))
                        problems.Add(new(ReadProblemKind.IncompleteTraversal, "issue"));
                }
            }
            else if (content.ValueKind == JsonValueKind.Undefined)
                problems.Add(new(ReadProblemKind.InvalidResponse, "content"));
            // An inaccessible item is still a member of the Project connection. Its
            // hidden field connection cannot establish empty values or block other items.
            if (item.Kind == ProjectItemKind.Unavailable && content.ValueKind == JsonValueKind.Null)
            {
                item.Complete = true;
                return;
            }
            try { AcceptValuePage(item, Property(node, "fieldValues")); }
            catch (ReadException error) {
                stopped = true;
                problems.Add(new(error.Kind, "values", FailureKind.InvalidResponse));
            }
        }

        private void AcceptValuePage(ItemBuilder item, JsonElement page)
        {
            var total = NonnegativeInt(page, "totalCount");
            if (item.ValueTotal is not null && item.ValueTotal != total) throw new ReadException(ReadProblemKind.ConcurrentChange);
            item.ValueTotal = total;
            var nodes = Array(page, "nodes");
            foreach (var node in nodes.EnumerateArray()) { AddValue(item, node); item.ValueCount++; }
            var info = Property(page, "pageInfo");
            var next = Boolean(info, "hasNextPage");
            var cursor = OptionalText(info, "endCursor");
            if (item.ValueCount > total || !next && item.ValueCount != total || next && (nodes.GetArrayLength() == 0 || item.ValueCount >= total || cursor is null))
                throw new ReadException(ReadProblemKind.IncompleteTraversal);
            if (cursor is not null && !item.ValueCursors.Add(cursor)) throw new ReadException(ReadProblemKind.RepeatedCursor);
            item.NextValueCursor = next ? cursor : null;
            item.Complete = !next && !stopped;
        }

        private async Task ReadValuePages()
        {
            try {
                while (!stopped)
                {
                    var batch = items.Values.Where(i => i.NextValueCursor is not null).Take(20).ToArray();
                    if (batch.Length == 0) return;
                    var variables = new Dictionary<string, object?>();
                    for (var i = 0; i < batch.Length; i++) {
                        variables["id" + i] = batch[i].Id.NodeId;
                        variables["after" + i] = batch[i].NextValueCursor;
                    }
                    var arguments = string.Join(",", Enumerable.Range(0, batch.Length).Select(i => "$id" + i + ":ID!,$after" + i + ":String!"));
                    var query = "query ProjectItemValues(" + arguments + "){" + string.Join(" ", Enumerable.Range(0, batch.Length).Select(ProjectQueries.ItemValueSelection)) + "}";
                    var response = await SendAsync(query, variables);
                    if (!response.IsSuccess) {
                        stopped = true; interruption = response.Outcome;
                        problems.Add(new(ReadProblemKind.Api, "values", response.Failure, response.Outcome, response.HttpStatus, response.RetryAfter));
                        if (response.Failure != FailureKind.GraphQl) return;
                    }
                    var data = Property(response.Data ?? default, "data");
                    for (var i = 0; i < batch.Length; i++) {
                        var node = Property(data, "v" + i);
                        MatchNode(node, batch[i].Id.NodeId, "ProjectV2Item"); MatchProject(node);
                        AcceptValuePage(batch[i], Property(node, "fieldValues"));
                    }
                }
            }
            catch (ReadException error) {
                stopped = true;
                problems.Add(new(error.Kind, "values", FailureKind.InvalidResponse));
            }
        }

        private ReadValue<NativeLabel[]> ReadIssueLabels(JsonElement content)
        {
            var id=Id(content);
            var connection=Property(content,"labels");
            var values=Array(connection,"nodes").EnumerateArray().Select(n=>new NativeLabel(Text(n,"id"),Text(n,"name"))).ToArray();
            var total=Property(connection,"totalCount").GetInt32();
            if (values.Select(l=>l.Id).Distinct().Count()!=values.Length || total<values.Length) throw new ReadException(ReadProblemKind.InvalidResponse);
            if (Boolean(Property(connection,"pageInfo"),"hasNextPage"))
            {
                var cursor=Text(Property(connection,"pageInfo"),"endCursor");
                labelPages.Add(id,(cursor,total)); labelCursors.Add(id,[cursor]);
            }
            else if (total!=values.Length) throw new ReadException(ReadProblemKind.IncompleteTraversal);
            return new(values.Length==0?ValueAvailability.Empty:ValueAvailability.Present,values);
        }
        private async Task ReadLabelPages()
        {
            while (!stopped && labelPages.Count>0)
            {
                var batch=labelPages.Take(20).ToArray();
                var variables=new Dictionary<string,object>(); var arguments=new List<string>(); var selections=new List<string>();
                for(var i=0;i<batch.Length;i++)
                {
                    variables["id"+i]=batch[i].Key.NodeId; variables["after"+i]=batch[i].Value.Cursor;
                    arguments.Add("$id"+i+":ID!,$after"+i+":String!");
                    selections.Add("l"+i+":node(id:$id"+i+"){__typename ... on Issue{id labels(first:100,after:$after"+i+"){totalCount pageInfo{hasNextPage endCursor} nodes{id name}}}}");
                }
                var response=await SendAsync("query ProjectIssueLabels("+string.Join(",",arguments)+"){"+string.Join(" ",selections)+"}",variables);
                if(!response.IsSuccess || response.Data is not {} body) throw new ReadException(ReadProblemKind.IncompleteTraversal);
                var data=Property(body,"data");
                for(var i=0;i<batch.Length;i++)
                {
                    var id=batch[i].Key; var node=Property(data,"l"+i); MatchNode(node,id.NodeId,"Issue");
                    var connection=Property(node,"labels");
                    if(Property(connection,"totalCount").GetInt32()!=batch[i].Value.Total) throw new ReadException(ReadProblemKind.ConcurrentChange);
                    var added=Array(connection,"nodes").EnumerateArray().Select(n=>new NativeLabel(Text(n,"id"),Text(n,"name"))).ToArray();
                    var all=issues[id].Labels.Value!.Concat(added).ToArray();
                    if(all.Select(l=>l.Id).Distinct().Count()!=all.Length || all.Length>batch[i].Value.Total) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                    issues[id]=issues[id] with {Labels=new(ValueAvailability.Present,all)};
                    if(Boolean(Property(connection,"pageInfo"),"hasNextPage"))
                    {
                        var cursor=Text(Property(connection,"pageInfo"),"endCursor");
                        if(!labelCursors[id].Add(cursor)) throw new ReadException(ReadProblemKind.RepeatedCursor);
                        labelPages[id]=(cursor,batch[i].Value.Total);
                    }
                    else { if(all.Length!=batch[i].Value.Total) throw new ReadException(ReadProblemKind.IncompleteTraversal); labelPages.Remove(id); }
                }
            }
        }
        private MilestoneReadModel ReadMilestone(JsonElement node, ScopedId repository)
        {
            var due = Property(node, "dueOn");
            DateOnly? date = null;
            if (due.ValueKind != JsonValueKind.Null)
            {
                if (due.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(due.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
                    throw new ReadException(ReadProblemKind.InvalidResponse);
                date = DateOnly.FromDateTime(timestamp.UtcDateTime);
            }
            var state = Text(node, "state");
            if (state is not ("OPEN" or "CLOSED")) throw new ReadException(ReadProblemKind.InvalidResponse);
            return new(Id(node), repository, Text(node, "title"), date, state == "CLOSED");
        }

        private async Task<IssuePlanningObservation> ReadNativeAsync(JsonElement issue)
        {
            var id = Id(issue);
            var assignees = new Dictionary<ScopedId, NativePerson>();
            var predecessors = new HashSet<ScopedId>();
            var peopleComplete = await WalkAsync("assignees", ProjectQueries.Assignees, id.NodeId,
                node => { MatchNode(node, id.NodeId, "Issue"); return Property(node, "assignees"); },
                node => { var person = new NativePerson(Id(node), Text(node, "login"));
                    if (!assignees.TryAdd(person.Id, person)) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                    return Task.CompletedTask; }, Property(issue, "assignees"));
            var linksComplete = await WalkAsync("predecessors", ProjectQueries.Predecessors, id.NodeId,
                node => { MatchNode(node, id.NodeId, "Issue"); return Property(node, "blockedBy"); },
                node => { if (!predecessors.Add(Id(node))) throw new ReadException(ReadProblemKind.DuplicateIdentity);
                    return Task.CompletedTask; }, Property(issue, "blockedBy"));
            var parent = Property(issue, "parent");
            var parentValue = parent.ValueKind == JsonValueKind.Null ? new ReadValue<ScopedId>(ValueAvailability.Empty)
                : new ReadValue<ScopedId>(ValueAvailability.Present, Id(parent));
            var children = new List<ScopedId>(); var childrenComplete = true;
            if (lease is not null)
                childrenComplete = await WalkAsync("subIssues", ProjectQueries.SubIssues, id.NodeId,
                    node => { MatchNode(node, id.NodeId, "Issue"); return Property(node, "subIssues"); },
                    node => { var child = Id(node); if (children.Contains(child)) throw new ReadException(ReadProblemKind.DuplicateIdentity); children.Add(child); return Task.CompletedTask; }, Property(issue, "subIssues"));
            return new(assignees.Values.ToArray(), predecessors.ToArray(), parentValue, peopleComplete && linksComplete && childrenComplete) { SubIssues = children.ToArray() };
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
            if (definition.DataType is "TEXT" or "ITERATION")
            {
                var raw = Optional(node, definition.DataType == "TEXT" ? "text" : "title");
                var scalar = raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
                var scalarAvailability = raw.ValueKind == JsonValueKind.Null ? ValueAvailability.Empty : scalar is null ? ValueAvailability.Unavailable : ValueAvailability.Present;
                if (scalarAvailability == ValueAvailability.Unavailable) problems.Add(new(ReadProblemKind.IncompleteTraversal,"scalar"));
                item.Values.Add(new(fieldId,valueId,type,scalarAvailability,Scalar:scalar)); return;
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
                            : PlanningScalars.RemoteDate(raw.GetString()!);
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
                        if (stage == "items" && total is null) progress?.Report(new("プロジェクトの項目を取得"));
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
                    var expected = NonnegativeInt(page, "totalCount");
                    if (total is not null && total != expected) throw new ReadException(ReadProblemKind.ConcurrentChange);
                    total = expected;
                    if (stage == "items" && count == 0) progress?.Report(new("プロジェクトの項目を取得", count, total));
                    var nodes = Array(page, "nodes");
                    foreach (var node in nodes.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await accept(node);
                        count++;
                        if (stage == "items") progress?.Report(new("プロジェクトの項目を取得", count, total));
                    }
                    var info = Property(page, "pageInfo");
                    var next = Boolean(info, "hasNextPage");
                    var cursor = OptionalText(info, "endCursor");
                    // Project totals include items omitted for this viewer. Cursor exhaustion
                    // establishes traversal; omitted members remain unavailable, never empty tasks.
                    if (count > total || stage != "items" && ((!next && count != total) || (next && (nodes.GetArrayLength() == 0 || count >= total))))
                        throw new ReadException(ReadProblemKind.IncompleteTraversal);
                    if (next && cursor is null) throw new ReadException(ReadProblemKind.IncompleteTraversal);
                    if (cursor is not null && !cursors.Add(cursor)) throw new ReadException(ReadProblemKind.RepeatedCursor);
                    if (!trusted || stopped) return false;
                    if (!next) {
                        if (stage == "items") undeliveredItems = expected - count;
                        return true;
                    }
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
        private static ReadValue<DateTimeOffset?> ReadClosedAt(JsonElement node)
        {
            var value = Optional(node, "closedAt");
            return value.ValueKind switch
            {
                JsonValueKind.Null => new(ValueAvailability.Empty),
                JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp) => new(ValueAvailability.Present, timestamp),
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
            public int? ValueTotal { get; set; }
            public int ValueCount { get; set; }
            public string? NextValueCursor { get; set; }
            public HashSet<string> ValueCursors { get; } = new(StringComparer.Ordinal);
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
