using System.Text;

namespace GhProjectsBoards.Core.PlanEditor;

// #324: the import template follows the column settings, so it lists exactly the columns the importer accepts.
internal static class PlanCsvTemplate
{
    // The two identity columns a new row needs, and the Issue target of a new row.
    internal const string KeyHeader = "キー", ParentHeader = "親", RepositoryHeader = "リポジトリ";

    internal static byte[] Create(PlanDocument document)
    {
        var headers = new List<string> { KeyHeader, ParentHeader };
        // Two columns can end up with one name (a Project field renamed to a standard name). A header can only
        // name one column, so the first column keeps it and the template stays importable as written.
        var used = new HashSet<string>(headers, StringComparer.Ordinal) { RepositoryHeader };
        foreach (var column in PlanSheetColumns.All(document).Where(c => c.Visible && c.Editability == "編集できる"))
            if (used.Add(column.Name)) headers.Add(column.Name);
        headers.Add(RepositoryHeader);
        return Encoding.UTF8.GetBytes("\uFEFF" + string.Join(',', headers.Select(Field)) + "\r\n");
    }

    // RFC 4180, as the export writes it: a header with a comma, quote or line break is quoted and its quotes double.
    private static string Field(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
