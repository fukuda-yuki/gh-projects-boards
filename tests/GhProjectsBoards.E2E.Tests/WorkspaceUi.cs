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
    internal static AutomationElement ProjectNavigation(Window window) => window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Tree)).Single(Visible);
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
            var group = Find(window, "ProjectDiscoverySearchExpander");
            if (group is not null) group.Patterns.ExpandCollapse.Pattern.Expand();
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
