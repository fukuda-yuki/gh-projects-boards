using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace GhProjectsBoards.App.GridPrototype;

internal enum GridField { Title, State, Number, Date, Choice }
internal readonly record struct GridAddress(int RowId, GridField Field);
internal sealed record GridValues(string Title, string State, decimal? Number, DateOnly? Date, string? Choice);
internal sealed record GridInputError(GridAddress Address, string Message);
internal sealed record GridResult(bool Succeeded, int ChangedCells, IReadOnlyList<GridInputError> Errors);

internal sealed class GridPrototypeRow(int id, GridValues values, bool isNew = false) : INotifyPropertyChanged
{
    private Dictionary<GridField, string> errors = [];
    public int Id { get; } = id;
    public string RowLabel => $"{Id:000}";
    public bool IsNew { get; } = isNew;
    public override string ToString() => $"行 {RowLabel}";
    public GridValues Values { get; private set; } = values;
    public string Title => Values.Title;
    public string State => Values.State;
    public string NumberText => Values.Number?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string DateText => Values.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    public string Choice => Values.Choice ?? "";
    public string TitleError => Error(GridField.Title);
    public string StateError => Error(GridField.State);
    public string NumberError => Error(GridField.Number);
    public string DateError => Error(GridField.Date);
    public string ChoiceError => Error(GridField.Choice);
    public string Error(GridField field) => errors.GetValueOrDefault(field)
        ?? (field == GridField.Title && string.IsNullOrWhiteSpace(Title) ? "タイトルは必須です。" : "");
    public string Text(GridField field) => field switch
    {
        GridField.Title => Title, GridField.State => State, GridField.Number => NumberText,
        GridField.Date => DateText, GridField.Choice => Choice, _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    internal void SetValues(GridValues next)
    {
        Values = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
    internal void SetErrors(IEnumerable<GridInputError> next)
    {
        var updated = next.GroupBy(error => error.Address.Field).ToDictionary(group => group.Key, group => group.First().Message);
        if (errors.Count == 0 && updated.Count == 0) return;
        errors = updated;
        foreach (var property in new[] { nameof(TitleError), nameof(StateError), nameof(NumberError), nameof(DateError), nameof(ChoiceError) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
