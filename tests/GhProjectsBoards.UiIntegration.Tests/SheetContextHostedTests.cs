using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class SheetContextHostedTests
{
    [TestCase(false), TestCase(true)]
    public async Task RepositoryIdentityUsesAcceptedProjectAndDetailsRemainKeyboardAccessible(bool multiple)
    {
        var p = ColumnTests.Project(); var issues = p.Snapshot.Issues.ToDictionary();
        var second = issues.Values.Last();
        if (multiple) issues[second.Id] = second with { Repository = second.Repository with { Id = new(p.Snapshot.Id.Scope, "R2"), NameWithOwner = "owner/second" } };
        p = p with { Snapshot = p.Snapshot with { Issues = issues } };
        var work = new EditingWorkspace(p.Snapshot.Id.Scope); work.SetRegistrations([p]); work.Open(p);
        work.SaveRowView(work.PrepareRowView(p) with { Definition = new(Title: "Issue 1") });
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-identity-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)) { Width = 740, Height = 480 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "P1T1" }));
                Assert.That(Ui.Find<TextBlock>("GridRowIdentity0").Text, Is.EqualTo(multiple ? "#1  owner/repo" : "#1"));
                Assert.That(ToolTipService.GetToolTip(Ui.Find<TextBlock>("GridRowIdentity0"))?.ToString(), Does.Contain("owner/repo"));
                Assert.That(Ui.Find<TextBox>("GridCell0_0").Focus(FocusState.Keyboard), Is.True);
            });
            await Ui.Until(() => grid.SelectionIdentity?.Field == new FieldKey("Title", "I1"));
            await Ui.Run(() =>
            {
                Ui.Find<TextBox>("GridCell0_0").Text = "pending title";
                var details = Ui.Find<Button>("GridDetails"); Assert.That(details.Focus(FocusState.Keyboard), Is.True); Ui.Click(details);
                Assert.That(work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending title"));
                Assert.That(work.DifferenceCount, Is.Zero);
            });
            await Ui.Until(() => Ui.Tree(grid).OfType<TextBlock>().Any(t => t.IsLoaded && t.Text.Contains("#1  owner/repo\nProject:")));
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    [Test]
    public async Task VisibleScrollbarMovesTheViewportAndRetainsThePendingNativeEditor()
    {
        var p = EditingTests.Registration(count: 101); var work = new EditingWorkspace(p.Snapshot.Id.Scope);
        work.SetRegistrations([p]); work.Open(p);
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-scrollbar-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!; TextBox title = null!; ScrollViewer scroll = null!; ScrollBar bar = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)) { Width = 740, Height = 480 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                title = Ui.Find<TextBox>("GridCell0_0"); title.Focus(FocusState.Keyboard); title.Text = "pending first"; title.Select(4, 0);
                scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                bar = Ui.Find<ScrollBar>("SheetVerticalScroll");
                Assert.That(bar.Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(bar.Maximum, Is.GreaterThan(0));
                ((IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(bar).GetPattern(PatternInterface.RangeValue)).SetValue(bar.Maximum);
            });
            await Ui.Until(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);
            await Ui.Ready<TextBox>("GridCell100_0");
            await Ui.Run(() =>
            {
                Assert.That(Ui.Find<TextBox>("GridCell100_0").Text, Is.EqualTo("Issue 101"));
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(title));
                bar.Value = 0;
            });
            await Ui.Until(() => scroll.VerticalOffset < 1);
            await Ui.Run(() =>
            {
                Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(title));
                Assert.That(title.SelectionStart, Is.EqualTo(4));
                Assert.That(work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending first"));
                Assert.That(work.DifferenceCount, Is.Zero);
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    private static FrameworkElement? PopupElement(string id) => VisualTreeHelper.GetOpenPopupsForXamlRoot(Ui.Root.XamlRoot)
        .SelectMany(p => Ui.Tree(p.Child)).OfType<FrameworkElement>().SingleOrDefault(e => AutomationProperties.GetAutomationId(e) == id);
    private static async Task HeaderCommand(int column, string id)
    {
        await Ui.Ready<Button>($"GridHeaderMenu{column}");
        await Ui.Run(() => Ui.Click($"GridHeaderMenu{column}"));
        await Ui.Until(() => PopupElement(id) is { IsLoaded: true });
        await Ui.Run(() => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(PopupElement(id)!).GetPattern(PatternInterface.Invoke)).Invoke());
    }

    [Test]
    public async Task HeaderAndInlineFilterSaveTheViewWhileRetainingPendingWorkAndItsNativeEditorOnResize()
    {
        var p = ColumnTests.Project(); var work = new EditingWorkspace(p.Snapshot.Id.Scope);
        work.SetRegistrations([p]); work.Open(p);
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-direct-view-" + Guid.NewGuid().ToString("N")));
        var session = new DraftSession(store, work, 0); EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)));
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<TextBox>("GridCell0_0"); TextBox original = null!;
            await Ui.Run(() => { original = Ui.Find<TextBox>("GridCell0_0"); original.Focus(FocusState.Keyboard); original.Text = "未確定のタイトル"; });
            await HeaderCommand(0, "HeaderWiden");
            await Ui.Until(() => session.Workspace.Columns(p).Visible[0].Preference.Width == 400);
            await Ui.Run(() => { Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(original)); Assert.That(original.Text, Is.EqualTo("未確定のタイトル")); });
            await HeaderCommand(2, "HeaderHide");
            await Ui.Until(() => session.Workspace.Columns(p).Hidden("P1B"));
            await Ui.Run(() => { Ui.Find<TextBox>("GridQuickTitleFilter").Text = "Issue 2"; Ui.Click("GridQuickFilterApply"); });
            await Ui.Until(() => grid.DisplayedRowIds.SequenceEqual(new[] { "P1T2" }));
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("Issue 2"));
                Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("未確定のタイトル"));
                Assert.That(session.Workspace.DifferenceCount, Is.Zero);
                Ui.Click("GridQuickFilterClear");
            });
            await Ui.Until(() => grid.DisplayedRowIds.Length == 2);
            await Ui.Ready<TextBox>("GridCell0_0");
            await HeaderCommand(1, "HeaderFilter");
            await Ui.Until(() => PopupElement("HeaderFilter-A1") is { IsLoaded: true });
            await Ui.Run(() => { ((CheckBox)PopupElement("HeaderFilter-A1")!).IsChecked = true; Ui.Click((Button)PopupElement("HeaderFilterApply")!); });
            await Ui.Until(() => grid.DisplayedRowIds.Length == 0);
            await HeaderCommand(1, "HeaderFilter");
            await Ui.Until(() => PopupElement("HeaderFilterClear") is { IsLoaded: true });
            await Ui.Run(() => Ui.Click((Button)PopupElement("HeaderFilterClear")!));
            await Ui.Until(() => grid.DisplayedRowIds.Length == 2);
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(async () => { Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("未確定のタイトル")); Assert.That(await session.FlushAsync(), Is.True); });
            var record = await store.LoadAsync(work.Scope);
            var restored = EditingWorkspace.Restore(record!);
            Assert.That(restored.Columns(p).Hidden("P1B"), Is.True);
            Assert.That(restored.Columns(p).Visible[0].Preference.Width, Is.EqualTo(400));
            Assert.That(restored.RowView(p).Title, Is.Empty);
            Assert.That(restored.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("未確定のタイトル"));
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }

    [Test]
    public async Task FailedHeaderSaveKeepsAcceptedWidthAndPendingText()
    {
        var p = ColumnTests.Project(); var work = new EditingWorkspace(p.Snapshot.Id.Scope);
        work.SetRegistrations([p]); work.Open(p);
        var root = Path.Combine(Path.GetTempPath(), "ghpb-direct-failed-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(root, "intentional storage obstruction");
        var session = new DraftSession(new DraftStore(root), work, 0); EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)));
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() => { var title = Ui.Find<TextBox>("GridCell0_0"); title.Focus(FocusState.Keyboard); title.Text = "保持する入力"; });
            await HeaderCommand(0, "HeaderWiden");
            await Ui.Until(() => Ui.Find<TextBlock>("ColumnTransitionStatus").Text.Contains("保存できません"));
            await Ui.Run(() =>
            {
                Assert.That(session.Workspace.Columns(p).Visible[0].Preference.Width, Is.EqualTo(360));
                Assert.That(Ui.Find<Grid>("SheetHeader").ColumnDefinitions[1].Width.Value, Is.EqualTo(360));
                Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("保持する入力"));
                Assert.That(session.Workspace.DifferenceCount, Is.Zero);
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Idle(); }
    }

    [TestCase(320)]
    [TestCase(1200)]
    public async Task HorizontalScrollKeepsIssueIdentityAndPendingTitleAtTheSameScreenPosition(int titleWidth)
    {
        var p = ColumnTests.Project(); var work = new EditingWorkspace(p.Snapshot.Id.Scope);
        work.SetRegistrations([p]); work.Open(p);
        var columns = work.PrepareColumns(p);
        work.SaveColumns(columns with { Columns = columns.Columns.Select(c => c.Id.Role == "Title" ? c with { Width = titleWidth } : c).ToArray() });
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-context-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)) { Width = 740, Height = 480 });
        await Ui.Mount(grid);
        try
        {
            await Ui.Ready<TextBox>("GridCell0_0");
            TextBox title = null!; ScrollViewer scroll = null!; double left = 0;
            await Ui.Run(() =>
            {
                title = Ui.Find<TextBox>("GridCell0_0"); title.Focus(FocusState.Keyboard); title.Text = "pending日本語"; title.Select(3, 0);
            });
            await Ui.Idle();
            await Ui.Run(() =>
            {
                left = title.TransformToVisual(grid).TransformPoint(new(0, 0)).X;
                scroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                scroll.ChangeView(scroll.ScrollableWidth, null, null, true);
            });
            await Ui.Until(() => Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1);
            await Ui.Run(() =>
            {
                Assert.That(title.TransformToVisual(grid).TransformPoint(new(0, 0)).X, Is.EqualTo(left).Within(1), "The title must stay readable beside later fields.");
                var identity = Ui.Find<TextBlock>("GridRowIdentity0");
                Assert.That(identity.Text, Is.EqualTo("#1"));
                Assert.That(ToolTipService.GetToolTip(identity)?.ToString(), Does.Contain("owner/repo"));
                Assert.That(identity.TransformToVisual(grid).TransformPoint(new(0, 0)).X, Is.InRange(0, 740));
                Assert.That(Ui.Find<Grid>("SheetHeader").ColumnDefinitions[1].Width.Value + 44, Is.LessThan(600), "Frozen identity must leave room for editable fields in a narrow viewport.");
                Assert.That(work.Columns(p).Visible[0].Preference.Width, Is.EqualTo(titleWidth), "Responsive presentation must preserve the saved width.");
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(title));
                Assert.That(title.SelectionStart, Is.EqualTo(3));
                Assert.That(work.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("pending日本語"));
                Assert.That(work.DifferenceCount, Is.Zero);
            });
        }
        finally { await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle(); }
    }
}
