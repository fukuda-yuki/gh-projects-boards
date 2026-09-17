using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

// Follow the ordinary workspace routes before operating their public UIA controls.
internal static class WorkspaceUi
{
    private static readonly HashSet<string> ConnectionControls = [
        "ConnectionScreen", "ExecutablePath", "HostInput", "IssueUrlInput", "ProjectUrlInput",
        "DetectGhButton", "BrowseGhButton", "CheckConnectionButton", "NewConnectionButton", "CancelConnectionButton",
        "ConnectionStatus", "AccountValue", "StorageValue", "IssueResult", "ProjectResult", "LoginHelpExpander"
    ];
    private static readonly HashSet<string> ProjectSettingsControls = ["ProjectInformation", "DefaultRepository", "SaveProjectSettingButton", "UnregisterProjectButton"];
    private static readonly HashSet<string> DiscoveryControls = [
        "DiscoveryOwners", "LoadOwnersButton", "DiscoveryOwner", "LoadRepositoriesButton", "DiscoveryRepositories", "DiscoverySearch", "SearchProjectsButton"
    ];
    private static readonly HashSet<string> GridCommands = [
        "GridAddRow", "GridDuplicateRows", "GridRemoveRows", "GridAppendRows", "GridCopy", "GridPaste", "GridClear", "GridUndo",
        "GridColumns", "GridRowSettings", "GridSave", "GridConflicts"
    ];
    private static AutomationElement? Find(Window window, string id)
    {
        try { return window.FindFirstDescendant(cf => cf.ByAutomationId(id)); }
        // WinUI can temporarily reject a tree query during a presentation update.
        // The caller's bounded wait must still observe the requested public control.
        catch (System.Runtime.InteropServices.COMException error) when (error.HResult == unchecked((int)0x80131505)) { return null; }
    }
    private static bool Visible(AutomationElement? element) => element is not null && !element.Properties.IsOffscreen.Value;
    internal static bool HasVisibleElement(Window window, string id) => Visible(Find(window, id));
    private static void Wait(Func<bool> condition, string message, TimeSpan? timeout = null) => Assert.That(
        Retry.WhileFalse(condition, timeout ?? TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100)).Result, Is.True, message);
    private static void InvokeRoute(Window window, string id)
    {
        Wait(() => Find(window, id)?.IsEnabled == true, "The route must be available: " + id);
        Find(window, id)!.AsButton().Invoke();
    }
    internal static bool OpenProjectNavigation(Window window)
    {
        if (Visible(Find(window, "SavedProfiles"))) return false;
        InvokeRoute(window, "ToggleProjectNavigation");
        Wait(() => Visible(Find(window, "SavedProfiles")), "Project navigation must be visible before selecting saved work.");
        return true;
    }
    internal static void CloseProjectNavigation(Window window)
    {
        if (!Visible(Find(window, "SavedProfiles"))) return;
        InvokeRoute(window, "ToggleProjectNavigation");
        Wait(() => !Visible(Find(window, "SavedProfiles")), "Project navigation must dismiss before returning to the sheet.");
    }
    internal static AutomationElement ProjectNavigation(Window window)
    {
        var candidates = Array.Empty<AutomationElement>();
        var result = Retry.WhileNull(() =>
        {
            candidates = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Tree)).Where(Visible).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)).Result;
        if (result is not null) return result;
        var description = string.Join("; ", candidates.Select(e => $"id={e.AutomationId}, class={e.ClassName}, runtime={string.Join(',', e.Properties.RuntimeId.Value)}, bounds={e.BoundingRectangle}, parent={e.Parent?.ClassName}"));
        throw new AssertionException("The visible Project tree did not become unique: " + description);
    }
    internal static string ChoiceText(Window window, string id) => Element(window, id + "Value").Name;
    internal static void HeaderCommand(Window window, int column, string id)
    {
        Element(window, "GridHeaderMenu" + column).AsButton().Invoke();
        Wait(() => Visible(Find(window, id)), "The header menu action must be visible: " + id);
        Element(window, id).Patterns.Invoke.Pattern.Invoke();
        Wait(() => !Visible(Find(window, id)), "The header menu must finish: " + id);
    }
    internal static void SelectCombo(Window window, string id, int index)
    {
        if (id.StartsWith("GridCell")) SelectChoice(window, id, items => items.ElementAtOrDefault(index));
        else SelectCombo(window, id, items => items.Length > index ? items[index] : null);
    }
    internal static void SelectCombo(Window window, string id, string name)
    {
        if (id.StartsWith("GridCell")) SelectChoice(window, id, items => items.SingleOrDefault(item => item.Name == name));
        else SelectCombo(window, id, items => items.FirstOrDefault(item => item.Name == name));
    }
    internal static void SelectLastChoice(Window window, string id) => SelectChoice(window, id, items => items.LastOrDefault());
    internal static void ToggleChoice(Window window, string id)
    {
        var current = ChoiceText(window, id);
        SelectChoice(window, id, items => items.FirstOrDefault(item => item.Properties.HelpText.ValueOrDefault != current));
    }
    private static void SelectChoice(Window window, string id, Func<AutomationElement[], AutomationElement?> choose)
    {
        Element(window, id).AsButton().Invoke();
        AutomationElement? choice = null;
        Wait(() => (choice = choose(window.FindAllDescendants().Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("ChoiceOption-") && Visible(e)).ToArray())) is not null,
            "The requested native choice must be visible: " + id);
        var expected = choice!.Properties.HelpText.Value;
        choice.Patterns.Invoke.Pattern.Invoke();
        Wait(() => ChoiceText(window, id) == expected, "The chosen cell value must be rendered: " + id);
    }
    private static void SelectCombo(Window window, string id, Func<ComboBoxItem[], ComboBoxItem?> choose)
    {
        var combo = Element(window, id).AsComboBox();
        // FlaUI Items expands the popup. Select through its public item pattern,
        // then explicitly finish that popup before querying the underlying view.
        var item = Retry.WhileNull(() => choose(combo.Items), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100)).Result
            ?? throw new AssertionException("The requested option did not load: " + id);
        var name = item.Name;
        item.Select();
        Wait(() => combo.Patterns.Selection.Pattern.Selection.Value.Any(selected => selected.Name == name), "The option must be selected: " + id);
        combo.Patterns.ExpandCollapse.Pattern.Collapse();
        Wait(() => combo.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == FlaUI.Core.Definitions.ExpandCollapseState.Collapsed,
            "The options popup must close: " + id);
    }
    internal static void CloseProjectSettings(Window window)
    {
        if (!Visible(Find(window, "DefaultRepository"))) return;
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Wait(() => !Visible(Find(window, "DefaultRepository")), "Project settings must dismiss before returning to the sheet.");
    }
    internal static string ProjectInformation(Window window)
    {
        var information = Element(window, "ProjectInformation").Name;
        CloseProjectSettings(window);
        return information;
    }
    internal static AutomationElement Element(Window window, string id)
    {
        if (ConnectionControls.Contains(id) && !Visible(Find(window, "ConnectionScreen")))
            InvokeRoute(window, "ConnectionPageButton");
        if (ProjectSettingsControls.Contains(id) && Find(window, id) is null)
            InvokeRoute(window, "ProjectSettingsButton");
        if (DiscoveryControls.Contains(id))
        {
            Wait(() => Visible(Find(window, "ProjectDiscoverySearchExpander")), "The discovery form must load before expanding its search route.");
            var group = Find(window, "ProjectDiscoverySearchExpander")!;
            group.Patterns.ExpandCollapse.Pattern.Expand();
            Wait(() => group.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value == FlaUI.Core.Definitions.ExpandCollapseState.Expanded,
                "Project discovery search must expand.");
        }
        if (id.StartsWith("RowFilter-", StringComparison.Ordinal))
        {
            var group = window.FindAllDescendants().Where(e => (e.AutomationId ?? "").StartsWith("RowFilterGroup-", StringComparison.Ordinal))
                .OrderByDescending(e => e.AutomationId.Length)
                .FirstOrDefault(e => id.StartsWith("RowFilter-" + e.AutomationId["RowFilterGroup-".Length..] + "-", StringComparison.Ordinal));
            group?.Patterns.ExpandCollapse.Pattern.Expand();
        }
        if (GridCommands.Contains(id) && !Visible(Find(window, id)))
        {
            var bar = Find(window, "GridCommandBar");
            var more = bar?.FindFirstDescendant(cf => cf.ByAutomationId("MoreButton"));
            Assert.That(more, Is.Not.Null, "The command must have a public overflow route: " + id);
            more!.AsButton().Invoke();
            Wait(() => Visible(Find(window, id)), "The overflow command must become visible: " + id);
        }
        return Retry.WhileNull(() => Find(window, id), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)).Result
            ?? throw new AssertionException("Missing control: " + id);
    }
    internal static void Invoke(Window window, string id, TimeSpan? timeout = null)
    {
        if (!ProjectSettingsControls.Contains(id) && id != "ProjectSettingsButton") CloseProjectSettings(window);
        // The return command is only shown on the connection page.
        if (id == "ProjectsPageButton" && Visible(Find(window, "WorkspaceIdentity"))) return;
        var button = Element(window, id);
        Wait(() => button.IsEnabled, "The command must be enabled: " + id, timeout);
        button.AsButton().Invoke();
    }
}
