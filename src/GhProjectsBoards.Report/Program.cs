using System.Globalization;
using System.Text;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Reports;

const string Usage = """
    ghpb-report snapshot --project <Project URL> --settings <settings file> --data <folder>
                         [--status-date yyyy-MM-dd] [--time-zone Asia/Tokyo] [--gh <gh executable>]
    ghpb-report report --data <folder> --out <REPORT.md>
    """;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0 || args[0] is not ("snapshot" or "report")) return Fail(Usage, 2);
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var n = 1; n < args.Length; n += 2)
{
    if (!args[n].StartsWith("--", StringComparison.Ordinal) || n + 1 >= args.Length || !options.TryAdd(args[n][2..], args[n + 1]))
        return Fail(Usage, 2);
}
string? Option(string name) => options.GetValueOrDefault(name);
try
{
    if (args[0] == "snapshot")
    {
        if (Option("project") is not { } project || Option("settings") is not { } settingsPath || Option("data") is not { } data) return Fail(Usage, 2);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Option("time-zone") ?? "Asia/Tokyo");
        var statusDate = Option("status-date") is { } text
            ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var host = Uri.TryCreate(project, UriKind.Absolute, out var uri) ? uri.Host : throw new ArgumentException("プロジェクトの URL が不正です: " + project);
        var settings = ProgressSnapshotReader.ReadSettings(await File.ReadAllBytesAsync(settingsPath));
        var service = new GhConnectionService(Option("gh") ?? "gh", host, timeout: TimeSpan.FromSeconds(120));
        var snapshot = await ProgressSnapshotReader.ReadAsync(service, project, settings, statusDate, zone, () => DateTimeOffset.UtcNow);
        var path = Path.Combine(data, statusDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
        Write(path, snapshot.ToJson());
        Console.WriteLine($"{path}: タスク {snapshot.Tasks.Length} · 遅れ {snapshot.Lateness.Late.Length} · 遅れの原因 {snapshot.Lateness.Cause.Length} · 見積超過 {snapshot.Lateness.OverEstimate.Length}");
    }
    else
    {
        if (Option("data") is not { } data || Option("out") is not { } output) return Fail(Usage, 2);
        var snapshots = Directory.EnumerateFiles(data, "*.json").Order(StringComparer.Ordinal)
            .Select(file => ProgressSnapshot.Read(File.ReadAllBytes(file))).ToArray();
        var report = ProgressReport.Render(snapshots);
        Write(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ProgressReport.TrendPath), Encoding.UTF8.GetBytes(report.TrendSvg));
        Write(output, Encoding.UTF8.GetBytes(report.Markdown));
        Console.WriteLine($"{output}: スナップショット {snapshots.Length}");
    }
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    return Fail(ex.Message, 1);
}

static int Fail(string message, int code)
{
    Console.Error.WriteLine(message);
    return code;
}

// A failed run leaves the previous file in place rather than a partial one.
static void Write(string path, byte[] bytes)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}
