using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.Core.Reports;

internal static class ProgressSnapshotReader
{
    internal static ProjectPlanSettings ReadSettings(byte[] bytes)
    {
        var file = PlanJson.Read<PlanSettingsFile>(bytes);
        PlanOperations.Require(file.Version == 1, "未対応の設定ファイル形式です。");
        PlanOperations.ValidateSettings(file.Settings);
        return file.Settings;
    }

    // Reads one Project on a read-only lease and builds the plan GitHub holds, with the exported settings in place of
    // the app's local ones. Nothing is written: the lease refuses mutations.
    internal static async Task<ProgressSnapshot> ReadAsync(GhConnectionService service, string projectUrl, ProjectPlanSettings settings,
        DateOnly statusDate, TimeZoneInfo timeZone, Func<DateTimeOffset> clock, CancellationToken token = default)
    {
        var connection = await service.ConnectAsync(token);
        if (!connection.IsConnected) throw new InvalidOperationException(ConnectionViewModel.FailureText(connection.Result.Failure));
        var context = connection.Context!;
        var project = await new ProjectDiscovery(service).ResolveAsync(context, projectUrl, token);
        ProjectReadResult read;
        using (var lease = await service.BeginOperationAsync(context, token, mutation: false))
            read = await PlanSnapshot.ReadConsistentAsync(service, lease, context, project.Id, token);
        if (read.Outcome != ProjectReadOutcome.Complete || read.Project is null)
            throw new InvalidOperationException("プロジェクト全体を取得できませんでした: " + PlanSnapshot.ReadDiagnostic(read));
        var readAt = clock();
        // Like a settings import, the file's 基準日 is not portable; the run's 基準日 replaces it.
        settings = settings with { StatusDate = statusDate };
        // PlanSnapshot refuses an unreadable mapping too; naming the column tells the workflow's reader what to fix.
        var missing = settings.Columns.Where(c => read.Project.Fields.All(f => f.Id.NodeId != c.FieldId)).Select(c => c.Name).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("設定ファイルの列がプロジェクトにありません: " + string.Join(", ", missing));
        var remote = PlanSnapshot.From(read, settings, timeZone);
        var document = new PlanDocument(project.Id, remote.Baseline, new(remote.Baseline.Rows, settings))
        { Sync = new() { IssueLinks = remote.IssueLinks, PeopleNames = remote.PeopleNames, NativeOrders = remote.SubIssueOrders } };
        return ProgressSnapshot.Build(project, document, statusDate, timeZone.Id, readAt);
    }
}
