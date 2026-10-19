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
          ... on ProjectV2ItemIssueFieldValue { FIELD_REFERENCE }
        }
        """;
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
                  ... on ProjectV2SingleSelectField { options { id name } }
                }
              }
            }
          }
        }
        """.Replace("PAGE_INFO", PageInfo);
    public static readonly string Items = "query ProjectItems($id:ID!,$after:String){node(id:$id){__typename ... on ProjectV2{id items(first:100,after:$after){" + PageInfo + " nodes{id}}}}}";
    public static readonly string ItemDetails = """
        query ProjectItemDetails($ids:[ID!]!) {
          nodes(ids:$ids) {
            __typename id
            ... on ProjectV2Item {
              type isArchived project { id }
              content {
                __typename
                ... on Node { id }
                ... on Issue {
                  labels(first:100){totalCount pageInfo{hasNextPage endCursor} nodes{id name}} number url title state stateReason closedAt viewerCanUpdate milestone { id title dueOn state } NATIVE
                  repository { id nameWithOwner owner { id } }
                }
              }
              fieldValues(first: 100) { PAGE_INFO VALUES }
            }
          }
        }
        """.Replace("NATIVE", Native).Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values).Replace("FIELD_REFERENCE", FieldReference);
    internal static string ItemValueSelection(int index)
        => ("v" + index + ":node(id:$id" + index + "){__typename ... on ProjectV2Item{id project{id} fieldValues(first:100,after:$after" + index + "){PAGE_INFO VALUES}}}")
            .Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values).Replace("FIELD_REFERENCE", FieldReference);
    // #201: the publication read resolves only the Issues it is about to write to their Project items,
    // so a publish never walks the Project's item connection. ProjectItemDetails then reads those items.
    public static readonly string PublishItems = """
        query PlanPublishItems($ids:[ID!]!) {
          nodes(ids:$ids) {
            __typename id
            ... on Issue {
              projectItems(first: 100) {
                PAGE_INFO
                nodes { id project { id } }
              }
            }
          }
        }
        """.Replace("PAGE_INFO", PageInfo);
}
