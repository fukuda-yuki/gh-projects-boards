using System.Collections.Immutable;
using System.Globalization;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace GhProjectsBoards.App;

// #260 / #227 / #238: the attention strip above the command row and the one filter model behind it. The
// strip has a fixed height, so the read result at its right end never moves the rows (#233). Everything it
// filters on is a current-state value: no part of it compares with a previous read (D1).
internal sealed partial class PlanSheetView
{
    private static readonly CultureInfo StripCulture = CultureInfo.GetCultureInfo("ja-JP");
    private sealed record ToggleSpec(PlanToggle Toggle, string Label, string Id, string Help);
    // The strip's order is the agreed order of the attention sets (#260 / #122 terms).
    private static readonly ToggleSpec[] ToggleSpecs = [
        new(PlanToggle.Late, "遅れ", "PlanAttentionLate",
            "遅れ: 終了日を過ぎて未完了、または予定より遅れているタスク（赤: 終了日を過ぎた、黄: それ以外）"),
        new(PlanToggle.Cause, "遅れの原因", "PlanAttentionCause",
            "遅れの原因: 自分の遅れで後続を 1 つ以上遅らせているタスク"),
        new(PlanToggle.OverEstimate, "見積超過", "PlanAttentionOverEstimate",
            "見積超過: 実績 + 残が見積を超えたタスク"),
        new(PlanToggle.NotUpdated, "未更新", "PlanAttentionNotUpdated",
            "未更新: 開始日を過ぎて、実績と残が GitHub で 7 日以上変わっていないタスク")];
    private static readonly ToggleSpec ConflictSpec = new(PlanToggle.Conflict, "競合", "PlanAttentionConflict",
        "競合: 発行を止める競合があるタスク");
    private static readonly ToggleSpec AddedSpec = new(PlanToggle.Added, "新しい Issue", "PlanAttentionAdded",
        "新しい Issue: 最後の読み込みで計画に追加された Issue");

    // The attention sets of the open document; plan rows in the open order that match the active filter and
    // the requirement rows kept as dimmed context (#227).
    internal PlanAttention Attention { get; private set; } = PlanAttention.Empty;
    private PlanFilter activeFilter = PlanFilter.None;
    private PlanFilterResult filterResult = new([], ImmutableHashSet<string>.Empty);
    private HashSet<string> filterContext = [];
    private List<PlanChaseGroup> chaseGroups = [];
    internal bool FilterActive => activeFilter.IsActive;
    internal int FilterMatchCount => filterResult.Matches.Length;
    internal IReadOnlyList<PlanChaseGroup> FilterChase => chaseGroups;

