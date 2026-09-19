using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

// Follow the ordinary workspace routes before operating their public UIA controls.
internal static class WorkspaceUi
{
    internal static void WaitForApplyStopped(Window window)
    {
        Wait(() => Find(window, "CancelProjectButton")?.IsEnabled != true &&
            (Find(window, "ApplyHistoryButton")?.IsEnabled == true || Visible(Find(window, "ApplyOutcomeWarning"))),
            "The approved execution must settle.", TimeSpan.FromSeconds(60));
        if (!RegistrationStatusText(window).Contains("反映完了"))
        {
            Wait(() => Visible(Find(window, "ApplyOutcomeWarning")), "Incomplete work must show its brief warning.");
            Element(window, "CloseButton").AsButton().Invoke();
        }
    }
    private static readonly HashSet<string> ConnectionControls = [
        "ConnectionScreen", "ExecutablePath", "HostInput",
        "DetectGhButton", "BrowseGhButton", "CheckConnectionButton", "NewConnectionButton", "CancelConnectionButton",
        "ConnectionStatus", "AccountValue", "StorageValue", "LoginHelpExpander", "ConnectionDetails"
    ];
    private static readonly HashSet<string> ConnectionDetailsControls = ["ExecutablePath", "DetectGhButton", "BrowseGhButton", "VersionValue", "StorageValue", "ScopeValue", "EnvironmentValue"];
    private static readonly HashSet<string> ProjectSettingsControls = ["ProjectInformation", "DefaultRepository", "SaveProjectSettingButton", "UnregisterProjectButton", "ProjectStatusDetailsButton", "ProjectTargetDiagnostics"];
    private static readonly HashSet<string> DiscoveryControls = [
        "DiscoveryOwners", "LoadOwnersButton", "DiscoveryOwner", "LoadRepositoriesButton", "DiscoveryRepositories", "DiscoverySearch", "SearchProjectsButton"
    ];
    private static readonly HashSet<string> GridCommands = [
        "GridAddRow", "GridDuplicateRows", "GridRemoveRows", "GridAppendRows", "GridCopy", "GridPaste", "GridClear", "GridUndo", "GridFillDown",
        "GridColumns", "GridRowSettings", "GridSave", "GridConflicts", "GridPlanning", "GridPlanningSettings", "GridInitializePlans"
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
        // During native pane animation the closing content can still report visible.
        // The public toggle name reflects the actual open/closed navigation state.
        if (Find(window, "ToggleProjectNavigation")?.Name == "Project一覧を折りたたむ" && Visible(Find(window, "SavedProfiles"))) return false;
        InvokeRoute(window, "ToggleProjectNavigation");
        Wait(() => Find(window, "ToggleProjectNavigation")?.Name == "Project一覧を折りたたむ" && Visible(Find(window, "SavedProfiles")),
            "Project navigation must be open and visible before selecting saved work.");
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
        Element(window, id.Replace("GridCell", "GridChoiceArrow")).AsButton().Invoke();
        AutomationElement? choice = null;
        Wait(() => {
            try { choice = choose(window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.MenuItem))
                .Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("ChoiceOption-") && Visible(e)).ToArray()); return choice is not null; }
            catch (System.Runtime.InteropServices.COMException) { return false; } // Retry only the read during native popup transition.
        },
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
    internal static string RegistrationStatusText(Window window)
    {
        var visibleStatus = Find(window, "RegistrationStatus");
        if (Visible(visibleStatus)) return visibleStatus!.Name;
        // Routine status remains in the public accessible description of the
        // Project details entry without moving focus out of native input.
        return Element(window, "ProjectSettingsButton").Properties.HelpText.Value;
    }
    internal static AutomationElement Element(Window window, string id)
    {
        // The always-visible return button identifies the page even when a
        // ScrollViewer peer is omitted or a system picker obscures its owner.
        if (ConnectionControls.Contains(id) && Find(window, "ProjectsPageButton") is null)
        {
            Wait(() => Find(window, "ProjectsPageButton") is not null || Find(window, "ConnectionPageButton")?.IsEnabled == true,
                "Connection settings or its entry must be available after the native dialog settles.");
            if (Find(window, "ProjectsPageButton") is null) InvokeRoute(window, "ConnectionPageButton");
        }
        if (ConnectionDetailsControls.Contains(id))
        {
            Wait(() => Find(window, "ConnectionDetails") is not null, "Connection settings must load before opening CLI details.");
            Find(window, "ConnectionDetails")!.Patterns.ExpandCollapse.Pattern.Expand();
            Wait(() => Visible(Find(window, id)), "The connection detail must be visible: " + id);
        }
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
        if (id == "ApplyHistoryButton" && Visible(Find(window, "ApplyHistoryDialog"))) return;
        if (!ProjectSettingsControls.Contains(id) && id != "ProjectSettingsButton") CloseProjectSettings(window);
        // The return command is only shown on the connection page.
        if (id == "ProjectsPageButton" && Visible(Find(window, "WorkspaceIdentity"))) return;
        var button = Element(window, id);
        Wait(() => button.IsEnabled, "The command must be enabled: " + id, timeout);
        if (button.Patterns.Toggle.IsSupported) button.Patterns.Toggle.Pattern.Toggle();
        else button.AsButton().Invoke();
    }
    internal static void WaitForApplyReady(Window window) => Wait(() =>
        Find(window, "ApplyReviewDialog") is not null && Find(window, "PrimaryButton")?.IsEnabled == true,
        "The selected changes must finish their automatic latest-state check.");
    internal static ListBoxItem[] ApplyRows(Window window)
    {
        var list = Element(window, "ApplyTargetRows");
        return list.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem)).Select(e => e.AsListBoxItem()).ToArray();
    }
    internal static void OpenTargetDiagnostics(Window window)
    {
        Invoke(window, "ProjectsPageButton");
        Invoke(window, "AddProjectButton");
        Invoke(window, "RegistrationTargetDiagnostics");
        Wait(() => Find(window, "TargetDiagnosticsDialog") is not null, "Target diagnostics must open separately from connection settings.");
    }
}
