using System.Collections.Immutable;
using System.Text.Json;
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
    internal static T Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new ArgumentException("保存内容が空です。");
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
            (sync.Unverified.IsEmpty || sync.Publish is not null) && sync.Unverified.All(id => checkpoint.Document.State.Rows.Any(r => r.Identity == id)) && !sync.Failures.IsDefault && sync.Failures.All(f => Enum.IsDefined(f.Field) && !string.IsNullOrWhiteSpace(f.Reason)) && !sync.Conflicts.IsDefault && !sync.Unavailable.IsDefault && sync.DraftCount >= 0 && sync.PullRequestCount >= 0 &&
            sync.Unavailable.Distinct().Count() == sync.Unavailable.Length && sync.Unavailable.All(id => checkpoint.Document.Baseline.Rows.Any(r => r.Identity == id)), "更新状態が不正です。");
        PlanOperations.Require(sync.Conflicts.Select(c => (c.Identity, c.Field)).Distinct().Count() == sync.Conflicts.Length &&
            sync.Conflicts.All(c => Enum.IsDefined(c.Field) && c.Field != PlanField.NewTask && c.Baseline is not null && c.Local is not null && c.Remote is not null &&
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
        var state = PlanOperations.Replay(document.State, patch, forward);
        if (document.Sync.Publish is { DispatchStarted: true } attempt)
            PlanOperations.Require(patch.BeforeSettings is null && attempt.Writes.All(w => state.Rows.Any(r => r.Identity == w.Identity)),
                "送信済みの発行対象または設定を履歴から変更できません。");
        var next = document with { State = state, Baseline = PlanOperations.ReplayBaseline(document.Baseline, patch, forward),
            Sync = document.Sync with { Conflicts = (forward ? patch.AfterConflicts : patch.BeforeConflicts) ?? document.Sync.Conflicts,
                Unavailable = (forward ? patch.AfterUnavailable : patch.BeforeUnavailable) ?? document.Sync.Unavailable } };
        // A never-dispatched record is released on reopen before any user can travel through history.
        if (next.Sync.Publish is { DispatchStarted: false } pending && pending.Writes.All(w => w.State == PlanWriteState.Pending))
            next = next with { Sync = next.Sync with { Publish = null, NotBefore = null, Unverified = [] } };
        // Use the complete load contract, including reconciliation identities, for each reachable state.
        Validate(new(1, 0, next, [], []), next.Project);
        return next;
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
