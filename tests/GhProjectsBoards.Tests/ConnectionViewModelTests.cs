using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class ConnectionViewModelTests
{
    [Test]
    public void FailureWordingStaysTheSentenceTheAppShows()
    {
        var expected = new Dictionary<FailureKind, string>
        {
            [FailureKind.None] = "確認できませんでした。",
            [FailureKind.InvalidInput] = "入力を確認してください。URLは接続先と同じホストのIssue／プロジェクトを指定します。",
            [FailureKind.MissingExecutable] = "gh.exeが見つかりません。GitHub CLIを導入するか、実行ファイルを指定してください。",
            [FailureKind.StartFailed] = "gh.exeを起動できません。実行ファイルと端末の実行権限を確認してください。",
            [FailureKind.NotLoggedIn] = "このホストには未ログインです。下のログイン手順を実行して再確認してください。",
            [FailureKind.AuthenticationExpired] = "ghの認証が無効または期限切れです。ログインし直して再確認してください。",
            [FailureKind.PermissionDenied] = "必要なスコープまたは対象への権限がありません。",
            [FailureKind.NotFoundOrInaccessible] = "対象を取得できません。URL、存在、閲覧権限を確認してください。",
            [FailureKind.RateLimited] = "GitHubの利用制限に達しました。時間を置いて再確認してください。",
            [FailureKind.Network] = "GitHubとの通信に失敗しました。ネットワークを確認して再確認してください。",
            [FailureKind.InvalidResponse] = "ghの応答を確認できません。対応するGitHub CLIのバージョンと導入状態を確認してください。",
            [FailureKind.GraphQl] = "取得に一部エラーがあります。未確認の項目を成功として扱いません。",
            [FailureKind.Cancelled] = "確認をキャンセルしました。",
            [FailureKind.TimedOut] = "確認がタイムアウトしました。接続先とネットワークを確認してください。",
            [FailureKind.IdentityChanged] = "利用者・ホスト・実行ファイルの変更を検出し、接続を停止しました。対象を確認して「新しい接続として確認」を選んでください。",
            [FailureKind.PlaintextCredentials] = "資格情報が平文で保存されているため書き込みを停止しています。資格情報ストアを利用できる環境でghへログインし直してください。",
            [FailureKind.UnknownCredentialStore] = "資格情報の保存方式が不明なため書き込みを停止しています。",
        };
        Assert.That(expected.Keys, Is.EquivalentTo(Enum.GetValues<FailureKind>()));
        Assert.Multiple(() =>
        {
            foreach (var (kind, text) in expected)
                Assert.That(ConnectionViewModel.FailureText(kind), Is.EqualTo(text), kind.ToString());
        });
    }

    [Test, NonParallelizable]
    public void FindGhReturnsTheFirstExecutableOnThePath()
    {
        var previous = Environment.GetEnvironmentVariable("PATH");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ghpb-findgh-" + Guid.NewGuid().ToString("N")));
        try
        {
            var executable = Path.Combine(directory.FullName, "gh.exe");
            File.WriteAllBytes(executable, []);
            Environment.SetEnvironmentVariable("PATH", "\"" + Path.Combine(directory.FullName, "missing") + "\"" + Path.PathSeparator + directory.FullName);
            Assert.That(ConnectionViewModel.FindGh(), Is.EqualTo(Path.GetFullPath(executable)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
            directory.Delete(true);
        }
    }
}
