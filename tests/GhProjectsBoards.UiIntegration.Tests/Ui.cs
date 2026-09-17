using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

internal static class Ui
{
    public static DispatcherQueue Queue = null!;
    public static Window Window = null!;
    public static Grid Root = null!;
    public static Exception? Fatal;
    public static void RecordFailure(Exception error) => Interlocked.CompareExchange(ref Fatal, error, null);
    public static async Task Run(Action action) => await Run(() => { action(); return Task.CompletedTask; });
    public static async Task Run(Func<Task> action, bool check = true)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Queue.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
        })) throw new InvalidOperationException("UI dispatch rejected");
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException error) { RecordFailure(error); throw; }
        if (check) Check();
    }
    public static void Check() { if (Fatal is { } error) throw new AssertionException("Unhandled asynchronous UI failure", error); }
    public static async Task Idle()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref TrackedContext.Operations) != 0 || Volatile.Read(ref TrackedContext.Posts) != 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var error = new TimeoutException("Incomplete asynchronous event teardown");
                RecordFailure(error); throw error;
            }
            await Task.Yield();
        }
        await Run(() => Task.CompletedTask, check: false);
        Check();
    }
    public static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            bool ready = false;
            await Run(() => ready = predicate());
            if (ready) return;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Observable UI condition did not complete");
            await Task.Yield();
        }
    }
    public static async Task Mount(FrameworkElement view)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
        await Run(() => { view.Loaded += handler; Root.Children.Add(view); });
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException error) { RecordFailure(error); throw; }
        finally { await Run(() => view.Loaded -= handler); }
        await Until(() => view.IsLoaded && view.XamlRoot is not null && view.ActualWidth > 0);
    }
    public static async Task Unmount(FrameworkElement view, bool check = true)
    {
        var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = (_, _) => unloaded.TrySetResult();
        await Run(() => { view.Unloaded += handler; if (!Root.Children.Remove(view)) unloaded.TrySetResult(); return Task.CompletedTask; }, check);
        try { await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException error) { RecordFailure(error); throw; }
        finally { await Run(() => { view.Unloaded -= handler; return Task.CompletedTask; }, check); }
    }
    public static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    public static T Find<T>(string id, DependencyObject? root = null) where T : DependencyObject =>
        Tree(root ?? Root).OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    public static Task Ready<T>(string id) where T : FrameworkElement => Until(() =>
        Tree(Root).OfType<T>().Any(c => AutomationProperties.GetAutomationId(c) == id && c.IsLoaded));
    public static ContentDialog? Dialog(string id) => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
        .SelectMany(p => Tree(p.Child)).OfType<ContentDialog>().SingleOrDefault(d => AutomationProperties.GetAutomationId(d) == id);
    public static Task DialogReady(string id) => Until(() => Dialog(id)?.IsLoaded == true);
    public static void DialogButton(string id, string name)
    {
        var dialog = Dialog(id)!;
        var content = dialog.Content is DependencyObject root ? Tree(root).ToHashSet() : [];
        Click(Tree(dialog).OfType<Button>().Single(b => b.Name == name && !content.Contains(b)));
    }
    public static string DialogText(string id) => string.Join("\n", Tree(Dialog(id)!).OfType<TextBlock>().Select(t => t.Text));
    public static void Select(ListView list, int index)
    {
        var item = (ListViewItem)list.ContainerFromIndex(index);
        Assert.That(item?.IsLoaded, Is.True);
        // Change the actual selector's public selection, as with ComboBox.SelectedItem.
        // The confirmation is still invoked through its production automation peer.
        list.SelectedItems.Add(list.Items[index]);
    }
    public static async Task ClickCommand(string id, bool focus = false)
    {
        Button button = null!;
        await Run(() =>
        {
            var bar = Tree(Root).OfType<CommandBar>().Single(c => c.PrimaryCommands.Concat(c.SecondaryCommands)
                .OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == id));
            button = bar.PrimaryCommands.Concat(bar.SecondaryCommands).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
            if (!button.IsLoaded || button is AppBarButton { IsInOverflow: true }) bar.IsOpen = true;
        });
        await Until(() => button.IsLoaded && button.IsEnabled);
        await Run(() => { if (focus) Assert.That(button.Focus(FocusState.Keyboard), Is.True); Click(button); });
    }
    public static void Click(string id) => Click(Find<Button>(id));
    public static async Task ChooseCell(string id, string optionId)
    {
        await Ready<Button>(id);
        await Run(() => { Find<Button>(id).Focus(FocusState.Keyboard); Click(id); });
        MenuFlyoutItem? item = null;
        await Until(() => (item = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).SelectMany(p => Tree(p.Child)).OfType<MenuFlyoutItem>()
            .SingleOrDefault(i => AutomationProperties.GetAutomationId(i) == "ChoiceOption-" + optionId)) is { IsLoaded: true });
        await Run(() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item!).GetPattern(PatternInterface.Invoke)).Invoke());
        await Until(() => !item!.IsLoaded);
    }
    public static void Click(Button button)
    {
        Assert.That(button.IsLoaded && button.IsEnabled, Is.True, "The bound button must be loaded and enabled");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
