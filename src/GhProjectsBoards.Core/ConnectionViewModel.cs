using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.App;

internal sealed class ConnectionViewModel(Func<string, string, GhConnectionService>? serviceFactory = null) : INotifyPropertyChanged
{
    private string executablePath = FindGh();
    private string host = "github.com";
    private string issueUrl = "";
    private string projectUrl = "";
    private ConnectionContext? binding;
    private CancellationTokenSource? cancellation;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ExecutablePath { get => executablePath; set { if (executablePath == value) return; executablePath = value; InvalidateDisplay(); Changed(); } }
    public string Host { get => host; set { if (host == value) return; host = value; InvalidateDisplay(); Changed(); } }
    public string IssueUrl { get => issueUrl; set { if (issueUrl == value) return; issueUrl = value; Issue = new(); Changed(); Changed(nameof(IssueText)); } }
    public string ProjectUrl { get => projectUrl; set { if (projectUrl == value) return; projectUrl = value; Project = new(); Changed(); Changed(nameof(ProjectText)); } }
    public bool IsBusy { get; private set; }
    public bool CanCheck => !IsBusy;
    public bool CanSwitch => !IsBusy && binding is not null;
    public ConnectionReport? Connection { get; private set; }
    public TargetReport Issue { get; private set; } = new();
    public TargetReport Project { get; private set; } = new();
    public string StatusText { get; private set; } = "接続は未確認です。接続先とgh.exeを確認してください。";
    public string VersionText => Connection?.Version ?? "不明";
    public string AccountText => Connection?.Context is { } context ? $"{context.Login}  /  ID {context.ViewerId}  /  {context.Host}" : "不明";
    public string StorageText => Connection?.Authentication is { } auth ? auth.Store switch
    {
        CredentialStore.Keyring => "Windows の資格情報ストア（keyring）",
        CredentialStore.Plaintext => $"平文ファイル：{auth.StoragePath}（書き込み停止）",
        _ => "不明（書き込み停止）"
    } : "不明";
    public string ScopeText => Connection?.Authentication is { } auth
        ? $"repo：{State(auth.HasScope("repo"))}　project：{State(auth.HasScope("project"))}　read:org：{State(auth.HasScope("read:org"))}" : "不明";
    public string IssueText => TargetText(Issue);
    public string ProjectText => TargetText(Project);
    public string EnvironmentText
    {
        get
        {
            var names = GhProcessRunner.TokenVariables.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToArray();
            return names.Length == 0 ? "保存済みのgh認証を使用します。"
                : $"{string.Join(", ", names)} が設定されています。このアプリのgh呼び出しからは除外します（値は表示しません）。";
        }
    }
    public string LoginCommand => Command("login --web --skip-ssh-key --scopes 'repo,read:org,project'");
    public string RefreshCommand => Command("refresh --scopes 'repo,read:org,project'");

