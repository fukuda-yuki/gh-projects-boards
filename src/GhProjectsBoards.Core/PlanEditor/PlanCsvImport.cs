using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanCsvError(int Line, string Reason);
internal sealed record PlanCsvRow(int Line, string Key, string Title, string Estimate, string Assignees,
    string Predecessors, string Parent, string Start, string Repository);
internal sealed record PlanCsvFile(string Hash, ImmutableArray<PlanCsvRow> Rows, ImmutableArray<PlanCsvError> Errors);
internal sealed record PlanCsvRepository(string Name, IReadOnlyDictionary<string, string> Assignees);
internal sealed record PlanCsvPreview(ImmutableArray<PlanCsvError> Errors, InsertPlanRows? Command, bool IsDuplicate);

internal static class PlanCsvImport
{
    private static readonly string[] Headers = ["キー", "タイトル", "見積", "担当者", "先行タスク", "親", "開始日指定", "リポジトリ"];
    public static PlanCsvFile Read(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rows = ImmutableArray.CreateBuilder<PlanCsvRow>();
        var errors = ImmutableArray.CreateBuilder<PlanCsvError>();
        PlanCsvFile Result() => new(hash, rows.ToImmutable(), errors.ToImmutable());
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
            var headers = parser.ReadFields()?.Select(h => h.Trim()).ToArray() ?? [];
            foreach (var required in Headers.Take(3)) if (!headers.Contains(required)) errors.Add(new(1, $"{required}列がありません。"));
            if (headers.Distinct().Count() != headers.Length) errors.Add(new(1, "列名が重複しています。"));
            if (headers.Any(h => !Headers.Contains(h))) errors.Add(new(1, "未対応の列名があります。"));
            if (errors.Count > 0) return Result();
            while (!parser.EndOfData)
            {
                var line = checked((int)parser.LineNumber);
                // TextFieldParser skips blank physical lines inside ReadFields.
                // Keep the reported start at the record the user can correct.
                while (line <= physicalLines.Length && string.IsNullOrWhiteSpace(physicalLines[line - 1])) line++;
                var values = parser.ReadFields()!;
                if (values.Length != headers.Length) { errors.Add(new(line, "列数が見出しと一致しません。")); continue; }
                string Get(string header) { var index = Array.IndexOf(headers, header); return index < 0 ? "" : values[index].Trim(); }
                rows.Add(new(line, Get("キー"), Get("タイトル"), Get("見積"), Get("担当者"), Get("先行タスク"), Get("親"), Get("開始日指定"), Get("リポジトリ")));
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
        var additions = ImmutableArray.CreateBuilder<PlanRow>();
        var people = new Dictionary<string, PlanResource>();
        foreach (var source in file.Rows)
        {
            void Error(string reason) => errors.Add(new(source.Line, reason));
            var repository = source.Repository.Length == 0 ? document.State.Settings.DefaultRepository ?? "" : source.Repository;
            var catalog = repositories.FirstOrDefault(r => string.Equals(r.Name, repository, StringComparison.OrdinalIgnoreCase));
            if (!PlanOperations.Repository(repository) || catalog is null) Error("リポジトリが見つからないか、Issueを作成できません。");
            if (source.Title.Length == 0) Error("タイトルを入力してください。");
            decimal? estimate = null;
            if (source.Estimate.Length > 0)
            {
                if (!decimal.TryParse(source.Estimate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0) Error("見積は0以上の数値にしてください。");
                else estimate = number;
            }
            DateOnly? start = null;
            if (source.Start.Length > 0)
            {
                if (!DateOnly.TryParseExact(source.Start, ["yyyy-MM-dd", "yyyy/M/d", "yyyy/MM/dd", "yyyy/M/dd", "yyyy/MM/d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) Error("開始日指定はyyyy-MM-ddまたはyyyy/M/dにしてください。");
                else start = date;
            }
            var assignees = ImmutableArray.CreateBuilder<string>();
            foreach (var login in Split(source.Assignees).Select(s => s.TrimStart('@')))
            {
                var person = catalog?.Assignees.FirstOrDefault(p => string.Equals(p.Key, login, StringComparison.OrdinalIgnoreCase));
                if (person is null || string.IsNullOrEmpty(person.Value.Value)) Error($"担当者 {login} を割り当てできません。");
                else
                {
                    assignees.Add(person.Value.Value);
                    people.TryAdd(person.Value.Value, new(person.Value.Value, person.Value.Key, 100, null, []));
                }
            }
            string? Resolve(string token)
            {
                if (token.Length == 0) return null;
                if (keys.TryGetValue(token, out var id)) return id;
                var caption = token.Contains('/') ? token : repository + "#" + token.TrimStart('#');
                if (existing.TryGetValue(caption, out id)) return id;
                Error($"参照 {token} が見つかりません。"); return null;
            }
            var predecessors = Split(source.Predecessors).Select(Resolve).Where(id => id is not null).Cast<string>().Distinct().ToImmutableArray();
            var parent = Resolve(source.Parent);
            if (keys.TryGetValue(source.Key, out var identity)) additions.Add(new(identity, source.Title, catalog?.Name ?? repository)
            { Estimate = estimate, StartNoEarlierThan = start, Assignees = assignees.Distinct().ToImmutableArray(),
                Parent = parent, Predecessors = predecessors, CsvSourceHash = file.Hash });
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
        return errors.Count > 0 ? Invalid() : new([], command, duplicate);
    }
    private static IEnumerable<string> Split(string value) => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
