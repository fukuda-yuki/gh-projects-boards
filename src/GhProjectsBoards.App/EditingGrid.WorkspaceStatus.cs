using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private readonly Grid workspaceStatus = new() { ColumnSpacing = 8, RowSpacing = 2, Padding = new(8, 2, 8, 2) };
    private readonly TextBlock workspaceSaved = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button workspaceChanges = new() { Padding = new(8, 4, 8, 4), MinHeight = 32, Visibility = Visibility.Collapsed };
    private readonly TextBlock workspaceChangesText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button workspaceSaveRetry = new() { Content = "保存を再試行", Padding = new(8, 4, 8, 4), MinHeight = 32, Visibility = Visibility.Collapsed };
    private EditingWorkspace? statusWorkspace;
    private long statusRevision = -1;
    internal event EventHandler? ApplyReviewRequested;

    private void InitializeWorkspaceStatus()
    {
        AutomationProperties.SetAutomationId(workspaceStatus, "ProjectWorkStatus");
        AutomationProperties.SetAutomationId(workspaceSaved, "WorkspaceSaveStatus");
        AutomationProperties.SetLiveSetting(workspaceSaved, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(workspaceChanges, "WorkspaceUnpublished");
        AutomationProperties.SetHelpText(workspaceChanges, "このProjectの反映内容を確認します。ここではGitHubへ送信しません。");
        AutomationProperties.SetAutomationId(workspaceSaveRetry, "WorkspaceSaveRetry");
        workspaceChanges.Content = workspaceChangesText;
        workspaceStatus.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspaceStatus.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspaceStatus.ColumnDefinitions.Add(new());
        workspaceStatus.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        workspaceStatus.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        workspaceStatus.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        workspaceStatus.Children.Add(workspaceSaved);
        SetColumn(workspaceChanges, 1); workspaceStatus.Children.Add(workspaceChanges);
        SetColumn(pendingInput, 2); workspaceStatus.Children.Add(pendingInput);
        SetColumn(workspaceSaveRetry, 3); workspaceStatus.Children.Add(workspaceSaveRetry);
        foreach (var button in new[] { workspaceChanges, workspaceSaveRetry })
            button.GettingFocus += (_, args) => { if (!CanRefresh) args.Cancel = true; };
        workspaceChanges.Click += (_, _) => { if (CanRefresh) ApplyReviewRequested?.Invoke(this, EventArgs.Empty); };
        workspaceSaveRetry.Click += async (_, _) => {
            if (!CanRefresh) return;
            var request = generation;
            await FlushDraftsAsync("workspace-retry");
            if (IsLoaded && request == generation) Update("workspace-retry");
        };
        workspaceStatus.SizeChanged += (_, _) => LayoutWorkspaceStatus();
        SetRow(workspaceStatus, RowDefinitions.Count - 1); Children.Add(workspaceStatus);
    }

    private void UpdateWorkspaceStatus()
    {
        // Counts describe the selected Project, independent of timeline, filter
        // and selection. Save acknowledgement comes from the same draft session.
        workspaceSaved.Text = session.Status.Replace("（GitHub未反映）", "");
        workspaceSaveRetry.Visibility = session.Status.Contains("失敗", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        if (ReferenceEquals(statusWorkspace, session.Workspace) && statusRevision == session.Workspace.PresentationRevision) return;
        var candidates = session.Workspace.ReadApplyCandidates(registration);
        var changed = candidates.SelectMany(candidate => candidate.Fields).Where(field => field.Change is not null)
            .DistinctBy(field => field.Key).ToArray();
        var cells = changed.Count(field => field.Key.Kind != "Dependency");
        var dependencies = changed.Count(field => field.Key.Kind == "Dependency");
        var localRows = candidates.Count(candidate => candidate.IsCreation);
        var availableKeys = canonicalRows.SelectMany(row => row.Cells).Select(cell => cell.Key).ToHashSet();
        var needsReview = candidates.SelectMany(candidate => candidate.Fields.Where(field =>
                (candidate.Missing || field.Key.Kind != "Dependency" && !availableKeys.Contains(field.Key))
                && (field.Change is not null || field.Buffer is not null || field.Conflict)))
            .DistinctBy(field => field.Key).Count();
        var changes = new List<string>();
        if (cells > 0) changes.Add($"{cells}セル");
        if (dependencies > 0) changes.Add($"先行関係 {dependencies}件");
        if (localRows > 0) changes.Add($"新規 {localRows}行");
        workspaceChangesText.Text = changes.Count > 0 ? "GitHub未反映 " + string.Join(" · ", changes) : "";
        if (needsReview > 0)
            workspaceChangesText.Text += (changes.Count > 0 ? " · " : "") + $"要確認 {needsReview}項目";
        AutomationProperties.SetName(workspaceChanges, workspaceChangesText.Text);
        workspaceChanges.Visibility = changes.Count > 0 || needsReview > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePendingCount(canonicalRows);
        statusWorkspace = session.Workspace; statusRevision = session.Workspace.PresentationRevision;
    }

    private void LayoutWorkspaceStatus()
    {
        var narrow = workspaceStatus.ActualWidth < 800;
        SetColumnSpan(workspaceSaved, narrow ? 4 : 1);
        var controls = new[] { workspaceChanges, pendingInput, workspaceSaveRetry };
        for (var i = 0; i < controls.Length; i++)
        {
            SetRow(controls[i], narrow ? 1 : 0);
            SetColumn(controls[i], narrow ? i : i + 1);
        }
    }
}
