using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed partial class RegistrationWorkspace
{
    public TargetReport IssueDiagnostic { get; private set; } = new();
    public TargetReport ProjectDiagnostic { get; private set; } = new();
    public Task DiagnoseTargetsAsync(string issueUrl, string projectUrl) => RunAsync(async token =>
    {
        IssueDiagnostic = new(); ProjectDiagnostic = new();
        RequireConnection();
        var revision = ConnectionRevision;
        var report = await service!.RecheckAsync(context!, token);
        if (!report.IsConnected) { Status = ConnectionViewModel.FailureText(report.Result.Failure); return; }
        var diagnostics = new TargetDiagnostics(service);
        var issue = string.IsNullOrWhiteSpace(issueUrl) ? new TargetReport()
            : await diagnostics.IssueAsync(report.Context!, report.Authentication!, issueUrl, token);
        var project = string.IsNullOrWhiteSpace(projectUrl) ? new TargetReport()
            : await diagnostics.ProjectAsync(report.Context!, report.Authentication!, projectUrl, token);
        token.ThrowIfCancellationRequested();
        if (revision != ConnectionRevision || !CanRead) return;
        IssueDiagnostic = issue; ProjectDiagnostic = project;
        Status = "対象のアクセス診断が完了しました。取得・登録・GitHub更新はしていません。";
    });
}
