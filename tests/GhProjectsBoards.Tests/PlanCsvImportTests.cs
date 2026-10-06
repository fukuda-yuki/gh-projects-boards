using System.Collections.Immutable;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanCsvImportTests
{
    internal const string Header = "キー,タイトル,見積,担当者,先行タスク,親,開始日指定,リポジトリ\n";
    internal static readonly DateOnly Today = new(2026, 10, 5);
    internal static readonly PlanCsvRepository[] Catalog = [new("acme/repo", new Dictionary<string, string> { ["alice"] = "U1" })];
    internal static PlanDocument Empty() => new(new(new("github.com", 42), "P1"), new([], []),
        new([], new() { DefaultRepository = "acme/repo", ProjectStart = Today }));
    internal static PlanCsvFile Read(string body) => PlanCsvImport.Read(Encoding.UTF8.GetBytes(Header + body));
    internal static string HundredRows() => string.Join('\n', Enumerable.Range(0, 10).SelectMany(g =>
        new[] { $"p{g},Group {g},,,,,," }.Concat(Enumerable.Range(1, 9).Select(i =>
            $"t{g}-{i},Task {g}-{i},8,alice,{(i == 1 ? "" : $"t{g}-{i - 1}")},p{g},,"))));

    [TestCase("UTF8"), TestCase("BOM"), TestCase("SJIS")]
    public void ExcelEncodingsPreserveQuotedTitlesAndPhysicalLineNumbers(string encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = Header + "a,\"日本語,\"\"設計\"\"\n次の行\",8,alice,,,,\nb,確認,4,,a,,,";
        var bytes = encoding == "SJIS" ? Encoding.GetEncoding(932).GetBytes(text) : Encoding.UTF8.GetBytes(text);
        if (encoding == "BOM") bytes = [0xef, 0xbb, 0xbf, .. bytes];
        var file = PlanCsvImport.Read(bytes);
        var preview = PlanCsvImport.Prepare(Empty(), file, Catalog, Today);
        Assert.That(preview.Errors, Is.Empty);
        Assert.That(preview.Command!.Rows[0].Title, Is.EqualTo("日本語,\"設計\"\n次の行"));
        Assert.That(file.Rows.Select(r => r.Line), Is.EqualTo(new[] { 2, 4 }));
    }

    [TestCase("a,,8,,,,,", 2, "タイトル")]
    [TestCase("a,A,-1,,,,,", 2, "見積")]
    [TestCase("a,A,one,,,,,", 2, "見積")]
    [TestCase("a,A,8,,,,2026-02-30,", 2, "開始日指定")]
    [TestCase("a,A,8,,missing,,,", 2, "参照")]
    [TestCase("a,A,8,,,missing,,", 2, "参照")]
    [TestCase("a,A,8,,,,,unknown/repo", 2, "リポジトリ")]
    [TestCase("a,A,8,bob,,,,", 2, "担当者")]
    [TestCase("a,A,8,,,,,\na,B,4,,,,,", 3, "キー")]
    [TestCase("a,A,8,,b,,,\nb,B,8,,a,,,", 2, "循環")]
    [TestCase("a,A,8,,,b,,\nb,B,8,,,a,,", 2, "循環")]
    [TestCase("\n\na,,8,,,,,", 4, "タイトル")]
    public void InvalidFileHasLineErrorsAndNoApplicableCommand(string body, int line, string reason)
    {
        var document = Empty();
        var preview = PlanCsvImport.Prepare(document, Read(body), Catalog, Today);
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Any(e => e.Line == line && e.Reason.Contains(reason)), Is.True,
            string.Join("; ", preview.Errors));
        Assert.That(document.State.Rows, Is.Empty);
    }

    [TestCase("タイトル,見積\nA,8", "キー")]
    [TestCase("キー,タイトル,見積,見積\na,A,8,8", "重複")]
    [TestCase("キー,タイトル,見積,unknown\na,A,8,x", "列")]
    public void InvalidHeadersReportLineOne(string text, string reason)
    {
        var file = PlanCsvImport.Read(Encoding.UTF8.GetBytes(text));
        Assert.That(file.Errors.Any(e => e.Line == 1 && e.Reason.Contains(reason)), Is.True);
    }

    [Test]
    public void FileKeysAndExistingIssueNumbersResolveToStableIdentities()
    {
        var old = new PlanRow("I1", "Existing", "acme/repo");
        var parent = new PlanRow("I2", "Parent", "acme/repo");
        var document = Empty() with { Baseline = new([old, parent], []), State = Empty().State with { Rows = [old, parent] },
            Sync = new() { IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I1", new("acme/repo#7", "https://github.com/acme/repo/issues/7"))
                .Add("I2", new("acme/repo#8", "https://github.com/acme/repo/issues/8")) } };
        var preview = PlanCsvImport.Prepare(document, Read("a,A,8,alice,#7,,,\nb,B,4,alice,a,8,,\nc,C,4,,acme/repo#7,,,"), Catalog, Today);
        Assert.That(preview.Errors, Is.Empty);
        var rows = preview.Command!.Rows;
        Assert.That(rows[0].Predecessors, Is.EqualTo(new[] { "I1" }));
        Assert.That(rows[1].Predecessors, Is.EqualTo(new[] { rows[0].Identity }));
        Assert.That(rows[1].Parent, Is.EqualTo("I2"));
        Assert.That(rows[2].Predecessors, Is.EqualTo(new[] { "I1" }));
    }

    [Test]
    public void ExistingParentAsPredecessorRejectsInheritedCycleAtImportedLine()
    {
        var old = new PlanRow("I1", "Existing parent", "acme/repo");
        var document = Empty() with { Baseline = new([old], []), State = Empty().State with { Rows = [old] },
            Sync = new() { IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I1", new("acme/repo#7", "https://github.com/acme/repo/issues/7")) } };
        var result = PlanCsvImport.Prepare(document, Read("a,A,8,alice,#7,7,,"), Catalog, Today);
        Assert.That(result.Command, Is.Null);
        Assert.That(result.Errors.Single().Line, Is.EqualTo(2));
        Assert.That(result.Errors.Single().Reason, Does.Contain("循環"));
    }

    [Test]
    public async Task HundredRowsScheduleAndPersistAsOneUndoIncludingPeopleAndDuplicateWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-csv-" + Guid.NewGuid().ToString("N"));
        var session = await PlanSession.CreateAsync(new(root), Empty(), Today);
        try
        {
            var file = Read(HundredRows());
            var preview = PlanCsvImport.Prepare(session.Document, file, Catalog, Today);
            Assert.That(preview.Errors, Is.Empty);
            Assert.That((await session.Execute(preview.Command!, Today)).Succeeded, Is.True);
            Assert.That(session.Document.State.Rows.Length, Is.EqualTo(100));
            Assert.That(session.UndoCount, Is.EqualTo(1));
            Assert.That(PlanOperations.Schedule(session.Document, Today).All(r => r.Start.Value.HasValue && r.End.Value.HasValue), Is.True);
            Assert.That(session.Document.State.Settings.People.Single().Identity, Is.EqualTo("U1"));
            var reopened = (await PlanSession.OpenAsync(new(root), session.Document.Project, Today)).Session!;
            var duplicate = PlanCsvImport.Prepare(reopened.Document, file, Catalog, Today);
            Assert.That(duplicate.IsDuplicate, Is.True);
            Assert.Throws<ArgumentException>(() => reopened.Execute(duplicate.Command!, Today));
            await reopened.Execute(duplicate.Command! with { AllowDuplicateCsv = true }, Today);
            Assert.That(reopened.Document.State.Rows.Length, Is.EqualTo(200));
            await reopened.Undo(Today);
            Assert.That(reopened.Document.State.Rows.Length, Is.EqualTo(100));
            await reopened.Undo(Today);
            Assert.That(reopened.Document.State.Rows, Is.Empty);
            Assert.That(reopened.Document.State.Settings.People, Is.Empty);
            Assert.That(PlanCsvImport.Prepare(reopened.Document, file, Catalog, Today).IsDuplicate, Is.False);
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    [TestCase("templates/new-tasks.csv", 3), TestCase("docs/evaluation/sandbox-plan.csv", 24)]
    public void ShippedFilesHaveBomAndValidateWithSandboxIdentities(string relative, int count)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GhProjectsBoards.sln"))) directory = directory.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, relative));
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
        var document = Empty() with { State = Empty().State with { Settings = new() { DefaultRepository = "fukuda-yuki/codex-sandbox" } } };
        var preview = PlanCsvImport.Prepare(document, PlanCsvImport.Read(bytes), [new("fukuda-yuki/codex-sandbox", new Dictionary<string, string> { ["fukuda-yuki"] = "U1" })], Today);
        Assert.That(preview.Errors, Is.Empty);
        Assert.That(preview.Command!.Rows.Length, Is.EqualTo(count));
        Assert.That(preview.Command.Rows.All(r => r.Repository == "fukuda-yuki/codex-sandbox" && r.Assignees.All(p => p == "U1")), Is.True);
    }
}
