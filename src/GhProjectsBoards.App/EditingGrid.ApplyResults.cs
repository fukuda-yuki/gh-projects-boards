using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private ApplyAttention[] applyAttention = [];
    private readonly Dictionary<FieldKey, ApplyAttention> applyFields = [];
    private readonly TextBlock applyProblemText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid applyProblemStrip = new() { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly HashSet<string> temporaryApplyColumns = [];
    internal IEnumerable<string> TemporaryApplyColumns => temporaryApplyColumns;
    private readonly ToolTip applyProblemTip = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom };
    private ApplyAttention? activeApplyProblem;
    private string? unavailableApplyTarget;
    internal event EventHandler? ApplyHistoryRequested;

    private void InitializeApplyProblems(StackPanel footer)
    {
        applyProblemStrip.ColumnDefinitions.Add(new());
        applyProblemStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        applyProblemStrip.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(applyProblemText, "ApplyProblemStatus");
        AutomationProperties.SetLiveSetting(applyProblemText, AutomationLiveSetting.Polite);
        applyProblemStrip.Children.Add(applyProblemText);
        var next = new Button { Content = "次の問題へ", MinHeight = 28, Padding = new(8, 4, 8, 4) };
        AutomationProperties.SetAutomationId(next, "NextApplyProblem");
        next.Click += (_, _) =>
        {
            var current = Array.FindIndex(applyAttention, a => a == activeApplyProblem);
            if (applyAttention.Length > 0) GoToApplyProblem(applyAttention[(current + 1) % applyAttention.Length]);
        };
        var history = new Button { Content = "反映結果", MinHeight = 28, Padding = new(8, 4, 8, 4) };
        AutomationProperties.SetAutomationId(history, "GridApplyHistory");
        history.Click += (_, _) => { if (CanRefresh) ApplyHistoryRequested?.Invoke(this, EventArgs.Empty); };
        SetColumn(next, 1); SetColumn(history, 2); applyProblemStrip.Children.Add(next); applyProblemStrip.Children.Add(history);
        footer.Children.Add(applyProblemStrip);
        Unloaded += (_, _) => applyProblemTip.IsOpen = false;
    }

    private void RefreshApplyProblems()
    {
        applyAttention = ApplyResultsPresentation.Attention(session.Workspace.Journal).Where(a => a.Project == registration.Snapshot.Id).ToArray();
        applyFields.Clear();
        foreach (var item in applyAttention.Where(a => a.Field is not null)) applyFields.TryAdd(item.Field!, item);
        if (activeApplyProblem is not null)
            activeApplyProblem = applyAttention.FirstOrDefault(a => a.BatchId == activeApplyProblem.BatchId
                && a.RowId == activeApplyProblem.RowId && a.Field == activeApplyProblem.Field);
        if (activeApplyProblem is null) unavailableApplyTarget = null;
        applyProblemStrip.Visibility = applyAttention.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateApplyProblemText();
    }

    private void UpdateApplyProblemText()
    {
        if (unavailableApplyTarget is not null) { applyProblemText.Text = unavailableApplyTarget; return; }
        var selected = active ? ApplyProblem(rows[currentRow].Cells[currentColumn]) : null;
        applyProblemText.Text = selected is null ? ApplyResultsPresentation.Summary(applyAttention)
            : $"{selected.FieldName} — {selected.Description}";
        if (temporaryApplyColumns.Count > 0) applyProblemText.Text += "（問題の列を一時表示中）";
        if (selected is not null && CanRefresh && controls[currentRow].Length > currentColumn
            && controls[currentRow][currentColumn] is not TitleCell { Editing: true })
        {
            var target = controls[currentRow][currentColumn];
            applyProblemTip.XamlRoot = XamlRoot;
            if (applyProblemTip.PlacementTarget != target)
            {
                applyProblemTip.IsOpen = false;
                if (applyProblemTip.PlacementTarget is DependencyObject previous) ToolTipService.SetToolTip(previous, null);
                applyProblemTip.PlacementTarget = target;
                ToolTipService.SetToolTip(target, applyProblemTip);
            }
            applyProblemTip.Content = new TextBlock { Text = selected.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            applyProblemTip.IsOpen = target.IsLoaded;
        }
        else applyProblemTip.IsOpen = false;
    }

    private ApplyAttention? ApplyProblem(EditCell cell) => cell.Key is { } key ? applyFields.GetValueOrDefault(key) : null;

    internal bool GoToApplyProblem(ApplyAttention target)
    {
        if (!IsLoaded || target.Project != registration.Snapshot.Id) return false;
        if (!CanRefresh) { applyProblemText.Text = "IME変換中です。確定・取消後に「次の問題へ」で移動できます。"; return false; }
        activeApplyProblem = target;
        applyProblemTip.IsOpen = false;
        unavailableApplyTarget = null;
        var canonical = session.Workspace.Open(registration);
        var row = canonical.SingleOrDefault(r => r.ItemId == target.RowId);
        if (row is null || target.Field is not null && !row.Cells.Any(c => c.Key == target.Field))
        {
            applyProblemText.Text = unavailableApplyTarget = $"{target.Identity} / {target.FieldName}：対象を現在のProjectで確認できません。「反映結果」で確認してください。";
            return false;
        }
        var changed = !rows.Any(r => r.ItemId == target.RowId);
        projection.IncludeNew(canonical, [target.RowId]);
        if (target.Field?.FieldId is { } fieldId && layout.Hidden(fieldId))
        {
            temporaryApplyColumns.Add(fieldId);
            layout = new(layout.Columns.Select(c => temporaryApplyColumns.Contains(c.Id.FieldId ?? "")
                ? c with { Preference = c.Preference with { Visible = true } } : c).ToArray());
            changed = true;
        }
        if (changed) RebuildRows();
        var r = Array.FindIndex(rows, r => r.ItemId == target.RowId);
        var c = target.Field is null ? 0 : Array.FindIndex(rows[r].Cells, c => c.Key == target.Field);
        if (c < 0) return false;
        Select(r, c, false);
        UpdateApplyProblemText();
        return true;
    }
}
