namespace GhProjectsBoards.Core.Projects;

internal sealed partial class EditingWorkspace
{
    private ProjectRegistration WeeklyBulkProject(string projectId) => CheckpointRegistrations.SingleOrDefault(project => project.Snapshot.Id.NodeId == projectId)
        ?? throw new InvalidOperationException("一括入力するProjectを確認できません。");

    private (PlanningTask Task, ActualInputContext Context) WeeklyActualTarget(ProjectRegistration project, EditRow row,
        EditCell cell, DateOnly? reportedThrough, EditRow[] currentRows)
    {
        if (reportedThrough is null || reportedThrough == default(DateOnly)) throw new InvalidOperationException("実績の報告日を選んでください。");
        var (_, current, task) = PlanningInputTarget(project, row.ItemId, "Actual", currentRows);
        if (current.Key != cell.Key) throw new InvalidOperationException("実績の入力先が変わりました。現在の範囲を選び直してください。");
        var context = ActualInput(project, current, task);
        if (!context.HasPerson || context.MultipleReports || context.Problem is not null)
            throw new InvalidOperationException("実績担当者・内訳を確認してから一括入力してください。");
        return (task, context);
    }

    private void ApplyBulk(string projectId, EditRow[] rows, (EditCell Cell, string Text, bool Clear, bool OptionId)[] batch, DateOnly? reportedThrough)
    {
        if (!batch.Any(edit => PlanningInputRole(edit.Cell) == "Actual")) { Apply(projectId, batch); return; }
        var project = WeeklyBulkProject(projectId);
        var plan = Planning(projectId)!;
        // Resolve the current Project once for the whole batch, preserving the
        // captured displayed order while avoiding a full row rebuild per Actual.
        var currentRows = ReadRows(project);
        var tasks = new Dictionary<string, PlanningTask>();
        var values = new List<PlanningValueEdit>();
        foreach (var edit in batch)
        {
            var position = Array.FindIndex(rows, row => row.Cells.Any(cell => cell.Key == edit.Cell.Key));
            if (position < 0) throw new InvalidOperationException("一括入力する行を確認できません。");
            var row = rows[position];
            try
            {
                var role = plan.Fields.SingleOrDefault(binding => binding.FieldId == edit.Cell.Key?.FieldId)?.Role;
                if (role == "Actual")
                {
                    var (task, context) = WeeklyActualTarget(project, row, edit.Cell, reportedThrough, currentRows);
                    var hours = PlanningContract.ParseHours(edit.Text);
                    var next = task with { Actuals = [new(context.PersonId, hours, reportedThrough!.Value)] };
                    if (tasks.TryGetValue(task.Id, out var existing) && !PlanningContract.SameRetainedTask(existing, next))
                        throw new InvalidOperationException("同じIssueへの実績が異なります。値を揃えてください。");
                    tasks[task.Id] = next;
                }
                else if (role is "Estimate" or "Remaining") values.Add(new(row.ItemId, role, edit.Text));
                else throw new InvalidOperationException("実績と一括入力できるのは見積・残工数です。ほかの列は別に貼り付けてください。");
            }
            catch (InvalidOperationException error)
            { throw new InvalidOperationException($"行 {position + 1} [{row.ItemId}] / {edit.Cell.Display}: {error.Message}"); }
        }
        var candidate = plan with { Tasks = plan.Tasks.Select(task => tasks.GetValueOrDefault(task.Id) ?? task)
            .Concat(tasks.Values.Where(task => !plan.Tasks.Any(existing => existing.Id == task.Id))).ToArray() };
        CommitPlanning(project, candidate, Revision, values.ToArray(), consumeBuffers: batch.Select(edit => edit.Cell.Key!).Distinct().ToArray());
    }
}
