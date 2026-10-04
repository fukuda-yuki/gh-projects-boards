using System.Security.Cryptography;
using System.Text;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class HolidayCsvImportTests
{
    internal static byte[] OfficialBytes()
    {
        // Cabinet Office original CSV, retrieved 2026-10-04; the full raw payload
        // includes 1955–2027 and matches the bundled preset's source SHA256.
        using var stream = typeof(HolidayCsvImportTests).Assembly.GetManifestResourceStream("GhProjectsBoards.Tests.Fixtures.JapanHolidays.csv")!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    internal static string OfficialText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932).GetString(OfficialBytes());
    }
    [TestCase(false), TestCase(true)]
    public void OriginalOfficialRowsImportWithoutInventedDatesAndRetainLocalByteProvenance(bool utf8)
    {
        var bytes = utf8 ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(OfficialText())).ToArray() : OfficialBytes();
        var when = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.FromHours(9));

        var result = HolidayCsvImport.Parse(bytes, "syukujitsu.csv", when, 2025, 2027);

        Assert.That(result.FirstYear, Is.EqualTo(2025)); Assert.That(result.LastYear, Is.EqualTo(2027));
        Assert.That(result.Dates, Is.EqualTo(PlanningContract.BundledHolidays().Dates));
        Assert.That(result.SourceSha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
        Assert.That(result.RetrievedAt, Is.EqualTo(when));
        Assert.That(result.Source, Does.Contain("Local CSV import").And.Contain("syukujitsu.csv").And.Contain(HolidayCsvImport.ReferenceUrl));
        Assert.That(result.Version, Does.StartWith("csv-2025-2027-"));
    }
    [TestCase("header"), TestCase("date"), TestCase("duplicate"), TestCase("gap"), TestCase("few-dates"), TestCase("empty-name"), TestCase("shorter-coverage"), TestCase("too-large")]
    public void MalformedOrIncompleteDataCannotBecomeAnAdoptableCalendar(string kind)
    {
        var rows = OfficialText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        switch (kind)
        {
            case "header": rows[0] = "date,name"; break;
            case "date": rows.Add("2027/2/30,invalid"); break;
            case "duplicate": rows.Add(rows.Last()); break;
            case "gap": rows.RemoveAll(row => row.StartsWith("2026/")); break;
            case "few-dates": rows.RemoveAll(row => row.StartsWith("2027/") && !row.StartsWith("2027/1/1,")); break;
            case "empty-name": rows.Add("2027/12/1,"); break;
            case "shorter-coverage": rows.RemoveAll(row => row.StartsWith("2027/")); break;
        }
        var bytes = kind == "too-large" ? new byte[HolidayCsvImport.MaximumBytes + 1] : Encoding.UTF8.GetBytes(string.Join('\n', rows));

        Assert.Throws<InvalidOperationException>(() => HolidayCsvImport.Parse(bytes, "selected.csv", DateTimeOffset.UtcNow, 2025, 2027));
    }
}
