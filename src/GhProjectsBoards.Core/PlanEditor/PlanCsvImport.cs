using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanCsvError(int Line, string Reason);
// #324: which column a header names depends on the open Project's settings, so a record keeps its cells in the
// header order the file wrote them and the document is matched later.
internal sealed record PlanCsvRow(int Line, string Key, string Repository, ImmutableArray<string> Values);
internal sealed record PlanCsvFile(string Hash, ImmutableArray<string> Headers, ImmutableArray<PlanCsvRow> Rows, ImmutableArray<PlanCsvError> Errors);
internal sealed record PlanCsvRepository(string Name, IReadOnlyDictionary<string, string> Assignees);
internal sealed record PlanCsvPreview(ImmutableArray<PlanCsvError> Errors, InsertPlanRows? Command, bool IsDuplicate);

internal static class PlanCsvImport
{
    // The removed static template's names stay accepted so its files still import (#324 C3).
    private static readonly (string Old, string Current)[] Aliases = [("タイトル", "タスク名"), ("見積", "見積 h"), ("先行タスク", "先行")];
    public static PlanCsvFile Read(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rows = ImmutableArray.CreateBuilder<PlanCsvRow>();
        var errors = ImmutableArray.CreateBuilder<PlanCsvError>();
        var headers = ImmutableArray<string>.Empty;
        PlanCsvFile Result() => new(hash, headers, rows.ToImmutable(), errors.ToImmutable());
        if (bytes.Length > 5 * 1024 * 1024) { errors.Add(new(1, "CSVは5 MiB以内にしてください。")); return Result(); }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            catch (DecoderFallbackException) { errors.Add(new(1, "UTF-8またはShift-JISで保存してください。")); return Result(); }
        }
        using var parser = new TextFieldParser(new StringReader(text.TrimStart('\uFEFF')))
            { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        var physicalLines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        parser.SetDelimiters(",");
        try
        {
            headers = (parser.ReadFields() ?? []).Select(h => h.Trim())
                .Select(h => Aliases.FirstOrDefault(alias => alias.Old == h).Current ?? h).ToImmutableArray();
            var keyIndex = headers.IndexOf(PlanCsvTemplate.KeyHeader);
            var repositoryIndex = headers.IndexOf(PlanCsvTemplate.RepositoryHeader);
            if (keyIndex < 0) errors.Add(new(1, $"{PlanCsvTemplate.KeyHeader}列がありません。"));
            if (!headers.Contains("タスク名")) errors.Add(new(1, "タスク名列がありません。"));
            var repeated = headers.GroupBy(h => h, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (repeated.Length > 0) errors.Add(new(1, $"列名が重複しています: {string.Join(", ", repeated)}"));
            if (errors.Count > 0) return Result();
            while (!parser.EndOfData)
            {
                var line = checked((int)parser.LineNumber);
                // TextFieldParser skips blank physical lines inside ReadFields.
                // Keep the reported start at the record the user can correct.
                while (line <= physicalLines.Length && string.IsNullOrWhiteSpace(physicalLines[line - 1])) line++;
                var values = parser.ReadFields()!;
                if (values.Length != headers.Length) { errors.Add(new(line, "列数が見出しと一致しません。")); continue; }
                var cells = values.Select(v => v.Trim()).ToImmutableArray();
                rows.Add(new(line, cells[keyIndex], repositoryIndex < 0 ? "" : cells[repositoryIndex], cells));
                if (rows.Count > 10_000) { errors.Add(new(line, "タスクは10,000行以内にしてください。")); break; }
            }
        }
        catch (MalformedLineException) { errors.Add(new(checked((int)parser.ErrorLineNumber), "引用符または区切りが不正です。")); }
        if (rows.Count == 0 && errors.Count == 0) errors.Add(new(2, "追加するタスクがありません。"));
        return Result();
    }

    public static PlanCsvPreview Prepare(PlanDocument document, PlanCsvFile file, IReadOnlyList<PlanCsvRepository> repositories, DateOnly today)
    {
        var errors = file.Errors.ToBuilder();
        var duplicate = document.State.Rows.Any(r => r.CsvSourceHash == file.Hash);
        PlanCsvPreview Invalid() => new(errors.ToImmutable(), null, duplicate);
        if (errors.Count > 0) return Invalid();
        var fields = Fields(document, file.Headers, errors);
        if (errors.Count > 0) return Invalid();
        var index = file.Headers.Select((header, at) => (header, at)).ToDictionary(p => p.header, p => p.at, StringComparer.Ordinal);
        string Value(PlanCsvRow row, string header) => index.TryGetValue(header, out var at) ? row.Values[at] : "";
        var columns = file.Headers.Where(h => h is not (PlanCsvTemplate.KeyHeader or PlanCsvTemplate.RepositoryHeader)).ToArray();

        // File-local keys name the new rows before any value is read, so a parent or predecessor may point forward.
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new Dictionary<string, int>();
        foreach (var row in file.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Key) || row.Key.Contains('#') || row.Key.Contains(';') || row.Key.Any(char.IsControl))
                errors.Add(new(row.Line, "キーは空白・#・;・制御文字を含まない値にしてください。"));
            else if (!keys.TryAdd(row.Key, PlanRow.New("").Identity)) errors.Add(new(row.Line, "キーが重複しています。"));
            else lines.Add(keys[row.Key], row.Line);
        }
        var existing = document.Sync.IssueLinks.Where(p => document.State.Rows.Any(r => r.Identity == p.Key))
            .GroupBy(p => p.Value.Caption, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        var defaultRepository = document.State.Settings.DefaultRepository ?? "";
        // The rows being read are not in the plan yet. Label, milestone and 種類 values resolve per repository,
        // and the sheet's rules look that up by identity, so the candidates are added to a working document.
        var working = document;
        {
            var candidates = file.Rows.Where(r => keys.TryGetValue(r.Key, out var id) && lines.TryGetValue(id, out var line) && line == r.Line)
                .Select(r => new PlanRow(keys[r.Key], Value(r, "タスク名"), r.Repository.Length == 0 ? defaultRepository : r.Repository)).ToImmutableArray();
            if (!candidates.IsEmpty) working = document with { State = document.State with { Rows = document.State.Rows.AddRange(candidates) } };
        }
        var additions = ImmutableArray.CreateBuilder<PlanRow>();
        var people = new Dictionary<string, PlanResource>();
        foreach (var source in file.Rows)
        {
            void Error(string reason) => errors.Add(new(source.Line, reason));
            var repository = source.Repository.Length == 0 ? defaultRepository : source.Repository;
            var catalog = repositories.FirstOrDefault(r => string.Equals(r.Name, repository, StringComparison.OrdinalIgnoreCase));
            if (!PlanOperations.Repository(repository) || catalog is null) Error("リポジトリが見つからないか、Issueを作成できません。");
            var title = Value(source, "タスク名");
            if (title.Length == 0) Error("タスク名を入力してください。");
            // A repeated key names no second row: its line already carries the duplicate-key error, and only the
            // first row of a key is read so two candidates cannot share one identity.
            if (!keys.TryGetValue(source.Key, out var identity) || !lines.TryGetValue(identity, out var at) || at != source.Line) continue;
            var row = new PlanRow(identity, title, catalog?.Name ?? repository) { CsvSourceHash = file.Hash };
            string? Resolve(string token)
            {
                if (token.Length == 0) return null;
                if (keys.TryGetValue(token, out var id)) return id;
                var caption = token.Contains('/') ? token : repository + "#" + token.TrimStart('#');
                if (existing.TryGetValue(caption, out id)) return id;
                // repo#N names the repository without its owner, as the sheet does (#263).
                var named = token.IndexOf('#') > 0 && !token.Contains('/')
                    ? existing.Where(p => p.Key.EndsWith("/" + token, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray() : [];
                if (named.Length == 1) return named[0];
                Error($"参照 {token} が見つかりません。"); return null;
            }
            foreach (var header in columns)
            {
                var field = fields[header];
                // タスク名 is the row's required title, not a cell value: the file's own format keeps a quoted
                // line break in it (the sheet's one-line cell rule does not apply to a file).
                if (field == PlanField.Title) continue;
                var text = Value(source, header);
                if (text.Length == 0) continue;
                try
                {
                    row = field switch
                    {
                        PlanField.Parent => row with { Parent = Resolve(text) },
                        PlanField.Predecessors => row with { Predecessors = Split(text).Select(Resolve).Where(id => id is not null).Cast<string>().Distinct().ToImmutableArray() },
                        PlanField.Assignees => row with { Assignees = Assignees(text, catalog, people, Error) },
                        PlanField.Labels => row with { Labels = (ImmutableArray<string>)PlanSheetEditing.Parse(working, field, text.Replace(';', ','), today, identity)! },
                        PlanField.MilestoneTitle => row with { MilestoneTitle = (string?)PlanSheetEditing.Parse(working, field, text, today, identity) },
                        PlanField.Status => row with { Status = Status(document, text) },
                        _ when PlanSheetColumns.IsExtra(field) => PlanValues.Set(row, field,
                            PlanJson.Text(PlanExtraEditing.Validate(working, field, PlanSheetEditing.Parse(working, field, text, today)))),
                        _ => Set(row, field, PlanSheetEditing.Parse(working, field, text, today, identity))
                    };
                }
                catch (ArgumentException ex) { Error(ex.Message); }
            }
            // A date pair is refused exactly as the insert command refuses it, so the line names the row.
            var task = PlanOperations.TaskInput(row);
            if (task.KeepsDates && task.KeptDates.Start > task.KeptDates.End) Error("終了日は開始日以降にしてください。");
            additions.Add(row);
        }
        if (errors.Count > 0) return Invalid();
        var command = new InsertPlanRows(additions.ToImmutable(), Kind: PlanOperationKind.CsvImport)
        { CsvPeople = people.Values.Where(p => !document.State.Settings.People.Any(old => old.Identity == p.Identity)).ToImmutableArray() };
        try { PlanOperations.Apply(document, command with { AllowDuplicateCsv = true }, today); }
        catch (PlanCycleException ex)
        {
            foreach (var line in ex.Identities.Where(lines.ContainsKey).Select(id => lines[id]).Distinct().Order()) errors.Add(new(line, "親または先行タスクが循環しています。"));
            if (errors.Count == 0) errors.Add(new(1, ex.Message));
        }
        catch (ArgumentException ex) { errors.Add(new(1, ex.Message)); }
        return errors.Count > 0 ? Invalid() : new([], command with { Rows = new PlanOutline(command.Rows).Arrange() }, duplicate);
    }

    // The headers this file may use: the generated template's set for the open Project, in any order. A read-only
    // or computed column, a column the sheet does not show, and an unknown name are all refused at the header.
    private static ImmutableDictionary<string, string> Fields(PlanDocument document, ImmutableArray<string> headers,
        ImmutableArray<PlanCsvError>.Builder errors)
    {
        var columns = PlanSheetColumns.All(document);
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal) { [PlanCsvTemplate.ParentHeader] = PlanField.Parent };
        foreach (var column in columns.Where(c => c.Visible && c.Editability == "編集できる"))
        {
            accepted.TryAdd(column.Name, column.Key);
            // A Project field may also be headed by its GitHub name, its default name.
            if (PlanSheetColumns.IsExtra(column.Key)) accepted.TryAdd(column.DefaultName, column.Key);
        }
        var readOnly = columns.Where(c => c.Visible && c.Editability != "編集できる").Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var hidden = columns.Where(c => !c.Visible && c.Editability == "編集できる").Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (header is PlanCsvTemplate.KeyHeader or PlanCsvTemplate.RepositoryHeader) continue;
            if (accepted.TryGetValue(header, out var field)) mapped[header] = field;
            else errors.Add(new(1, readOnly.Contains(header) ? $"読み取り専用の列は取り込めません: {header}"
                : hidden.Contains(header) ? $"表示していない列は取り込めません: {header}"
                : $"未対応の列名です: {header}"));
        }
        // A Project field headed by both its display name and its GitHub name names one column twice.
        foreach (var repeated in mapped.GroupBy(p => p.Value).Where(g => g.Count() > 1))
            errors.Add(new(1, $"列名が重複しています: {string.Join(", ", repeated.Select(p => p.Key))}"));
        return mapped.ToImmutableDictionary(StringComparer.Ordinal);
    }

