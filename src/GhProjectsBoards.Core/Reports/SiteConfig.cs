using System.Globalization;
using System.Text.Json;

namespace GhProjectsBoards.Core.Reports;

// #271: one site = one configuration file in the data repository. It names the Projects to show and the two fields
// whose values the pages present; everything else (options, order, iteration dates) comes from the record itself.
internal sealed record SiteProjectConfig(
    string Url, string Owner, int Number, string Name, string StateField, string GroupField,
    DateOnly? PeriodStart, DateOnly? PeriodEnd)
{
    internal string FolderName => Owner + "-" + Number.ToString(CultureInfo.InvariantCulture);
}

internal sealed record SiteConfig(string Title, bool Demo, IReadOnlyList<SiteProjectConfig> Projects);

internal static class SiteConfigReader
{
    internal const string DefaultStateField = "Status";
    internal static readonly string DefaultTitle = "プロジェクト進捗";

    internal static SiteConfig Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("site.json を読み込めません: " + ex.Message);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("site.json の形式が違います。");
            var title = Text(root, "title");
            if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("site.json に title がありません。");
            var demo = root.TryGetProperty("demo", out var flag) && flag.ValueKind is JsonValueKind.True;
            if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() == 0)
                throw new InvalidOperationException("site.json に projects がありません。");
            var list = new List<SiteProjectConfig>();
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var project in projects.EnumerateArray())
            {
                var name = Text(project, "name");
                var label = string.IsNullOrWhiteSpace(name) ? $"projects[{index}]" : name!;
                var url = Text(project, "url");
                if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException($"プロジェクト {label} の url がありません。");
                var (owner, number) = ParseProjectUrl(url!, label);
                if (!urls.Add(owner + "/" + number.ToString(CultureInfo.InvariantCulture)))
                    throw new InvalidOperationException($"プロジェクト {label} が重複しています: {url}");
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException($"プロジェクト {label} の name がありません。");
                var stateField = Text(project, "stateField");
                var groupField = Text(project, "groupField");
                if (string.IsNullOrWhiteSpace(groupField)) throw new InvalidOperationException($"プロジェクト {label} の groupField がありません。");
                var periodStart = Period(project, "periodStart", label);
                var periodEnd = Period(project, "periodEnd", label);
                if (periodStart is null != periodEnd is null)
                    throw new InvalidOperationException($"プロジェクト {label} の期間は periodStart と periodEnd の両方を指定してください。");
                list.Add(new SiteProjectConfig(
                    url!, owner, number, name!, string.IsNullOrWhiteSpace(stateField) ? DefaultStateField : stateField!,
                    groupField!, periodStart, periodEnd));
                index++;
            }
            return new SiteConfig(title!, demo, list);
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static DateOnly? Period(JsonElement project, string name, string label)
    {
        var text = Text(project, name);
        if (text is null) return null;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new InvalidOperationException($"プロジェクト {label} の {name} が日付ではありません: {text}");
    }

    // Personal Projects read https://github.com/users/<owner>/projects/<n>; organisation Projects use orgs/ instead.
    // Neither the host nor a GitHub Enterprise path prefix belongs in the raw folder layout, only the owner and number.
    private static (string Owner, int Number) ParseProjectUrl(string url, string label)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"プロジェクト {label} の URL が不正です: {url}");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var n = 0; n + 3 < segments.Length; n++)
            if (segments[n] is "users" or "orgs" && segments[n + 2] == "projects"
                && int.TryParse(segments[n + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return (segments[n + 1], number);
        throw new InvalidOperationException($"プロジェクト {label} の URL から owner と number を読めません: {url}");
    }
}
