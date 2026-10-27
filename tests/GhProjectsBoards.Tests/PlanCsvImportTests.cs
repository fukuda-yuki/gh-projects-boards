using System.Collections.Immutable;
using System.Text;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanCsvImportTests
{
    internal const string Header = "キー,タイトル,見積,担当者,先行タスク,親,開始日,リポジトリ\n";
    internal static readonly DateOnly Today = new(2026, 10, 5);
    internal static readonly PlanCsvRepository[] Catalog = [new("acme/repo", new Dictionary<string, string> { ["alice"] = "U1" })];
    internal static PlanDocument Empty() => new(new(new("github.com", 42), "P1"), new([], []),
        new([], new() { DefaultRepository = "acme/repo", ProjectStart = Today }));
    internal static PlanCsvFile Read(string body) => PlanCsvImport.Read(Encoding.UTF8.GetBytes(Header + body));
    internal static string HundredRows() => string.Join('\n', Enumerable.Range(0, 10).SelectMany(g =>
        new[] { $"p{g},Group {g},,,,,," }.Concat(Enumerable.Range(1, 9).Select(i =>
            $"t{g}-{i},Task {g}-{i},8,alice,{(i == 1 ? "" : $"t{g}-{i - 1}")},p{g},,"))));

    private static PlanCsvFile FileWith(string header, string body) => PlanCsvImport.Read(Encoding.UTF8.GetBytes(header + "\n" + body));
    private static PlanCsvPreview Preview(string header, string body, PlanDocument? document = null, PlanCsvRepository[]? catalog = null)
        => PlanCsvImport.Prepare(document ?? Empty(), FileWith(header, body), catalog ?? Catalog, Today);
    private static PlanDocument Show(PlanDocument document, params string[] keys) => document with {
        State = document.State with { Settings = document.State.Settings with { SheetColumns = [
            .. PlanSheetColumns.All(document).Select(c => new PlanSheetColumnChoice(c.Key, c.Key is "Id" or "Title" || keys.Contains(c.Key), null))] } } };
    // The Project the settings can name: a mapped 工程, labels, a milestone and four unmapped Project fields.
    private static PlanDocument Project() => Empty() with { Baseline = Empty().Baseline with {
        Columns = [
            new PlanColumnDefinition("F0", "工程", "SINGLE_SELECT") { Options = ["設計", "実装"] },
            new PlanColumnDefinition("F1", "説明", "TEXT"),
            new PlanColumnDefinition("F2", "規模", "NUMBER"),
            new PlanColumnDefinition("F3", "追加日", "DATE"),
            new PlanColumnDefinition("F4", "優先度", "SINGLE_SELECT") { Options = ["高", "低"] }],
        RepositoryLabels = [new("acme/repo", "L1", "設計"), new("acme/repo", "L2", "確認")],
        RepositoryMilestones = [new("acme/repo", "M1", "v1.0")] } };
    private static PlanDocument WithSettings(PlanDocument document, ProjectPlanSettings settings) => document with { State = document.State with { Settings = settings } };
    private static PlanDocument Mapped(PlanDocument document) => WithSettings(document,
        document.State.Settings with { Columns = [new(PlanField.Phase, "F0", "工程", "SINGLE_SELECT")] });
    private static PlanDocument Typed(PlanDocument document) => document with { Baseline = document.Baseline with {
        IssueTypes = [new("IT1", "Task"), new("IT2", "Bug")], IssueTypeRepositories = ["acme/repo"] } };
    private static string? Extra(PlanRow row, string key) => PlanExtraEditing.Value(row, key)?.ToString();

    [Test]
    public void ImportedEstimateLeavesRemainingAsCalculatedDefault()
    {
        var preview = PlanCsvImport.Prepare(Empty(), Read("a,A,8,alice,,,,"), Catalog, Today);
        Assert.That(preview.Errors, Is.Empty);
        var row = preview.Command!.Rows.Single();
        Assert.That(row.Remaining, Is.Null);
        Assert.That(PlanScheduler.Calculate([PlanOperations.TaskInput(row)], new(), Today).Single().Remaining, Is.EqualTo(8));
    }

    [Test]
    public void ImportedStartIsThePlanStartOfTheNewTask()
    {
        // #268: the CSV 開始日 is the new task's plan start, which is the floor of its schedule.
        var preview = PlanCsvImport.Prepare(Empty(), Read("a,A,8,alice,,,2026-10-14,"), Catalog, Today);
        Assert.That(preview.Errors, Is.Empty);
        var row = preview.Command!.Rows.Single();
        Assert.That(row.Start, Is.EqualTo(new DateOnly(2026, 10, 14)));
        var scheduled = PlanScheduler.Calculate([PlanOperations.TaskInput(row)], new() { StatusDate = Today }, Today).Single();
        Assert.That((scheduled.Start.Value, scheduled.End.Value), Is.EqualTo((new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 14))));
    }

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

    [TestCase("a,,8,,,,,", 2, "タスク名")]
    [TestCase("a,A,-1,,,,,", 2, "工数")]
    [TestCase("a,A,one,,,,,", 2, "工数")]
    [TestCase("a,A,8,,,,2026-02-30,", 2, "日付")]
    [TestCase("a,A,8,,missing,,,", 2, "参照")]
    [TestCase("a,A,8,,,missing,,", 2, "参照")]
    [TestCase("a,A,8,,,,,unknown/repo", 2, "リポジトリ")]
    [TestCase("a,A,8,bob,,,,", 2, "担当者")]
    [TestCase("a,A,8,,,,,\na,B,4,,,,,", 3, "キー")]
    [TestCase("a,A,8,,b,,,\nb,B,8,,a,,,", 2, "循環")]
    [TestCase("a,A,8,,,b,,\nb,B,8,,,a,,", 2, "循環")]
    [TestCase("\n\na,,8,,,,,", 4, "タスク名")]
    public void InvalidFileHasLineErrorsAndNoApplicableCommand(string body, int line, string reason)
    {
        var document = Empty();
        var preview = PlanCsvImport.Prepare(document, Read(body), Catalog, Today);
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Any(e => e.Line == line && e.Reason.Contains(reason)), Is.True,
            string.Join("; ", preview.Errors));
        Assert.That(document.State.Rows, Is.Empty);
    }

    [TestCase("キー", "a", "タスク名列がありません。")]
    [TestCase("タスク名", "A", "キー列がありません。")]
    public void RequiredHeadersAreReportedAtLineOne(string header, string body, string reason)
    {
        var file = FileWith(header, body);
        Assert.That(file.Errors.Any(e => e.Line == 1 && e.Reason.Contains(reason)), Is.True, string.Join("; ", file.Errors));
    }

    [Test]
    public void OnlyTheKeyAndTaskNameAreRequiredAndOldAliasesStayAccepted()
    {
        // The removed static template's headers name the same columns, and 見積 is no longer required.
        var preview = PlanCsvImport.Prepare(Empty(), FileWith("キー,タイトル,見積", "a,A,8"), Catalog, Today);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That((preview.Command!.Rows.Single().Title, preview.Command.Rows.Single().Estimate), Is.EqualTo(("A", (decimal?)8)));
        var minimal = PlanCsvImport.Prepare(Empty(), FileWith("キー,タスク名", "a,A"), Catalog, Today);
        Assert.That(minimal.Errors, Is.Empty, string.Join("; ", minimal.Errors));
        Assert.That(minimal.Command!.Rows.Single().Estimate, Is.Null);
    }

    [TestCase("状態", "完了")]
    [TestCase("ID", "新1")]
    [TestCase("着手日", "2026-10-14")]
    [TestCase("完了日", "2026-10-15")]
    [TestCase("見込終了", "2026-10-16")]
    public void ReadOnlyAndComputedHeadersAreLineOneErrors(string header, string value)
    {
        var preview = Preview("キー,タスク名," + header, "a,A," + value);
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Any(e => e.Line == 1 && e.Reason.Contains("読み取り専用")), Is.True, string.Join("; ", preview.Errors));
    }

    [Test]
    public void HiddenEditableHeadersAreLineOneErrors()
    {
        // ステータス is editable but hidden by default; the template only offers shown columns.
        var preview = Preview("キー,タスク名,ステータス", "a,A,Todo");
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Any(e => e.Line == 1 && e.Reason.Contains("表示していない")), Is.True, string.Join("; ", preview.Errors));
        var shown = Preview("キー,タスク名,ステータス", "a,A,Todo", Show(Empty(), "Title", "Status"));
        Assert.That(shown.Errors, Is.Empty, string.Join("; ", shown.Errors));
        Assert.That(shown.Command!.Rows.Single().Status, Is.EqualTo("Todo"));
    }

    [Test]
    public void IterationHeadersAreReadOnlyAndRefusedAtLineOne()
    {
        var document = Show(Empty() with { Baseline = Empty().Baseline with { Columns = [new PlanColumnDefinition("I1", "期間", "ITERATION")] } }, "Title", "Project:I1");
        var preview = Preview("キー,タスク名,期間", "a,A,今週", document);
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Any(e => e.Line == 1 && e.Reason.Contains("読み取り専用")), Is.True, string.Join("; ", preview.Errors));
    }

    [Test]
    public void UnknownHeaderIsALineOneError()
    {
        var preview = Preview("キー,タスク名,unknown", "a,A,x");
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Any(e => e.Line == 1 && e.Reason.Contains("未対応")), Is.True, string.Join("; ", preview.Errors));
    }

    [Test]
    public void ADuplicateHeaderIncludingAnAliasAndItsCanonicalNameIsALineOneError()
    {
        foreach (var header in new[] { "キー,タスク名,タスク名", "キー,タスク名,タイトル", "キー,タスク名,見積 h,見積" })
        {
            var preview = Preview(header, "a,A,B");
            Assert.That(preview.Command, Is.Null);
            Assert.That(preview.Errors.Any(e => e.Line == 1 && e.Reason.Contains("重複")), Is.True, header + ": " + string.Join("; ", preview.Errors));
        }
    }

    [Test]
    public void ProjectFieldsAcceptBothTheirDisplayNameAndGitHubName()
    {
        var document = Show(Project(), "Title", "Project:F1");
        document = WithSettings(document, PlanSheetColumns.Change(document, "Project:F1", name: "説明文", rename: true));
        foreach (var header in new[] { "説明文", "説明" })
        {
            var preview = Preview("キー,タスク名," + header, "a,A,補足", document);
            Assert.That(preview.Errors, Is.Empty, header + ": " + string.Join("; ", preview.Errors));
            Assert.That(Extra(preview.Command!.Rows.Single(), "Project:F1"), Is.EqualTo("補足"));
        }
    }

    [Test]
    public void EveryShownEditableColumnSetsTheNewRowValues()
    {
        var document = Mapped(Show(Project(), "Title", "Phase", "Assignees", "Estimate", "Remaining", "Actual",
            "Start", "End", "Predecessors", "Status", "Labels", "MilestoneTitle", "Project:F1", "Project:F2", "Project:F3", "Project:F4"));
        var columns = "キー,親,タスク名,工程,担当者,見積 h,残 h,実績 h,開始日,終了日,先行,ステータス,説明,規模,追加日,優先度,ラベル,マイルストーン,リポジトリ".Split(',');
        string Row(params (string Header, string Value)[] cells)
            => string.Join(',', columns.Select(column => cells.FirstOrDefault(cell => cell.Header == column).Value ?? ""));
        var body = string.Join('\n', [
            Row(("キー", "p"), ("タスク名", "親"), ("工程", "設計"), ("担当者", "alice"), ("見積 h", "16"), ("残 h", "0"),
                ("実績 h", "0"), ("開始日", "2026-10-05"), ("終了日", "2026-10-08"), ("ステータス", "Todo")),
            Row(("キー", "a"), ("親", "p"), ("タスク名", "子"), ("工程", "実装"), ("担当者", "alice"), ("見積 h", "8"),
                ("残 h", "4"), ("実績 h", "2"), ("開始日", "2026-10-14"), ("終了日", "2026/10/15"), ("ステータス", "Todo"),
                ("説明", "補足"), ("規模", "3"), ("追加日", "2026/10/20"), ("優先度", "高"), ("ラベル", "\"設計, 確認\""),
                ("マイルストーン", "v1.0"), ("リポジトリ", "acme/repo")),
            Row(("キー", "q"), ("タスク名", "後続"), ("工程", "設計"), ("担当者", "alice"), ("見積 h", "8"), ("先行", "p"),
                ("ステータス", "Todo"), ("リポジトリ", "acme/repo"))]);
        var preview = Preview(string.Join(',', columns), body, document);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        var rows = preview.Command!.Rows;
        var task = rows.Single(r => r.Title == "子");
        Assert.That(task.Parent, Is.EqualTo(rows.Single(r => r.Title == "親").Identity));
        Assert.That(rows.Single(r => r.Title == "後続").Predecessors, Is.EqualTo(new[] { rows.Single(r => r.Title == "親").Identity }));
        Assert.Multiple(() =>
        {
            Assert.That((task.Phase, task.Status, task.MilestoneTitle), Is.EqualTo(("実装", "Todo", "v1.0")));
            Assert.That(task.Assignees, Is.EqualTo(new[] { "U1" }));
            Assert.That((task.Estimate, task.Remaining, task.Actual), Is.EqualTo(((decimal?)8, (decimal?)4, (decimal?)2)));
            Assert.That((task.Start, task.End), Is.EqualTo((new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 15))));
            Assert.That(task.Labels, Is.EqualTo(new[] { "確認", "設計" }));
            Assert.That((Extra(task, "Project:F1"), Extra(task, "Project:F2"), Extra(task, "Project:F3"), Extra(task, "Project:F4")),
                Is.EqualTo(("補足", "3", "2026-10-20", "高")));
        });
    }

    [TestCase("見積 h", "1d", 8)]
    [TestCase("見積 h", "８", 8)]
    public void EffortUsesTheSheetCellRules(string header, string value, decimal expected)
    {
        var preview = Preview("キー,タスク名," + header, "a,A," + value, Show(Empty(), "Title", "Estimate"));
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(preview.Command!.Rows.Single().Estimate, Is.EqualTo(expected));
    }

    [Test]
    public void DatesUseTheSheetCellRulesIncludingTheYearNearestTheStatusDate()
    {
        var preview = Preview("キー,タスク名,開始日", "a,A,10/14", Show(Mapped(Empty()), "Title", "Start"));
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(preview.Command!.Rows.Single().Start, Is.EqualTo(new DateOnly(2026, 10, 14)));
    }

    [Test]
    public void ChoiceValuesMatchTheConfiguredNamesIgnoringCase()
    {
        var document = Mapped(Show(Project(), "Title", "Phase"));
        var preview = Preview("キー,タスク名,工程", "a,A,設計", document);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(preview.Command!.Rows.Single().Phase, Is.EqualTo("設計"));
        var refused = Preview("キー,タスク名,工程", "a,A,営業", document);
        Assert.That(refused.Command, Is.Null);
        Assert.That(refused.Errors.Single().Line, Is.EqualTo(2));
        Assert.That(refused.Errors.Single().Reason, Does.Contain("工程は 設計, 実装 のどれか"));
    }

    [Test]
    public void StatusValuesMustMatchTheProjectsStatusOptions()
    {
        var fields = new[] { new PlanColumnDefinition("S1", "Status", "SINGLE_SELECT") { Options = ["Todo", "Done"] } };
        var document = Show(Empty() with { Baseline = Empty().Baseline with { Columns = [.. fields] } }, "Title", "Status");
        var accepted = Preview("キー,タスク名,ステータス", "a,A,todo", document);
        Assert.That(accepted.Errors, Is.Empty, string.Join("; ", accepted.Errors));
        Assert.That(accepted.Command!.Rows.Single().Status, Is.EqualTo("Todo"));
        var refused = Preview("キー,タスク名,ステータス", "a,A,Blocked", document);
        Assert.That(refused.Command, Is.Null);
        Assert.That(refused.Errors.Single().Line, Is.EqualTo(2));
        Assert.That(refused.Errors.Single().Reason, Does.Contain("ステータスは Todo, Done のどれか"));
    }

    [Test]
    public void LabelsAndMilestonesMustBelongToTheRowsRepository()
    {
        var document = Show(Project(), "Title", "Labels", "MilestoneTitle");
        // The file accepts either separator, as the columns that list several values do.
        foreach (var labels in new[] { "設計, 確認", "設計; 確認" })
        {
            var preview = Preview("キー,タスク名,ラベル,マイルストーン", "a,A,\"" + labels + "\",v1.0", document);
            Assert.That(preview.Errors, Is.Empty, labels + ": " + string.Join("; ", preview.Errors));
            var row = preview.Command!.Rows.Single();
            Assert.That((row.Labels, row.MilestoneTitle), Is.EqualTo((new[] { "確認", "設計" }, "v1.0")));
        }
        var refused = Preview("キー,タスク名,ラベル,マイルストーン", "a,A,営業,v2.0", document);
        Assert.That(refused.Command, Is.Null);
        Assert.That(refused.Errors.Select(e => e.Line), Is.EqualTo(new[] { 2, 2 }));
        Assert.That(refused.Errors[0].Reason, Does.Contain("ラベルは 設計, 確認 のどれか"));
        Assert.That(refused.Errors[1].Reason, Does.Contain("マイルストーンは v1.0 のどれか"));
    }

    [TestCase("補足", "補足")]
    public void ProjectTextFieldsUseTheirColumnType(string input, string expected)
    {
        var document = Show(Project(), "Title", "Project:F1");
        var preview = Preview("キー,タスク名,説明", "a,A," + input, document);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(Extra(preview.Command!.Rows.Single(), "Project:F1"), Is.EqualTo(expected));
    }

    [TestCase("規模", "3", null)]
    [TestCase("追加日", "2026/10/20", null)]
    [TestCase("優先度", "高", null)]
    [TestCase("規模", "多い", "数値を入力してください。")]
    [TestCase("追加日", "いつか", "日付は")]
    [TestCase("優先度", "中", "優先度は 高, 低 のどれか")]
    public void ProjectFieldValuesFollowTheirType(string header, string value, string? error)
    {
        var keys = header switch { "規模" => "Project:F2", "追加日" => "Project:F3", _ => "Project:F4" };
        var document = Show(Project(), "Title", keys);
        var preview = Preview("キー,タスク名," + header, "a,A," + value, document);
        if (error is null) { Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors)); return; }
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Single().Line, Is.EqualTo(2));
        Assert.That(preview.Errors.Single().Reason, Does.Contain(error));
    }

    [TestCase("Bug", "Bug")]
    [TestCase("bug", "Bug")]
    public void IssueTypeValuesMatchTheProjectsTypesIgnoringCase(string input, string expected)
    {
        var document = Typed(Show(Typed(Empty()), "Title", "IssueType"));
        var preview = Preview("キー,タスク名,種類", "a,A," + input, document);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(preview.Command!.Rows.Single().IssueType, Is.EqualTo(expected));
    }

    [Test]
    public void AnUnknownOrUnavailableIssueTypeIsRefusedAtItsLine()
    {
        var document = Typed(Show(Typed(Empty()), "Title", "IssueType"));
        var unknown = Preview("キー,タスク名,種類", "a,A,Epic", document);
        Assert.That(unknown.Command, Is.Null);
        Assert.That(unknown.Errors.Single().Line, Is.EqualTo(2));
        Assert.That(unknown.Errors.Single().Reason, Does.Contain("種類は Task, Bug のどれか"));
        var personal = Preview("キー,タスク名,種類", "a,A,Task", Show(Empty(), "Title", "IssueType"));
        Assert.That(personal.Command, Is.Null);
        Assert.That(personal.Errors.Single().Reason, Does.Contain("このリポジトリには Issue の種類がありません"));
    }

    [Test]
    public void InvalidValuesAreReportedAtTheirPhysicalLineAndNoCommandIsProduced()
    {
        var document = Show(Project(), "Title", "Estimate", "Start");
        var preview = Preview("キー,タスク名,見積 h,開始日", "a,A,8,2026-10-14\nb,B,one,\nc,C,8,2026-02-30", document);
        Assert.That(preview.Command, Is.Null);
        Assert.That(preview.Errors.Select(e => e.Line), Is.EqualTo(new[] { 3, 4 }));
    }

    [Test]
    public void FileKeysAndExistingIssueNumbersResolveToStableIdentities()
    {
        var old = new PlanRow("I1", "Existing", "acme/repo");
        var parent = new PlanRow("I2", "Parent", "acme/repo");
        var document = Empty() with { Baseline = new([old, parent], []), State = Empty().State with { Rows = [old, parent] },
            Sync = new() { IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty.Add("I1", new("acme/repo#7", "https://github.com/acme/repo/issues/7"))
                .Add("I2", new("acme/repo#8", "https://github.com/acme/repo/issues/8")) } };
        var preview = PlanCsvImport.Prepare(document, Read("a,A,8,alice,#7,,,\nb,B,4,alice,a,8,,\nc,C,4,,acme/repo#7,,,\nd,D,4,,REPO#8,,,"), Catalog, Today);
        Assert.That(preview.Errors, Is.Empty);
        var rows = preview.Command!.Rows;
        Assert.That(rows[0].Predecessors, Is.EqualTo(new[] { "I1" }));
        Assert.That(rows[1].Predecessors, Is.EqualTo(new[] { rows[0].Identity }));
        Assert.That(rows[1].Parent, Is.EqualTo("I2"));
        Assert.That(rows[2].Predecessors, Is.EqualTo(new[] { "I1" }));
        Assert.That(rows[3].Predecessors, Is.EqualTo(new[] { "I2" }), "repo#N names the repository as the sheet does (#263).");
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
            var importedState = PlanJson.Text(session.Document.State);
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
            await reopened.Redo(Today);
            Assert.That(PlanJson.Text(reopened.Document.State), Is.EqualTo(importedState));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "GhProjectsBoards.sln"))) return directory.FullName;
        throw new InvalidOperationException("Run from a checkout of the repository.");
    }

    [Test]
    public void SandboxEvaluationFileStillImportsWithTheRemovedTemplatesColumnNames()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "docs/evaluation/sandbox-plan.csv"));
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
        var document = Empty() with { State = Empty().State with { Settings = new() { DefaultRepository = "fukuda-yuki/codex-sandbox" } } };
        var preview = PlanCsvImport.Prepare(document, PlanCsvImport.Read(bytes), [new("fukuda-yuki/codex-sandbox", new Dictionary<string, string> { ["fukuda-yuki"] = "U1" })], Today);
        Assert.That(preview.Errors, Is.Empty, string.Join("; ", preview.Errors));
        Assert.That(preview.Command!.Rows.Length, Is.EqualTo(24));
        Assert.That(preview.Command.Rows.All(r => r.Repository == "fukuda-yuki/codex-sandbox" && r.Assignees.All(p => p == "U1")), Is.True);
    }
}
