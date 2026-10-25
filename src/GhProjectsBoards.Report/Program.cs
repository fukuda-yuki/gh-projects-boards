using System.Globalization;
using System.Text;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Reports;

const string Usage = """
    ghpb-report snapshot --project <Project URL> --settings <settings file> --data <folder>
                         [--status-date yyyy-MM-dd] [--time-zone Asia/Tokyo] [--gh <gh executable>]
    ghpb-report report --data <folder> --out <REPORT.md>
    ghpb-report record --project <Project URL> --data <folder> [--time-zone Asia/Tokyo] [--gh <gh executable>]
    ghpb-report demo --out <folder>
    ghpb-report site --data <raw folder> --config <site.json> --out <folder>
    """;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0 || args[0] is not ("snapshot" or "report" or "record" or "demo" or "site")) return Fail(Usage, 2);
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var n = 1; n < args.Length; n += 2)
{
    if (!args[n].StartsWith("--", StringComparison.Ordinal) || n + 1 >= args.Length || !options.TryAdd(args[n][2..], args[n + 1]))
        return Fail(Usage, 2);
}
string? Option(string name) => options.GetValueOrDefault(name);
try
{
    if (args[0] == "demo")
    {
        // #271: fictional demo records in the #272 format plus the site configuration, generated at build time and
        // never committed. Fixed dates and no clock, so the output is byte-identical on every run.
        if (Option("out") is not { } output) return Fail(Usage, 2);
        DemoData.Write(output, Plan("version-plan.json"), Plan("version-plan-12m.json"));
        Console.WriteLine($"{output}: デモデータを書き出しました");
    }
    else if (args[0] == "site")
    {
        // #271: reads only the raw records and the site configuration, so production can run it on data that no demo
        // generated.
        if (Option("data") is not { } data || Option("config") is not { } configPath || Option("out") is not { } output) return Fail(Usage, 2);
        SiteBuilder.Build(data, configPath, output);
        Console.WriteLine($"{output}: サイトを書き出しました");
    }
    else if (args[0] == "record")
    {
        // #272: every raw value of the Project, no settings file. The day is the read time's date in the time zone.
        if (Option("project") is not { } project || Option("data") is not { } data) return Fail(Usage, 2);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Option("time-zone") ?? "Asia/Tokyo");
        var host = Uri.TryCreate(project, UriKind.Absolute, out var uri) ? uri.Host : throw new ArgumentException("プロジェクトの URL が不正です: " + project);
        var service = new GhConnectionService(Option("gh") ?? "gh", host, timeout: TimeSpan.FromSeconds(120));
        var snapshot = await RawRecord.ReadAsync(service, project, zone, () => DateTimeOffset.UtcNow);
        var (path, items, removed) = RawStore.Write(data, snapshot);
        Console.WriteLine($"{path}: アイテム {snapshot.Items.Count} · 書いた行 {items} · 外れた {removed}");
    }
    else if (args[0] == "snapshot")
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

// The demo's waterfall shapes are embedded so the command does not depend on the working directory.
static string Plan(string name)
{
    var resource = "GhProjectsBoards.Report." + name;
    using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
        ?? throw new InvalidOperationException("デモ用の計画ファイルが見つかりません: " + resource);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}
