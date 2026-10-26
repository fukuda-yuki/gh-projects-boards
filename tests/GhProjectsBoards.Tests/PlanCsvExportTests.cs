using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.VisualBasic.FileIO;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanCsvExportTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private static PlanDocument Document(params PlanRow[] rows) => new(new(new("github.com", 42), "P1"), new([.. rows], []),
        new([.. rows], new() { StatusDate = Today }));
    private static PlanDocument Columns(PlanDocument document, params string[] keys) => document with {
        State = document.State with { Settings = document.State.Settings with { SheetColumns = [
            .. keys.Select(key => new PlanSheetColumnChoice(key, true, null)),
            .. PlanSheetColumns.All(document).Where(c => !keys.Contains(c.Key)).Select(c => new PlanSheetColumnChoice(c.Key, false, null))] } }
    };
    private static string[][] Read(byte[] bytes)
    {
        using var parser = new TextFieldParser(new MemoryStream(bytes), Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        return rows.ToArray();
    }

    [Test]
    public void PartialSummaryExportsKnownEffortAndNoStateMark()
    {
        var document = Columns(Document(new("P", "Parent", "acme/repo"),
            new PlanRow("C", "Known", "acme/repo") { Parent = "P", Estimate = 120, Actual = 8 },
            new PlanRow("N", "Missing", "acme/repo") { Parent = "P" }), "Estimate", "Remaining", "Actual", "Indicator");
        var rows = Read(PlanCsvExport.Create(document, Today));
        // #264: the missing-estimate badge is part of the 見積 h cell, not a 状態 mark, so 状態 exports empty.
        Assert.That(rows[1].Take(5), Is.EqualTo(new[] { "新1", "120", "112", "8", "" }));
    }

    [Test]
    public void HeadersFollowVisibleOrderAndDisplayNamesWithFixedIdentityColumns()
    {
        var document = Columns(Document(new PlanRow("I1", "設計", "acme/repo")), "Actual", "Id", "Title", "Repository");
        document = document with { State = document.State with { Settings = PlanSheetColumns.Change(document, "Title", name: "作業名", rename: true) } };
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows[0], Is.EqualTo(new[] { "ID", "実績 h", "作業名", "リポジトリ", "要求事項", "リポジトリ", "Issue 番号", "URL", "未発行" }));
        Assert.That(rows[1], Is.EqualTo(new[] { "新1", "", "設計", "acme/repo", "", "acme/repo", "", "", "" }));
    }

    [Test]
    public void AllRowsKeepPlanOrderAndIssueIdentityIncludingSummaryAndLocalTask()
    {
        var local = PlanRow.New("新しい作業", "acme/repo");
        var document = Columns(Document(new("I2", "要求事項", "acme/repo"), new PlanRow("I1", "子タスク", "acme/repo") { Parent = "I2" }), "Title");
        document = document with { State = document.State with { Rows = document.State.Rows.Add(local) }, Sync = new() {
            IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I1", new("acme/repo#123", "https://github.com/acme/repo/issues/123")) } };
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows, Has.Length.EqualTo(4));
        Assert.That(rows[1], Is.EqualTo(new[] { "新1", "要求事項", "", "acme/repo", "", "", "" }));
        Assert.That(rows[2], Is.EqualTo(new[] { "#123", "子タスク", "要求事項", "acme/repo", "123", "https://github.com/acme/repo/issues/123", "" }));
        Assert.That(rows[3], Is.EqualTo(new[] { "新2", "新しい作業", "", "acme/repo", "", "", "新規" }));
    }

    [Test]
    public void RequirementColumnNamesTheTopLevelAncestorAtEveryDepth()
    {
        var document = Columns(Document(new("R1", "=要求", "acme/repo"), new PlanRow("S1", "中間", "acme/repo") { Parent = "R1" },
            new PlanRow("T1", "孫タスク", "acme/repo") { Parent = "S1" }, new PlanRow("T2", "単独タスク", "acme/repo")), "Title");
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows.Skip(1).Select(r => r[2]), Is.EqualTo(new[] { "", "'=要求", "'=要求", "" }));
    }

    [Test]
    public void UnpublishedUsesReviewNamesIncludingHiddenAndRenamedFields()
    {
        var document = Columns(Document(new PlanRow("I1", "設計", "acme/repo") { Estimate = 16 }), "Title");
        document = document with { State = document.State with { Rows = [document.State.Rows[0] with { Title = "変更", Estimate = 20, Actual = 2.5m }],
            Settings = PlanSheetColumns.Change(document, "Actual", name: "消化工数", rename: true) } };
        Assert.That(Read(PlanCsvExport.Create(document, Today))[1][^1], Is.EqualTo("タスク名, 見積工数, 消化工数"));
    }

    [Test]
    public void ValuesIncludeCalculatedRemainingSummaryRollupsIsoDatesAndSheetLists()
    {
        var document = Columns(Document(new("I1", "要求事項", "acme/repo"), new PlanRow("I2", "設計", "acme/repo") {
            Parent = "I1", Estimate = 16, Actual = 2.5m, Start = Today, End = Today.AddDays(1),
            Assignees = ["U1", "U2"], Labels = ["設計", "確認"], Predecessors = ["outside"] }),
            "Title", "Estimate", "Remaining", "Actual", "Start", "End", "Assignees", "Labels", "Predecessors");
        document = document with { State = document.State with { Settings = document.State.Settings with { People = [new("U1", "Alice", 100, null)] } },
            Sync = new() { PeopleNames = ImmutableDictionary<string, string>.Empty.Add("U2", "Bob") } };
        using var culture = new CultureScope("fr-FR");
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows[1].Skip(2).Take(5), Is.EqualTo(new[] { "16", "13.5", "2.5", "2026-10-13", "2026-10-14" }));
        Assert.That(rows[2].Skip(2).Take(8), Is.EqualTo(new[] { "16", "13.5", "2.5", "2026-10-13", "2026-10-14", "Alice, Bob", "設計, 確認", "計画外" }));
    }

    [TestCase(ValueAvailability.Empty, "null", "")]
    [TestCase(ValueAvailability.Unavailable, null, "取得できません")]
    [TestCase(ValueAvailability.Unsupported, null, "取得できません")]
    public void UnreadExtraValueIsDistinctFromEmpty(ValueAvailability availability, string? json, string expected)
    {
        var row = new PlanRow("I1", "設計", "acme/repo") { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty.Add("Project:F1", new(availability, json)) };
        var document = Document(row);
        document = Columns(document with { Baseline = document.Baseline with { Columns = [new("F1", "追加日付", "DATE")] } }, "Title", "Project:F1");
        Assert.That(Read(PlanCsvExport.Create(document, Today))[1][2], Is.EqualTo(expected));
    }

    [TestCase("=SUM(A1)")]
    [TestCase("+123")]
    [TestCase("-3")]
    [TestCase("@name")]
    [TestCase("\tname")]
    [TestCase("\rname")]
    public void TextFormulaPrefixesAreNeutralizedButNegativeNumberIsNot(string title)
    {
        var row = new PlanRow("I1", title, "acme/repo") { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty.Add("Project:F1", new(ValueAvailability.Present, "-3")) };
        var document = Document(row);
        document = Columns(document with { Baseline = document.Baseline with { Columns = [new("F1", "=number", "NUMBER")] } }, "Title", "Project:F1");
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows[0][2], Is.EqualTo("'=number"));
        Assert.That(rows[1][1], Is.EqualTo("'" + title));
        Assert.That(rows[1][2], Is.EqualTo("-3"));
    }

    [Test]
    public void Utf8BomCrLfAndRfc4180PreserveJapaneseQuotesAndEmbeddedNewlines()
    {
        var document = Columns(Document(new PlanRow("I1", "設計,\"確認\"\r\n次\n最後", "acme/repo")), "Title");
        var bytes = PlanCsvExport.Create(document, Today);
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("\uFEFFID,タスク名,要求事項,リポジトリ,Issue 番号,URL,未発行\r\n新1,\"設計,\"\"確認\"\"\r\n次\n最後\",,acme/repo,,,\r\n"));
    }

    [Test]
    public void EmptyProjectStillExportsHeaders()
        => Assert.That(Read(PlanCsvExport.Create(Columns(Document(), "Title"), Today)), Has.Length.EqualTo(1));

    [TestCase("ordinary", "")]
    [TestCase("closed", "完了")]
    [TestCase("changed", "")]
    [TestCase("unverified", "GitHub で未確認 — 最新の情報に更新で確認")]
    [TestCase("failed", "発行に失敗: GitHubへの反映を確認できません")]
    public void IndicatorExportsReadableStateWords(string state, string expected)
    {
        var row = new PlanRow("I1", "設計", "acme/repo") { Closed = state == "closed" };
        var document = Columns(Document(row), "Indicator", "Title");
        document = document with {
            State = document.State with { Rows = [state == "changed" ? row with { Title = "変更" } : row] },
            Sync = new() { Unverified = state == "unverified" ? ["I1"] : [], Failures = state == "failed" ? [new("I1", PlanField.Title, "VerificationMismatch")] : [] } };
        Assert.That(Read(PlanCsvExport.Create(document, Today))[1][1], Is.EqualTo(expected));
    }

    [Test]
    public void HiddenMarkLeavesTheExportedIndicatorCellWithoutIt()
    {
        var row = new PlanRow("I1", "遅れ", "acme/repo") { Start = Today.AddDays(-3), End = Today.AddDays(-3), Remaining = 8 };
        var document = Columns(Document(row), "Indicator", "Title");
        Assert.That(Read(PlanCsvExport.Create(document, Today))[1][1], Does.Contain("終了日"));
        // #251 D2: the export shows what the plan sheet shows, so a hidden mark is absent here too.
        Assert.That(Read(PlanCsvExport.Create(document, Today, new PlanMarkVisibility(Overdue: false)))[1][1], Does.Not.Contain("終了日"));
    }

    [Test]
    public void SummaryExportsBaselineOnlyCellsAndExtraScalarsUseTheirDisplayedValues()
    {
        var parent = new PlanRow("I1", "要求事項", "acme/repo") { Labels = ["元のラベル"], Status = "Todo" };
        var child = new PlanRow("I2", "設計", "acme/repo") { Parent = "I1", Predecessors = ["I3"],
            ExtraValues = new Dictionary<string, PlanExtraValue> {
                ["Project:D"] = new(ValueAvailability.Present, "\"2026-10-14\""),
                ["Project:T"] = new(ValueAvailability.Present, "\"=SUM(A1)\""),
                ["Project:S"] = new(ValueAvailability.Present, "\"選択肢\""),
                ["Project:I"] = new(ValueAvailability.Present, "\"今週\"") }.ToImmutableDictionary() };
        var document = Document(parent, child, new("I3", "準備", "acme/repo"));
        document = Columns(document with { Baseline = document.Baseline with { Columns = [new("D", "追加日付", "DATE"), new("T", "説明", "TEXT"), new("S", "選択", "SINGLE_SELECT"), new("I", "期間", "ITERATION")] },
            State = document.State with { Rows = document.State.Rows.SetItem(0, parent with { Labels = ["隠れた編集"], Status = "Done" }) } },
            "Title", "Labels", "Status", "Predecessors", "Project:D", "Project:T", "Project:S", "Project:I");
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows[1].Skip(2).Take(2), Is.EqualTo(new[] { "元のラベル", "Todo" }));
        Assert.That(rows[2].Skip(4).Take(5), Is.EqualTo(new[] { "新3", "2026-10-14", "'=SUM(A1)", "選択肢", "今週" }));
    }

    [Test]
    public void VersionSizedDocumentExports1040RowsUnderTwoSeconds()
    {
        var rows = Enumerable.Range(0, 40).SelectMany(group => new[] { new PlanRow("P" + group, "要求事項 " + group, "acme/repo") }
            .Concat(Enumerable.Range(1, 25).Select(task => new PlanRow($"T{group}-{task}", $"設計 {group}-{task}", "acme/repo") {
                Parent = "P" + group, Estimate = 16, Actual = 2.5m, Assignees = ["U1"] }))).ToArray();
        var document = Document(rows);
        document = document with { State = document.State with { Settings = document.State.Settings with { People = [new("U1", "Alice", 100, null)] } } };
        var timer = Stopwatch.StartNew();
        var bytes = PlanCsvExport.Create(document, Today);
        timer.Stop();
        Assert.That(Read(bytes), Has.Length.EqualTo(1041));
        TestContext.Out.WriteLine($"1,040 rows: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public void RecordColumnsExportTheIssueHistoryDatesAndStayBlankWithoutTheRecord()
    {
        // #308 R8: 着手日 and 完了日 print as yyyy-MM-dd from PlanSync.IssueHistory, never from a field.
        var start = new DateOnly(2026, 10, 24);
        var finish = new DateOnly(2026, 10, 27);
        var recorded = new PlanRow("I1", "記録", "acme/repo") { Closed = true, CloseDate = finish };
        var absent = new PlanRow("I2", "未記録", "acme/repo");
        var document = Columns(Document(recorded, absent), "Title", "ActualStart", "ActualFinish");
        var history = new PlanIssueHistory(
            [new(PlanHistoryEventKind.StatusChanged, LocalStamp(start), "fukuda-yuki", false, "Ready", "In progress"),
                new(PlanHistoryEventKind.Closed, LocalStamp(finish), "fukuda-yuki", false, StateReason: "COMPLETED")],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        document = document with { Sync = new() { IssueHistory = ImmutableDictionary<string, PlanIssueHistory>.Empty.Add("I1", history) } };
        var rows = Read(PlanCsvExport.Create(document, Today));
        Assert.That(rows[0].Skip(2).Take(2), Is.EqualTo(new[] { "着手日", "完了日" }));
        Assert.That(rows[1].Skip(2).Take(2), Is.EqualTo(new[] { "2026-10-24", "2026-10-27" }));
        Assert.That(rows[2].Skip(2).Take(2), Is.EqualTo(new[] { "", "" }), "A row without the record exports blank.");
    }

    private static DateTimeOffset LocalStamp(DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;
        internal CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
