namespace GhProjectsBoards.Core.Projects;

internal static class ProjectQueries
{
    private const string PageInfo = "totalCount pageInfo { hasNextPage endCursor }";
    public static readonly string Milestones = "query ProjectMilestones($id:ID!,$after:String){node(id:$id){__typename ... on Repository{id milestones(first:100,after:$after,states:[OPEN,CLOSED]){" + PageInfo + " nodes{id title dueOn state}}}}}";
    public static readonly string Labels = "query ProjectLabels($id:ID!,$after:String){node(id:$id){__typename ... on Repository{id labels(first:100,after:$after){" + PageInfo + " nodes{id name}}}}}";
    private const string Native = "assignees(first: 100) { totalCount pageInfo { hasNextPage endCursor } nodes { id login } } blockedBy(first: 100) { totalCount pageInfo { hasNextPage endCursor } nodes { id } } parent { id }";
    public static readonly string Assignees = "query PlanningAssignees($id: ID!, $after: String) { node(id: $id) { __typename ... on Issue { id assignees(first:100, after:$after) { " + PageInfo + " nodes { id login } } } } }";
    public static readonly string Predecessors = "query PlanningPredecessors($id: ID!, $after: String) { node(id: $id) { __typename ... on Issue { id blockedBy(first:100, after:$after) { " + PageInfo + " nodes { id } } } } }";
    public static readonly string SubIssues = "query PlanSubIssues($id:ID!,$after:String){node(id:$id){__typename ... on Issue{id subIssues(first:100,after:$after){" + PageInfo + " nodes{id}}}}}";
    // #253: verify one typed login against the row's repository on demand, with the read-only lease. The
    // repository-scoped connection is the assignment-eligibility check. Its query is a substring search on login
    // and name ordered by login (`rob` also returns `a-rob` and a user named Robo, ahead of `rob`), so the app
    // reads matches until it meets the node whose login equals the typed text.
    public static readonly string ResolveAssignee = "query PlanResolveAssignee($owner:String!,$name:String!,$login:String,$after:String){viewer{databaseId} repository(owner:$owner,name:$name){nameWithOwner assignableUsers(first:100,after:$after,query:$login){totalCount nodes{id login} pageInfo{hasNextPage endCursor}}}}";
    private const string FieldReference = "field { ... on ProjectV2FieldCommon { id project { id } } }";
    private const string Values = """
        nodes {
          __typename
          ... on ProjectV2ItemFieldValueCommon { id FIELD_REFERENCE }
          ... on ProjectV2ItemFieldSingleSelectValue { optionId }
          ... on ProjectV2ItemFieldTextValue { text }
          ... on ProjectV2ItemFieldIterationValue { title }
          ... on ProjectV2ItemFieldNumberValue { number }
          ... on ProjectV2ItemFieldDateValue { date }
          ... on ProjectV2ItemFieldLabelValue { FIELD_REFERENCE }
          ... on ProjectV2ItemFieldMilestoneValue { FIELD_REFERENCE }
          ... on ProjectV2ItemFieldRepositoryValue { FIELD_REFERENCE }
          ... on ProjectV2ItemFieldPullRequestValue { FIELD_REFERENCE }
          ... on ProjectV2ItemFieldReviewerValue { FIELD_REFERENCE }
          ... on ProjectV2ItemFieldUserValue { FIELD_REFERENCE }
          ... on ProjectV2ItemIssueFieldValue { FIELD_REFERENCE issueFieldValue { ISSUE_FIELD_VALUE } }
        }
        """;
    // #258: an organization Issue Field value has no ID of its own on the Project item; the value inside it does.
    private const string IssueFieldValue = "__typename ... on IssueFieldDateValue { id value } ... on IssueFieldNumberValue { id value } ... on IssueFieldTextValue { id value } ... on IssueFieldSingleSelectValue { id optionId }";
    // #258: on the Project side an Issue Field reports options: []; its options live on the organization field.
    private const string IssueField = "__typename ... on IssueFieldDate { id } ... on IssueFieldNumber { id } ... on IssueFieldText { id } ... on IssueFieldSingleSelect { id options { id name } }";
    public static readonly string Fields = """
        query ProjectFields($id: ID!, $after: String) {
          node(id: $id) {
            __typename
            ... on ProjectV2 {
              id number url title viewerCanUpdate owner { __typename id }
              fields(first: 100, after: $after) {
                PAGE_INFO
                nodes {
                  __typename
                  ... on ProjectV2FieldCommon { id name dataType isIssueField project { id } }
                  ... on ProjectV2Field { issueField { ISSUE_FIELD } }
                  ... on ProjectV2SingleSelectField { options { id name } issueField { ISSUE_FIELD } }
                }
              }
            }
          }
        }
        """.Replace("PAGE_INFO", PageInfo).Replace("ISSUE_FIELD", IssueField);
    public static readonly string Items = "query ProjectItems($id:ID!,$after:String){node(id:$id){__typename ... on ProjectV2{id items(first:100,after:$after){" + PageInfo + " nodes{id}}}}}";
    // #201: the item-detail selection shared by the complete read and the scoped publication read. The
    // publication read resolves each Issue to its Project items and reads those items in the same request,
    // so its pre-write and verification reads cost one request per batch instead of two.
    private const string ItemDetail = """
        type isArchived project { id }
        content {
          __typename
          ... on Node { id }
          ... on Issue {
            labels(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{id name}} number url title state stateReason closedAt viewerCanUpdate viewerCanSetFields milestone { id title dueOn state } NATIVE
            repository { id nameWithOwner owner { id } }
          }
        }
        fieldValues(first: 100) { PAGE_INFO VALUES }
        """;
    public static readonly string ItemDetails = ("query ProjectItemDetails($ids:[ID!]!) { nodes(ids:$ids) { __typename id ... on ProjectV2Item { " + ItemDetail + " } } }")
        .Replace("NATIVE", Native).Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values)
        .Replace("FIELD_REFERENCE", FieldReference).Replace("ISSUE_FIELD_VALUE", IssueFieldValue);
    // #201: a publication read substitutes the native sub-issue connection into the same detail request,
    // because its lease also carries the sibling order that a child-order write needs.
    public static readonly string PublishItemDetails = ("query PlanPublishItemDetails($ids:[ID!]!) { nodes(ids:$ids) { __typename id ... on Issue { projectItems(first: 100) { " + PageInfo + " nodes { __typename id ... on ProjectV2Item { " + ItemDetail + " } } } } } }")
        .Replace("NATIVE", Native).Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values)
        .Replace("FIELD_REFERENCE", FieldReference).Replace("ISSUE_FIELD_VALUE", IssueFieldValue)
        .Replace("parent { id }", "parent { id } subIssues(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{id}}");

    internal static string ItemValueSelection(int index)
        => ("v" + index + ":node(id:$id" + index + "){__typename ... on ProjectV2Item{id project{id} fieldValues(first:100,after:$after" + index + "){PAGE_INFO VALUES}}}")
            .Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values).Replace("FIELD_REFERENCE", FieldReference).Replace("ISSUE_FIELD_VALUE", IssueFieldValue);
}
