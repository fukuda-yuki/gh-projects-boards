using System.Collections.Immutable;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanCsvTemplateTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private static PlanDocument Document(PlanBaseline? baseline = null, params PlanRow[] rows) => new(new(new("github.com", 42), "P1"),
        baseline ?? new([], []), new([.. rows], new() { StatusDate = Today, DefaultRepository = "acme/repo" }));
    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    private static string[] Lines(byte[] bytes) => Text(bytes).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    private static PlanDocument Show(PlanDocument document, params string[] keys) => document with {
        State = document.State with { Settings = document.State.Settings with { SheetColumns = [
            .. PlanSheetColumns.All(document).Select(c => new PlanSheetColumnChoice(c.Key, c.Key is "Id" or "Title" || keys.Contains(c.Key), null))] } }
    };
    private static PlanCsvRepository[] Catalog = [new("acme/repo", new Dictionary<string, string> { ["alice"] = "U1" })];

    [Test]
    public void DefaultSettingsWriteOnlyVisibleEditableColumnsBetweenKeyAndRepository()
    {
        var bytes = PlanCsvTemplate.Create(Document());
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }), "Excel needs the UTF-8 BOM.");
        Assert.That(Text(bytes), Is.EqualTo("キー,親,タスク名,工程,担当者,見積 h,残 h,実績 h,開始日,終了日,先行,リポジトリ\r\n"));
    }

    [Test]
    public void TemplateIsAHeaderOnlyFileEvenWhenThePlanHasRows()
    {
        var document = Document(null, new PlanRow("I1", "設計", "acme/repo"));
        Assert.That(Lines(PlanCsvTemplate.Create(document)), Has.Length.EqualTo(1));
    }

    [Test]
    public void ShownEditableColumnsFollowSettingsOrderAndNamesWhileReadOnlyAndComputedStayOut()
    {
        var field = new PlanColumnDefinition("F1", "優先度", "SINGLE_SELECT") { Options = ["高", "低"] };
        var iteration = new PlanColumnDefinition("F2", "期間", "ITERATION");
        var document = Show(Document(new([], [field, iteration])),
            "Title", "Phase", "Assignees", "Estimate", "Remaining", "Actual", "Start", "End", "Predecessors",
            "Project:F1", "Project:F2", "Status", "Labels", "MilestoneTitle", "Id", "ActualStart", "ForecastEnd", "Repository");
        document = document with { State = document.State with { Settings = PlanSheetColumns.Change(document, "Project:F1", name: "優先度（高）", rename: true) } };
        Assert.That(Text(PlanCsvTemplate.Create(document)),
            Is.EqualTo("キー,親,タスク名,工程,担当者,見積 h,残 h,実績 h,開始日,終了日,先行,ステータス,優先度（高）,ラベル,マイルストーン,リポジトリ\r\n"));
    }

    [Test]
    public void HiddenEditableColumnsAreNotOffered()
    {
        var document = Show(Document(), "Title", "Estimate");
        Assert.That(Text(PlanCsvTemplate.Create(document)), Is.EqualTo("キー,親,タスク名,見積 h,リポジトリ\r\n"));
    }

    [Test]
    public void HeaderNamesWithCommaOrQuoteAreQuoted()
    {
        var field = new PlanColumnDefinition("F1", "優先度", "TEXT");
        var document = Show(Document(new([], [field])), "Title", "Project:F1");
        document = document with { State = document.State with { Settings = PlanSheetColumns.Change(document, "Project:F1", name: "優先,\"度\"", rename: true) } };
        Assert.That(Text(PlanCsvTemplate.Create(document)), Is.EqualTo("キー,親,タスク名,\"優先,\"\"度\"\"\",リポジトリ\r\n"));
    }

    [Test]
    public void ItsOwnHeaderRowIsAcceptedByTheImporter()
    {
        var field = new PlanColumnDefinition("F1", "優先度", "SINGLE_SELECT") { Options = ["高", "低"] };
        var document = Show(Document(new([], [field])), "Title", "Assignees", "Estimate", "Project:F1", "Labels");
        var header = Lines(PlanCsvTemplate.Create(document))[0];
        var cells = header.Split(',');
        for (var i = 0; i < cells.Length; i++)
            cells[i] = cells[i] switch { "キー" => "a", "タスク名" => "A", "リポジトリ" => "acme/repo", _ => "" };
        var file = PlanCsvImport.Read(Encoding.UTF8.GetBytes(header + "\r\n" + string.Join(',', cells) + "\r\n"));
        Assert.That(file.Errors, Is.Empty, string.Join("; ", file.Errors));
        var preview = PlanCsvImport.Prepare(document, file, Catalog, Today);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(preview.Command!.Rows.Single().Title, Is.EqualTo("A"));
    }

    [Test]
    public void AQuotedHeaderNameImportsThroughTheSameEscaping()
    {
        var field = new PlanColumnDefinition("F1", "説明", "TEXT");
        var document = Show(Document(new([], [field])), "Title", "Project:F1");
        document = document with { State = document.State with { Settings = PlanSheetColumns.Change(document, "Project:F1", name: "優先,度", rename: true) } };
        var header = Lines(PlanCsvTemplate.Create(document))[0];
        Assert.That(header, Does.Contain("\"優先,度\""));
        var file = PlanCsvImport.Read(Encoding.UTF8.GetBytes(header + "\r\n" + "a,,A,補足,acme/repo\r\n"));
        var preview = PlanCsvImport.Prepare(document, file, Catalog, Today);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(PlanExtraEditing.Value(preview.Command!.Rows.Single(), "Project:F1"), Is.EqualTo("補足"));
    }

    [Test]
    public void ValuesStillImportWhenTheShownColumnsAreTheOnesWritten()
    {
        var document = Show(Document(), "Title", "Estimate", "Start", "End", "Assignees");
        var template = Text(PlanCsvTemplate.Create(document));
        var file = PlanCsvImport.Read(Encoding.UTF8.GetBytes(template + "a,,A,alice,8,2026-10-14,2026-10-15,acme/repo\r\n"));
        var preview = PlanCsvImport.Prepare(document, file, Catalog, Today);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        var row = preview.Command!.Rows.Single();
        Assert.That((row.Title, row.Estimate, row.Start, row.End), Is.EqualTo(("A", (decimal?)8, (DateOnly?)new DateOnly(2026, 10, 14), (DateOnly?)new DateOnly(2026, 10, 15))));
        Assert.That(row.Assignees, Is.EqualTo(new[] { "U1" }));
    }
}
