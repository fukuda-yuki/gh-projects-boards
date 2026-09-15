using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class ColumnHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration p = null!;
    [SetUp]
    public async Task Setup()
    {
        p = ColumnTests.Project(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p, ColumnTests.Project("P2")]);
        w.Open(p); w.AddRow(p);
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-ui-columns-" + Guid.NewGuid().ToString("N")));
        session = new(store, w, 0);
        await Ui.Run(() => grid = new EditingGrid(p, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
    }
    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => Ui.Dialog("ColumnSettingsDialog")?.Hide());
        await Ui.Unmount(grid); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); await Ui.Idle();
    }
    private Task Open() => OpenDialog();
    private async Task OpenDialog() { await Ui.Run(() => Ui.Click("GridColumns")); await Ui.DialogReady("ColumnSettingsDialog"); }
    private static T Setting<T>(string id) where T : DependencyObject => Ui.Find<T>(id, Ui.Dialog("ColumnSettingsDialog"));
    private static async Task Close(string name)
    {
        await Ui.Run(() => Ui.DialogButton("ColumnSettingsDialog", name));
        await Ui.Until(() => Ui.Dialog("ColumnSettingsDialog") is null);
    }
    [Test]
    public async Task ColumnCandidatePreviewShowsVisibleOrderAndDuplicateIdentityBeforeSave()
    {
        await Open();
        await Ui.Run(() => {
            Assert.That(Setting<TextBlock>("ColumnLayoutPreview").Text, Does.Contain("P1A").And.Contain("P1B").And.Contain("P1C"));
            Setting<CheckBox>("ColumnVisible-P1B").IsChecked = false;
            var preview = Setting<TextBlock>("ColumnLayoutPreview").Text;
            Assert.That(preview, Does.Contain("表示 4 列").And.Not.Contain("P1B"));
            Assert.That(session.Workspace.Columns(p).Visible, Has.Length.EqualTo(5));
            Ui.Click(Setting<Button>("ColumnUp-P1C"));
        });
        await Ui.Until(() => Setting<Button>("ColumnUp-P1C").IsLoaded);
        await Ui.Run(() => {
            Ui.Click(Setting<Button>("ColumnUp-P1C"));
            var preview = Setting<TextBlock>("ColumnLayoutPreview").Text;
            Assert.That(preview.IndexOf("P1C", StringComparison.Ordinal), Is.LessThan(preview.IndexOf("P1A", StringComparison.Ordinal)));
        });
        await Close("CloseButton");
    }
    [Test]
    public async Task ColumnCancelResetWidthPreserveNativePendingEditor()
    {
        TextBox original = null!;
        await Ui.Run(() => { original = Ui.Find<TextBox>("GridCell0_0"); original.Focus(FocusState.Programmatic); original.Text = "pending日本語"; });
        await Open();
        await Ui.Run(() => { Setting<NumberBox>("ColumnWidth-Title").Value = 480; Setting<CheckBox>("ColumnVisible-P1B").IsChecked = false; });
        await Close("CloseButton");
        await Ui.Run(() => { Assert.That(session.Workspace.Columns(p).Visible.Length, Is.EqualTo(5)); Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(original)); });
        await Open();
        await Ui.Run(() => { Setting<NumberBox>("ColumnWidth-Title").Value = 480; Ui.Click(Setting<Button>("ColumnsReset")); });
        await Ui.Run(() => Assert.That(Setting<NumberBox>("ColumnWidth-Title").Value, Is.EqualTo(320)));
        await Ui.Run(() => Setting<NumberBox>("ColumnWidth-Title").Value = 440);
        await Close("PrimaryButton");
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(original)); Assert.That(original.Text, Is.EqualTo("pending日本語"));
            Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(new FieldKey("Title", "I1")));
            Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Change, Is.Null);
            var header = Ui.Find<Grid>("SheetHeader");
            Assert.That(header.ColumnDefinitions[1].Width.Value, Is.EqualTo(440));
            var row = (Grid)((ListViewItem)Ui.Find<ListView>("ProjectItems").Items[0]).Content;
            Assert.That(row.ColumnDefinitions.Select(c => c.Width), Is.EqualTo(header.ColumnDefinitions.Select(c => c.Width)));
            for (var c = 1; c < header.Children.Count; c++)
                Assert.That(((FrameworkElement)row.Children[c]).TransformToVisual(grid).TransformPoint(new(0, 0)).X,
                    Is.EqualTo(((FrameworkElement)header.Children[c]).TransformToVisual(grid).TransformPoint(new(0, 0)).X - ((FrameworkElement)header.Children[c]).Margin.Left).Within(1), $"Column {c} boundaries align");
        });
    }
    [Test]
    public async Task ColumnReorderHideSelectionAndEditUsesFieldIds()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("GridCell0_2").Focus(FocusState.Programmatic));
        await Open();
        await Ui.Run(() => { Setting<CheckBox>("ColumnVisible-P1B").IsChecked = false; Ui.Click(Setting<Button>("ColumnUp-P1C")); });
        await Ui.Until(() => Setting<Button>("ColumnUp-P1C").IsLoaded);
        await Ui.Run(() => Ui.Click(Setting<Button>("ColumnUp-P1C")));
        await Close("PrimaryButton");
        await Ui.Ready<ComboBox>("GridCell0_1");
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<Button>("GridReapply")));
        await Ui.Run(() =>
        {
            Assert.That(grid.SelectionIdentity, Is.Null);
            Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(Ui.Find<Button>("GridReapply")));
            Assert.That(session.Workspace.Columns(p).Visible.Select(c => c.Id.FieldId), Is.EqualTo(new string?[] { null, "P1C", "P1A", null }));
            var combo = Ui.Find<ComboBox>("GridCell0_1"); combo.Focus(FocusState.Programmatic); combo.SelectedIndex = 1;
        });
        await Ui.Until(() => session.Workspace.Fields.Any(f => f.Key.FieldId == "P1C" && f.Change?.Value == "C1"));
        await Open(); await Ui.Run(() => Ui.Click(Setting<Button>("ColumnsReset"))); await Close("PrimaryButton");
        await Ui.Run(() =>
        {
            Assert.That(grid.SelectionIdentity?.Field?.FieldId, Is.EqualTo("P1C"));
            Ui.Click("GridUndo");
            Assert.That(session.Workspace.Value(session.Workspace.Open(p)[0].Cells[3]), Is.EqualTo("C0"));
            Assert.That(session.Workspace.Value(session.Workspace.Open(p)[0].Cells[1]), Is.EqualTo("A0"));
        });
    }
    [Test]
    public async Task ColumnInvalidWidthRetainsCandidateForRetry()
    {
        await Open(); await Ui.Run(() => Setting<NumberBox>("ColumnWidth-Title").Value = 79);
        await Ui.Run(() => Ui.DialogButton("ColumnSettingsDialog", "PrimaryButton"));
        await Ui.Until(() => Setting<TextBlock>("ColumnSettingsStatus").Text.Contains("保存できません"));
        await Ui.Run(() => { Assert.That(session.Workspace.Columns(p).Visible[0].Preference.Width, Is.EqualTo(320)); Setting<NumberBox>("ColumnWidth-Title").Value = 500; });
        await Close("PrimaryButton");
        await Ui.Run(() => Assert.That(session.Workspace.Columns(p).Visible[0].Preference.Width, Is.EqualTo(500)));
    }
    [Test]
    public async Task ColumnObsoleteDefinitionsCannotSilentlySaveCandidate()
    {
        await Open();
        await Ui.Run(async () =>
        {
            Setting<NumberBox>("ColumnWidth-Title").Value = 450;
            var changed = p with { Snapshot = p.Snapshot with { Fields = p.Snapshot.Fields.Select(f => f with { Name = "New definition" }).ToArray() } };
            Assert.That(await session.CommitAsync(w => { w.SetRegistrations([changed]); return w; }, () => true), Is.True);
            Ui.DialogButton("ColumnSettingsDialog", "PrimaryButton");
        });
        await Ui.Until(() => Setting<TextBlock>("ColumnSettingsStatus").Text.Contains("保存できません"));
        await Ui.Run(() => { Assert.That(Setting<NumberBox>("ColumnWidth-Title").Value, Is.EqualTo(450)); Assert.That(session.Workspace.Columns(p).Visible[0].Preference.Width, Is.EqualTo(320)); });
        await Close("CloseButton");
    }
    [Test]
    public async Task ColumnReplacedEditorCannotPublishLateText()
    {
        TextBox old = null!;
        await Ui.Run(() => old = Ui.Find<TextBox>("GridCell0_0"));
        await Open(); await Ui.Run(() => Setting<CheckBox>("ColumnVisible-P1B").IsChecked = false); await Close("PrimaryButton");
        await Ui.Until(() => !old.IsLoaded);
        await Ui.Ready<TextBox>("GridCell0_0");
        await Ui.Run(() =>
        {
            Assert.That(old.IsLoaded, Is.False); Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.Not.SameAs(old));
            old.Text = "late old editor";
            Assert.That(session.Workspace.Buffer(session.Workspace.Open(p)[0].Cells[0]), Is.Null);
        });
    }
    [Test]
    public async Task ColumnHiddenPendingBufferReappearsWithoutCommit()
    {
        await Ui.Run(async () => { session.Workspace.SetBuffer(session.Workspace.Open(p)[0].Cells[2], "recoverable pending select"); await session.FlushAsync(); });
        await Open(); await Ui.Run(() => Setting<CheckBox>("ColumnVisible-P1B").IsChecked = false); await Close("PrimaryButton");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("DraftStatus").Text, Does.Contain("未確定 1")));
        await Open(); await Ui.Run(() => Setting<CheckBox>("ColumnVisible-P1B").IsChecked = true); await Close("PrimaryButton");
        await Ui.Ready<ComboBox>("GridCell0_2");
        await Ui.Run(() => { Ui.Find<ComboBox>("GridCell0_2").Focus(FocusState.Programmatic); Ui.Click("GridDetails"); });
        await Ui.Ready<TextBlock>("SelectedCellDetails");
        await Ui.Until(() => Ui.Find<TextBlock>("SelectedCellDetails").Text.Contains("recoverable pending select"));
        await Ui.Run(() => { var cell = session.Workspace.Open(p)[0].Cells[2]; Assert.That(session.Workspace.Value(cell), Is.EqualTo("B0")); Assert.That(session.Workspace.Buffer(cell), Is.EqualTo("recoverable pending select")); });
    }
}
