using System.Text.Json;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

// #251 D1: display preferences belong to the person and apply to every Project on this PC, so they live beside
// assist.json instead of in a plan, outside Project Undo and settings export/import. #298 adds the Gantt
// visibility to the same file; it is on by default, so a display file written before it existed still shows
// the chart and keeps its stored mark switches.
internal sealed record PlanDisplaySettings(int Version, PlanMarkVisibility Marks, bool GanttVisible = true)
{
    internal static readonly PlanDisplaySettings Default = new(1, PlanMarkVisibility.All);
}

internal sealed class PlanDisplaySettingsStore(PlanStore store)
{
    public string FilePath { get; } = Path.Combine(store.Root, "display.json");

    // Reads this PC's display preferences. While none are stored, the switches the given Project's stored plan
    // still holds (an earlier version kept them per Project) become this PC's preferences once (#251 D1).
    public async Task<PlanDisplaySettings> LoadAsync(ScopedId opening)
    {
        if (File.Exists(FilePath))
        {
            try { return Read(await File.ReadAllBytesAsync(FilePath).ConfigureAwait(false)) ?? PlanDisplaySettings.Default; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PlanDisplaySettings.Default; }
        }
        var carried = await CarriedOver(store.FileFor(opening)).ConfigureAwait(false);
        try { await SaveAsync(carried).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return carried;
    }

    // A tolerant read, not a version migration: the Gantt switch was added to an existing file, so its absence
    // means "shown" instead of making the strict plan reader discard the mark switches stored beside it.
    private static PlanDisplaySettings? Read(byte[] bytes)
    {
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject root) return null;
            if (root["version"] is not JsonValue version || !version.TryGetValue(out int number) || number != 1) return null;
            var marks = root["marks"] is JsonObject values
                ? new PlanMarkVisibility(Shown(values, "overdue"), Shown(values, "later"), Shown(values, "overEstimate"))
                : PlanMarkVisibility.All;
            var gantt = root["ganttVisible"] is JsonValue visible && visible.TryGetValue(out bool shown) ? shown : true;
            return new(1, marks, gantt);
        }
        catch (JsonException) { return null; }
        static bool Shown(JsonObject values, string name)
            => values[name] is not JsonValue value || !value.TryGetValue(out bool shown) || shown;
    }

    private static async Task<PlanDisplaySettings> CarriedOver(string planFile)
    {
        try
        {
            if (JsonNode.Parse(await File.ReadAllBytesAsync(planFile).ConfigureAwait(false))?["document"]?["state"]?["settings"] is not JsonObject settings)
                return PlanDisplaySettings.Default;
            bool Shown(string name) => settings[name] is not JsonValue value || !value.TryGetValue(out bool shown) || shown;
            return new(1, new(Shown(PlanJson.RemovedSettings[0]), Shown(PlanJson.RemovedSettings[1]), Shown(PlanJson.RemovedSettings[2])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return PlanDisplaySettings.Default; }
    }

    public async Task SaveAsync(PlanDisplaySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, PlanJson.Text(settings)).ConfigureAwait(false);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
