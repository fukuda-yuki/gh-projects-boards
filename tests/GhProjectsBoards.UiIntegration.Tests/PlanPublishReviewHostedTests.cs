using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanPublishReview")]
internal sealed class PlanPublishReviewHostedTests
{
    private string root = null!;
    private PlanWorkspace workspace = null!;
    private PlanWorkspaceView view = null!;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private static string FakeExecutable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GHPB_UI_FAKE_GH_PATH") is { Length: > 0 } path) return path;
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GhProjectsBoards.sln"))) directory = directory.Parent;
            return Path.Combine(directory!.FullName, "tests", "GhProjectsBoards.Tests", "bin", "Release", "net10.0-windows", "GhProjectsBoards.Tests.exe");
        }
    }
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-publish-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        workspace = new(new(root));
        await Ui.Run(() => view = new(workspace, (_, host) => new(FakeExecutable, host,
            new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }))));
        await Ui.Mount(view);
    }
    private async Task Open(int count)
    {
        FakePlanEditor.Save(root, new(Enumerable.Range(1, count).Select(i => new PlanFakeIssue(
            new("I" + i, "要求タスク " + i, "acme/repo") { Estimate = 8, Remaining = 8, Actual = 0 }, "", true)).ToImmutableArray(), count + 1));
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => workspace.Available.Count == 2 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => { Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty); Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0; });
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Ready<PlanSheetCell>("PlanCell1_Title");
        await Ui.Idle();
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            try { await Ui.Run(async () => await view.StopAsync()); }
            finally
            {
                try { await Ui.Unmount(view, check: false); await Ui.Idle(); }
                finally { await workspace.Flush(); Directory.Delete(root, true); }
            }
        }
        finally { Ui.EndTest(); }
    }
    [Test]
    public async Task SeveralFieldDifferencesShareTheirIssueAndFullLongValuesRemainReadable()
    {
        await Open(2);
        var longTitle = "要求タスク 1 の変更内容を確認するための長いタイトル・末尾まで表示";
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste, [
                new("I1", PlanField.Title, longTitle), new("I1", PlanField.Actual, 3m), new("I1", PlanField.Remaining, 5m),
                new("I2", PlanField.Actual, 2m)]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            Assert.That(list.Items.Count, Is.EqualTo(2), "One review group must contain every difference for its Issue.");
            var first = (ListViewItem)list.ContainerFromIndex(0);
            var text = string.Join("\n", Ui.Tree(first).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("acme/repo#1").And.Contain("要求タスク 1 → " + longTitle)
                .And.Contain("Actual  0 → 3").And.Contain("Remaining  8 → 5"));
            var comparison = Ui.Tree(first).OfType<TextBlock>().Single(t => t.Text.Contains("Actual  0 → 3"));
            Assert.That(comparison.Text, Does.Contain("Remaining  8 → 5"));
            Assert.That(comparison.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(comparison.TextTrimming, Is.EqualTo(TextTrimming.None));
            Assert.That(first.ActualWidth, Is.LessThanOrEqualTo(list.ActualWidth));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "publish-grouped-full-values");
        });
    }
    [Test]
    public async Task VersionReviewVirtualizesIssueGroupsAndReachesFinalDifferencesWithoutWriting()
    {
        await Open(1040);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste, workspace.Session.Document.State.Rows
                .SelectMany(r => new[] { new PlanCellChange(r.Identity, PlanField.Estimate, 16m),
                    new PlanCellChange(r.Identity, PlanField.Actual, 3m), new PlanCellChange(r.Identity, PlanField.Remaining, 13m) }).ToImmutableArray()), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            Assert.That(list.Items.Count, Is.EqualTo(1040));
            Assert.That(list.ItemsSource, Is.Not.Null);
            Assert.That(list.ContainerFromIndex(1039), Is.Null, "Offscreen groups must remain virtualized.");
            var first = (ListViewItem)list.ContainerFromIndex(0);
            Assert.That(first.ActualHeight, Is.LessThan(100), "Three short differences must not consume three repeated Issue rows.");
            await RenderedEvidence.Capture(view, "publish-grouped-version-start");
            list.ScrollIntoView(list.Items[1039], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(1039) is ListViewItem { IsLoaded: true } item &&
            Ui.Tree(item).OfType<TextBlock>().Any(t => t.Text == "1040 要求タスク 1040"));
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            var last = (ListViewItem)list.ContainerFromIndex(1039);
            var text = string.Join("\n", Ui.Tree(last).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("要求タスク 1040").And.Contain("Estimate  8 → 16")
                .And.Contain("Actual  0 → 3").And.Contain("Remaining  8 → 13"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "publish-grouped-version-end");
        });
    }
    [Test]
    public async Task RecycledConflictKeepsItsIssueAndResolutionReturnsToThatIssueWithoutWriting()
    {
        await Open(60);
        await Ui.Run(async () => await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste,
            workspace.Session.Document.State.Rows.SelectMany(r => new[] {
                new PlanCellChange(r.Identity, PlanField.Actual, 3m), new PlanCellChange(r.Identity, PlanField.Remaining, 5m) }).ToImmutableArray()), Today));
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I60" ? i with { Row = i.Row with { Actual = 2 } } : i).ToImmutableArray() });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.Length == 1);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False);
            var list = Ui.Find<ListView>("PlanPublishLines"); list.ScrollIntoView(list.Items[59], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Ready<Button>("PlanResolveI60_Actual_True");
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanPublishLines"); list.ScrollIntoView(list.Items[0], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanPublishLines"); list.ScrollIntoView(list.Items[59], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Ready<Button>("PlanResolveI60_Actual_True");
        await Ui.Run(() => {
            var group = Ui.Find<PlanPublishGroupView>("PlanPublishGroupI60");
            Assert.That(string.Join(" ", Ui.Tree(group).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("要求タスク 60").And.Contain("競合  GitHub: 2"));
            Ui.Click("PlanResolveI60_Actual_True");
        });
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.IsEmpty && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        try { await Ui.Until(() => {
            var group = Ui.Tree(view).OfType<PlanPublishGroupView>().FirstOrDefault(g =>
                AutomationProperties.GetAutomationId(g) == "PlanPublishGroupI60" && g.IsLoaded && g.ActualHeight > 0);
            if (group is null) return false;
            var list = Ui.Find<ListView>("PlanPublishLines");
            var bounds = group.TransformToVisual(list).TransformBounds(new(0, 0, group.ActualWidth, group.ActualHeight));
            return bounds.Bottom > 0 && bounds.Top < list.ActualHeight;
        }); }
        catch {
            await Ui.Run(async () => {
                var list = Ui.Find<ListView>("PlanPublishLines");
                TestContext.Out.WriteLine("Review realized groups: " + string.Join(", ", Ui.Tree(list).OfType<PlanPublishGroupView>().Select(g =>
                    $"{AutomationProperties.GetAutomationId(g)}:{g.TransformToVisual(list).TransformBounds(new(0, 0, g.ActualWidth, g.ActualHeight))}")));
                await RenderedEvidence.Capture(view, "publish-position-failure");
            });
            throw;
        }
        await Ui.Run(async () => {
            Assert.That(workspace.Session!.Document.State.Rows.Single(r => r.Identity == "I60").Actual, Is.EqualTo(2));
            var group = Ui.Find<PlanPublishGroupView>("PlanPublishGroupI60");
            var list = Ui.Find<ListView>("PlanPublishLines");
            var bounds = group.TransformToVisual(list).TransformBounds(new(0, 0, group.ActualWidth, group.ActualHeight));
            Assert.That(bounds.Bottom, Is.GreaterThan(0));
            Assert.That(bounds.Top, Is.LessThan(list.ActualHeight), "The resolved Issue must remain in the review viewport.");
            Assert.That(string.Join(" ", Ui.Tree(group).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("Remaining  8 → 5").And.Not.Contain("競合"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "publish-recycled-conflict-resolved");
        });
    }
}
