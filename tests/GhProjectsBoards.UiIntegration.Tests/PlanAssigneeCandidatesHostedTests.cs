using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

// #225: the 担当者 cell offers the Project's known people as it is edited. The fixture opens a Project through
// the fake gh, so typing a login the Project does not know takes the real #253 verification path, and the
// candidate source itself stays local.
[TestFixture, NonParallelizable, Category("PlanAssigneeCandidates")]
internal sealed class PlanAssigneeCandidatesHostedTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private string root = null!;
    private PlanWorkspace workspace = null!;
    private PlanSession session = null!;
    private PlanSheetView sheet = null!;
    private Grid sheetHost = null!;

    private static string FakeExecutable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GHPB_UI_FAKE_GH_PATH") is { Length: > 0 } configured) return configured;
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "GhProjectsBoards.sln"))) folder = folder.Parent;
            return Path.Combine(folder?.FullName ?? throw new InvalidOperationException("Repository root required."),
                "tests", "GhProjectsBoards.Tests", "bin", "Release", "net10.0-windows", "GhProjectsBoards.Tests.exe");
        }
    }

    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-assignee-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true}""");
        // The Project's people are its assignees (#253), so 20 assigned tasks make 20 known people with
        // EMU-style logins — the case where recalling 「person-U8」 is the work this list removes.
        FakePlanEditor.Save(root, new(Enumerable.Range(1, 20).Select(number => new PlanFakeIssue(
            new PlanRow("I" + number, "Task " + number, "acme/repo") { Estimate = 8, Remaining = 8, Assignees = ["U" + number] },
            "", true)).ToImmutableArray(), 21));
        workspace = new(new(root), () => Today);
        await Ui.Run(async () =>
        {
            await workspace.Connect(new(FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
        });
        session = workspace.Session!;
        await Ui.Run(() =>
        {
            sheet = new(session, () => Task.FromResult(new PlanClipboardContent("", null)), _ => { },
                resolveAssignee: workspace.ResolveAssigneeAsync);
            var host = sheetHost = new Grid();
            host.RowDefinitions.Add(new() { Height = GridLength.Auto });
            host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate); host.Children.Add(sheet); Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
        await Ui.Ready<PlanSheetCell>("PlanCell1_Assignees");
    }

    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            try { await Ui.Idle(); }
            finally
            {
                try
                {
                    await Ui.Unmount(sheetHost, check: false);
                    await Ui.Run(() => Ui.Window.AppWindow.Resize(new(1400, 1000)));
                }
                finally
                {
                    await workspace.Flush();
                    var deletion = System.Diagnostics.Stopwatch.StartNew();
                    while (Directory.Exists(root))
                    {
                        try { Directory.Delete(root, true); break; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                            && deletion.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(100); }
                    }
                }
            }
        }
        finally { Ui.EndTest(); }
    }

    [Test]
    public async Task TypingFiltersTheKnownPeopleInAnOverlayThatDoesNotMoveTheRows()
    {
        await SelectCell(1);
        double top = 0, listHeight = 0;
        string[] rows = [];
        await Ui.Run(() =>
        {
            top = Cell(1).TransformToVisual(sheet).TransformPoint(new Windows.Foundation.Point()).Y;
            rows = sheet.RowIds.ToArray(); listHeight = sheet.List.ActualHeight;
        });
        var cell = await BeginAssigneeEdit(1);
        await Ui.Run(() => cell.Text = "per");
        await Ui.Until(() => CandidateLabels().Length == 20, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() =>
        {
            var labels = CandidateLabels();
            Assert.That(labels, Has.All.StartWith("person-U"), "Typing 「per」 offers the EMU-style logins.");
            Assert.That(labels, Does.Contain("person-U1").And.Contain("person-U20"));
            // The list is an overlay: it inserts no line and moves neither the edited row nor any other.
            Assert.That(Cell(1).TransformToVisual(sheet).TransformPoint(new Windows.Foundation.Point()).Y, Is.EqualTo(top));
            Assert.That(sheet.RowIds, Is.EqualTo(rows));
            Assert.That(sheet.List.ActualHeight, Is.EqualTo(listHeight));
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(sheet.XamlRoot)
                .Single(item => item.Child is Border border && AutomationProperties.GetAutomationId(border) == "PlanAssigneeCandidates");
            Assert.That(popup.PlacementTarget, Is.SameAs(Cell(1)));
            Assert.That(Ui.Find<TextBlock>("PlanAssigneeCandidatesEmpty").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        await Ui.Run(() => Cell(1).Text = "person-U2");
        await Ui.Until(() => CandidateLabels().Length == 2, () => string.Join(",", CandidateLabels()));
    }

    [Test]
    public async Task DownAndEnterTakeTheHighlightedPersonInOneUndoStepWithoutALookup()
    {
        File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
        await SelectCell(3);
        var undo = session.UndoCount;
        var cell = await BeginAssigneeEdit(3);
        await Ui.Run(() => cell.Text = "person-U2");
        await Ui.Until(() => CandidateLabels().Length == 2, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() =>
        {
            Assert.That(sheet.AssigneePickerKey(cell, VirtualKey.Down), Is.True, "↓ moves the highlight.");
            Assert.That(CandidateLabels()[0], Is.EqualTo("person-U2"));
        });
        await Ui.Run(() => Assert.That(sheet.AssigneePickerKey(cell, VirtualKey.Enter), Is.True, "Enter takes the highlighted person."));
        await Ui.Until(() => Row("I3").Assignees.SequenceEqual(new[] { "U2" }));
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(session.UndoCount, Is.EqualTo(undo + 1), "Taking one person is one Undo step.");
            Assert.That(Ui.Popup<Border>("PlanAssigneeCandidates"), Is.Null, "The list closes when the cell commits.");
            Assert.That(Cell(3).Editing, Is.False);
        });
        Assert.That(RecordedQueries(root), Has.None.Contains("PlanResolveAssignee"),
            "A chosen known person commits locally and sends no lookup.");
    }

    [Test]
    public async Task ASecondChoiceAfterSemicolonAddsThePersonToTheCell()
    {
        await SelectCell(2);
        var undo = session.UndoCount;
        var cell = await BeginAssigneeEdit(2);
        // The PMO keeps the current person and starts the next value after 「;」.
        await Ui.Run(() => cell.Text = "person-U2; ");
        await Ui.Until(() => CandidateLabels().Length == 20, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() => Assert.That(sheet.AssigneePickerKey(cell, VirtualKey.Down), Is.True));
        await Ui.Run(() => Assert.That(sheet.AssigneePickerKey(cell, VirtualKey.Enter), Is.True));
        await Ui.Until(() => Row("I2").Assignees.SequenceEqual(new[] { "U2", "U1" }));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(session.UndoCount, Is.EqualTo(undo + 1), "The added person is one more Undo step."));
    }

    [Test]
    public async Task EscapeClosesTheListAndASecondEscapeCancelsTheEdit()
    {
        await SelectCell(1);
        var undo = session.UndoCount;
        var cell = await BeginAssigneeEdit(1);
        await Ui.Run(() => cell.Text = "per");
        await Ui.Until(() => CandidateLabels().Length == 20, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() => Assert.That(sheet.AssigneePickerKey(cell, VirtualKey.Escape), Is.True, "The first Escape closes the list."));
        await Ui.Until(() => Ui.Popup<Border>("PlanAssigneeCandidates") is null);
        await Ui.Run(() =>
        {
            Assert.That(cell.Editing, Is.True, "Closing the list keeps the edit.");
            Assert.That(cell.Text, Is.EqualTo("per"));
            Assert.That(Row("I1").Assignees, Is.EqualTo(new[] { "U1" }));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
            cell.CancelOrClose();
        });
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(Cell(1).Editing, Is.False, "The second Escape cancels the edit.");
            Assert.That(Cell(1).Text, Is.EqualTo("person-U1"), "Cancelling restores the accepted display value.");
            Assert.That(Row("I1").Assignees, Is.EqualTo(new[] { "U1" }));
            Assert.That(session.UndoCount, Is.EqualTo(undo));
        });
    }

    [Test]
    public async Task AnUnknownLoginIsVerifiedAndARefusalKeepsTheListOpenWithTheClosestPeople()
    {
        await SelectCell(1);
        var before = PlanJson.Text(session.Document);
        var cell = await BeginAssigneeEdit(1);
        await Ui.Run(() => cell.Text = "person-U88");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanAssigneeCandidatesEmpty") is { IsLoaded: true, Visibility: Visibility.Visible },
            () => string.Join(",", CandidateLabels()));
        // Enter with no highlighted person commits the typed text through the ordinary verification path.
        await Ui.Run(async () => await sheet.CommitCell("I1", PlanField.Assignees, "person-U88"));
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is { IsLoaded: true });
        await Ui.Until(() => CandidateLabels().Length == 20, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() =>
        {
            Assert.That(((TextBlock)Ui.Popup<Border>("SheetInputProblem")!.Child).Text,
                Is.EqualTo("担当者を確認できません: person-U88"), "The existing verification message stays at the cell.");
            Assert.That(CandidateLabels()[0], Is.EqualTo("person-U8"), "The closest person — the login the typed text extends — is offered first.");
            Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before), "A refused login writes nothing.");
            Assert.That(session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public async Task AltDownOpensTheListFromASelectedCell()
    {
        await SelectCell(2);
        await Ui.Run(() =>
        {
            Assert.That(Cell(2).Editing, Is.False);
            sheet.OpenAssigneeListFromSelection();
        });
        await Ui.Until(() => Ui.Popup<Border>("PlanAssigneeCandidates") is { IsLoaded: true });
        await Ui.Run(() => Assert.That(Cell(2).Editing, Is.True, "Alt+↓ starts editing the selected 担当者 cell and opens its list."));
    }

    [Test]
    public async Task PointerAndKeyboardChoicesBothEndInExactlyOneUndoStepAndKeepTheEditorFocused()
    {
        await SelectCell(4);
        var undo = session.UndoCount;
        var cell = await BeginAssigneeEdit(4);
        await Ui.Run(() => cell.Text = "person-U5");
        await Ui.Until(() => CandidateLabels().Length == 1, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() =>
        {
            // The list is not focusable: the editor keeps keyboard focus while it is open.
            Assert.That(FocusManager.GetFocusedElement(cell.XamlRoot), Is.SameAs(cell));
            // The pointer route runs the same choice body a candidate row's tap runs.
            Assert.That(sheet.ChooseAssigneeCandidate(0), Is.True);
        });
        await Ui.Until(() => Row("I4").Assignees.SequenceEqual(new[] { "U5" }));
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(session.UndoCount, Is.EqualTo(undo + 1), "One pointer choice is one Undo step.");
            Assert.That(Ui.Popup<Border>("PlanAssigneeCandidates"), Is.Null, "The list closes when the cell commits.");
            Assert.That(FocusManager.GetFocusedElement(Cell(4).XamlRoot), Is.SameAs(Cell(4)), "The editor keeps focus.");
        });
        // Choosing the person the editor already shows ends the edit without another Undo step.
        var beforeSame = session.UndoCount;
        await BeginAssigneeEdit(4);
        await Ui.Until(() => CandidateLabels().Length == 1, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() => Assert.That(sheet.ChooseAssigneeCandidate(0), Is.True));
        await Ui.Idle();
        await Ui.Run(() =>
        {
            Assert.That(session.UndoCount, Is.EqualTo(beforeSame), "An unchanged choice creates no Undo step.");
            Assert.That(Cell(4).Editing, Is.False);
            Assert.That(Row("I4").Assignees, Is.EqualTo(new[] { "U5" }));
        });
        // The keyboard route ends in the same single step.
        var beforeKey = session.UndoCount;
        var next = await BeginAssigneeEdit(5);
        await Ui.Run(() => next.Text = "person-U6");
        await Ui.Until(() => CandidateLabels().Length == 1, () => string.Join(",", CandidateLabels()));
        await Ui.Run(() =>
        {
            Assert.That(sheet.AssigneePickerKey(next, VirtualKey.Down), Is.True);
            Assert.That(sheet.AssigneePickerKey(next, VirtualKey.Enter), Is.True);
        });
        await Ui.Until(() => Row("I5").Assignees.SequenceEqual(new[] { "U6" }));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(session.UndoCount, Is.EqualTo(beforeKey + 1), "One keyboard choice is one Undo step."));
    }

    private static async Task SelectCell(int number)
    {
        await Ui.Ready<FrameworkElement>($"PlanCell{number}_Assignees");
        await Ui.Run(() => SelectProvider($"PlanCell{number}_Assignees").Select());
        await Ui.Until(() => Cell(number).FocusState != FocusState.Unfocused);
    }

    private async Task<PlanSheetCell> BeginAssigneeEdit(int number)
    {
        PlanSheetCell cell = null!;
        await Ui.Run(() => { cell = Cell(number); cell.BeginEditing(); });
        await Ui.Until(() => Ui.Popup<Border>("PlanAssigneeCandidates") is { IsLoaded: true });
        return cell;
    }

    private static PlanSheetCell Cell(int number) => Ui.Find<PlanSheetCell>($"PlanCell{number}_Assignees");

    private static ISelectionItemProvider SelectProvider(string id)
        => (ISelectionItemProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>(id)).GetPattern(PatternInterface.SelectionItem);

    private static string[] CandidateLabels()
    {
        var container = Ui.Popup<Border>("PlanAssigneeCandidates");
        if (container is null) return [];
        return Ui.Tree(container).OfType<Border>()
            .Select(row => AutomationProperties.GetAutomationId(row))
            .Where(id => id.StartsWith("PlanAssigneeChoice", StringComparison.Ordinal))
            .Select(id => AutomationProperties.GetName(Ui.Find<Border>(id)))
            .ToArray();
    }

    private PlanRow Row(string identity) => session.Document.State.Rows.Single(row => row.Identity == identity);

    private static string[] RecordedQueries(string root)
    {
        var path = Path.Combine(root, "calls.jsonl");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Select(call => call.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString()! : "")
                .Where(query => query.Length > 0).ToArray()
            : [];
    }
}
