namespace GhProjectsBoards.Core.PlanEditor;

// #275 / #286: the gh executable belongs to this PC, not to a Project, so it lives beside display.json and is
// read before any connection exists. Null means the PATH lookup.
internal sealed record PlanConnectionSettings(int Version, string? GhPath)
{
    internal static readonly PlanConnectionSettings Default = new(1, null);
}

internal sealed class PlanConnectionSettingsStore(PlanStore store)
{
    public string FilePath { get; } = Path.Combine(store.Root, "connection.json");

    // An unreadable file falls back to the PATH lookup; the connection screen then asks for the path again.
    public PlanConnectionSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return PlanConnectionSettings.Default;
            var stored = PlanJson.Read<PlanConnectionSettings>(File.ReadAllBytes(FilePath));
            return stored.Version == 1 ? stored : PlanConnectionSettings.Default;
        }
        catch (Exception ex) when (PlanJson.IsDataError(ex) || ex is IOException or UnauthorizedAccessException) { return PlanConnectionSettings.Default; }
    }

    public async Task SaveAsync(PlanConnectionSettings settings)
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
