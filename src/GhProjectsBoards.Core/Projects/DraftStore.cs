using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { info =>
        {
            // Optional constructor defaults are for deliberate new input, not
            // permission to reconstruct missing persisted Manual/weight data.
            if (info.Type == typeof(ProjectPlanning) || info.Type == typeof(PlanningTask) || info.Type == typeof(PlanningPerson)
                || info.Type == typeof(PlanningCalendar) || info.Type == typeof(PlanningFieldBinding) || info.Type == typeof(PlanningLink)
                || info.Type == typeof(ActualContribution) || info.Type == typeof(WorkContribution)
                || info.Type == typeof(CalendarException) || info.Type == typeof(WorkingInterval)
                || info.Type == typeof(HolidayPreset) || info.Type == typeof(HolidayDate))
                foreach (var property in info.Properties) property.IsRequired = true;
        } } }
    };
    public string FileFor(ConnectionScope scope) => Path.Combine(root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope.Host}\n{scope.ViewerId}"))) + ".json");
    public IDisposable AcquireExecution(ConnectionScope scope)
    {
        Directory.CreateDirectory(root);
        return new FileStream(FileFor(scope) + ".execution.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task<(DraftRecord[] Records, StorageProblem[] Problems)> CheckpointsAsync()
    {
        var records = new List<DraftRecord>(); var problems = new List<StorageProblem>();
        if (!Directory.Exists(root)) return ([], []);
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var record = await ReadAsync(file);
                if (FileFor(record.Scope) != file) throw new InvalidDataException("Checkpoint filename mismatch.");
                if (record.Registrations is not null) records.Add(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { problems.Add(new(Path.GetFileName(file), "InvalidCheckpoint")); }
        }
        foreach (var file in Directory.EnumerateFiles(root).Where(f => f.EndsWith(".bak") || f.EndsWith(".tmp")))
        {
            var name = Path.GetFileName(file); var end = name.IndexOf(".json", StringComparison.Ordinal);
            if (end >= 0 && !File.Exists(Path.Combine(root, name[..(end + 5)]))) problems.Add(new(name[..(end + 5)], "InterruptedCheckpoint"));
        }
        return (records.ToArray(), problems.ToArray());
    }
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
    public async Task ExportBackupAsync(ConnectionScope scope, string destination)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("An absolute backup filename is required.");
        using var execution = AcquireExecution(scope);
        var record = await LoadAsync(scope) ?? throw new InvalidDataException("No saved checkpoint to export.");
        await using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, record, Json); await stream.FlushAsync(); stream.Flush(true);
        }
        _ = await ReadAsync(destination);
    }
    public async Task RestoreBackupAsync(string source, ConnectionScope expectedScope)
    {
        if (!Path.IsPathFullyQualified(source)) throw new ArgumentException("An absolute backup filename is required.");
        // Restore is deliberately a new-root operation. Never merge another
        // profile, replace pending work, or reactivate a second live executor.
        if (Directory.Exists(registrationRoot) && Directory.EnumerateFileSystemEntries(registrationRoot).Any())
            throw new InvalidOperationException("Restore requires an empty data root.");
        var record = await ReadAsync(source);
        if (record.Scope != expectedScope) throw new InvalidDataException("Backup account/host mismatch.");
        await SaveAsync(record, 0);
    }
    public async Task SaveAsync(DraftRecord record, long expectedRevision, Func<bool>? canCommit = null)
    {
        using var measured = PerformanceTrace.Span("checkpoint-save");
        using (PerformanceTrace.Span("checkpoint-validation-sync")) Validate(record);
        using (PerformanceTrace.Span("checkpoint-store-gate-wall")) await saves.WaitAsync();
        try
        {
            Directory.CreateDirectory(root);
            using var profileGate = new FileStream(Path.Combine(registrationRoot, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var gate = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var file = FileFor(record.Scope);
            DraftRecord? current;
            using (PerformanceTrace.Span("checkpoint-current-load-wall")) current = await LoadAsync(record.Scope);
            if ((current?.Revision ?? 0) != expectedRevision || record.Revision < expectedRevision)
                throw new InvalidDataException("Stale draft revision; reopen after preserving local work.");
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (PerformanceTrace.Span("checkpoint-serialize-write-wall")) await JsonSerializer.SerializeAsync(stream, record, Json);
                using (PerformanceTrace.Span("checkpoint-stream-flush-wall")) await stream.FlushAsync();
                using (PerformanceTrace.Span("checkpoint-durable-flush-sync")) stream.Flush(true);
                PerformanceTrace.Count("checkpoint-bytes", stream.Position);
            }
            using (PerformanceTrace.Span("checkpoint-temp-readback-wall")) _ = await ReadAsync(temp);
            if (canCommit is not null && !canCommit()) throw new InvalidDataException("Checkpoint changed before commit.");
            using (PerformanceTrace.Span("checkpoint-replace-sync"))
            {
                if (File.Exists(file)) File.Replace(temp, file, file + ".bak"); else File.Move(temp, file);
            }
            PerformanceTrace.Count("checkpoint-commits");

        }
        finally { saves.Release(); }
    }
    private static async Task<DraftRecord> ReadAsync(string file)
    {
        await using var stream = File.OpenRead(file);
        DraftRecord record;
        using (PerformanceTrace.Span("checkpoint-deserialize-read-wall"))
            record = await JsonSerializer.DeserializeAsync<DraftRecord>(stream, Json) ?? throw new InvalidDataException("Missing draft record.");
        using (PerformanceTrace.Span("checkpoint-read-validation-sync")) Validate(record);
        return record;
    }
    private static void ValidateLocalRows(DraftRecord r)
    {
        if (r.Version >= 4 && r.LocalRows is null) throw new InvalidDataException("Missing local row payload.");
        if (r.Version < 4 && (r.LocalRows is { Length: > 0 } || r.History.Any(t => t?.Rows is { Length: > 0 })))
            throw new InvalidDataException("Unversioned local rows.");
        bool Valid(LocalRow? row) => row is not null && row.Id.StartsWith("local-", StringComparison.Ordinal)
            && Guid.TryParseExact(row.Id[6..], "N", out _) && !string.IsNullOrWhiteSpace(row.ProjectId)
            && row.Title is not null && row.Repository is not null && row.Stamp >= 0 && row.Stamp <= r.Revision
            && row.CreatedRevision > 0 && row.CreatedRevision <= row.Stamp && row.Ordinal >= 0
            && !row.Selects.IsDefault && row.Selects.All(s => s is not null && !string.IsNullOrWhiteSpace(s.FieldId)
                && s.FieldName is not null && (s.OptionId is null ? s.OptionName is null : !string.IsNullOrWhiteSpace(s.OptionId) && s.OptionName is not null))
            && row.Selects.Select(s => s.FieldId).Distinct().Count() == row.Selects.Length;
        var rows = r.LocalRows ?? [];
        if (rows.Any(row => !Valid(row)) || rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            throw new InvalidDataException("Invalid local rows.");
        foreach (var t in r.History.Where(t => t is not null))
        {
            var changes = t.Rows ?? [];
            if (changes.Select(c => c?.Id).Distinct().Count() != changes.Length || changes.Any(c => c is null
                || c.Position < 0 || c.Before is null && c.After is null
                || c.Before is not null && (!Valid(c.Before) || c.Before.Id != c.Id || c.Before.ProjectId != t.ProjectId)
                || c.After is not null && (!Valid(c.After) || c.After.Id != c.Id || c.After.ProjectId != t.ProjectId)
                || c.Before is not null && c.After is not null && (c.Before.Stamp >= c.After.Stamp
                    || c.Before.CreatedRevision != c.After.CreatedRevision || c.Before.Ordinal != c.After.Ordinal))
                || t.Resolution && changes.Length > 0)
                throw new InvalidDataException("Invalid local row transaction.");
        }
        var all = rows.Concat(r.History.Where(t => t is not null).SelectMany(t => t.Rows ?? [])
            .SelectMany(c => new[] { c.Before, c.After }).OfType<LocalRow>()).ToArray();
        if (!rows.SequenceEqual(rows.OrderBy(row => row.CreatedRevision).ThenBy(row => row.Ordinal))
            || all.GroupBy(row => row.Id).Any(g => g.Select(row => (row.CreatedRevision, row.Ordinal, row.ProjectId)).Distinct().Count() != 1)
            || all.GroupBy(row => (row.CreatedRevision, row.Ordinal)).Any(g => g.Select(row => row.Id).Distinct().Count() != 1))
            throw new InvalidDataException("Inconsistent local row placement.");
    }
    internal static void Validate(DraftRecord r)
    {
        if (r.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8) || r.Revision < 0 || r.Scope is null || !GitHubAddress.TryHost(r.Scope.Host, out var host)
            || host != r.Scope.Host || r.Scope.ViewerId <= 0 || r.Fields is null || r.History is null)
            throw new InvalidDataException("Invalid draft schema.");
        ApplyJournal.Validate(r);
        if (r.Version >= 6 && r.ColumnPreferences is null || r.Version < 6 && r.ColumnPreferences is { Length: > 0 }) throw new InvalidDataException("Invalid column schema version.");
        EditingWorkspace.ValidateColumns(r.ColumnPreferences ?? []);
        if (r.Version >= 7 && r.RowPreferences is null || r.Version < 7 && r.RowPreferences is { Length: > 0 }) throw new InvalidDataException("Invalid row schema version.");
        EditingWorkspace.ValidateRowPreferences(r.RowPreferences ?? []);
        if (r.Version >= 8 && r.Planning is null || r.Version < 8 && r.Planning is { Length: > 0 }) throw new InvalidDataException("Invalid planning schema version.");
        foreach (var plan in r.Planning ?? []) PlanningContract.Validate(plan, r.Revision);
        if ((r.Planning ?? []).Select(p => p.ProjectId).Distinct().Count() != (r.Planning ?? []).Length) throw new InvalidDataException("Duplicate Project plan.");
        ValidateLocalRows(r);
        bool Key(FieldKey? k) => k is not null && !string.IsNullOrWhiteSpace(k.NodeId)
            && (k.Kind == "Title" ? k.ProjectId is null && k.FieldId is null : k.Kind == "Select" && !string.IsNullOrWhiteSpace(k.ProjectId) && !string.IsNullOrWhiteSpace(k.FieldId));
        bool Field(DraftField? f) => f is not null && Key(f.Key) && f.SourceProject is not null && f.SourceProject.Scope == r.Scope
            && (f.Key.Kind != "Select" || f.SourceProject.NodeId == f.Key.ProjectId)
            && !string.IsNullOrWhiteSpace(f.SourceProject.NodeId) && f.RetrievedAt != default && f.Stamp >= 0 && f.Stamp <= r.Revision
            && (f.Key.Kind != "Title" || !string.IsNullOrWhiteSpace(f.Baseline))
            && (f.Key.Kind != "Title" || f.Change?.Value is not { } title || title.IndexOfAny(['\r','\n','\t']) < 0)
            && (f.Change is null || f.Change.Value != f.Baseline && (f.Change.Clear ? f.Key.Kind == "Select" && f.Change.Value is null : !string.IsNullOrWhiteSpace(f.Change.Value)));
        if (r.Fields.Any(f => !Field(f)) || r.Fields.Select(f => f.Key).Distinct().Count() != r.Fields.Length
            || r.History.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.ProjectId) || t.Changes is null || t.Changes.Length == 0 && t.Rows is not { Length: > 0 }
                || t.Changes.Any(c => c is null || c.Before is null || c.After is null)
                || t.Changes.Select(c => c?.Key).Distinct().Count() != t.Changes.Length
                || t.Changes.Any(c => c is null || !Key(c.Key) || !Field(c.Before) || !Field(c.After) || c.Key != c.Before.Key || c.Key != c.After.Key
                    || c.Key.Kind == "Select" && c.Key.ProjectId != t.ProjectId
                    || c.Before.Stamp >= c.After.Stamp || t.Changes.Any(other => other.After.Stamp != c.After.Stamp)
                    || t.Resolution && (!c.Before.Conflict || c.After.Conflict || c.Before.Observation is null
                        || c.Before.Observation.Id != c.After.Observation?.Id || c.After.Baseline != c.Before.Observation.Value)
                    || !r.Fields.Any(f => f.Key == c.Key) || !t.Resolution && c.Before.Baseline != c.After.Baseline || c.Before.SourceProject != c.After.SourceProject))
            || r.History.Select(t => t.Id).Distinct().Count() != r.History.Length)
            throw new InvalidDataException("Inconsistent draft record.");
        foreach (var f in r.Fields.Concat(r.History.SelectMany(t => t.Changes.SelectMany(c => new[] { c.Before, c.After }))))
        {
            if (f.Conflict && (f.Observation is null || f.Change is null) || f.Observation is { } o && (string.IsNullOrWhiteSpace(o.Id) || o.Project.Scope != r.Scope
                || o.At == default || !Enum.IsDefined(o.Availability) || o.Options is null || o.Options.Any(x => x is null)
                || string.IsNullOrWhiteSpace(o.Project.NodeId) || f.Key.Kind == "Select" && o.Project.NodeId != f.Key.ProjectId
                || o.Options.Any(x => string.IsNullOrWhiteSpace(x.Id)) || o.Options.Select(x => x.Id).Distinct().Count() != o.Options.Length
                || o.Availability is not (ValueAvailability.Present or ValueAvailability.Empty) && o.Reason is null
                || o.Availability == ValueAvailability.Empty && (o.Value is not null || f.Key.Kind == "Title")
                || o.Availability == ValueAvailability.Present && string.IsNullOrWhiteSpace(o.Value))) throw new InvalidDataException("Invalid reconciliation observation.");
        }
        if (r.Registrations is { } records)
        {
            var projects = records.Select(RegistrationStore.FromRecord).ToArray();
            if (projects.Any(p => p.Snapshot.Id.Scope != r.Scope) || projects.Select(p => p.Snapshot.Id).Distinct().Count() != projects.Length)
                throw new InvalidDataException("Checkpoint registration identity mismatch.");
        }
    }
}

