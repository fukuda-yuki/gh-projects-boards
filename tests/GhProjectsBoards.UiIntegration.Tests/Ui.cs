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
    // NUnit buffers case output. Lifecycle diagnostics must survive a process
    // deadline while the current case is still running.
    private static readonly TextWriter diagnostics = TextWriter.Synchronized(new StreamWriter(
        Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
    public static DispatcherQueue Queue = null!;
    public static Window Window = null!;
    public static Grid Root = null!;
    public static Exception? Fatal;
    public static int FailureCount;
    internal static void Trace(string message) => diagnostics.WriteLine($"{DateTime.UtcNow:O} {caseName} {message}");
    public static void RecordFailure(Exception error, TrackedContext? owner = null)
    {
        Interlocked.Increment(ref FailureCount);
        diagnostics.WriteLine(error);
        if (owner is null || owner.IsCurrent) Interlocked.CompareExchange(ref Fatal, error, null);
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> dispatches = new();
    private static long dispatchSequence;
    private static string caseName = "startup";
    private static readonly System.Diagnostics.Stopwatch caseTimer = new();
    public static Task BeginTest()
    {
        caseName = TestContext.CurrentContext.Test.FullName; caseTimer.Restart();
        diagnostics.WriteLine($"[START] {DateTime.UtcNow:O} {caseName}");
        return Run(() => {
            // A prior failure remains in NUnit and the host exit code.
            Fatal = null; TrackedContext.Start(Queue); return Task.CompletedTask;
        }, check: false);
    }
    public static void EndTest()
    {
        // A detached view's WinRT references release its native XAML tree, and the memory
        // pressure CsWinRT adds per reference, only once they are collected and finalized.
        // Left to later cases, sheets pile up and that pressure forces ever longer blocking
        // gen2 collections on the UI thread. Finalizers can need the UI thread, so the wait
        // is bounded.
        var released = Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }).Wait(TimeSpan.FromSeconds(10));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        diagnostics.WriteLine($"[END] {DateTime.UtcNow:O} {caseName} {caseTimer.Elapsed.TotalSeconds:F3}s operations={TrackedContext.Operations} posts={TrackedContext.Posts} released={released} gcPauseMs={GC.GetTotalPauseDuration().TotalMilliseconds:F0} privateMB={process.PrivateMemorySize64 >> 20}");
    }
    public static async Task Run(Action action, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => await Run(() => { action(); return Task.CompletedTask; }, operation: operation);
    public static async Task Run(Func<Task> action, bool check = true, [System.Runtime.CompilerServices.CallerMemberName] string operation = "", TimeSpan? timeout = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref dispatchSequence);
        dispatches[id] = $"{caseName}/{operation}/{action.Method.Name}";
        if (!Queue.TryEnqueue(() => { TrackedContext.Install(); Execute(); }))
        {
            dispatches.TryRemove(id, out _); throw new InvalidOperationException("UI dispatch rejected");
        }
        async void Execute()
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
            finally { dispatches.TryRemove(id, out _); }
        }
        try { await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30)); }
        catch (TimeoutException error) {
            // A native operation may finish only after the dispatcher deadline. Its
            // exception still belongs to this failed operation, never a later case.
            var origin = dispatches.GetValueOrDefault(id) ?? caseName + "/" + operation;
            _ = completion.Task.ContinueWith(t => diagnostics.WriteLine($"[LATE DISPATCH FAILURE] {origin}: {t.Exception}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            RecordFailure(error); await Diagnose("dispatch " + operation); throw;
        }
        if (check) Check();
    }
    private static string Describe(FrameworkElement view)
    {
        var work = view switch {
            App.PlanSheetView sheet => sheet.WorkDescription,
            App.PlanWorkspaceView workspace => workspace.WorkDescription, _ => "" };
        var root = Root.XamlRoot;
        var focus = root is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject;
        var popups = root is null ? [] : VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
            .Select(p => $"{p.Child?.GetType().Name}/{(p.Child is null ? "" : AutomationProperties.GetAutomationId(p.Child))}:open={p.IsOpen}").ToArray();
        return $"view={view.GetType().Name}, loaded={view.IsLoaded}, attached={Root.Children.Contains(view)}, parent={VisualTreeHelper.GetParent(view)?.GetType().Name ?? "none"}, rootLoaded={Root.IsLoaded}, focus={focus?.GetType().Name}/{(focus is null ? "" : AutomationProperties.GetAutomationId(focus))}, popups=[{string.Join(", ", popups)}], {work}";
    }
    internal static async Task Diagnose(string phase, FrameworkElement? view = null)
    {
        diagnostics.WriteLine($"[DIAGNOSTIC] {caseName} phase={phase} elapsed={caseTimer.Elapsed.TotalSeconds:F3}s operations={TrackedContext.Operations} posts={TrackedContext.Posts} dispatches=[{string.Join("; ", dispatches.Values)}] {TrackedContext.DescribePosts()}");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Queue.TryEnqueue(() => {
            try { diagnostics.WriteLine("[UI STATE] " + Describe(view ?? Root)); }
            catch (Exception error) { diagnostics.WriteLine("[UI STATE unavailable] " + error); }
            finally { completion.TrySetResult(); }
        })) { diagnostics.WriteLine("[UI STATE] dispatcher rejected diagnostics"); return; }
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { diagnostics.WriteLine("[UI STATE] dispatcher did not respond within 2s"); }
    }
    public static void Check() { if (Fatal is { } error) throw new AssertionException("Unhandled asynchronous UI failure: " + error, error); }
    public static async Task Idle()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (TrackedContext.Operations != 0 || TrackedContext.Posts != 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var error = new TimeoutException($"Incomplete asynchronous event teardown (operations={TrackedContext.Operations}, posts={TrackedContext.Posts})");
                RecordFailure(error); await Diagnose("idle"); throw error;
            }
            // Yielding immediately spins a worker while persistence and native
            // dispatch still need it; leave a bounded idle interval instead.
            await Task.Delay(10);
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
            // Leave native input, dispatcher timers and layout time to run between
            // observations. A continuously replenished normal-priority queue can
            // otherwise starve the very scrolling operation being observed.
            await Task.Delay(10);
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
        RoutedEventHandler handler = (_, _) => { diagnostics.WriteLine($"[UNLOADED] {view.GetType().Name}"); unloaded.TrySetResult(); };
        await Run(() => {
            diagnostics.WriteLine("[UNMOUNT before remove] " + Describe(view));
            view.Unloaded += handler;
            if (!Root.Children.Remove(view)) unloaded.TrySetResult();
            diagnostics.WriteLine("[UNMOUNT after remove] " + Describe(view));
            return Task.CompletedTask;
        }, check);
        try { await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException error) { RecordFailure(error); await Diagnose("unloaded event", view); throw; }
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
    public static T? Popup<T>(string id) where T : FrameworkElement => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
        .SelectMany(p => Tree(p.Child)).OfType<T>().SingleOrDefault(c => AutomationProperties.GetAutomationId(c) == id);
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
    public static void Toggle(CheckBox checkbox)
    {
        Assert.That(checkbox.IsLoaded && checkbox.IsEnabled, Is.True);
        ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(checkbox).GetPattern(PatternInterface.Toggle)).Toggle();
    }
    public static void Click(Button button)
    {
        Assert.That(button.IsLoaded && button.IsEnabled, Is.True, "The bound button must be loaded and enabled");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