    private readonly List<Control> attentionControls = [];
    private readonly Dictionary<PlanToggle, ToggleButton> attentionToggles = [];
    private readonly Dictionary<PlanToggle, TextBlock> attentionToggleLabels = [];
    private readonly Dictionary<PlanToggle, FontIcon> attentionToggleCrosses = [];
    private ToggleButton conflictToggle = null!;
    private ToggleButton addedToggle = null!;
    private readonly TextBlock readResult = Id(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("TextFillColorSecondaryBrush") }, "PlanReadResult");
    private Button assigneeButton = null!;
    private readonly TextBlock assigneeText = new() { VerticalAlignment = VerticalAlignment.Center };
    private Button assigneeClear = null!;
    private Button phaseButton = null!;
    private readonly TextBlock phaseText = new() { VerticalAlignment = VerticalAlignment.Center };
    private Button phaseClear = null!;
    private readonly Flyout assigneeFlyout = new();
    private readonly Flyout phaseFlyout = new();
    private readonly TextBox assigneeSearch = Id(new TextBox { PlaceholderText = "担当者を検索", Width = 240 }, "PlanAttentionAssigneeSearch");
    private readonly StackPanel assigneeOptions = new() { Spacing = 2 };
    private readonly StackPanel phaseOptions = new() { Spacing = 2 };
    private readonly Button namedChip = Id(new Button { Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        MinHeight = 28, Height = 28, Padding = new(8, 0, 8, 0), Visibility = Visibility.Collapsed }, "PlanAttentionNamedSet");
    private readonly Button namedClear = Id(new Button { Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        MinHeight = 28, Height = 28, Padding = new(4, 0, 4, 0), Visibility = Visibility.Collapsed }, "PlanAttentionClearNamed");

    // Builds the strip and returns the row's content. The caller places it above the command row.
    private Grid InitializeAttention()
    {
        var strip = new Grid { Height = 40, Padding = new(8, 0, 8, 0), ColumnSpacing = 8,
            BorderBrush = Brush("WorkspaceCardStrokeBrush"), BorderThickness = new(0, 0, 0, 1) };
        for (var column = 0; column < 7; column++)
            strip.ColumnDefinitions.Add(new() { Width = column == 4 ? new(1, GridUnitType.Star) : GridLength.Auto });
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var spec in ToggleSpecs)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var cross = new FontIcon { Glyph = "\uE711", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(label); content.Children.Add(cross);
            var button = Id(new ToggleButton { Content = content, MinHeight = 28, Height = 28, Padding = new(10, 0, 10, 0) }, spec.Id);
            button.Click += async (_, _) => {
                if (rendering) return;
                var on = button.IsChecked == true;
                await Run(async () => { await CommitPending(); AssignFilter(activeFilter with {
                    Toggles = on ? activeFilter.Toggles.Add(spec.Toggle) : activeFilter.Toggles.Remove(spec.Toggle) }); }, "Attention toggle");
                RenderAttentionStrip();
            };
            attentionToggles[spec.Toggle] = button; attentionToggleLabels[spec.Toggle] = label; attentionToggleCrosses[spec.Toggle] = cross;
            attentionControls.Add(button); toggles.Children.Add(button);
        }
        strip.Children.Add(toggles);
        // 担当者 ▾: multi-select with a search box over the Project's people and 担当者なし.
        assigneeButton = FilterButton("PlanAttentionAssignees", assigneeText, () => {
            RenderAssigneeOptions(assigneeSearch.Text = "");
            assigneeFlyout.ShowAt(assigneeButton);
        });
        assigneeClear = FilterClear("PlanAttentionClearAssignees", () => ClearPart(filter => filter with { Assignees = [] }));
        var assigneeGroup = FilterGroup(assigneeButton, assigneeClear);
        strip.Children.Add(assigneeGroup); SetColumn(assigneeGroup, 1);
        phaseButton = FilterButton("PlanAttentionPhases", phaseText, () => {
            RenderPhaseOptions();
            phaseFlyout.ShowAt(phaseButton);
        });
        phaseClear = FilterClear("PlanAttentionClearPhases", () => ClearPart(filter => filter with { Phases = [] }));
        var phaseGroup = FilterGroup(phaseButton, phaseClear);
        strip.Children.Add(phaseGroup); SetColumn(phaseGroup, 2);
        // The title search box moves onto the strip (#260); it is the only text part of the filter model.
        var search = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        filter.Padding = new(30, 4, 24, 4);
        var searchBox = new Grid();
        searchBox.Children.Add(filter);
        searchBox.Children.Add(Id(new FontIcon { Glyph = "\uE721", FontSize = 14, Width = 14, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0),
            IsHitTestVisible = false, IsTabStop = false, Foreground = Brush("TextFillColorSecondaryBrush") }, "PlanSheetFilterSearch"));
        search.Children.Add(searchBox); search.Children.Add(filterCount);
        strip.Children.Add(search); SetColumn(search, 3);
        var named = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        named.Children.Add(namedChip); named.Children.Add(namedClear);
        strip.Children.Add(named); SetColumn(named, 5);
        namedClear.Click += async (_, _) => await Run(async () => { await CommitPending(); AssignFilter(activeFilter with { Named = null }); }, "Clear named set");
        // Right end: the last complete read (#238). 競合 and 新しい Issue appear only while they hold rows.
        var read = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
        read.Children.Add(readResult);
        conflictToggle = AttentionChip(ConflictSpec);
        addedToggle = AttentionChip(AddedSpec);
        read.Children.Add(conflictToggle); read.Children.Add(addedToggle);
        strip.Children.Add(read); SetColumn(read, 6);
        var assigneePanel = new StackPanel { Spacing = 6, Padding = new(8) };
        assigneeSearch.TextChanged += (_, _) => RenderAssigneeOptions(assigneeSearch.Text);
        assigneePanel.Children.Add(assigneeSearch);
        assigneePanel.Children.Add(new ScrollViewer { Content = assigneeOptions, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        assigneeFlyout.Content = assigneePanel;
        assigneeFlyout.Placement = FlyoutPlacementMode.Bottom;
        phaseFlyout.Content = new ScrollViewer { Content = phaseOptions, MaxHeight = 360, Padding = new(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        phaseFlyout.Placement = FlyoutPlacementMode.Bottom;
        AutomationProperties.SetName(filter, "タイトルで絞り込み");
        AutomationProperties.SetAutomationId(strip, "PlanAttentionStrip");
        return strip;
    }
    private static Button FilterButton(string id, TextBlock text, Action open)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(text);
        content.Children.Add(new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var button = Id(new Button { Content = content, MinHeight = 28, Height = 28, Padding = new(8, 0, 8, 0) }, id);
        button.Click += (_, _) => open();
        return button;
    }
    private Button FilterClear(string id, Action clear)
    {
        var button = Id(new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, MinHeight = 28, Height = 28,
            Padding = new(4, 0, 4, 0), Visibility = Visibility.Collapsed }, id);
        button.Click += async (_, _) => await Run(async () => { await CommitPending(); clear(); }, "Clear filter part");
        attentionControls.Add(button);
        return button;
    }
    private StackPanel FilterGroup(Button button, Button clear)
    {
        attentionControls.Add(button);
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        group.Children.Add(button); group.Children.Add(clear);
        return group;
    }
    private ToggleButton AttentionChip(ToggleSpec spec)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var cross = new FontIcon { Glyph = "\uE711", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        content.Children.Add(label); content.Children.Add(cross);
        var button = Id(new ToggleButton { Content = content, MinHeight = 28, Height = 28, Padding = new(8, 0, 8, 0),
            Visibility = Visibility.Collapsed }, spec.Id);
        button.Click += async (_, _) => {
            if (rendering) return;
            var on = button.IsChecked == true;
            await Run(async () => { await CommitPending(); AssignFilter(activeFilter with {
                Toggles = on ? activeFilter.Toggles.Add(spec.Toggle) : activeFilter.Toggles.Remove(spec.Toggle) }); }, "Attention chip");
            RenderAttentionStrip();
        };
        attentionToggles[spec.Toggle] = button; attentionToggleLabels[spec.Toggle] = label; attentionToggleCrosses[spec.Toggle] = cross;
        attentionControls.Add(button);
        return button;
    }
    // The active filter's pressed state, counts, chosen values and read result.
    private void RenderAttentionStrip()
    {
        if (disposed) return;
        foreach (var spec in ToggleSpecs)
        {
            var count = Attention.Set(spec.Toggle).Count;
            var unavailable = spec.Toggle == PlanToggle.NotUpdated ? Attention.NotUpdatedUnavailable : null;
            var pressed = activeFilter.Toggles.Contains(spec.Toggle);
            var button = attentionToggles[spec.Toggle];
            rendering = true;
            try { button.IsChecked = pressed; }
            finally { rendering = false; }
            button.IsEnabled = count > 0 && unavailable is null;
            var label = attentionToggleLabels[spec.Toggle];
            label.Text = $"{spec.Label} {count.ToString("N0", StripCulture)}";
            AutomationProperties.SetName(button, label.Text);
            attentionToggleCrosses[spec.Toggle].Visibility = pressed ? Visibility.Visible : Visibility.Collapsed;
            var help = unavailable ?? spec.Help;
            ToolTipService.SetToolTip(button, help);
            AutomationProperties.SetHelpText(button, help);
        }
        RenderAttentionChip(ConflictSpec, conflictToggle, PlanToggle.Conflict, Attention.Conflict.Count);
        RenderAttentionChip(AddedSpec, addedToggle, PlanToggle.Added, Attention.Added.Count);
        var read = Session.Document.Sync.LastRead;
        readResult.Text = read is null ? "" : $"{read.At.ToString("M/d H:mm", StripCulture)} に GitHub から読み込み";
        readResult.Visibility = read is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(readResult, readResult.Text);
        var people = activeFilter.Assignees.Select(FilterAssigneeName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        assigneeText.Text = people.Length switch { 0 => "担当者", 1 => "担当者: " + people[0], _ => $"担当者: {people[0]} ほか {people.Length - 1}" };
        AutomationProperties.SetName(assigneeButton, people.Length == 0 ? "担当者で絞り込み" : assigneeText.Text);
        ToolTipService.SetToolTip(assigneeButton, people.Length == 0 ? "担当者で絞り込み" : assigneeText.Text);
        assigneeClear.Visibility = people.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(assigneeClear, "担当者の絞り込みを解除");
        var phases = activeFilter.Phases.OrderBy(name => name, StringComparer.Ordinal).Select(FilterPhaseName).ToArray();
        phaseText.Text = phases.Length switch { 0 => "工程", 1 => "工程: " + phases[0], _ => $"工程: {phases[0]} ほか {phases.Length - 1}" };
        AutomationProperties.SetName(phaseButton, phases.Length == 0 ? "工程で絞り込み" : phaseText.Text);
        ToolTipService.SetToolTip(phaseButton, phases.Length == 0 ? "工程で絞り込み" : phaseText.Text);
        phaseClear.Visibility = phases.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(phaseClear, "工程の絞り込みを解除");
        namedChip.Visibility = namedClear.Visibility = activeFilter.Named is null ? Visibility.Collapsed : Visibility.Visible;
        if (activeFilter.Named is { } named)
        {
            namedChip.Content = new TextBlock { Text = named.Name, FontSize = 12 };
            AutomationProperties.SetName(namedChip, named.Name);
            ToolTipService.SetToolTip(namedChip, named.Name);
        }
        AutomationProperties.SetName(namedClear, "絞り込みを解除");
        var culture = StripCulture;
        filterCount.Text = FilterActive ? $"{filterResult.Matches.Length.ToString("N0", culture)} 件" : "";
        filterCount.Visibility = FilterActive ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RenderAttentionChip(ToggleSpec spec, ToggleButton button, PlanToggle toggle, int count)
    {
        button.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) return;
        var pressed = activeFilter.Toggles.Contains(toggle);
        rendering = true;
        try { button.IsChecked = pressed; }
        finally { rendering = false; }
        var label = attentionToggleLabels[toggle];
        label.Text = $"{spec.Label} {count.ToString("N0", StripCulture)}";
        AutomationProperties.SetName(button, label.Text);
        attentionToggleCrosses[toggle].Visibility = pressed ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(button, spec.Help);
        AutomationProperties.SetHelpText(button, spec.Help);
    }
    private string FilterAssigneeName(string identity) => identity == PlanPeople.Unassigned ? "担当者なし" : AssigneeName(identity);
    private static string FilterPhaseName(string phase) => phase.Length == 0 ? "工程なし" : phase;
    private void RenderAssigneeOptions(string term)
    {
        assigneeOptions.Children.Clear();
        var matches = PeopleOptions().Where(person => term.Length == 0
            || person.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || person.Identity.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var (identity, name) in matches)
        {
            var check = Id(new CheckBox { Content = name, IsChecked = activeFilter.Assignees.Contains(identity), MinWidth = 0 }, "PlanAttentionAssignee_" + identity);
            check.Click += async (_, _) => {
                var on = check.IsChecked == true;
                await Run(async () => { await CommitPending(); AssignFilter(activeFilter with {
                    Assignees = on ? activeFilter.Assignees.Add(identity) : activeFilter.Assignees.Remove(identity) }); }, "Assignee filter");
                RenderAttentionStrip();
            };
            assigneeOptions.Children.Add(check);
        }
        if (matches.Length == 0) assigneeOptions.Children.Add(new TextBlock { Text = "一致する担当者はありません", Margin = new(4, 4, 4, 4) });
    }
    private IReadOnlyList<(string Identity, string Name)> PeopleOptions()
    {
        var found = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var person in Session.Document.State.Settings.People) if (seen.Add(person.Identity)) found.Add(person.Identity);
        foreach (var identity in Session.Document.Sync.PeopleNames.Keys.OrderBy(key => key, StringComparer.Ordinal)) if (seen.Add(identity)) found.Add(identity);
        foreach (var identity in Session.Document.State.Rows.SelectMany(row => row.Assignees).Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)) if (seen.Add(identity)) found.Add(identity);
        return found.Where(identity => identity != PlanPeople.Unassigned).Select(identity => (identity, AssigneeName(identity)))
            .Append((PlanPeople.Unassigned, "担当者なし")).ToArray();
    }
    private void RenderPhaseOptions()
    {
        phaseOptions.Children.Clear();
        foreach (var phase in PhaseOptions())
        {
            var value = phase.Length == 0 ? "" : phase;
            var check = Id(new CheckBox { Content = FilterPhaseName(value), IsChecked = activeFilter.Phases.Contains(value), MinWidth = 0 },
                "PlanAttentionPhase_" + (value.Length == 0 ? "none" : value));
            check.Click += async (_, _) => {
                var on = check.IsChecked == true;
                await Run(async () => { await CommitPending(); AssignFilter(activeFilter with {
                    Phases = on ? activeFilter.Phases.Add(value) : activeFilter.Phases.Remove(value) }); }, "Phase filter");
                RenderAttentionStrip();
            };
            phaseOptions.Children.Add(check);
        }
    }
    private IReadOnlyList<string> PhaseOptions()
    {
        var document = Session.Document;
        var mapping = document.State.Settings.Columns.FirstOrDefault(column => column.Role == PlanField.Phase);
        var definition = mapping is null ? null : document.Baseline.Columns.FirstOrDefault(column => column.Id == mapping.FieldId);
        if (definition is null) return [];
        var options = definition.Options.ToList();
        // 工程なし is a value only while the plan holds a task without one (#227).
        if (document.State.Rows.Any(row => string.IsNullOrEmpty(row.Phase))) options.Insert(0, "");
        return options;
    }
    private void ClearPart(Func<PlanFilter, PlanFilter> change) => AssignFilter(change(activeFilter));
    internal void ClearIdentityFilter() => ClearFilters();
    internal void ClearFilters() => AssignFilter(PlanFilter.None);
    // A named task set another surface hands in: the summary drill-down, a milestone or 後続を表示 (#260 4).
    internal void FilterTasks(IReadOnlySet<string> identities, string label, bool includeSummaries = false)
        => AssignFilter(new PlanFilter { Named = new(label, identities.ToImmutableHashSet(StringComparer.Ordinal), includeSummaries) });
    // The summary verdict's count buttons press this toggle and nothing else (#260 3).
    internal void FilterToggle(PlanToggle toggle) => AssignFilter(new PlanFilter { Toggles = [toggle] });
    private void AssignFilter(PlanFilter next)
    {
        activeFilter = next;
        // The title box is text, never a count (D2); every other part shows its own value on the strip.
        rendering = true;
        try { filter.Text = next.Title; }
        finally { rendering = false; }
        filterDelay.Stop(); filterGeneration++;
        Refresh();
        SelectFirstMatch();
    }
    // Applying or changing a filter selects the first matching row and brings it into view (#260 / #227).
    private void SelectFirstMatch()
    {
        if (!FilterActive || filterResult.Matches.Length == 0) return;
        var first = filterResult.Matches[0];
        if (!RowIds.Contains(first)) return;
        Select(first, PlanField.Title, false);
        FocusCell(first, PlanField.Title, scrollIntoView: true);
    }
    // The match set and the dimmed requirement parents for the active filter. Called from RefreshVisibleRows
    // once the document's effective rows are known.
    private void UpdateFilterResult(PlanDocument document)
    {
        if (!activeFilter.IsActive)
        {
            filterResult = new([], ImmutableHashSet<string>.Empty);
            filterContext = [];
            chaseGroups = [];
            return;
        }
        filterResult = Attention.Apply(activeFilter, document.State.Rows, SummaryIds);
        filterContext = filterResult.Context.ToHashSet(StringComparer.Ordinal);
        chaseGroups = PlanChase.Groups(document, filterResult.Matches, SummaryIds).ToList();
    }
    private bool IsFilterMatch(string identity) => filterContext.Count == 0 && filterResult.Matches.Length == 0 ? false
        : filterResult.Matches.Contains(identity) || filterContext.Contains(identity);
    // Restores the accepted filter text after a cancelled or unaccepted edit of the search box (#227).
    internal void CancelPendingFilter()
    {
        filterDelay.Stop();
        filterGeneration++;
        var wasRendering = rendering;
        rendering = true;
        try { filter.Text = activeFilter.Title; }
        finally { rendering = wasRendering; }
    }
    // The title search is the only text part of the filter model (D2). It applies after the debounce, and
    // Enter or clearing the box applies it at once.
    private Task ApplyFilter()
    {
        filterDelay.Stop();
        var proposed = filter.Text;
        var generation = filterGeneration;
        if (disposed || proposed == activeFilter.Title) return Task.CompletedTask;
        return Run(async () => {
            if (generation != filterGeneration || filter.Text != proposed) return;
            try { await CommitPending(); }
            catch {
                rendering = true;
                try { if (generation == filterGeneration && filter.Text == proposed) filter.Text = activeFilter.Title; }
                finally { rendering = false; }
                throw;
            }
            if (generation != filterGeneration || filter.Text != proposed || disposed) return;
            pendingFrame = metrics.Begin(Session.Document.State.Rows.Length, "filter");
            activeFilter = activeFilter with { Title = proposed };
            metrics.Mark("accepted");
            Refresh();
            SelectFirstMatch();
            metrics.Mark("presented");
            CompositionTarget.Rendered -= FrameRendered; CompositionTarget.Rendered += FrameRendered; frameSubscribed = true;
        }, "Filter");
    }
    // 担当者ごとにコピー: the chase list of the matches, through the sheet's clipboard path (#260 / #238 D7).
    internal async Task<bool> CopyFilteredByAssignee()
    {
        if (!FilterActive || chaseGroups.Count == 0) return false;
        var text = PlanChase.Text(Session.Document, chaseGroups);
        var write = writeClipboard;
        await WithClipboard(_ => {
            write(new PlanClipboardContent(text, null), CancellationToken.None);
            return Task.FromResult(true);
        });
        return true;
    }
}
