namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningChange(ProjectPlanning? Before, ProjectPlanning After);

internal sealed partial class EditingWorkspace
{
    private readonly Dictionary<string, AdoptedPlan> calculatedPlans = [];
    private void InvalidatePlan(string projectId) => calculatedPlans.Remove(projectId);
    public string TaskId(ProjectRegistration registration, string rowId) => localRows.Any(r => r.Id == rowId && r.ProjectId == registration.Snapshot.Id.NodeId)
        ? rowId : registration.Snapshot.Items.Single(i => i.Id.NodeId == rowId).ContentId?.NodeId
            ?? throw new InvalidOperationException("Issueを選択してください。");
    public AdoptedPlan PlanFor(ProjectRegistration registration)
    {
        var id = registration.Snapshot.Id.NodeId;
        if (registration.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの計画です。");
        if (calculatedPlans.TryGetValue(id, out var cached)) return cached;
        var plan = Planning(id); if (plan is null) return new(id, Revision, []);
        var metadata = plan.Tasks.ToDictionary(t => t.Id);
        var roles = plan.Fields.ToDictionary(f => f.Role, f => f.FieldId);
        var inputs = new List<PlanningInput>();
        var issueRows = registration.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null).ToDictionary(i => i.Id.NodeId, i => i.ContentId!.NodeId);
        var allRows = ReadRows(registration);
        var dependencies = fields.Values.Where(f => f.Key.Kind == "Dependency" && f.Key.ProjectId == id).ToLookup(f => f.Key.NodeId);
        foreach (var row in allRows.Where(r => r.IsLocal || issueRows.ContainsKey(r.ItemId))
            .DistinctBy(r => r.IsLocal ? r.ItemId : issueRows[r.ItemId]))
        {
            var taskId = row.IsLocal ? row.ItemId : issueRows[row.ItemId]; var task = metadata.GetValueOrDefault(taskId) ?? new(taskId);
            decimal? Work(string role)
            {
                if (!roles.TryGetValue(role, out var field)) return null;
                var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == field);
                if (cell is null || cell.Reason is not null || Field(cell)?.Conflict == true) return null;
                var value = Value(cell);
                if (Field(cell) is { Observation: { Reason: not null } observation } saved
                    && !(observation.Reason == PendingObservationReason
                        && observation.Availability is ValueAvailability.Present or ValueAvailability.Empty
                        && (observation.Value == saved.Baseline || observation.Value == value))) return null;
                try { return value is null ? null : PlanningContract.ParseHours(value); }
                catch (InvalidOperationException) { return null; }
            }
            var issue = registration.Snapshot.Issues.GetValueOrDefault(new(Scope, taskId));
            var links = AdoptedLinks(registration, taskId, task, dependencies[taskId]);
            var complete = row.IsLocal || issue?.Native?.Complete == true;
            if (dependencies[taskId].Any(f => f.Conflict || f.Observation?.Reason is not null)) complete = false;
            inputs.Add(new(task, Work("Estimate"), Work("Remaining"), issue?.Native?.Assignees.Select(a => a.Id.NodeId).ToArray() ?? [], links,
                complete, issue?.State.Availability == ValueAvailability.Present ? issue.State.Value == IssueState.Closed : null,
                task.Actuals is null ? Work("Actual") : task.Actuals.Length == 0 ? null : task.Actuals.Sum(a => a.Hours)));
        }
        return calculatedPlans[id] = PlanningEngine.Calculate(plan, inputs.ToArray(), Revision);
    }
    public void CommitPlanning(ProjectRegistration registration, ProjectPlanning candidate, long expectedRevision,
        PlanningValueEdit[]? values = null, PlanningDependencyEdit[]? dependencies = null, PlanningProjectionDecision[]? decisions = null,
        FieldKey[]? consumeBuffers = null)
    {
        if (!HasCheckpoint || registration.Snapshot.Id.Scope != Scope || registration.Snapshot.Id.NodeId != candidate.ProjectId || expectedRevision != Revision)
            throw new InvalidOperationException("計画を開いた後に変更がありました。現在の値を確認してください。");
        if (registration.Snapshot.Capability is not { CanUpdate: true } capability || capability.ObservedAt == default)
            throw new InvalidOperationException("Projectの更新権限を確認してください。");
        foreach (var binding in candidate.Fields)
            if (!registration.Snapshot.Fields.Any(f => f.Id.NodeId == binding.FieldId && f.DataType == binding.DataType && f.ValueOwner == FieldOwner.ProjectItem && f.Availability == ValueAvailability.Present))
                throw new InvalidOperationException("計画フィールドのID・型を確認できません。");
        var existing = Planning(candidate.ProjectId);
        // Detached save/preview snapshots own their arrays. Compare protected
        // values rather than treating that ownership boundary as a replacement.
        if (!SummaryContract.SameSettings(candidate.Summary, existing?.Summary))
            throw new InvalidOperationException("投入可能工数と基準計画はSummaryの専用操作で変更してください。");
        if (candidate.Tasks.Any(t => t.LaborKind != TaskLaborKind.Unspecified))
            candidate = candidate with { Version = candidate.Version >= 3 ? 4 : 2 };
        foreach (var task in candidate.Tasks)
        {
            var previous = existing?.Tasks.SingleOrDefault(t => t.Id == task.Id);
            if (previous?.Mode != PlanningMode.Unplanned && previous is not null && task.Mode == PlanningMode.Unplanned)
                throw new InvalidOperationException("採用済みの計画は未設定に戻せません。Manualの解除はAutoを明示的に選んでください。");
            if (task.Progress == PlanningProgress.Reopened && task.Mode == PlanningMode.Auto
                && (previous?.Progress != PlanningProgress.Reopened || previous.Mode != PlanningMode.Auto)
                && !(values ?? []).Any(v => v.Role == "Remaining" && v.Value is not null && TaskId(registration, v.RowId) == task.Id))
                throw new InvalidOperationException("再開時の残時間を明示的に入力・確認してください。");
            if (previous?.Progress == PlanningProgress.Completed && task.Progress is not (PlanningProgress.Completed or PlanningProgress.Reopened))
                throw new InvalidOperationException("完了したタスクは「再開」を選び、残時間を確認してください。");
        }
        if (existing is not null && existing.Fields.Any(b => candidate.Fields.SingleOrDefault(c => c.Role == b.Role) != b)
            && fields.Values.Any(f => f.Key.ProjectId == candidate.ProjectId && existing.Fields.Any(b => b.FieldId == f.Key.FieldId)
                && (f.Change is not null || f.Buffer is not null || f.Conflict || f.Observation?.Reason == ProjectionDecisionReason)))
            throw new InvalidOperationException("フィールドの変更前に、既存の工数・日付の差分と入力を解決してください。");
        var ids = registration.Snapshot.Issues.Keys.Select(i => i.NodeId).Concat(localRows.Where(r => r.ProjectId == candidate.ProjectId).Select(r => r.Id)).ToHashSet();
        if (candidate.Tasks.Any(t => !ids.Contains(t.Id) && existing?.Tasks.Any(e => e.Id == t.Id && PlanningContract.SameRetainedTask(e, t)) != true)
            || existing?.Tasks.Any(e => !ids.Contains(e.Id) && !candidate.Tasks.Any(t => t.Id == e.Id && PlanningContract.SameRetainedTask(e, t))) == true)
            throw new InvalidOperationException("計画対象のIssueを確認できません。");
        var staged = Restore(Snapshot());
        staged.AcceptProjectionBaselines(registration, candidate, decisions ?? []);
        staged.CommitPlanningCore(registration, candidate, values ?? [], dependencies ?? [], consumeBuffers ?? [],
            (decisions ?? []).Select(d => d.Key).ToArray());
        fields.Clear(); foreach (var pair in staged.fields) fields.Add(pair.Key, pair.Value);
        planning.Clear(); planning.AddRange(staged.planning);
        history.Clear(); history.AddRange(staged.history);
        Revision = staged.Revision; InvalidatePlan(candidate.ProjectId);
    }
    private void CommitPlanningCore(ProjectRegistration registration, ProjectPlanning candidate, PlanningValueEdit[] values,
        PlanningDependencyEdit[] dependencies, FieldKey[] consumeBuffers, FieldKey[] projectionDecisions)
    {
        var before = Planning(candidate.ProjectId);
        SetPlanning(candidate, before?.Stamp ?? 0);
        var rows = Open(registration);
        var changes = new Dictionary<FieldKey, FieldChange>();
        foreach (var key in consumeBuffers)
        {
            if (fields.TryGetValue(key, out var old) && old.Observation is { Reason: PendingObservationReason } observation)
            {
                var next = old with { Observation = observation with { Reason = null } };
                fields[key] = next; changes[key] = new(key, old, next);
            }
        }
        if (values.Length != 0)
        {
            var start = history.Count;
            Apply(candidate.ProjectId, values.Select(v => {
                if (v.Role is not ("Estimate" or "Remaining")) throw new InvalidOperationException("工数の入力先が無効です。");
                var field = candidate.Fields.Single(f => f.Role == v.Role).FieldId;
                return (rows.Single(r => r.ItemId == v.RowId).Cells.Single(c => c.Key?.FieldId == field), v.Value ?? "", v.Value is null, false);
            }).ToArray());
            foreach (var c in history.Skip(start).SelectMany(t => t.Changes)) changes[c.Key] = c;
            history.RemoveRange(start, history.Count - start);
        }
        ChangeDependencies(registration, dependencies, changes);
        ValidateWorkContributions(candidate.ProjectId, new Dictionary<FieldKey, FieldChange>());
        ProjectPlan(registration, changes, before, consumeBuffers.Concat(projectionDecisions).ToHashSet());
        foreach (var key in consumeBuffers)
        {
            var cell = rows.SelectMany(r => r.Cells).FirstOrDefault(c => c.Key == key);
            if (cell is null || PlanningInputRole(cell) is null || key.ProjectId != candidate.ProjectId)
                throw new InvalidOperationException("確定する計画セルの入力状態を確認してください。");
            var old = fields[key];
            if (old.Buffer is null) continue;
            var next = old with { Buffer = null, Stamp = Revision };
            fields[key] = next;
            changes[key] = new(key, changes.TryGetValue(key, out var prior) ? prior.Before : old, next);
        }
        foreach (var task in candidate.Tasks.Where(t => t.Progress == PlanningProgress.Completed))
        {
            var input = PlanFor(registration).Inputs!.SingleOrDefault(i => i.Task.Id == task.Id);
            if (input is not null && (input.Remaining != 0 || task.ActualStart is null || task.ActualFinish is null))
                throw new InvalidOperationException("完了にするには残時間0と実際の開始・終了日時を入力してください。");
        }
        history.Add(new(Guid.NewGuid().ToString("N"), candidate.ProjectId, changes.Values.ToArray(), Plan: new(before, Planning(candidate.ProjectId)!)));
    }
    private void ProjectPlan(ProjectRegistration registration, Dictionary<FieldKey, FieldChange> changes,
        ProjectPlanning? previous = null, HashSet<FieldKey>? explicitEndpoints = null)
    {
        var id = registration.Snapshot.Id.NodeId; InvalidatePlan(id);
        var p = Planning(id); if (p is null) return;
        var result = PlanFor(registration); var byTask = result.Tasks.ToDictionary(t => t.Id);
        var metadata = p.Tasks.ToDictionary(t => t.Id);
        var priorTasks = (previous ?? p).Tasks.ToDictionary(t => t.Id);
        var issueRows = registration.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null).ToDictionary(i => i.Id.NodeId, i => i.ContentId!.NodeId);
        foreach (var row in Open(registration).Where(r => r.IsLocal || issueRows.ContainsKey(r.ItemId)))
        {
            var taskId = row.IsLocal ? row.ItemId : issueRows[row.ItemId]; if (!byTask.TryGetValue(taskId, out var task)) continue;
            foreach (var binding in p.Fields.Where(f => f.Role is "Start" or "Finish" or "Actual"))
            {
                if (binding.Role is "Start" or "Finish" && task.Mode == PlanningMode.Unplanned) continue;
                // An unresolved Auto result must not erase previously published dates.
                if (binding.Role is "Start" or "Finish" && task.Mode == PlanningMode.Auto && !task.Resolved) continue;
                var reports = metadata.GetValueOrDefault(taskId)?.Actuals;
                if (binding.Role == "Actual" && reports is null) continue;
                var value = binding.Role switch { "Start" => PlanningContract.ProjectDate(task.Start), "Finish" => PlanningContract.ProjectDate(task.Finish),
                    _ => reports!.Length == 0 ? null : PlanningContract.CanonicalHours(reports.Sum(a => a.Hours)) };
                var cell = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
                if (cell?.Key is not { } key || cell.Reason is not null || !fields.TryGetValue(key, out var old) || old.Conflict || old.Observation?.Reason is not null) continue;
                if (value is null && binding.Role is "Start" or "Finish" && task.Mode == PlanningMode.Manual
                    && explicitEndpoints?.Contains(key) != true)
                {
                    var priorTask = priorTasks.GetValueOrDefault(taskId);
                    var priorEndpoint = binding.Role == "Start" ? priorTask?.ManualStart : priorTask?.ManualFinish;
                    // An unchanged unknown exact endpoint is not a deletion of
                    // an observed day during an unrelated effort/report edit.
                    if (priorTask?.Mode != PlanningMode.Manual || priorEndpoint is null || previous is null) continue;
                }
                if ((old.Change is null ? old.Baseline : old.Change.Value) == value) continue;
                var next = old with { Change = value == old.Baseline ? null : new(value, value is null), Stamp = Revision };
                fields[key] = next;
                changes[key] = new(key, changes.TryGetValue(key, out var prior) ? prior.Before : old, next);
            }
        }
    }
    private void ProjectCommittedPlan(string projectId, Dictionary<FieldKey, FieldChange> changes)
    {
        var registration = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id.NodeId == projectId);
        if (registration is not null && Planning(projectId) is not null) ProjectPlan(registration, changes);
    }
    internal string? PlanningPublicationProblem(ProjectRegistration registration, string rowId, DraftField field)
    {
        if (field.Change is null) return null;
        var role = ProjectionRole(field.Key);
        if (role == "Actual")
        {
            var reports = Planning(registration.Snapshot.Id.NodeId)?.Tasks.SingleOrDefault(t => t.Id == TaskId(registration, rowId))?.Actuals;
            var total = reports is not { Length: > 0 } ? null : PlanningContract.CanonicalHours(reports.Sum(a => a.Hours));
            if (reports is null || total != field.Change.Value)
                return "実績の反映には一致する報告内訳・報告対象日の確認が必要です。保持した合計を計画画面で照合してください。";
        }
        if (field.Key.Kind != "Date" || field.Change is null || ProjectionRole(field.Key) is not ("Start" or "Finish")) return null;
        var task = PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == TaskId(registration, rowId));
        return task is { Mode: PlanningMode.Auto, Resolved: false }
            ? $"計画が未解決です。以前の日付下書き（入力revision {field.Stamp}）は反映できません。Autoを再計算するかManualで日時を採用してください。"
            : null;
    }
}
