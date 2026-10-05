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
        foreach (var (patches, forward) in new[] { (checkpoint.Undo, false), (checkpoint.Redo, true) })
        {
            var state = checkpoint.Document.State;
            for (var i = patches.Length - 1; i >= 0; i--)
            {
                state = PlanOperations.Replay(state, patches[i], forward);
                PlanOperations.ValidateDocument(checkpoint.Document with { State = state }, day);
            }
        }
    }
    internal static bool IsDataError(Exception ex) => ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException or NullReferenceException;
}