    public async Task CheckAsync(bool newConnection = false)
    {
        if (IsBusy) return;
        if (newConnection) binding = null;
        using var currentCancellation = new CancellationTokenSource();
        cancellation = currentCancellation;
        IsBusy = true;
        Issue = new();
        Project = new();
        StatusText = "接続を確認しています…";
        Changed(null);
        try
        {
            var path = ExecutablePath.Trim();
            var selectedHost = Host;
            var selectedIssue = IssueUrl;
            var selectedProject = ProjectUrl;
            if (path.Length == 0)
            {
                Connection = new ConnectionReport(new ApiResult(ApiOutcome.Failed, FailureKind.MissingExecutable));
                StatusText = FailureText(FailureKind.MissingExecutable);
                return;
            }
            var service = serviceFactory?.Invoke(path, selectedHost) ?? new GhConnectionService(path, selectedHost);
            Connection = binding is null ? await service.ConnectAsync(currentCancellation.Token)
                : await service.RecheckAsync(binding, currentCancellation.Token);
            if (!Connection.IsConnected)
            {
                StatusText = FailureText(Connection.Result.Failure);
                return;
            }
            binding = Connection.Context;
            Changed(null);
            var diagnostics = new TargetDiagnostics(service);
            if (!string.IsNullOrWhiteSpace(selectedIssue))
                Issue = await diagnostics.IssueAsync(binding!, Connection.Authentication!, selectedIssue, currentCancellation.Token);
            currentCancellation.Token.ThrowIfCancellationRequested();
            if (binding!.IsInvalidated)
            {
                Connection = Connection with { Result = new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged) };
                StatusText = FailureText(FailureKind.IdentityChanged);
                return;
            }
            if (!string.IsNullOrWhiteSpace(selectedProject))
                Project = await diagnostics.ProjectAsync(binding, Connection.Authentication!, selectedProject, currentCancellation.Token);
            currentCancellation.Token.ThrowIfCancellationRequested();
            if (binding.IsInvalidated)
            {
                Connection = Connection with { Result = new ApiResult(ApiOutcome.Failed, FailureKind.IdentityChanged) };
                StatusText = FailureText(FailureKind.IdentityChanged);
            }
            else if (Connection.Authentication!.Store != CredentialStore.Keyring)
                StatusText = FailureText(Connection.Authentication.Store == CredentialStore.Plaintext ? FailureKind.PlaintextCredentials : FailureKind.UnknownCredentialStore);
            else
                StatusText = Issue.Failure == FailureKind.None && Project.Failure == FailureKind.None
                    ? "接続を確認しました。対象ごとの読み取り・更新権限とスコープを確認できます。"
                    : "接続を確認しました。対象の診断に取得できない項目があります。下の理由を確認してください。";
        }
        catch (OperationCanceledException)
        {
            StatusText = FailureText(FailureKind.Cancelled);
        }
        catch (Exception)
        {
            // This is the UI boundary: never surface exception messages containing
            // request bodies, paths from remote errors, or raw process output.
            Connection = new ConnectionReport(new ApiResult(ApiOutcome.Failed, FailureKind.InvalidResponse));
            StatusText = FailureText(FailureKind.InvalidResponse);
        }
        finally
        {
            cancellation = null;
            IsBusy = false;
            Changed(null);
        }
    }

    public void Cancel() => cancellation?.Cancel();
    public void ShowClipboardFailure() { StatusText = "クリップボードへコピーできませんでした。コマンド欄を選択してコピーしてください。"; Changed(nameof(StatusText)); }

    private void InvalidateDisplay()
    {
        Connection = null;
        Issue = new();
        Project = new();
        StatusText = "接続先の入力が変わりました。再確認してください。";
        Changed(null);
    }

    public static string FindGh()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI")]);
        foreach (var directory in directories)
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), "gh.exe");
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { }
        }
        return "";
    }

    private string Command(string operation)
        => !GitHubAddress.TryHost(Host, out var normalized) || string.IsNullOrWhiteSpace(ExecutablePath) ? "gh.exeとホストを指定してください。"
            : $"& '{ExecutablePath.Replace("'", "''", StringComparison.Ordinal)}' auth {operation} --hostname '{normalized}'";
    private static string State(bool? value) => value switch { true => "あり", false => "なし", null => "不明" };
    private static string TargetText(TargetReport target)
        => $"読み取り：{State(target.CanRead)}　更新権限：{State(target.CanUpdate)}"
            + (target.RequiredWriteScope is null ? "" : $"　更新スコープ {target.RequiredWriteScope}：{State(target.HasWriteScope)}")
            + (target.Failure == FailureKind.None ? "" : $"\n{FailureText(target.Failure)}");
    internal static string FailureText(FailureKind failure) => failure switch
    {
        FailureKind.InvalidInput => "入力を確認してください。URLは接続先と同じホストのIssue／Projectを指定します。",
        FailureKind.MissingExecutable => "gh.exeが見つかりません。GitHub CLIを導入するか、実行ファイルを指定してください。",
        FailureKind.StartFailed => "gh.exeを起動できません。実行ファイルと端末の実行権限を確認してください。",
        FailureKind.NotLoggedIn => "このホストには未ログインです。下のログイン手順を実行して再確認してください。",
        FailureKind.AuthenticationExpired => "ghの認証が無効または期限切れです。ログインし直して再確認してください。",
        FailureKind.PermissionDenied => "必要なスコープまたは対象への権限がありません。",
        FailureKind.NotFoundOrInaccessible => "対象を取得できません。URL、存在、閲覧権限を確認してください。",
        FailureKind.RateLimited => "GitHubの利用制限に達しました。時間を置いて再確認してください。",
        FailureKind.Network => "GitHubとの通信に失敗しました。ネットワークを確認して再確認してください。",
        FailureKind.InvalidResponse => "ghの応答を確認できません。対応するGitHub CLIのバージョンと導入状態を確認してください。",
        FailureKind.GraphQl => "取得に一部エラーがあります。未確認の項目を成功として扱いません。",
        FailureKind.Cancelled => "確認をキャンセルしました。",
        FailureKind.TimedOut => "確認がタイムアウトしました。接続先とネットワークを確認してください。",
        FailureKind.IdentityChanged => "利用者・ホスト・実行ファイルの変更を検出し、接続を停止しました。対象を確認して「新しい接続として確認」を選んでください。",
        FailureKind.PlaintextCredentials => "資格情報が平文で保存されているため書き込みを停止しています。資格情報ストアを利用できる環境でghへログインし直してください。",
        FailureKind.UnknownCredentialStore => "資格情報の保存方式が不明なため書き込みを停止しています。",
        _ => "確認できませんでした。"
    };
    private void Changed([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