// The UI mutates buffers synchronously; serialized saves capture current state after acquiring the gate.
internal sealed class DraftSession(DraftStore store, EditingWorkspace workspace, long durableRevision)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly string recoveryNotice = store.HasInterruptedSave(workspace.Scope) ? " / 中断保存ファイルを保持しています（要確認）" : "";
    public EditingWorkspace Workspace { get; private set; } = workspace;
    public long DurableRevision { get; private set; } = durableRevision;
    public string Status { get; private set; } = store.HasInterruptedSave(workspace.Scope) ? "中断保存ファイルを保持しています。最後の確定済みデータを復元しました。" : "ローカル保存済み（GitHub未反映）";
    public event Action? Changed;
    public async Task<bool> CommitAsync(Func<EditingWorkspace, EditingWorkspace> prepare, Func<bool> canCommit)
    {
        using (PerformanceTrace.Span("draft-commit-gate-wall")) await gate.WaitAsync();
        try
        {
            var original = Workspace; var revision = original.Revision;
            EditingWorkspace candidate;
            using (PerformanceTrace.Span("draft-candidate-prepare-sync")) candidate = prepare(EditingWorkspace.Restore(original.Snapshot()));
            DraftRecord snapshot;
            using (PerformanceTrace.Span("draft-candidate-snapshot-sync")) snapshot = candidate.Snapshot();
            await store.SaveAsync(snapshot, DurableRevision, () => canCommit() && Workspace == original && original.Revision == revision);
            Workspace = candidate; DurableRevision = candidate.Revision;
            Status = "照合結果をローカル保存しました（GitHub未反映）";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        { Status = "ローカル保存失敗または比較後の変更。元の作業を保持しました。再試行してください。"; return false; }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    public async Task<bool> FlushAsync()
    {
        PerformanceTrace.Count("draft-flush-requests");
        using (PerformanceTrace.Span("draft-flush-gate-wall")) await gate.WaitAsync();
        try
        {
            while (DurableRevision != Workspace.Revision)
            {
                PerformanceTrace.Count("draft-flush-iterations");
                Status = "ローカル保存中…";
                using (PerformanceTrace.Span("draft-saving-notification-sync")) Changed?.Invoke();
                DraftRecord snapshot;
                using (PerformanceTrace.Span("draft-snapshot-sync")) snapshot = Workspace.Snapshot();
                await store.SaveAsync(snapshot, DurableRevision);
                DurableRevision = snapshot.Revision;
            }
            Status = "ローカル保存済み（GitHub未反映）" + recoveryNotice;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { Status = "ローカル保存失敗。文字は保持しています。保存先を確認し再試行してください。"; return false; }
        finally
        {
            gate.Release();
            using (PerformanceTrace.Span("draft-settled-notification-sync")) Changed?.Invoke();
        }
    }
}
