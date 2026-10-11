using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace GhProjectsBoards.App;

internal sealed partial class PlanSheetView
{
    private readonly Dictionary<AppBarButton, Func<Task>> commandActions = [];
    private readonly MenuFlyout commandMenu = new() { AreOpenCloseAnimationsEnabled = false };
    private readonly AppBarButton moreCommands = Id(new AppBarButton { Label = "その他のオプション", Icon = new SymbolIcon(Symbol.More),
        LabelPosition = CommandBarLabelPosition.Collapsed, Width = 48 }, "PlanSheetMore");
    private Func<Task>? pendingMenuAction;

    private Grid InitializeCommandMenu(SheetCommandBar bar)
    {
        // CommandBar still chooses which primary commands fit. Its built-in popup has
        // no public animation switch, so the visible overflow uses an owned native menu.
        var toolbar = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(bar);
        toolbar.Children.Add(moreCommands); SetColumn(moreCommands, 1);
        moreCommands.Flyout = commandMenu;
        bar.ChangeMenuOpen = open => { if (open && !remoteBusy) commandMenu.ShowAt(moreCommands); else commandMenu.Hide(); };
        commandMenu.Opened += (_, _) => bar.SetMenuOpenState(true);
        AutomationProperties.SetName(moreCommands, moreCommands.Label);
        ToolTipService.SetToolTip(moreCommands, moreCommands.Label);
        moreCommands.PreviewKeyDown += (_, args) => {
            if (args.Key is not (VirtualKey.Down or VirtualKey.Up)) return;
            args.Handled = true;
            commandMenu.ShowAt(moreCommands);
        };
        commandMenu.Opening += (_, _) => {
            commandMenu.Items.Clear();
            var separator = false;
            foreach (var command in bar.PrimaryCommands) {
                if (command is AppBarSeparator) separator = commandMenu.Items.Count > 0;
                else if (command is AppBarButton { IsInOverflow: true } button) {
                    if (separator) commandMenu.Items.Add(new MenuFlyoutSeparator());
                    AddItem(button); separator = false;
                }
            }
            if (commandMenu.Items.Count > 0) commandMenu.Items.Add(new MenuFlyoutSeparator());
            foreach (var command in bar.SecondaryCommands) {
                if (command is AppBarButton button) AddItem(button);
                else if (command is AppBarSeparator) commandMenu.Items.Add(new MenuFlyoutSeparator());
            }
        };
        commandMenu.Closed += async (_, _) => {
            bar.SetMenuOpenState(false);
            var action = pendingMenuAction;
            pendingMenuAction = null;
            // Dismissal restores menu focus first. Commands may then focus an invalid
            // editor or open their own flyout without the closing menu dismissing it.
            if (action is not null && !remoteBusy && !disposed && moreCommands.IsLoaded) await action();
        };
        return toolbar;

        void AddItem(AppBarButton button)
        {
            var item = Id(new MenuFlyoutItem { Text = button.Label, IsEnabled = button.IsEnabled,
                KeyboardAcceleratorTextOverride = button.KeyboardAcceleratorTextOverride, Icon = button.Icon switch {
                    SymbolIcon icon => new SymbolIcon(icon.Symbol),
                    PathIcon icon => new PathIcon { Data = icon.Data },
                    _ => null
                } }, AutomationProperties.GetAutomationId(button));
            AutomationProperties.SetName(item, button.Label);
            item.Click += (_, _) => {
                if (remoteBusy || !button.IsEnabled) return;
                pendingMenuAction = button.Flyout is { } flyout
                    ? () => { flyout.ShowAt(moreCommands); return Task.CompletedTask; }
                    : commandActions[button];
            };
            commandMenu.Items.Add(item);
        }
    }

    private sealed class SheetCommandBar : CommandBar
    {
        internal Action<bool> ChangeMenuOpen { get; set; } = _ => { };
        internal bool MenuOpen { get; private set; }
        internal void SetMenuOpenState(bool open)
        {
            if (MenuOpen == open) return;
            MenuOpen = open;
            if (FrameworkElementAutomationPeer.FromElement(this) is SheetCommandBarAutomationPeer peer) peer.MenuStateChanged(open);
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new SheetCommandBarAutomationPeer(this);
    }

    // AppBar's default peer opens its own popup even with the built-in More button
    // hidden. Route the public automation patterns to the same menu users operate.
    private sealed class SheetCommandBarAutomationPeer(SheetCommandBar owner) : AppBarAutomationPeer(owner), IExpandCollapseProvider, IToggleProvider
    {
        protected override object GetPatternCore(PatternInterface pattern) => pattern is PatternInterface.ExpandCollapse or PatternInterface.Toggle
            ? this : base.GetPatternCore(pattern);
        ExpandCollapseState IExpandCollapseProvider.ExpandCollapseState => owner.MenuOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        ToggleState IToggleProvider.ToggleState => owner.MenuOpen ? ToggleState.On : ToggleState.Off;
        void IExpandCollapseProvider.Expand() => owner.ChangeMenuOpen(true);
        void IExpandCollapseProvider.Collapse() => owner.ChangeMenuOpen(false);
        void IToggleProvider.Toggle() => owner.ChangeMenuOpen(!owner.MenuOpen);
        internal void MenuStateChanged(bool open)
        {
            RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                open ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded,
                open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
            RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                open ? ToggleState.Off : ToggleState.On, open ? ToggleState.On : ToggleState.Off);
        }
    }
}
