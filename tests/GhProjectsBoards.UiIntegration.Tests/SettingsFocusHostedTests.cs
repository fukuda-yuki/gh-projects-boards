using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class SettingsFocusHostedTests
{
    private EditingGrid grid = null!;
    private DraftSession session = null!;
    private ProjectRegistration registration = null!;

    [SetUp]
    public async Task Setup()
    {
        registration = ColumnTests.Project();
        var work = new EditingWorkspace(registration.Snapshot.Id.Scope);
        work.SetRegistrations([registration]); work.Open(registration);
        session = new(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-settings-focus-" + Guid.NewGuid().ToString("N"))), work, 0);
        await Ui.Run(() => grid = new EditingGrid(registration, session, () => Task.FromResult(true)));
        await Ui.Mount(grid); await Ui.Ready<TextBox>("GridCell0_0");
    }

    [TearDown]
    public async Task Teardown()
    {
        await Ui.Run(() => { Ui.Dialog("ColumnSettingsDialog")?.Hide(); Ui.Dialog("RowSettingsDialog")?.Hide(); });
        await Ui.Unmount(grid);
        await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
        await Ui.Idle();
    }

    [TestCase("columns")]
    [TestCase("rows")]
    public async Task SettingsSaveRestoresCommittedTitleReplacementAndCancelPreservesPendingCaret(string settings)
    {
        var command = settings == "columns" ? "GridColumns" : "GridRowSettings";
        var dialog = settings == "columns" ? "ColumnSettingsDialog" : "RowSettingsDialog";
        var restoredId = settings == "columns" ? "GridCell0_0" : "GridCell1_0";
        var key = new FieldKey("Title", "I1");
        TextBox original = null!;
        await Ui.Run(() => {
            original = Ui.Find<TextBox>("GridCell0_0");
            Assert.That(original.Focus(FocusState.Keyboard), Is.True);
        });
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), original));
        await Ui.ClickCommand(command); await Ui.DialogReady(dialog);
        await Ui.Run(() => {
            if (settings == "columns") Ui.Find<CheckBox>("ColumnVisible-P1B", Ui.Dialog(dialog)).IsChecked = false;
            else
            {
                Ui.Find<ComboBox>("RowSort", Ui.Dialog(dialog)).SelectedIndex = 1;
                Ui.Find<CheckBox>("RowDescending", Ui.Dialog(dialog)).IsChecked = true;
            }
            Ui.DialogButton(dialog, "PrimaryButton");
        });
        await Ui.Until(() => Ui.Dialog(dialog) is null);
        await Ui.Ready<TextBox>(restoredId);
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), Ui.Find<TextBox>(restoredId)));
        TextBox restored = null!;
        await Ui.Run(() => {
            restored = Ui.Find<TextBox>(restoredId);
            Assert.That(restored, Is.Not.SameAs(original), "This route must rebuild the displayed view.");
            Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(key));
            Assert.That(restored.Text, Is.EqualTo("Issue 1"));
            Assert.That(restored.SelectionStart, Is.Zero);
            Assert.That(restored.SelectionLength, Is.EqualTo(restored.Text.Length), "Returning to an unedited title must prepare replacement input.");
            // Exercise the native selection and TextChanging collaboration. Physical
            // keyboard and IME composition remain separate ordinary-app evidence.
            restored.SelectedText = "replacement";
        });
        await Ui.Until(() => session.Workspace.Fields.Single(f => f.Key == key).Buffer == "replacement");
        await Ui.Run(() => {
            Assert.That(restored.Text, Is.EqualTo("replacement"));
            Assert.That(session.Workspace.Value(session.Workspace.Open(registration)[0].Cells[0]), Is.EqualTo("Issue 1"));
            Assert.That(session.Workspace.Fields.Single(f => f.Key == key).Change, Is.Null);
            restored.Select(2, 0);
        });
        await Ui.ClickCommand(command); await Ui.DialogReady(dialog);
        await Ui.Run(() => Ui.DialogButton(dialog, "CloseButton"));
        await Ui.Until(() => Ui.Dialog(dialog) is null);
        await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), restored));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>(restoredId), Is.SameAs(restored));
            Assert.That(restored.Text, Is.EqualTo("replacement"));
            Assert.That(restored.SelectionStart, Is.EqualTo(2));
            Assert.That(restored.SelectionLength, Is.Zero, "Pending input resumes at its caret without selecting the whole buffer.");
            Assert.That(session.Workspace.Fields.Single(f => f.Key == key).Buffer, Is.EqualTo("replacement"));
            Assert.That(session.Workspace.Fields.Single(f => f.Key == key).Change, Is.Null);
        });
    }
}
