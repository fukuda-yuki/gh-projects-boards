using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind == JsonTypeInfoKind.Object)
                foreach (var property in info.Properties) property.IsRequired = true;
        });
        return new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true, TypeInfoResolver = resolver,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    }
    internal static string Text<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Read<T>(byte[] bytes) => (Upgrade(bytes) is { } upgraded ? upgraded.Deserialize<T>(Options) : JsonSerializer.Deserialize<T>(bytes, Options))
        ?? throw new ArgumentException("保存内容が空です。");
    // #268 removed the start-constraint and fixed-date fields. A plan or settings file the previous version saved still holds
    // them as row values, column mappings, sheet columns, conflicts, failures and publish writes; dropping
    // exactly those keeps every other stored value, including unpublished edits and the history.
    private static readonly string[] RemovedFields = ["StartNoEarlierThan", "Fixed"];
    // #238 stores read observations with the plan. A plan saved before them opens with no read result and no
    // judged 未更新 instead of failing the strict format; opening rewrites nothing.
    // #251 D1 moved the lateness mark switches from Project settings to this PC's display preferences. Their
    // stored values are carried over once by PlanDisplaySettingsStore before a plan is read, then dropped here.
    internal static readonly string[] RemovedSettings = ["showOverdueMark", "showLaterMark", "showOverEstimateMark"];
    private static JsonNode? Upgrade(byte[] bytes)
    {
        var span = bytes.AsSpan();
        var removed = span.IndexOf("\"startNoEarlierThan\""u8) >= 0 || span.IndexOf("\"StartNoEarlierThan\""u8) >= 0 || span.IndexOf("\"Fixed\""u8) >= 0;
        var removedSettings = span.IndexOf("Mark\""u8) >= 0;
        var readObservations = span.IndexOf("\"progressUpdatedAt\""u8) < 0;
        // #308: a plan or settings file saved before the Issue history or the not-started Status options still
        // opens: the new observations are empty and the options keep their defaults until the file is next saved.
        var noHistory = span.IndexOf("\"issueHistory\""u8) < 0;
        var noNotStarted = span.IndexOf("\"notStartedStatuses\""u8) < 0;
        if (!removed && !removedSettings && !readObservations && !noHistory && !noNotStarted) return null;

        var root = JsonNode.Parse(bytes);
        if (readObservations || noHistory || noNotStarted) AddReadObservations(root);
        if (removed || removedSettings) Strip(root);
        return root;
        static void AddReadObservations(JsonNode? node)
        {
            if (node is JsonObject item)
            {
                if (item["sync"] is JsonObject sync)
                {
                    sync["progressUpdatedAt"] ??= new JsonObject();
                    if (!sync.ContainsKey("lastRead")) sync["lastRead"] = null;
                    if (!sync.ContainsKey("issueHistory")) sync["issueHistory"] = new JsonObject();
                }
                // Every settings object (the document's and both sides of a settings history patch) gains the
                // not-started option list. Null keeps the defaults rather than freezing the names of the day.
                if (item.ContainsKey("sheetColumns") && !item.ContainsKey("notStartedStatuses")) item["notStartedStatuses"] = null;
                foreach (var (_, child) in item.ToArray()) AddReadObservations(child);
            }
            else if (node is JsonArray list)
                foreach (var child in list.ToArray()) AddReadObservations(child);
        }
        static void Strip(JsonNode? node)
        {
            if (node is JsonObject item)
            {
                item.Remove("startNoEarlierThan"); item.Remove("fixed");
                foreach (var name in RemovedSettings) item.Remove(name);
                foreach (var (_, child) in item.ToArray()) Strip(child);
            }
            else if (node is JsonArray list)
            {
                foreach (var child in list.ToArray())
                    if (child is JsonObject entry && new[] { "role", "key", "field" }.Any(name => entry[name] is JsonValue value
                        && value.TryGetValue(out string? text) && RemovedFields.Contains(text))) list.Remove(child);
                    else Strip(child);
            }
        }
    }
    internal static void Validate(PlanCheckpoint checkpoint, ScopedId project)
    {
        PlanOperations.Require(checkpoint.Version == 1 && checkpoint.Revision >= 0 && checkpoint.Document.Project == project &&
            !checkpoint.Undo.IsDefault && !checkpoint.Redo.IsDefault && checkpoint.Undo.Length + checkpoint.Redo.Length <= PlanSession.HistoryLimit,
            "保存形式、プロジェクトまたは履歴数が不正です。");
        // A stable date is sufficient for structural validation; today is supplied when displaying the plan.
        var day = checkpoint.Document.State.Settings.StatusDate ?? new DateOnly(2000, 1, 1);
        PlanOperations.ValidateDocument(checkpoint.Document, day);
        var sync = checkpoint.Document.Sync;
        PlanOperations.Require(!sync.Unverified.IsDefault && sync.Unverified.Distinct().Count() == sync.Unverified.Length &&
            (sync.Unverified.IsEmpty || sync.Publish is not null) && sync.Unverified.All(id => checkpoint.Document.State.Rows.Any(r => r.Identity == id)) && !sync.Failures.IsDefault && sync.Failures.All(f => PlanField.IsDefined(f.Field) && !string.IsNullOrWhiteSpace(f.Reason)) && !sync.Conflicts.IsDefault && !sync.Unavailable.IsDefault && sync.InaccessibleCount >= 0 && sync.DraftCount >= 0 && sync.PullRequestCount >= 0 &&
            sync.Unavailable.Distinct().Count() == sync.Unavailable.Length && sync.Unavailable.All(id => checkpoint.Document.Baseline.Rows.Any(r => r.Identity == id)) &&
            !sync.CannotUpdate.IsDefault && sync.CannotUpdate.Distinct().Count() == sync.CannotUpdate.Length &&
            // #238: read observations stay a set. They may name rows a later local delete removed, like the
            // latest read's placed Issues, so their membership is not part of the stored invariant.
            sync.ProgressUpdatedAt is not null &&
            // #308: like the progress observations, history keys may name a row a later local delete removed.
            sync.IssueHistory is not null && sync.IssueHistory.Values.All(h => !h.Events.IsDefault) &&
            (sync.LastRead is not { } lastRead || lastRead.Added.Distinct().Count() == lastRead.Added.Length), "更新状態が不正です。");
        PlanOperations.Require(sync.Conflicts.Select(c => (c.Identity, c.Field)).Distinct().Count() == sync.Conflicts.Length &&
            sync.Conflicts.All(c => PlanField.IsDefined(c.Field) && c.Field != PlanField.NewTask && c.Baseline is not null && c.Local is not null && c.Remote is not null &&
                (c.DeletedFieldName is null || PlanSheetColumns.IsExtra(c.Field) && c.Remote == "null" && !checkpoint.Document.Baseline.Columns.Any(f => "Project:" + f.Id == c.Field)) &&
                (c.Field == PlanField.Order ? c.Identity == "" : checkpoint.Document.State.Rows.Any(r => r.Identity == c.Identity))), "競合状態が不正です。");
        PlanOperations.Require(!sync.CreationStarts.IsDefault && sync.CreationStarts.All(s => s.Issues is >= 1 and <= 10) &&
            sync.CreationStarts.Select(s => s.Started).SequenceEqual(sync.CreationStarts.Select(s => s.Started).Order()), "作成ペースの記録が不正です。");
        for (var i = 0; i < sync.CreationStarts.Length; i++)
            PlanOperations.Require(PlanCreationPacing.NextStart(sync.CreationStarts.Take(i).ToImmutableArray(), sync.CreationStarts[i].Issues, sync.CreationStarts[i].Started) <= sync.CreationStarts[i].Started,
                "作成ペースの上限を超えています。");
        if (sync.Publish is { } publish) PlanPublishPlan.Validate(publish, checkpoint.Document);
        foreach (var (patches, forward) in new[] { (checkpoint.Undo, false), (checkpoint.Redo, true) })
        {
            var document = checkpoint.Document;
            for (var i = patches.Length - 1; i >= 0; i--)
                document = ReplayHistory(document, patches[i], forward);
        }
    }
    private static PlanDocument ReplayHistory(PlanDocument document, PlanPatch patch, bool forward)
    {
        var next = Step(document, patch, forward);
        // Use the complete load contract, including reconciliation identities, for each reachable state.
        Validate(new(1, 0, next, [], []), next.Project);
        return next;
    }
    private static PlanDocument Step(PlanDocument document, PlanPatch patch, bool forward)
    {
        var state = PlanOperations.Replay(document.State, patch, forward);
        if (document.Sync.Publish is { DispatchStarted: true } attempt)
            PlanOperations.Require(patch.BeforeSettings is null && attempt.Writes.All(w => state.Rows.Any(r => r.Identity == w.Identity)),
                "送信済みの発行対象または設定を履歴から変更できません。");
        var next = document with { State = state, Baseline = PlanOperations.ReplayBaseline(document.Baseline, patch, forward),
            Sync = document.Sync with { LocalOrderChoices = (forward ? patch.AfterLocalOrderChoices : patch.BeforeLocalOrderChoices) ?? document.Sync.LocalOrderChoices, Conflicts = (forward ? patch.AfterConflicts : patch.BeforeConflicts) ?? document.Sync.Conflicts,
                Unavailable = (forward ? patch.AfterUnavailable : patch.BeforeUnavailable) ?? document.Sync.Unavailable } };
        // A never-dispatched record is released on reopen before any user can travel through history.
        if (next.Sync.Publish is { DispatchStarted: false } pending && pending.Writes.All(w => w.State == PlanWriteState.Pending))
            next = next with { Sync = next.Sync with { Publish = null, NotBefore = null, Unverified = [] } };
        return next;
    }
    // #256: replaying and validating the whole history costs one full document validation per entry, on
    // every command. A command, Undo or Redo puts one patch on top of a history that was already replayable
    // (load and every save keep it so). When stepping that patch back from the new document reproduces the
    // previous document, every older state is unchanged and nothing needs replaying. Failures are left out:
    // they name only a field and a reason, so they are valid in every state, and edits only narrow them.
    // Anything else (a publish attempt, a baseline or list the patch does not restore exactly) takes the
    // full RetainReplayableHistory.
    internal static bool Restores(PlanDocument before, PlanDocument after, PlanPatch patch, bool forward)
    {
        if (before.Sync.Publish is not null || after.Sync.Publish is not null) return false;
        PlanDocument back;
        try { back = Step(after, patch, !forward); }
        catch (Exception ex) when (IsDataError(ex)) { return false; }
        static bool Same(ImmutableArray<PlanRow> a, ImmutableArray<PlanRow> b)
            => a.Length == b.Length && a.Zip(b).All(p => PlanOperations.RowEqual(p.First, p.Second));
        return Same(back.State.Rows, before.State.Rows) && PlanOperations.SettingsEqual(back.State.Settings, before.State.Settings)
            && back.State.ArchivedIdentities.SequenceEqual(before.State.ArchivedIdentities) && Same(back.Baseline.Rows, before.Baseline.Rows)
            && (back with { State = before.State, Baseline = back.Baseline with { Rows = before.Baseline.Rows }, Sync = back.Sync with { Failures = before.Sync.Failures } }) == before;
    }
    internal static PlanCheckpoint RetainReplayableHistory(PlanCheckpoint checkpoint)
    {
        ImmutableArray<PlanPatch> Trim(ImmutableArray<PlanPatch> patches, bool forward)
        {
            var document = checkpoint.Document;
            for (var i = patches.Length - 1; i >= 0; i--)
            {
                try { document = ReplayHistory(document, patches[i], forward); }
                catch (Exception ex) when (IsDataError(ex)) { return patches.Skip(i + 1).ToImmutableArray(); }
            }
            return patches;
        }
        return checkpoint with { Undo = Trim(checkpoint.Undo, false), Redo = Trim(checkpoint.Redo, true) };
    }
    internal static bool IsDataError(Exception ex) => ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException;
}
