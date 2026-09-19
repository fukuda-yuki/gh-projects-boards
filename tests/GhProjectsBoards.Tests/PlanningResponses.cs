using System.Text.Json.Nodes;

namespace GhProjectsBoards.Tests;

// Only the external boundary is substituted; production readers, drafts and Apply
// still parse, validate, persist, dispatch and independently read back these values.
internal static class PlanningResponses
{
    public static void Augment(JsonNode response, JsonObject? scalars = null)
    {
        foreach (var node in response["data"]!.AsObject().Select(p => p.Value).Where(n => n is not null).ToArray())
        {
            if (node!["fields"] is { } definitions)
            {
                var nodes = definitions["nodes"]!.AsArray();
                foreach (var role in new[] { "Estimate", "Remaining", "Actual", "Start", "Finish" })
                {
                    var type = role is "Start" or "Finish" ? "DATE" : "NUMBER";
                    nodes.Add(System.Text.Json.JsonSerializer.SerializeToNode(ProjectReaderTests.Field(node["id"]!.ToString(), "F-" + role, role, type)));
                }
                definitions["totalCount"] = nodes.Count;
            }
            var items = node["items"] is { } list ? list["nodes"]!.AsArray().ToArray() : node["fieldValues"] is not null ? [node] : [];
            foreach (var item in items)
            {
                item!["content"]!["state"] = "OPEN";
                var values = item["fieldValues"]!; var nodes = values["nodes"]!.AsArray();
                foreach (var role in new[] { "Estimate", "Remaining", "Actual", "Start", "Finish" })
                {
                    var field = "F-" + role; var key = item["id"] + "/" + field;
                    JsonNode? value = scalars?.ContainsKey(key) == true ? scalars[key]?.DeepClone() : null;
                    if (value is null) continue;
                    var date = role is "Start" or "Finish";
                    nodes.Add(new JsonObject { ["__typename"] = date ? "ProjectV2ItemFieldDateValue" : "ProjectV2ItemFieldNumberValue",
                        ["id"] = "V-" + key, [date ? "date" : "number"] = value,
                        ["field"] = new JsonObject { ["id"] = field, ["project"] = new JsonObject { ["id"] = item["project"]!["id"]!.ToString() } } });
                }
                values["totalCount"] = nodes.Count;
            }
        }
    }
}
