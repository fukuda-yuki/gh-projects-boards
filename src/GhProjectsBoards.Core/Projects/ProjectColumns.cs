using System.Text.Json;

namespace GhProjectsBoards.Core.Projects;

// A presentation role has no Issue, item or local-row identity.
internal sealed record ColumnIdentity(string Role, string? FieldId = null)
{
    public static readonly ColumnIdentity Title = new("Title");
    public static readonly ColumnIdentity Reference = new("Reference");
}
internal sealed record ColumnPreference(ColumnIdentity Id, bool Visible, double Width);
internal sealed record ProjectColumnPreferences(string ProjectId, ColumnPreference[] Columns);
internal sealed record ColumnCandidate(ScopedId Project, string Definitions, ColumnPreference[] Columns);
internal sealed record PresentationColumn(ColumnPreference Preference, string Name, bool Available)
{
    public ColumnIdentity Id => Preference.Id;
}

internal sealed class ColumnLayout(PresentationColumn[] columns)
{
    public PresentationColumn[] Columns { get; } = columns;
    public PresentationColumn[] Visible => Columns.Where(c => c.Available && c.Preference.Visible).ToArray();
    public bool Hidden(string? fieldId) => fieldId is not null && !Visible.Any(c => c.Id == new ColumnIdentity("Field", fieldId));
    public EditRow[] Resolve(EditRow[] canonical) => canonical.Select(row => row with {
        Cells = Visible.Select(column => column.Id.Role switch {
            "Title" => row.Cells[0], "Reference" => row.Cells[^1],
            _ => row.Cells.Single(c => c.Key?.FieldId == column.Id.FieldId)
        }).ToArray()
    }).ToArray();
}

internal sealed partial class EditingWorkspace
{
    public const double MinimumColumnWidth = 80, MaximumColumnWidth = 1200;
    private readonly List<ProjectColumnPreferences> columnPreferences = [];
    private ProjectFieldDefinition[] SupportedColumns(ProjectRegistration p) => p.Snapshot.Fields
        .Where(f => f.ValueOwner == FieldOwner.ProjectItem && (f.DataType == "SINGLE_SELECT"
            || Planning(p.Snapshot.Id.NodeId)?.Fields.Any(b => b.FieldId == f.Id.NodeId && b.DataType == f.DataType) == true)).ToArray();
    private static string ColumnDefinitions(ProjectRegistration p) => JsonSerializer.Serialize(new {
        p.Snapshot.FieldsComplete, Fields = p.Snapshot.Fields.Select(f => new { f.Id, f.Name, f.DataType, f.ValueOwner, f.Availability, f.Options })
    });
    public ColumnLayout Columns(ProjectRegistration p, ColumnPreference[]? candidate = null)
    {
        if (p.Snapshot.Id.Scope != Scope) throw new InvalidOperationException("別プロフィールの列設定です。");
        var configured = candidate ?? columnPreferences.SingleOrDefault(s => s.ProjectId == p.Snapshot.Id.NodeId)?.Columns ?? [];
        var supported = SupportedColumns(p);
        var result = new List<PresentationColumn>();
        ColumnPreference Fixed(ColumnIdentity id, double width) => configured.SingleOrDefault(c => c.Id == id) ?? new(id, true, width);
        result.Add(new(Fixed(ColumnIdentity.Title, 360), "タイトル", true));
        foreach (var pref in configured.Where(c => c.Id.Role == "Field"))
        {
            var field = supported.SingleOrDefault(f => f.Id.NodeId == pref.Id.FieldId);
            result.Add(new(pref, field?.Name ?? "未確認フィールド", field is not null));
        }
        foreach (var field in supported.Where(f => !configured.Any(c => c.Id.FieldId == f.Id.NodeId)))
            result.Add(new(new(new("Field", field.Id.NodeId), true, 144), field.Name, true));
        result.Add(new(Fixed(ColumnIdentity.Reference, 200), "Repository", true));
        return new(result.ToArray());
    }
    public ColumnCandidate PrepareColumns(ProjectRegistration p) => new(p.Snapshot.Id, ColumnDefinitions(p), Columns(p).Columns.Select(c => c.Preference).ToArray());
    public ColumnPreference[] DefaultColumns(ProjectRegistration p)
    {
        var defaults = Columns(p, []).Columns.Select(c => c.Preference).ToArray();
        return defaults[..^1].Concat(Columns(p).Columns.Where(c => !c.Available).Select(c => c.Preference)).Append(defaults[^1]).ToArray();
    }
    public void SaveColumns(ColumnCandidate candidate)
    {
        var current = registrations?.Select(RegistrationStore.FromRecord).SingleOrDefault(p => p.Snapshot.Id == candidate.Project);
        if (candidate.Project.Scope != Scope || current is null || ColumnDefinitions(current) != candidate.Definitions)
            throw new InvalidOperationException("列定義が変わりました。設定を開き直してください。");
        ValidateColumns([new(candidate.Project.NodeId, candidate.Columns)]);
        var expected = Columns(current).Columns.Select(c => c.Id).ToHashSet();
        if (!expected.SetEquals(candidate.Columns.Select(c => c.Id))) throw new InvalidOperationException("列の識別子が変わりました。設定を開き直してください。");
        columnPreferences.RemoveAll(p => p.ProjectId == candidate.Project.NodeId);
        columnPreferences.Add(new(candidate.Project.NodeId, candidate.Columns.ToArray())); Revision++;
    }
    internal static void ValidateColumns(ProjectColumnPreferences[] preferences)
    {
        if (preferences.Any(p => p is null || string.IsNullOrWhiteSpace(p.ProjectId) || p.Columns is null)
            || preferences.Select(p => p.ProjectId).Distinct().Count() != preferences.Length) throw new InvalidDataException("Invalid Project column settings.");
        foreach (var p in preferences)
        {
            var c = p.Columns;
            if (c.Length < 2 || c.Any(x => x is null || x.Id is null || !double.IsFinite(x.Width) || x.Width < MinimumColumnWidth || x.Width > MaximumColumnWidth
                || (x.Id.Role == "Field" ? string.IsNullOrWhiteSpace(x.Id.FieldId) : x.Id.Role is not ("Title" or "Reference") || x.Id.FieldId is not null))
                || c.Select(x => x.Id).Distinct().Count() != c.Length || c[0].Id != ColumnIdentity.Title || !c[0].Visible
                || c[^1].Id != ColumnIdentity.Reference || !c[^1].Visible) throw new InvalidDataException("Invalid column identity, order or width.");
        }
    }
}
