namespace GhProjectsBoards.Core.Projects;

internal sealed record PlanningChange(ProjectPlanning? Before, ProjectPlanning After);
internal sealed record ProjectSelectEdit(string RowId, string FieldId, string? OptionId);

internal sealed partial class EditingWorkspace
{
    private readonly Dictionary<string, AdoptedPlan> calculatedPlans = [];
    private void InvalidatePlan(string projectId) => calculatedPlans.Remove(projectId);
    public string TaskId(ProjectRegistration registration, string rowId) => localRows.Any(r => r.Id == rowId && r.ProjectId == registration.Snapshot.Id.NodeId)
        ? rowId : registration.Snapshot.Items.Single(i => i.Id.NodeId == rowId).ContentId?.NodeId
            ?? throw new InvalidOperationException("Issueを選択してください。");
    public AdoptedPlan PlanFor(ProjectRegistration registration)
    {
        using var measured = PerformanceTrace.Span("planning-inputs-and-schedule");
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
        foreach (var appearances in allRows.Where(r => r.IsLocal || issueRows.ContainsKey(r.ItemId))
            .GroupBy(r => r.IsLocal ? r.ItemId : issueRows[r.ItemId]))
        {
            var row = appearances.First();
            var taskId = row.IsLocal ? row.ItemId : issueRows[row.ItemId]; var task = metadata.GetValueOrDefault(taskId) ?? new(taskId);
            // Project-item values can disagree even when they refer to the same
            // Issue. Preserve both rows; neither ordering nor a cached first row
            // establishes canonical labor or date values.
            var conflict = appearances.Skip(1).Any(other => plan.Fields.Any(binding => {
                var first = row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
                var second = other.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId);
                return first is null || second is null || first.Reason != second.Reason
                    || first.Availability != second.Availability && (first.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)
                        || second.Availability is not (ValueAvailability.Present or ValueAvailability.Empty))
                    || Value(first) != Value(second)
                    || Field(first)?.Observation?.Reason != Field(second)?.Observation?.Reason
                    || Field(first)?.Conflict == true || Field(second)?.Conflict == true;
            }));
            var sourceProblem = conflict ? "同じIssueの重複行で値が不一致です。各行の工数・日時を確認してください。" : null;
            decimal? Work(string role)
            {
                if (conflict) return null;
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
                task.Actuals is null ? Work("Actual") : task.Actuals.Length == 0 ? null : task.Actuals.Sum(a => a.Hours), sourceProblem));
        }
        using (PerformanceTrace.Span("planning-schedule")) return calculatedPlans[id] = PlanningEngine.Calculate(plan, inputs.ToArray(), Revision);
    }
    public void CommitPlanning(ProjectRegistration registration, ProjectPlanning candidate, long expectedRevision,
        PlanningValueEdit[]? values = null, PlanningDependencyEdit[]? dependencies = null, PlanningProjectionDecision[]? decisions = null,
        FieldKey[]? consumeBuffers = null, ProjectSelectEdit[]? projectFields = null)
    {
        using var measured = PerformanceTrace.Span("planning-validation-and-commit");
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
        EditingWorkspace staged;
        using (PerformanceTrace.Span("planning-stage-snapshot-restore")) staged = Restore(Snapshot());
        if (projectFields is not null && existing is not null && PlanningContract.SameAdoptedPlan(existing, candidate)
            && (values ?? []).Length == 0 && (dependencies ?? []).Length == 0 && (decisions ?? []).Length == 0 && (consumeBuffers ?? []).Length == 0)
        {
            // A Project option is independent of scheduling. It must not create
            // a plan stamp or refresh unrelated date projections when edited alone.
            staged.ApplyProjectSelects(registration, projectFields ?? []);
        }
        else
        {
            staged.AcceptProjectionBaselines(registration, candidate, decisions ?? []);
            staged.CommitPlanningCore(registration, candidate, values ?? [], dependencies ?? [], consumeBuffers ?? [],
                (decisions ?? []).Select(d => d.Key).ToArray(), projectFields ?? []);
        }
        fields.Clear(); foreach (var pair in staged.fields) fields.Add(pair.Key, pair.Value);
        localRows.Clear(); localRows.AddRange(staged.localRows);
        planning.Clear(); planning.AddRange(staged.planning);
        history.Clear(); history.AddRange(staged.history);
        Revision = staged.Revision; InvalidatePlan(candidate.ProjectId);
    }
    private void CommitPlanningCore(ProjectRegistration registration, ProjectPlanning candidate, PlanningValueEdit[] values,
        PlanningDependencyEdit[] dependencies, FieldKey[] consumeBuffers, FieldKey[] projectionDecisions, ProjectSelectEdit[] projectFields)
    {
        var before = Planning(candidate.ProjectId);
        SetPlanning(candidate, before?.Stamp ?? 0);
        var rows = Open(registration);
        var changes = new Dictionary<FieldKey, FieldChange>();
        var rowChanges = new Dictionary<string, LocalRowChange>();
        var bufferWrites = consumeBuffers.ToHashSet();
        var writtenScalars = new HashSet<FieldKey>();
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
            var scalarWrites = values.Select(v => {
                if (v.Role is not ("Estimate" or "Remaining")) throw new InvalidOperationException("工数の入力先が無効です。");
                var field = candidate.Fields.Single(f => f.Role == v.Role).FieldId;
                return (rows.Single(r => r.ItemId == v.RowId).Cells.Single(c => c.Key?.FieldId == field), v.Value ?? "", v.Value is null, false);
            }).ToArray();
            Apply(candidate.ProjectId, scalarWrites);
            writtenScalars.UnionWith(scalarWrites.Select(write => write.Item1.Key!));
            foreach (var transaction in history.Skip(start))
            {
                bufferWrites.UnionWith(transaction.BufferWrites!);
                foreach (var c in transaction.Changes) changes[c.Key] = c;
            }
            history.RemoveRange(start, history.Count - start);
        }
        if (projectFields.Length != 0)
        {
            var start = history.Count;
            ApplyProjectSelects(registration, projectFields);
            foreach (var transaction in history.Skip(start))
            {
                bufferWrites.UnionWith(transaction.BufferWrites!);
                foreach (var c in transaction.Changes)
                    changes[c.Key] = c with { Before = changes.TryGetValue(c.Key, out var prior) ? prior.Before : c.Before };
                foreach (var c in transaction.Rows ?? []) rowChanges[c.Id] = c;
            }
            history.RemoveRange(start, history.Count - start);
        }
        ChangeDependencies(registration, dependencies, changes);
        ValidateWorkContributions(candidate.ProjectId, new Dictionary<FieldKey, FieldChange>());
        ProjectPlan(registration, changes, before, consumeBuffers.Concat(projectionDecisions).ToHashSet());
        foreach (var key in consumeBuffers)
        {
            var cell = rows.SelectMany(r => r.Cells).FirstOrDefault(c => c.Key == key);
            // Ordinary effort input belongs to this confirmation only when its
            // exact row/field was also validated and written in the scalar batch.
            if (cell is null || key.ProjectId != candidate.ProjectId
                || PlanningInputRole(cell) is null && !(cell.Editable && writtenScalars.Contains(key)))
                throw new InvalidOperationException("確定する計画セルの入力状態を確認してください。");
            var old = fields[key];
            // Exact time or report metadata may change without a new projected
            // scalar. Its explicitly confirmed field still guards later input.
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
        foreach (var key in consumeBuffers)
            if (changes.TryGetValue(key, out var change)) changes[key] = change with { Before = change.Before with { Buffer = null } };
        // A contextual confirmation can finish input without changing its
        // report, exact endpoints, or derived projections. Keep earlier Undo
        // guards intact while the workspace revision persists input completion.
        if (consumeBuffers.Length != 0 && projectionDecisions.Length == 0 && before is not null
            && PlanningContract.SameAdoptedPlan(before, Planning(candidate.ProjectId)!)
            && rowChanges.Count == 0 && changes.Values.All(c => c.Before == c.After with { Stamp = c.Before.Stamp }))
        {
            foreach (var c in changes.Values) fields[c.Key] = c.After with { Stamp = c.Before.Stamp };
            planning.RemoveAll(p => p.ProjectId == before.ProjectId); planning.Add(before);
            InvalidatePlan(before.ProjectId);
            return;
        }
        // Scalar and optional Project-field writes are staged separately, but
        // this confirmation is one durable operation with one final stamp.
        foreach (var (key, change) in changes.ToArray())
        {
            var after = change.After with { Stamp = Revision };
            fields[key] = after; changes[key] = change with { After = after };
        }
        history.Add(WithHistoryParts(new(Guid.NewGuid().ToString("N"), candidate.ProjectId, [],
            Plan: new(before, Planning(candidate.ProjectId)!), BufferWrites: bufferWrites.ToArray()), changes.Values.ToArray(), rowChanges.Values.ToArray()));
    }
    private void ApplyProjectSelects(ProjectRegistration registration, ProjectSelectEdit[] edits)
    {
        if (edits.Length == 0) return;
        if (edits.GroupBy(edit => (edit.RowId, edit.FieldId)).Any(group => group.Count() != 1))
            throw new InvalidOperationException("同じProject項目への変更が重複しています。");
        var rows = Open(registration);
        var writes = edits.Select(edit => {
            var definition = registration.Snapshot.Fields.SingleOrDefault(f => f.Id.NodeId == edit.FieldId);
            var cell = rows.SingleOrDefault(r => r.ItemId == edit.RowId)?.Cells.SingleOrDefault(c => c.Key?.FieldId == edit.FieldId);
            if (definition is not { DataType: "SINGLE_SELECT", ValueOwner: FieldOwner.ProjectItem, Availability: ValueAvailability.Present }
                || definition.Id.Scope != Scope || definition.ProjectId != registration.Snapshot.Id
                || cell is not { Editable: true, Key.Kind: "Select" or "LocalSelect" }
                || cell.Availability is not (ValueAvailability.Present or ValueAvailability.Empty)
                || Buffer(cell) is not null || Field(cell)?.Conflict == true || Field(cell)?.Observation?.Reason is not null)
                throw new InvalidOperationException("選択したProject項目を変更できません。現在の値と入力状態を確認してください。");
            if (edit.OptionId is not null && definition.Options.Count(o => o.Id == edit.OptionId) != 1)
                throw new InvalidOperationException("選択したProject項目の選択肢IDを確認できません。");
            return (cell, edit.OptionId ?? "", edit.OptionId is null, true);
        }).ToArray();
        Apply(registration.Snapshot.Id.NodeId, writes, projectPlan: false);
    }
    private void ProjectPlan(ProjectRegistration registration, Dictionary<FieldKey, FieldChange> changes,
        ProjectPlanning? previous = null, HashSet<FieldKey>? explicitEndpoints = null)
    {
        var id = registration.Snapshot.Id.NodeId; InvalidatePlan(id);
        var p = Planning(id); if (p is null) return;
        var result = PlanFor(registration); var byTask = result.Tasks.ToDictionary(t => t.Id);
        var ambiguous = (result.Inputs ?? []).Where(i => i.SourceProblem is not null).Select(i => i.Task.Id).ToHashSet();
        var metadata = p.Tasks.ToDictionary(t => t.Id);
        var priorTasks = (previous ?? p).Tasks.ToDictionary(t => t.Id);
        var issueRows = registration.Snapshot.Items.Where(i => i.Kind == ProjectItemKind.Issue && i.ContentId is not null).ToDictionary(i => i.Id.NodeId, i => i.ContentId!.NodeId);
        foreach (var row in Open(registration).Where(r => r.IsLocal || issueRows.ContainsKey(r.ItemId)))
        {
            var taskId = row.IsLocal ? row.ItemId : issueRows[row.ItemId]; if (!byTask.TryGetValue(taskId, out var task)) continue;
            if (ambiguous.Contains(taskId)) continue;
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
        if (role is null) return null;
        var taskId = TaskId(registration, rowId);
        var sourceProblem = PlanFor(registration).Inputs?.SingleOrDefault(i => i.Task.Id == taskId)?.SourceProblem;
        if (sourceProblem is not null) return sourceProblem;
        if (role == "Actual")
        {
            var reports = Planning(registration.Snapshot.Id.NodeId)?.Tasks.SingleOrDefault(t => t.Id == taskId)?.Actuals;
            var total = reports is not { Length: > 0 } ? null : PlanningContract.CanonicalHours(reports.Sum(a => a.Hours));
            if (reports is null || total != field.Change.Value)
                return "実績の反映には一致する報告内訳・報告対象日の確認が必要です。保持した合計を計画画面で照合してください。";
        }
        if (field.Key.Kind != "Date" || field.Change is null || ProjectionRole(field.Key) is not ("Start" or "Finish")) return null;
        var task = PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == taskId);
        return task is { Mode: PlanningMode.Auto, Resolved: false }
            ? $"計画が未解決です。以前の日付下書き（入力revision {field.Stamp}）は反映できません。Autoを再計算するかManualで日時を採用してください。"
            : null;
    }
}
