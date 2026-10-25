using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #229: typing a 開始日 that a later predecessor overrides tells the PMO at the commit which task won and
// the date that resulted, in the selection line, with one command that removes exactly that link.
[TestFixture, NonParallelizable, Category("PlanSheet")]
internal sealed class PlanStartDateNoticeHostedTests
{
    private static readonly DateOnly Today = new(2026, 1, 12); // Monday; the predecessor #44 ends 2/17 (火)
    private string root = null!;
    private PlanSession session = null!;
    private PlanSheetView sheet = null!;
    private Grid sheetHost = null!;
    private PlanClipboardContent clipboard = new("", null);

    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = Path.Combine(Path.GetTempPath(), "ghpb-start-notice-ui-" + Guid.NewGuid().ToString("N"));
        var predecessor = new PlanRow("I44", "先行の設計", "acme/repo")
        { Closed = true, CloseDate = new(2026, 2, 17), Start = new(2026, 2, 16), End = new(2026, 2, 17) };
        var task = new PlanRow("I45", "後続の実装", "acme/repo")
        { Estimate = 8, Remaining = 8, Assignees = ["U1"], Predecessors = ["I44"] };
        ImmutableArray<PlanRow> rows = [predecessor, task];
        var document = new PlanDocument(new(new("github.com", 1), "P1"), new(rows, []), new(rows, new()
        {
            StatusDate = Today, DefaultRepository = "acme/repo", ProjectEnd = new(2027, 6, 30),
            People = [new("U1", "alice", 100, null)],
            Columns = [new(PlanField.Start, "start", "Start date", "DATE"), new(PlanField.End, "end", "End date", "DATE")]
        }))
        {
            Sync = new()
            {
                IssueLinks = ImmutableDictionary<string, PlanIssueLink>.Empty
                    .Add("I44", new("acme/repo#44", "https://github.com/acme/repo/issues/44"))
                    .Add("I45", new("acme/repo#45", "https://github.com/acme/repo/issues/45"))
            }
        };
        session = await PlanSession.CreateAsync(new(root), document, Today);
        await Ui.Run(() => sheet = new(session, () => Task.FromResult(clipboard), value => clipboard = value));
        await Ui.Run(() =>
        {
            var host = sheetHost = new Grid();
            host.RowDefinitions.Add(new() { Height = GridLength.Auto });
            host.RowDefinitions.Add(new());
            host.Children.Add(sheet.statusDate);
            host.Children.Add(sheet);
            Grid.SetRow(sheet, 1);
        });
        await Ui.Mount(sheetHost);
    }

    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            try { await Ui.Idle(); }
            finally
            {
                try { await Ui.Unmount(sheetHost, check: false); await Ui.Idle(); }
                finally
                {
                    await session.FlushAsync();
                    Directory.Delete(root, true);
                }
            }
        }
        finally { Ui.EndTest(); }
    }

    // The PMO types the date and leaves the cell: entering focus departure commits it.
    private async Task TypeStartAndLeave(string text)
    {
        await Ui.Ready<TextBox>("PlanCell2_Start");
        var undo = session.UndoCount;
        await Ui.Run(() =>
        {
            var cell = Ui.Find<TextBox>("PlanCell2_Start");
            Assert.That(cell.Focus(FocusState.Programmatic), Is.True);
            cell.Text = text;
        });
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic), Is.True));
        await Ui.Until(() => session.UndoCount > undo);
        await Ui.Idle();
        // The commit presents the notice after the local operation is accepted and the sheet has refreshed.
        await Ui.Until(() => Ui.Find<TextBlock>("PlanStartNotice").Text.Length > 0);
        await Ui.Idle();
    }

    private static string NoticeText() => Ui.Find<TextBlock>("PlanStartNotice").Text;
    private static HyperlinkButton NoticeAction() => Ui.Find<HyperlinkButton>("PlanStartNoticeAction");
    // The selection route the sheet's own cells and the test policy's UI-automation cases use.
    private static ISelectionItemProvider Provider(string id)
        => (ISelectionItemProvider)FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>(id)).GetPattern(PatternInterface.SelectionItem);
    private static async Task Select(int row, string field)
    {
        await Ui.Ready<FrameworkElement>($"PlanCell{row}_{field}");
        await Ui.Run(() => Provider($"PlanCell{row}_{field}").Select());
        await Ui.Until(() => Provider($"PlanCell{row}_{field}").IsSelected);
    }
    private static async Task Invoke(string id)
    {
        await Ui.Until(() => Ui.Find<Control>(id) is { IsLoaded: true, IsEnabled: true });
        await Ui.Run(() =>
        {
            var control = Ui.Find<Control>(id);
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(control).GetPattern(PatternInterface.Invoke)).Invoke();
        });
    }

    [Test]
    public async Task TypingAStartEarlierThanThePredecessorEndsShowsTheNoticeAndTheResultingDate()
    {
        await TypeStartAndLeave("1/12");

        await Ui.Run(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(NoticeText(), Is.EqualTo("開始日 1/12 は #44 の終了後のため 2/17 (火) になりました"));
                Assert.That(NoticeText(), Does.Not.Contain("行目"), "The winning task is named by its Issue number, never by a row position.");
                Assert.That(AutomationProperties.GetName(Ui.Find<TextBlock>("PlanStartNotice")), Is.EqualTo(NoticeText()),
                    "The announcement carries the same sentence.");
                Assert.That(AutomationProperties.GetLiveSetting(Ui.Find<TextBlock>("PlanStartNotice")),
                    Is.EqualTo(AutomationLiveSetting.Polite), "It is announced without interrupting.");
                Assert.That(NoticeAction().Content, Is.EqualTo("#44 とのリンクを解除"));
                Assert.That(AutomationProperties.GetName(NoticeAction()), Is.EqualTo("#44 とのリンクを解除"));
                Assert.That(Ui.Find<TextBox>("PlanCell2_Start").Text, Is.EqualTo("2/17 (火)"),
                    "The cell shows the date the schedule actually holds.");
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I45").Start, Is.EqualTo(new DateOnly(2026, 1, 12)),
                    "The typed start stays the plan start.");
                Assert.That(session.UndoCount, Is.EqualTo(1));
            });
        });
    }

    [Test]
    public async Task TheNoticeActionRemovesTheLinkAndTheTypedStartHolds()
    {
        await TypeStartAndLeave("1/12");
        await Invoke("PlanStartNoticeAction");
        await Ui.Idle();
        await Ui.Until(() => Ui.Find<Control>("PlanCell2_Start").FocusState != FocusState.Unfocused);

        await Ui.Run(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I45").Predecessors, Is.Empty);
                Assert.That(session.UndoCount, Is.EqualTo(2), "Removing the link is one more ordinary cell operation.");
                Assert.That(Ui.Find<TextBox>("PlanCell2_Start").Text, Is.EqualTo("1/12 (月)"), "The typed start then holds.");
                Assert.That(NoticeText(), Is.Empty, "The notice is gone once its cause is.");
            });
        });
    }

    [Test]
    public async Task ClosingTheNoticeKeepsTheLinkAndChangesNothing()
    {
        await TypeStartAndLeave("1/12");
        var before = "";
        var undo = 0;
        await Ui.Run(() =>
        {
            before = PlanJson.Text(session.Document);
            undo = session.UndoCount;
        });
        await Ui.Run(() => Ui.Click("PlanStartNoticeDismiss"));
        await Ui.Idle();
        await Ui.Until(() => Ui.Find<Control>("PlanCell2_Start").FocusState != FocusState.Unfocused);

        await Ui.Run(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(NoticeText(), Is.Empty);
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I45").Predecessors, Is.EqualTo(new[] { "I44" }),
                    "Closing the notice keeps the link.");
                Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before), "Dismissing changes nothing in the plan.");
                Assert.That(session.UndoCount, Is.EqualTo(undo), "Dismissing creates no Undo step.");
            });
        });
    }

    [Test]
    public async Task PasteAndUndoNeverRaiseTheNotice()
    {
        // A pasted start loses to the predecessor without a notice: a range operation is not a typed commit.
        await Select(2, PlanField.Start);
        clipboard = new("1/12", null);
        await Ui.ClickCommand("PlanSheetPaste");
        await Ui.Idle();
        await Ui.Run(() =>
        {
            var error = Ui.Find<TextBlock>("PlanSheetError").Text;
            Assert.Multiple(() =>
            {
                Assert.That(error, Is.Empty, "The paste must be accepted.");
                Assert.That(session.UndoCount, Is.EqualTo(1), "error=" + error);
                Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I45").Start, Is.EqualTo(new DateOnly(2026, 1, 12)));
                Assert.That(NoticeText(), Is.Empty, "Paste never raises the notice.");
            });
        });

        // A typed commit raises it, and Undo neither keeps a stale notice nor raises a new one.
        await TypeStartAndLeave("1/13");
        await Ui.Run(() => Assert.That(NoticeText(), Does.StartWith("開始日 1/13")));
        await Ui.Run(() => sheet.KeyboardCommand(Windows.System.VirtualKey.Z));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(NoticeText(), Is.Empty, "Undo dismisses the notice and raises none."));
    }
}
