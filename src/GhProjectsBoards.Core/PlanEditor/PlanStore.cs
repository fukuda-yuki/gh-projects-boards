using System.Security.Cryptography;
using System.Text;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed class PlanStore(string dataRoot)
{
    public string Root => Path.Combine(dataRoot, "PlanningEditor", "v1");
    public static PlanStore ForUser() => new(RegistrationStore.ForUser().Root);
    public string FileFor(ScopedId project) => Path.Combine(Root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{project.Scope.Host}\n{project.Scope.ViewerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{project.NodeId}"))) + ".json");
    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<byte[]?> ReadExisting(string path)
    {
        try { return await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private void CheckInterrupted(string path, bool missing)
    {
        string[] candidates;
        try { candidates = Directory.GetFiles(Root, Path.GetFileName(path) + ".*.tmp"); }
        catch (DirectoryNotFoundException) { candidates = []; }
        if (candidates.Length > 0 || missing && File.Exists(path + ".bak")) throw new InvalidOperationException("中断した保存ファイルがあります。ファイルを保全して確認してください。");
    }
    public async Task<PlanLoadResult> LoadAsync(ScopedId project)
    {
        try
        {
            var path = FileFor(project); var bytes = await ReadExisting(path).ConfigureAwait(false);
            CheckInterrupted(path, bytes is null);
            if (bytes is null) return new(PlanLoadStatus.Missing, null, null, null);
            var checkpoint = PlanJson.Read<PlanCheckpoint>(bytes); PlanJson.Validate(checkpoint, project);
            return new(PlanLoadStatus.Loaded, checkpoint, Fingerprint(bytes), null);
        }
        catch (Exception ex) when (PlanJson.IsDataError(ex) || ex is IOException or UnauthorizedAccessException)
        { return new(PlanLoadStatus.Blocked, null, null, ex.Message); }
    }
    public async Task<PlanSaveResult> SaveAsync(PlanCheckpoint checkpoint, string? expectedFingerprint)
    {
        string? temporary = null;
        try
        {
            var path = FileFor(checkpoint.Document.Project);
            Directory.CreateDirectory(Root);
            using var writer = new FileStream(path + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var old = await ReadExisting(path).ConfigureAwait(false);
            CheckInterrupted(path, old is null);
            if ((old is null ? null : Fingerprint(old)) != expectedFingerprint)
                return new(false, PlanSaveFailure.ChangedFile, "保存元が別の操作で変更されました。現在の編集を保持して確認してください。", null);
            // Loaded history was validated when the session opened. Its fingerprint prevents later corruption from being replaced.
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(checkpoint, PlanJson.Options);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await WriteCandidate(temporary, bytes).ConfigureAwait(false);
            var verified = await File.ReadAllBytesAsync(temporary).ConfigureAwait(false);
            if (!bytes.AsSpan().SequenceEqual(verified)) throw new IOException("保存後の検証が一致しません。");
            _ = PlanJson.Read<PlanCheckpoint>(verified);
            // Recheck noncooperating external editors immediately before replacement as well.
            var latest = await ReadExisting(path).ConfigureAwait(false);
            if ((latest is null ? null : Fingerprint(latest)) != expectedFingerprint)
                return new(false, PlanSaveFailure.ChangedFile, "保存中に保存元が変更されました。", null);
            if (old is null) File.Move(temporary, path); else File.Replace(temporary, path, path + ".bak");
            temporary = null;
            return new(true, PlanSaveFailure.None, null, Fingerprint(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, PlanSaveFailure.Io, ex.Message, null); }
        catch (Exception ex) when (PlanJson.IsDataError(ex))
        { return new(false, PlanSaveFailure.InvalidFile, ex.Message, null); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    internal static async Task WriteCandidate(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
    internal static async Task ExportSettings(string path, ProjectPlanSettings settings)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PlanSettingsFile(1, settings), PlanJson.Options);
            await WriteCandidate(temporary, bytes).ConfigureAwait(false);
            var verified = PlanJson.Read<PlanSettingsFile>(await File.ReadAllBytesAsync(temporary).ConfigureAwait(false));
            PlanOperations.ValidateSettings(verified.Settings);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
