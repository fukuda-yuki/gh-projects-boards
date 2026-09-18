using GhProjectsBoards.App.GitHub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

public sealed partial class RegistrationPanel
{
    private async void ShowTargetDiagnostics(object sender, RoutedEventArgs e)
    {
        if (applyDialog) return;
        ProjectSettingsFlyout.Hide();
        var owner = Workspace;
        var issue = new TextBox { Header = "Issue URL（任意）" }; AutomationProperties.SetAutomationId(issue, "IssueUrlInput");
        var project = new TextBox { Header = "Project URL（任意）", Text = choice?.Url ?? owner.Selected?.Snapshot.Url ?? Url.Text };
        AutomationProperties.SetAutomationId(project, "ProjectUrlInput");
        var issueResult = ApplyText("Issue: 未確認"); AutomationProperties.SetAutomationId(issueResult, "IssueResult");
        var projectResult = ApplyText("Project: 未確認"); AutomationProperties.SetAutomationId(projectResult, "ProjectResult");
        var status = ApplyText(owner.CanRead ? "この診断ではGitHub更新やローカル登録を行いません。" : "接続設定を確認してから、対象の診断を再度開いてください。");
        var content = ApplyPanel(); content.Children.Add(issue); content.Children.Add(project);
        content.Children.Add(issueResult); content.Children.Add(projectResult); content.Children.Add(status);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "対象のアクセス権限", Content = content,
            PrimaryButtonText = "アクセス権限を確認", IsPrimaryButtonEnabled = owner.CanRead, CloseButtonText = "元の作業へ戻る", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog, "TargetDiagnosticsDialog");
        Task pending = Task.CompletedTask;
        string Report(string name, TargetReport value) => $"{name}: 読み取り {State(value.CanRead)} / 更新 {State(value.CanUpdate)}"
            + (value.RequiredWriteScope is null ? "" : $" / スコープ {value.RequiredWriteScope}: {State(value.HasWriteScope)}")
            + (value.Failure == FailureKind.None ? "" : "\n" + ConnectionViewModel.FailureText(value.Failure));
        async Task Check()
        {
            dialog.IsPrimaryButtonEnabled = false; issue.IsEnabled = project.IsEnabled = false; status.Text = "対象のアクセス権限を確認中…";
            await owner.DiagnoseTargetsAsync(issue.Text, project.Text);
            issueResult.Text = Report("Issue", owner.IssueDiagnostic); projectResult.Text = Report("Project", owner.ProjectDiagnostic); status.Text = owner.Status;
            dialog.IsPrimaryButtonEnabled = owner.CanRead; issue.IsEnabled = project.IsEnabled = true;
        }
        dialog.PrimaryButtonClick += (_, args) => { args.Cancel = true; if (pending.IsCompleted) pending = Check(); };
        await ShowDialogAsync(dialog);
        if (!pending.IsCompleted) { owner.Cancel(); await pending; }
    }
    private static string State(bool? value) => value switch { true => "あり", false => "なし", _ => "未確認" };
}
