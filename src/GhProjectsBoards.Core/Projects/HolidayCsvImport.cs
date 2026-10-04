using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace GhProjectsBoards.Core.Projects;

internal static class HolidayCsvImport
{
    internal const int MaximumBytes = 1_048_576;
    internal const string ReferenceUrl = "https://www8.cao.go.jp/chosei/shukujitsu/syukujitsu.csv";
    internal static HolidayPreset Parse(byte[] bytes, string fileName, DateTimeOffset importedAt, int firstYear = 2025, int minimumLastYear = 2025)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new InvalidOperationException("CSVは1MB以下のファイルを選択してください。");
        if (firstYear is < 1 or > 9999 || minimumLastYear < firstYear || minimumLastYear > 9999 || importedAt == default)
            throw new InvalidOperationException("採用済み祝日の対象年を確認してください。");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("CSVはUTF-8またはShift-JISで保存してください。"); }
        }
        var dates = new SortedDictionary<DateOnly, string>();
        using var parser = new TextFieldParser(new StringReader(text.TrimStart('\uFEFF'))) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        try
        {
            var header = parser.ReadFields();
            if (header is not ["国民の祝日・休日月日", "国民の祝日・休日名称"])
                throw new InvalidOperationException("内閣府形式の祝日CSVを選択してください。");
            while (!parser.EndOfData)
            {
                var row = parser.ReadFields();
                if (row is not [var dateText, var name] || !DateOnly.TryParseExact(dateText,
                    ["yyyy/M/d", "yyyy/MM/dd", "yyyy/M/dd", "yyyy/MM/d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                    throw new InvalidOperationException("CSVの日付と祝日名を確認してください。");
                if (!dates.TryAdd(date, name)) throw new InvalidOperationException($"{date:yyyy-MM-dd} が重複しています。");
            }
        }
        catch (MalformedLineException) { throw new InvalidOperationException("CSVの列や引用符を確認してください。"); }
        var lastYear = dates.Count == 0 ? 0 : dates.Keys.Last().Year;
        if (lastYear < minimumLastYear) throw new InvalidOperationException($"採用済みの{minimumLastYear}年まで含むCSVを選択してください。");
        for (var year = firstYear; year <= lastYear; year++)
        {
            var count = dates.Keys.Count(date => date.Year == year);
            // Detect truncated annual input without inventing missing holidays or
            // claiming that a plausible row count authenticates their publisher.
            if (count < (year >= 2016 ? 16 : 1)) throw new InvalidOperationException($"{year}年の祝日が不足しています。全期間のCSVを選択してください。");
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new($"csv-{firstYear}-{lastYear}-{hash[..12]}",
            $"Local CSV import: {Path.GetFileName(fileName)}; format reference: {ReferenceUrl}; publisher not independently verified",
            hash, importedAt, firstYear, lastYear, dates.Where(pair => pair.Key.Year >= firstYear).Select(pair => new HolidayDate(pair.Key, pair.Value)).ToArray());
    }
}