    // The Status column's choices are the Project's Status field options. The sheet stores whatever is typed, but
    // an imported file must name a real option, so a typo is refused at its line instead of at the later publish.
    // A Project whose baseline holds no Status field (an offline fixture) keeps the typed text.
    private static string? Status(PlanDocument document, string text)
    {
        var definition = document.Baseline.Columns.FirstOrDefault(c => c.Name == "Status" && c.DataType == "SINGLE_SELECT");
        return definition is null ? text : PlanExtraEditing.Select("ステータス", definition.Options, text);
    }
    // The value families the sheet's own cell rule already knows (#239). The sheet parses the same fields, so a
    // refusal here is the refusal the PMO sees at the cell.
    private static PlanRow Set(PlanRow row, string field, object? value) => field switch
    {
        PlanField.Estimate => row with { Estimate = (decimal?)value },
        PlanField.Remaining => row with { Remaining = (decimal?)value },
        PlanField.Actual => row with { Actual = (decimal?)value },
        PlanField.Start => row with { Start = (DateOnly?)value },
        PlanField.End => row with { End = (DateOnly?)value },
        PlanField.Phase => row with { Phase = (string?)value },
        PlanField.IssueType => row with { IssueType = (string?)value },
        _ => throw new ArgumentException("この列は取り込めません。")
    };
    // A person is matched against the repository's assignable users, as the old template did, and registered with
    // the import so the assignment and the registration stay one Undo step.
    private static ImmutableArray<string> Assignees(string text, PlanCsvRepository? catalog, Dictionary<string, PlanResource> people, Action<string> error)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var login in PlanAssigneeChoices.Values(text).Select(s => s.TrimStart('@')))
        {
            var person = catalog?.Assignees.FirstOrDefault(p => string.Equals(p.Key, login, StringComparison.OrdinalIgnoreCase));
            if (person is null || string.IsNullOrEmpty(person.Value.Value)) error($"担当者 {login} を割り当てできません。");
            else
            {
                result.Add(person.Value.Value);
                people.TryAdd(person.Value.Value, new(person.Value.Value, person.Value.Key, 100, null));
            }
        }
        return result.Distinct().ToImmutableArray();
    }
    private static IEnumerable<string> Split(string value) => value.Split([',', '、', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
