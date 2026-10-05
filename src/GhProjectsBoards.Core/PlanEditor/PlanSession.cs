using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal sealed class PlanSession
{
    public const int HistoryLimit = 200;
    private readonly object gate = new();
    private readonly PlanStore store;
    private PlanCheckpoint checkpoint;
    private string? fingerprint;
    private long savedRevision;
    private Task<PlanSaveResult>? pending;
    private PlanSaveResult lastSave;
    private PlanSession(PlanStore store, PlanCheckpoint checkpoint, string? fingerprint)
    {
        this.store = store; this.checkpoint = checkpoint; this.fingerprint = fingerprint;
        savedRevision = fingerprint is null ? -1 : checkpoint.Revision;
        lastSave = new(true, PlanSaveFailure.None, null, fingerprint);
    }
    public PlanDocument Document { get { lock (gate) return checkpoint.Document; } }
    public int UndoCount { get { lock (gate) return checkpoint.Undo.Length; } }
    public int RedoCount { get { lock (gate) return checkpoint.Redo.Length; } }
    public static async Task<PlanSession> CreateAsync(PlanStore store, PlanDocument document, DateOnly today)
    {
        PlanOperations.ValidateDocument(document, today);
        var loaded = await store.LoadAsync(document.Project).ConfigureAwait(false);
        if (loaded.Status != PlanLoadStatus.Missing) throw new InvalidOperationException(loaded.Error ?? "既存の計画を開いてください。");
        var session = new PlanSession(store, new(1, 0, document, [], []), null);
        await session.FlushAsync().ConfigureAwait(false);
        return session;
    }
    public static async Task<PlanOpenResult> OpenAsync(PlanStore store, ScopedId project, DateOnly today)
    {
        var result = await store.LoadAsync(project).ConfigureAwait(false);
        return new(result.Status, result.Status == PlanLoadStatus.Loaded ? new PlanSession(store, result.Checkpoint!, result.Fingerprint) : null, result.Error);
    }
    public Task<PlanSaveResult> Execute(PlanCommand command, DateOnly today)
    {
        lock (gate)
        {
            var (state, kind) = PlanOperations.Apply(checkpoint.Document, command, today);
            var patch = PlanOperations.Difference(checkpoint.Document.State, state, kind);
            if (patch.Rows.IsEmpty && patch.BeforeOrder is null && patch.BeforeSettings is null) return CurrentSave();
            var undo = checkpoint.Undo.Add(patch);
            if (undo.Length > HistoryLimit) undo = undo.RemoveAt(0);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = checkpoint.Document with { State = state }, Undo = undo, Redo = [] };
            return QueueSave();
        }
    }
    public Task<PlanSaveResult> Undo(DateOnly today) => Travel(false, today);
    public Task<PlanSaveResult> Redo(DateOnly today) => Travel(true, today);
    private Task<PlanSaveResult> Travel(bool forward, DateOnly today)
    {
        lock (gate)
        {
            var from = forward ? checkpoint.Redo : checkpoint.Undo;
            if (from.IsEmpty) return CurrentSave();
            var patch = from[^1]; var state = PlanOperations.Replay(checkpoint.Document.State, patch, forward);
            PlanOperations.ValidateDocument(checkpoint.Document with { State = state }, today);
            checkpoint = checkpoint with { Revision = checked(checkpoint.Revision + 1), Document = checkpoint.Document with { State = state },
                Undo = forward ? checkpoint.Undo.Add(patch) : checkpoint.Undo.RemoveAt(checkpoint.Undo.Length - 1),
                Redo = forward ? checkpoint.Redo.RemoveAt(checkpoint.Redo.Length - 1) : checkpoint.Redo.Add(patch) };
            return QueueSave();
        }
    }
    private Task<PlanSaveResult> CurrentSave() => pending ?? Task.FromResult(lastSave);
    private Task<PlanSaveResult> QueueSave()
    {
        if (pending is not null) return pending;
        pending = Task.Run(SaveLoop);
        return pending;
    }
    private async Task<PlanSaveResult> SaveLoop()
    {
        while (true)
        {
            PlanCheckpoint captured; string? expected;
            lock (gate) { captured = checkpoint; expected = fingerprint; }
            var result = await store.SaveAsync(captured, expected).ConfigureAwait(false);
            lock (gate)
            {
                lastSave = result;
                if (result.Succeeded) { fingerprint = result.Fingerprint; savedRevision = captured.Revision; }
                if (!result.Succeeded || savedRevision == checkpoint.Revision) { pending = null; return result; }
            }
        }
    }
    public Task<PlanSaveResult> FlushAsync()
    {
        lock (gate)
        {
            // Flush waits for work already requested; only explicit retry restarts a failed save.
            if (pending is not null || savedRevision == checkpoint.Revision || !lastSave.Succeeded) return CurrentSave();
            return QueueSave();
        }
    }
    public Task<PlanSaveResult> RetrySaveAsync() { lock (gate) return savedRevision == checkpoint.Revision ? CurrentSave() : QueueSave(); }
    public PlanUnpublished Changes(DateOnly today) => PlanOperations.Changes(Document, today);
    public IReadOnlyList<ScheduledTask> Schedule(DateOnly today) => PlanOperations.Schedule(Document, today);
    public Task ExportSettingsAsync(string path) => PlanStore.ExportSettings(path, Document.State.Settings);
    public async Task<PlanImportResult> ImportSettingsAsync(string path, DateOnly today)
    {
        try
        {
            var imported = PlanJson.Read<PlanSettingsFile>(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            PlanOperations.Require(imported.Version == 1, "未対応の設定ファイル形式です。");
            PlanOperations.ValidateSettings(imported.Settings);
            ImmutableArray<string> warnings; Task<PlanSaveResult> save;
            lock (gate)
            {
                var baseline = checkpoint.Document.Baseline;
                var knownPeople = baseline.Rows.SelectMany(r => r.Assignees).ToHashSet(); var knownFields = baseline.Columns.Select(c => c.Id).ToHashSet();
                warnings = imported.Settings.People.Where(p => !knownPeople.Contains(p.Identity)).Select(p => $"プロジェクトに未確認の担当者: {p.Name}")
                    .Concat(imported.Settings.Columns.Where(c => !knownFields.Contains(c.FieldId)).Select(c => $"プロジェクトに未確認の列: {c.Name}")).ToImmutableArray();
                save = Execute(new ReplacePlanSettings(imported.Settings), today);
            }
            return new(true, warnings, null, await save.ConfigureAwait(false));
        }
        catch (Exception ex) when (PlanJson.IsDataError(ex) || ex is IOException or UnauthorizedAccessException)
        { return new(false, [], ex.Message, null); }
    }
}
