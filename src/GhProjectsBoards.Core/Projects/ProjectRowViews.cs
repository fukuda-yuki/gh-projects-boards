using System.Text.Json;

namespace GhProjectsBoards.Core.Projects;

internal sealed record RowFilter(string FieldId, string[] OptionIds, string[] States);
internal sealed record RowViewDefinition(string Sort = "Source", bool Descending = false, string? FieldId = null,
    string Title = "", RowFilter[]? Filters = null);
internal sealed record ProjectRowPreference(string ProjectId, RowViewDefinition Definition);
internal sealed record RowViewCandidate(ScopedId Project, string Definitions, string Previous, RowViewDefinition Definition);
internal sealed record RowValue(string State, string? Value, bool Conflict, ValueAvailability Availability);
internal sealed record RowTargetSelection(ScopedId Project, string[] Visible, string[] Selected, bool IncludeHidden)
{
    public bool NeedsConfirmation(IEnumerable<string> freshVisible) => !Visible.ToHashSet().SetEquals(freshVisible);
}

// This transient projection owns positions only. It is never a snapshot or a durable selection.
internal sealed class RowProjection(ScopedId project)
{
    public ScopedId Project { get; } = project;
    public string[] Ids { get; private set; } = [];
    public HashSet<string> Temporary { get; } = [];
    public long Generation { get; private set; }
    private string fingerprint = "";
    public string? Problem { get; private set; }
    public void Reapply(EditingWorkspace w, ProjectRegistration p)
    {
        if (p.Snapshot.Id != Project || w.Scope != Project.Scope) throw new InvalidOperationException("別Projectの表示です。");
        Problem = w.ViewProblem(p, w.RowView(p));
        Ids = Problem is null ? w.EvaluateRows(p).Select(r => r.ItemId).ToArray() : [];
        fingerprint = w.ViewFingerprint(p); Temporary.Clear(); Generation++;
    }
    public bool NeedsReapply(EditingWorkspace w, ProjectRegistration p, EditRow[]? canonicalRows = null)
        => fingerprint != w.ViewFingerprint(p, canonicalRows);
    public void Promote(EditingWorkspace w)
    {
        var mapping = w.Journal.Where(b => b.Project == Project).SelectMany(b => b.Creations ?? []).Where(c => c.Completed && c.ItemId is not null && !w.LocalRows.Any(r => r.Id == c.LocalId)).DistinctBy(c => c.LocalId).ToDictionary(c => c.LocalId, c => c.ItemId!);
        var next = Ids.Select(id => mapping.GetValueOrDefault(id, id)).Distinct().ToArray();
        if (!Ids.SequenceEqual(next))
        {
            var temporary = Temporary.Select(id => mapping.GetValueOrDefault(id, id)).ToArray();
            Temporary.Clear(); Temporary.UnionWith(temporary); Ids = next; Generation++;
        }
    }
    public EditRow[] Resolve(EditRow[] canonical)
    {
        var byId = canonical.ToDictionary(r => r.ItemId);
        return Ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }
    public void IncludeNew(EditRow[] canonical, IEnumerable<string> ids)
    {
        var present = canonical.Select(r => r.ItemId).ToHashSet();
        // A promotion can acknowledge before the panel receives its new complete registration.
        // Retain unresolved positions until explicit evaluation; Resolve never invents a row.
        var next = Ids.ToList();
        foreach (var id in ids.Where(present.Contains)) if (!next.Contains(id)) { next.Add(id); Temporary.Add(id); }
        if (!Ids.SequenceEqual(next)) { Ids = next.ToArray(); Generation++; }
    }
}

