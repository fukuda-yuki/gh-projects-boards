using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class RowViewHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration p = null!;
    private readonly TaskCompletionSource<string> clipboard = new(TaskCreationOptions.RunContinuationsAsynchronously);
    [SetUp]
    public async Task Setup()
    {
        p = ColumnTests.Project(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); w.Open(p);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "row-host-" + Guid.NewGuid().ToString("N"))), w, 0);
        await Ui.Run(() => grid = new(p, session, () => Task.FromResult(true), readClipboard: () => clipboard.Task));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => { clipboard.TrySetResult("Done"); Ui.Dialog("RowSettingsDialog")?.Hide(); });
        await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle();
    }
    private async Task Open() { await Ui.Run(() => Ui.Click("GridRowSettings")); await Ui.Until(() => Ui.Dialog("RowSettingsDialog") is { IsLoaded: true }); }
    private static T Setting<T>(string id) where T : FrameworkElement => Ui.Find<T>(id, Ui.Dialog("RowSettingsDialog")!);
    private async Task Close(string button) { await Ui.Run(() => Ui.DialogButton("RowSettingsDialog", button)); await Ui.Until(() => Ui.Dialog("RowSettingsDialog") is null); await Ui.Idle(); }
    [Test]
    public async Task CandidateSummaryExplainsCombinedFiltersAndResetWithoutChangingSavedView()
    {
        await Open();
        await Ui.Run(() => {
            Setting<ComboBox>("RowSort").SelectedIndex = 1;
            Setting<CheckBox>("RowDescending").IsChecked = true;
            Setting<TextBox>("RowTitleFilter").Text = "Issue";
            Setting<Expander>("RowFilterGroup-P1A").IsExpanded = true;
        });
        await Ui.Until(() => Ui.Tree(Ui.Dialog("RowSettingsDialog")!).OfType<CheckBox>()
            .Any(c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c) == "RowFilter-P1A-A1" && c.IsLoaded));
        await Ui.Run(() => {
            Setting<CheckBox>("RowFilter-P1A-A1").IsChecked = true;
            Setting<CheckBox>("RowFilter-P1A-Empty").IsChecked = true;
            var summary = Setting<TextBlock>("RowCriteriaPreview").Text;
            Assert.That(summary, Does.Contain("タイトル / 降順").And.Contain("Issue").And.Contain("Done").And.Contain("空値").And.Contain("P1A"));
            Assert.That(session.Workspace.RowView(p), Is.EqualTo(new RowViewDefinition()));
            Ui.Click(Setting<Button>("RowsReset"));
            Assert.That(Setting<TextBlock>("RowCriteriaPreview").Text, Does.Contain("取得順").And.Contain("絞り込みなし"));
            Assert.That(Setting<TextBox>("RowTitleFilter").Text, Is.Empty);
        });
        await Close("CloseButton");
    }
    [Test]
    public async Task SavedFilterZeroMatchesResetAndColumnCoexistence()
    {
        await Open(); await Ui.Run(() => Setting<TextBox>("RowTitleFilter").Text = "no matches"); await Close("CloseButton");
        await Ui.Run(() => Assert.That(grid.DisplayedRowIds, Has.Length.EqualTo(2)));
        await Open(); await Ui.Run(() => Setting<TextBox>("RowTitleFilter").Text = "no matches"); await Close("PrimaryButton");
        await Ui.Run(() => { Assert.That(grid.DisplayedRowIds, Is.Empty); Assert.That(grid.SelectionIdentity, Is.Null); Assert.That(Ui.Find<TextBlock>("RowViewStatus").Text, Does.Contain("no matches")); });
        await Open(); await Ui.Run(() => Ui.Click(Setting<Button>("RowsReset"))); await Close("PrimaryButton");
        await Ui.Ready<TextBox>("GridCell0_0"); await Ui.Run(() => Assert.That(grid.DisplayedRowIds, Has.Length.EqualTo(2)));
    }
    [Test]
    public async Task EditKeepsArrangementPendingBufferSurvivesHidingAndNewRowsStayVisible()
    {
        await Open(); await Ui.Run(() => { Setting<ComboBox>("RowSort").SelectedIndex = 1; Setting<TextBox>("RowTitleFilter").Text = "Issue"; }); await Close("PrimaryButton");
        await Ui.Run(async () => {
            var row = session.Workspace.Open(p)[0]; session.Workspace.Commit("P1", row.Cells[0], "hidden now"); session.Workspace.SetBuffer(row.Cells[0], "unfinished"); await session.FlushAsync();
        });
        await Ui.Run(() => { Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "P1T1", "P1T2" })); Assert.That(Ui.Find<TextBlock>("RowViewStatus").Text, Does.Contain("再適用が必要")); Ui.Click("GridAddRow"); });
        await Ui.Until(() => grid.DisplayedRowIds.Length == 3);
        await Ui.Run(() => { Assert.That(Ui.Find<TextBlock>("RowViewStatus").Text, Does.Contain("一時表示 1")); Ui.Click("GridReapply"); });
        await Ui.Until(() => grid.DisplayedRowIds.Length == 1);
        await Ui.Run(() => { Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "P1T2" })); Assert.That(session.Workspace.Buffer(session.Workspace.Open(p)[0].Cells[0]), Is.EqualTo("unfinished")); });
    }
    [Test]
    public async Task SaveIncludesLaterColumnAndDraftChangesAndRejectsObsoleteCandidate()
    {
        await Open();
        await Ui.Run(async () => {
            Setting<TextBox>("RowTitleFilter").Text = "Issue 1";
            Assert.That(await session.CommitAsync(w => { w.SaveColumns(ColumnTests.Reordered(w, p)); w.SetBuffer(w.Open(p)[0].Cells[0], "later"); return w; }, () => true), Is.True);
        });
        await Close("PrimaryButton");
        await Ui.Run(() => { Assert.That(session.Workspace.Columns(p).Visible[1].Id.FieldId, Is.EqualTo("P1C")); Assert.That(session.Workspace.Buffer(session.Workspace.Open(p)[0].Cells[0]), Is.EqualTo("later")); });
        await Open();
        await Ui.Run(async () => {
            Setting<TextBox>("RowTitleFilter").Text = "unsaved";
            var changed = p with { Snapshot = p.Snapshot with { Fields = p.Snapshot.Fields.Select(f => f with { Name = "new" }).ToArray() } };
            Assert.That(await session.CommitAsync(w => { w.SetRegistrations([changed]); return w; }, () => true), Is.True);
            Ui.DialogButton("RowSettingsDialog", "PrimaryButton");
        });
        await Ui.Until(() => Setting<TextBlock>("RowSettingsStatus").Text.Contains("保存できません"));
        await Ui.Run(() => { Assert.That(Setting<TextBox>("RowTitleFilter").Text, Is.EqualTo("unsaved")); Assert.That(session.Workspace.RowView(p).Title, Is.EqualTo("Issue 1")); }); await Close("CloseButton");
    }
    [Test]
    public async Task LateClipboardRejectsChangedDestinationMapping()
    {
        await Ui.Run(() => { Ui.Find<ComboBox>("GridCell0_1").Focus(FocusState.Programmatic); Ui.Click("GridPaste"); });
        await Open(); await Ui.Run(() => { Setting<ComboBox>("RowSort").SelectedIndex = 1; Setting<CheckBox>("RowDescending").IsChecked = true; });
        await Ui.Run(() => Ui.DialogButton("RowSettingsDialog", "PrimaryButton")); await Ui.Until(() => Ui.Dialog("RowSettingsDialog") is null);
        await Ui.Run(() => clipboard.SetResult("Done"));
        await Ui.Until(() => Ui.Find<TextBlock>("DraftStatus").Text.Contains("貼り付けを中止"));
        await Ui.Run(() => { Assert.That(session.Workspace.DifferenceCount, Is.Zero); Assert.That(grid.DisplayedRowIds, Is.EqualTo(new[] { "P1T2", "P1T1" })); });
    }
}
