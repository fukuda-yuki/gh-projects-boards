using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed class DraftStore(string registrationRoot)
{
    private readonly string root = Path.Combine(registrationRoot, "Drafts");
    private readonly SemaphoreSlim saves = new(1);
    public bool HasInterruptedSave(ConnectionScope scope) => Directory.Exists(root) && Directory.EnumerateFiles(root, Path.GetFileName(FileFor(scope)) + ".*.tmp").Any();
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, WriteIndented = true
    };
    public string FileFor(ConnectionScope scope) => Path.Combine(root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope.Host}\n{scope.ViewerId}"))) + ".json");
    public async Task<DraftRecord?> LoadAsync(ConnectionScope scope)
    {
        var file = FileFor(scope);
        if (!File.Exists(file))
        {
            if (File.Exists(file + ".bak") || Directory.Exists(root) && Directory.EnumerateFiles(root, Path.GetFileName(file) + ".*.tmp").Any())
                throw new InvalidDataException("Interrupted draft save; retain recovery files.");
            return null;
        }
        var record = await ReadAsync(file);
        if (record.Scope != scope) throw new InvalidDataException("Draft scope mismatch.");
        return record;
    }
    public async Task SaveAsync(DraftRecord record, long expectedRevision)
    {
        Validate(record);
        await saves.WaitAsync();
        try
        {
            Directory.CreateDirectory(root);
            using var gate = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var file = FileFor(record.Scope);
            var current = await LoadAsync(record.Scope);
            if ((current?.Revision ?? 0) != expectedRevision || record.Revision < expectedRevision)
                throw new InvalidDataException("Stale draft revision; reopen after preserving local work.");
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, Json);
                await stream.FlushAsync(); stream.Flush(true);
            }
            _ = await ReadAsync(temp);
            if (File.Exists(file)) File.Replace(temp, file, file + ".bak"); else File.Move(temp, file);
        }
        finally { saves.Release(); }
    }
    private static async Task<DraftRecord> ReadAsync(string file)
    {
        await using var stream = File.OpenRead(file);
        var record = await JsonSerializer.DeserializeAsync<DraftRecord>(stream, Json) ?? throw new InvalidDataException("Missing draft record.");
        Validate(record); return record;
    }
    internal static void Validate(DraftRecord r)
    {
        if (r.Version != 1 || r.Revision < 0 || r.Scope is null || !GitHubAddress.TryHost(r.Scope.Host, out var host)
            || host != r.Scope.Host || r.Scope.ViewerId <= 0 || r.Fields is null || r.History is null)
            throw new InvalidDataException("Invalid draft schema.");
        bool Key(FieldKey? k) => k is not null && !string.IsNullOrWhiteSpace(k.NodeId)
            && (k.Kind == "Title" ? k.ProjectId is null && k.FieldId is null : k.Kind == "Select" && !string.IsNullOrWhiteSpace(k.ProjectId) && !string.IsNullOrWhiteSpace(k.FieldId));
        bool Field(DraftField? f) => f is not null && Key(f.Key) && f.SourceProject is not null && f.SourceProject.Scope == r.Scope
            && (f.Key.Kind != "Select" || f.SourceProject.NodeId == f.Key.ProjectId)
            && !string.IsNullOrWhiteSpace(f.SourceProject.NodeId) && f.RetrievedAt != default && f.Stamp >= 0 && f.Stamp <= r.Revision
            && (f.Key.Kind != "Title" || !string.IsNullOrWhiteSpace(f.Baseline))
            && (f.Key.Kind != "Title" || f.Change?.Value is not { } title || title.IndexOfAny(['\r','\n','\t']) < 0)
            && (f.Change is null || f.Change.Value != f.Baseline && (f.Change.Clear ? f.Key.Kind == "Select" && f.Change.Value is null : !string.IsNullOrWhiteSpace(f.Change.Value)));
        if (r.Fields.Any(f => !Field(f)) || r.Fields.Select(f => f.Key).Distinct().Count() != r.Fields.Length
            || r.History.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.ProjectId) || t.Changes is null || t.Changes.Length == 0
                || t.Changes.Select(c => c?.Key).Distinct().Count() != t.Changes.Length
                || t.Changes.Any(c => c is null || !Key(c.Key) || !Field(c.Before) || !Field(c.After) || c.Key != c.Before.Key || c.Key != c.After.Key
                    || c.Key.Kind == "Select" && c.Key.ProjectId != t.ProjectId
                    || c.Before.Stamp >= c.After.Stamp || t.Changes.Any(other => other.After.Stamp != c.After.Stamp)
                    || !r.Fields.Any(f => f.Key == c.Key) || c.Before.Baseline != c.After.Baseline || c.Before.SourceProject != c.After.SourceProject))
            || r.History.Select(t => t.Id).Distinct().Count() != r.History.Length)
            throw new InvalidDataException("Inconsistent draft record.");
    }
}

// The UI mutates buffers synchronously; serialized saves capture current state after acquiring the gate.
internal sealed class DraftSession(DraftStore store, EditingWorkspace workspace, long durableRevision)
{
    private readonly SemaphoreSlim gate = new(1);
    public EditingWorkspace Workspace { get; } = workspace;
    public long DurableRevision { get; private set; } = durableRevision;
    public string Status { get; private set; } = store.HasInterruptedSave(workspace.Scope) ? "中断保存ファイルを保持しています。最後の確定済みデータを復元しました。" : "ローカル保存済み（GitHub未反映）";
    public event Action? Changed;
    public async Task<bool> FlushAsync()
    {
        await gate.WaitAsync();
        try
        {
            while (DurableRevision != Workspace.Revision)
            {
                Status = "ローカル保存中…"; Changed?.Invoke();
                var snapshot = Workspace.Snapshot();
                await store.SaveAsync(snapshot, DurableRevision);
                DurableRevision = snapshot.Revision;
            }
            Status = "ローカル保存済み（GitHub未反映）";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { Status = "ローカル保存失敗。文字は保持しています。保存先を確認し再試行してください。"; return false; }
        finally { gate.Release(); Changed?.Invoke(); }
    }
}