internal sealed partial class EditingWorkspace
{
    private readonly List<ProjectRowPreference> rowPreferences = [];
    public RowViewDefinition RowView(ProjectRegistration p) => rowPreferences.SingleOrDefault(v => v.ProjectId == p.Snapshot.Id.NodeId)?.Definition ?? new();
    public RowViewCandidate PrepareRowView(ProjectRegistration p) => new(p.Snapshot.Id, ColumnDefinitions(p), JsonSerializer.Serialize(RowView(p)), RowView(p));
    public void SaveRowView(RowViewCandidate candidate)
    {
        var p = CheckpointRegistrations.SingleOrDefault(p => p.Snapshot.Id == candidate.Project);
        if (candidate.Project.Scope != Scope || p is null || candidate.Definitions != ColumnDefinitions(p)
            || candidate.Previous != JsonSerializer.Serialize(RowView(p))) throw new InvalidOperationException("表示設定・定義が変わりました。設定を開き直してください。");
        ValidateRowPreferences([new(candidate.Project.NodeId, candidate.Definition)]);
        if (ViewProblem(p, candidate.Definition) is { } problem) throw new InvalidOperationException(problem);
        rowPreferences.RemoveAll(v => v.ProjectId == candidate.Project.NodeId);
        rowPreferences.Add(new(candidate.Project.NodeId, candidate.Definition)); Revision++;
    }
    internal static void ValidateRowPreferences(ProjectRowPreference[] values)
    {
        if (values.Any(v => v is null || string.IsNullOrWhiteSpace(v.ProjectId) || v.Definition is null)
            || values.Select(v => v.ProjectId).Distinct().Count() != values.Length) throw new InvalidDataException("Invalid row preferences.");
        foreach (var v in values)
        {
            var d = v.Definition; var filters = d.Filters ?? [];
            if (d.Sort is not ("Source" or "Title" or "Field") || d.Title is null
                || (d.Sort == "Field" ? string.IsNullOrWhiteSpace(d.FieldId) : d.FieldId is not null)
                || filters.Any(f => f is null || string.IsNullOrWhiteSpace(f.FieldId) || f.OptionIds is null || f.States is null)
                || filters.Select(f => f.FieldId).Distinct().Count() != filters.Length) throw new InvalidDataException("Invalid row definition.");
            foreach (var f in filters)
                if (f.OptionIds.Any(string.IsNullOrWhiteSpace) || f.OptionIds.Distinct().Count() != f.OptionIds.Length
                    || f.States.Any(s => s is not ("Empty" or "Unspecified" or "Unknown")) || f.States.Distinct().Count() != f.States.Length)
                    throw new InvalidDataException("Invalid row value choices.");
        }
    }
    public string? ViewProblem(ProjectRegistration p, RowViewDefinition d)
    {
        if (p.Snapshot.Id.Scope != Scope) return "別プロフィールの表示設定です。";
        if (!p.Snapshot.FieldsComplete) return "完全な定義を取得してから表示設定を再適用してください。";
        foreach (var id in (d.Filters ?? []).Select(f => f.FieldId).Concat(d.Sort == "Field" ? [d.FieldId!] : []).Distinct())
        {
            var field = SupportedColumns(p).SingleOrDefault(f => f.Id.NodeId == id && f.Availability == ValueAvailability.Present);
            if (field is null) return $"フィールド [{id}] が未確認です。条件を修復、または行設定をリセットしてください。";
            var filter = d.Filters?.SingleOrDefault(f => f.FieldId == id);
            if (filter?.OptionIds.FirstOrDefault(option => !field.Options.Any(o => o.Id == option)) is { } missing)
                return $"選択肢 [{id}/{missing}] が未確認です。条件を修復、または行設定をリセットしてください。";
        }
        return null;
    }
    public RowValue EffectiveRowValue(EditCell cell)
    {
        var f = Field(cell); var value = Value(cell);
        if (cell.Key?.Kind == "LocalSelect")
        {
            var select = Local(cell).Selects.SingleOrDefault(s => s.FieldId == cell.Key.FieldId);
            var state = select?.Intent switch { "Set" => cell.Options.Any(o => o.Id == value) ? "Present" : "Unknown", "ExplicitClear" => "Empty", _ => "Unspecified" };
            return new(state, value, false, cell.Availability);
        }
        var availability = f?.Observation?.Availability ?? cell.Availability;
        if (f?.Change is null && f?.Observation is { } observation) value = observation.Value;
        var known = f?.Change is not null || availability is ValueAvailability.Present or ValueAvailability.Empty;
        var stateName = !known ? "Unknown" : string.IsNullOrEmpty(value) ? "Empty" : "Present";
        if (cell.Key?.Kind == "Select" && value is not null && !cell.Options.Any(o => o.Id == value)) stateName = "Unknown";
        return new(stateName, value, f?.Conflict == true, availability);
    }
    private string LogicalRowIdentity(ProjectRegistration p, EditRow row) => Journal.Where(b => b.Project == p.Snapshot.Id)
        .SelectMany(b => b.Creations ?? []).LastOrDefault(c => c.Completed && c.ItemId == row.ItemId)?.LocalId ?? row.ItemId;
    public EditRow[] EvaluateRows(ProjectRegistration p)
    {
        var definition = RowView(p);
        if (ViewProblem(p, definition) is { } problem) throw new InvalidOperationException(problem);
        RowValue ValueFor(EditRow row, string? id) => EffectiveRowValue(id is null ? row.Cells[0] : row.Cells.Single(c => c.Key?.FieldId == id));
        var result = Open(p).Where(row => (definition.Title.Length == 0 || ValueFor(row, null) is { State: "Present", Value: { } title }
            && title.Contains(definition.Title, StringComparison.OrdinalIgnoreCase)) && (definition.Filters ?? []).All(filter => {
                var value = ValueFor(row, filter.FieldId);
                return value.State == "Present" ? filter.OptionIds.Contains(value.Value) : filter.States.Contains(value.State);
            })).ToArray();
        if (definition.Sort == "Source") return result;
        var fieldId = definition.Sort == "Field" ? definition.FieldId : null;
        var options = fieldId is null ? [] : p.Snapshot.Fields.Single(f => f.Id.NodeId == fieldId).Options.Select(o => o.Id).ToArray();
        int Rank(string state) => state switch { "Present" => 0, "Empty" => 1, "Unspecified" => 2, _ => 3 };
        Array.Sort(result, (a, b) => {
            var av = ValueFor(a, fieldId); var bv = ValueFor(b, fieldId);
            var cmp = Rank(av.State).CompareTo(Rank(bv.State));
            if (cmp == 0 && av.State == "Present") {
                cmp = fieldId is null ? StringComparer.OrdinalIgnoreCase.Compare(av.Value, bv.Value)
                    : Array.IndexOf(options, av.Value).CompareTo(Array.IndexOf(options, bv.Value));
                if (definition.Descending) cmp = -cmp;
            }
            return cmp == 0 ? StringComparer.Ordinal.Compare(LogicalRowIdentity(p, a), LogicalRowIdentity(p, b)) : cmp;
        });
        return result;
    }
    public string ViewFingerprint(ProjectRegistration p, EditRow[]? canonicalRows = null)
    {
        var definition = RowView(p);
        var fieldIds = (definition.Filters ?? []).Select(f => f.FieldId).ToHashSet();
        if (definition.Sort == "Field") fieldIds.Add(definition.FieldId!);
        var title = definition.Sort == "Title" || definition.Title.Length > 0;
        return JsonSerializer.Serialize(new { Definition = definition, Definitions = ColumnDefinitions(p),
            // The sheet already owns the current canonical row identities. A
            // pending-text transition must not reconstruct all native field
            // descriptions just to compare committed sort/filter values.
            Rows = (canonicalRows ?? Open(p)).Select(r => new { r.ItemId, Values = r.Cells.Where(c => c.Key is not null
                && (c.Key.Kind is "Title" or "LocalTitle" ? title : c.Key.FieldId is { } id && fieldIds.Contains(id))).Select(EffectiveRowValue) }) });
    }
    public bool RowHasWork(EditRow row) => row.IsLocal || row.Cells.Any(c => Changed(c) || Buffer(c) is not null || Field(c)?.Conflict == true);
}
