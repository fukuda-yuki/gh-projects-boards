namespace GhProjectsBoards.Core.Projects;

internal static class ProjectQueries
{
    private const string PageInfo = "totalCount pageInfo { hasNextPage endCursor }";
    private const string FieldReference = "field { ... on ProjectV2FieldCommon { id project { id } } }";
    private const string Values = """
        nodes {
          __typename
          ... on ProjectV2ItemFieldValueCommon { id FIELD_REFERENCE }
          ... on ProjectV2ItemFieldSingleSelectValue { optionId }
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
              id number url title owner { __typename id }
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
    public static readonly string Items = """
        query ProjectItems($id: ID!, $after: String) {
          node(id: $id) {
            __typename
            ... on ProjectV2 {
              id
              items(first: 100, after: $after) {
                PAGE_INFO
                nodes {
                  __typename id type isArchived project { id }
                  content {
                    __typename
                    ... on Node { id }
                    ... on Issue {
                      number url title state
                      repository { id nameWithOwner owner { id } }
                    }
                  }
                  fieldValues(first: 100) { PAGE_INFO VALUES }
                }
              }
            }
          }
        }
        """.Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values).Replace("FIELD_REFERENCE", FieldReference);
    public static readonly string ItemValues = """
        query ProjectItemValues($id: ID!, $after: String) {
          node(id: $id) {
            __typename
            ... on ProjectV2Item {
              id project { id }
              fieldValues(first: 100, after: $after) { PAGE_INFO VALUES }
            }
          }
        }
        """.Replace("PAGE_INFO", PageInfo).Replace("VALUES", Values).Replace("FIELD_REFERENCE", FieldReference);
}

