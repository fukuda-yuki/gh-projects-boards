using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

internal enum AssistMode { None, Copilot, Byok }
internal enum AssistProvider { Openai, Anthropic, Azure }
internal enum AssistWireApi { Completions, Responses }

// Secrets belong to the App's credential store, never to this serializable contract.
internal sealed record AssistSettings
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public AssistMode Mode { get; init; }
    public string Model { get; init; } = "";
    public AssistProvider Provider { get; init; }
    public string Endpoint { get; init; } = "";
    public AssistWireApi WireApi { get; init; }
    public string? ConversationIdHeader { get; init; }

    public void Validate()
    {
        if (Version != 1 || !Enum.IsDefined(Mode) || !Enum.IsDefined(Provider) || !Enum.IsDefined(WireApi)
            || Model is null || Endpoint is null) throw new ArgumentException("AI アシストの設定形式が不正です。");
        if (Mode != AssistMode.None && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("モデルを入力してください。");
        if ((Mode == AssistMode.Byok || Endpoint.Length > 0) && (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            throw new ArgumentException("認証情報を含まないエンドポイント URL を入力してください。");
        if (ConversationIdHeader is { Length: > 0 } header &&
            (!Regex.IsMatch(header, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") ||
             new[] { "authorization", "proxy-authorization", "host", "content-length", "content-type", "cookie", "api-key", "x-api-key" }.Contains(header, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("会話 ID ヘッダーには認証や通信制御以外のヘッダー名を入力してください。");
    }
}
internal sealed record AssistSettingsLoad(AssistSettings Settings, string? Error);

internal sealed class AssistSettingsStore(string dataRoot)
{
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }, WriteIndented = true
    };
    public string FilePath { get; } = Path.Combine(new PlanStore(dataRoot).Root, "assist.json");
    public static AssistSettingsStore ForUser() => new(PlanStore.UserDataRoot);
    internal static AssistSettings Parse(byte[] bytes)
    {
        var settings = JsonSerializer.Deserialize<AssistSettings>(bytes, Json) ?? throw new JsonException();
        settings.Validate(); return settings;
    }
    public async Task<AssistSettingsLoad> LoadAsync()
    {
        try {
            var bytes = await File.ReadAllBytesAsync(FilePath).ConfigureAwait(false);
            return new(Parse(bytes), null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(new(), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) {
            // Do not echo file contents: an invalid file may contain a misplaced secret.
            return new(new(), "AI アシストの設定を読み込めません。使わないとして開きます。assist.json を保全して確認してください。");
        }
    }
    public async Task SaveAsync(AssistSettings settings)
    {
        settings.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var writer = new FileStream(FilePath + ".writer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if ((await LoadAsync().ConfigureAwait(false)).Error is { } error) throw new InvalidOperationException(error);
        var previous = File.Exists(FilePath) ? await File.ReadAllBytesAsync(FilePath).ConfigureAwait(false) : null;
        if (previous is not null) {
            try { _ = Parse(previous); }
            catch (Exception ex) when (ex is JsonException or ArgumentException) {
                throw new InvalidOperationException("AI アシストの設定を読み込めません。assist.json を保全して確認してください。");
            }
        }
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await PlanStore.WriteCandidate(temporary, JsonSerializer.SerializeToUtf8Bytes(settings, Json)).ConfigureAwait(false);
            var current = File.Exists(FilePath) ? await File.ReadAllBytesAsync(FilePath).ConfigureAwait(false) : null;
            if (previous is null ? current is not null : current is null || !previous.AsSpan().SequenceEqual(current))
                throw new InvalidOperationException("AI アシストの設定が別の操作で変更されました。読み直してください。");
            if (previous is null) File.Move(temporary, FilePath); else File.Replace(temporary, FilePath, null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
