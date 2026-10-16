using System.Collections.Concurrent;
using System.Xml.Linq;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    [Test, Category("Manual")]
    public async Task HelpBeforeConnectionOpensShippedManual()
    {
        var launched = new ConcurrentQueue<string>();
        await Ui.Run(() => {
            view.OpenDocument = path => launched.Enqueue(path);
            var help = Ui.Find<Button>("PlanHelp");
            Assert.That(AutomationProperties.GetName(help), Is.EqualTo("ヘルプ（F1）"));
            Assert.That(ToolTipService.GetToolTip(help), Is.EqualTo("ヘルプ（F1）"));
            Ui.Click("PlanHelp");
        });
        await Ui.Until(() => !launched.IsEmpty);
        AssertRenderedManual(launched.Single());
        Assert.That(workspace.Session, Is.Null);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase("tasks"), TestCase("summary"), TestCase("settings"), TestCase("publish"), Category("Manual")]
    public async Task HelpPreservesPlanHistoryAndUnpublishedChanges(string page)
    {
        await Open();
        await CommitText("PlanCell1_Title", "Edited title");
        // The accepted edit also releases the first take-in date hold; compare after it settles.
        await Ui.Until(() => workspace.Session!.UndoCount > 0 && workspace.Session.Document.Dates.Held is null);
        if (page == "summary") { await Ui.Run(() => Ui.Click("PlanShowSummary")); await Ui.Idle(); }
        if (page == "settings") await Settings();
        if (page == "publish") { await Ui.Run(() => Ui.Click("PlanPublish")); await Ui.Idle(); }
        var session = workspace.Session!;
        var document = session.Document;
        var undo = session.UndoCount; var redo = session.RedoCount;
        var launched = new ConcurrentQueue<string>();
        string unpublished = "";
        await Ui.Run(() => {
            unpublished = Ui.Find<TextBlock>("PlanUnpublished").Text;
            view.OpenDocument = path => launched.Enqueue(path);
            Ui.Click("PlanHelp");
        });
        await Ui.Until(() => !launched.IsEmpty);
        AssertRenderedManual(launched.Single());
        Assert.That(session.Document, Is.SameAs(document));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        Assert.That(session.RedoCount, Is.EqualTo(redo));
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanUnpublished").Text, Is.EqualTo(unpublished)));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("Manual")]
    public async Task HelpDoesNotCommitPendingCellInputOrBlockTheDispatcher()
    {
        await Open();
        var session = workspace.Session!;
        var document = session.Document;
        var undo = session.UndoCount;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            await Ui.Run(() => {
                view.OpenDocument = path => {
                    started.TrySetResult(path);
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test launch was not released.");
                };
                var cell = Ui.Find<TextBox>("PlanCell1_Title");
                cell.Focus(FocusState.Programmatic); cell.Text = "Pending title";
                Ui.Click("PlanHelp");
            });
            AssertRenderedManual(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBox>("PlanCell1_Title").Text, Is.EqualTo("Pending title"));
                Assert.That(session.Document, Is.SameAs(document));
                Assert.That(session.UndoCount, Is.EqualTo(undo));
            });
        }
        finally { release.Set(); await Ui.Idle(); }
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    private static void AssertRenderedManual(string path)
    {
        Assert.That(Path.IsPathFullyQualified(path), Is.True);
        Assert.That(Path.GetExtension(path), Is.EqualTo(".html"));
        var html = XDocument.Load(path);
        Assert.That(html.Descendants("h1").Single().Value, Is.EqualTo("プロジェクトの計画と設定"));
        var images = html.Descendants("img").Select(e => new Uri((string)e.Attribute("src")!, UriKind.Absolute)).ToArray();
        Assert.That(images, Is.Not.Empty);
        foreach (var image in images) {
            Assert.That(image.IsFile, Is.True);
            Assert.That(image.AbsoluteUri, Does.StartWith("file:///"));
            Assert.That(image.LocalPath, Does.StartWith(Path.Combine(AppContext.BaseDirectory, "manual", "images") + Path.DirectorySeparatorChar));
            Assert.That(File.Exists(image.LocalPath), Is.True, "Generate and rebuild the shipped manual images: " + image.LocalPath);
        }
    }

    [Test, Category("Manual")]
    public async Task UnreadableManualFallsBackToOriginalMarkdown()
    {
        var manual = Path.Combine(AppContext.BaseDirectory, "マニュアル.md");
        var launched = new ConcurrentQueue<string>();
        using var locked = File.Open(manual, FileMode.Open, FileAccess.Read, FileShare.None);
        await Ui.Run(() => { view.OpenDocument = path => launched.Enqueue(path); Ui.Click("PlanHelp"); });
        await Ui.Until(() => !launched.IsEmpty);
        Assert.That(launched, Is.EqualTo(new[] { manual }));
        Assert.That(workspace.Session, Is.Null);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("Manual")]
    public async Task MissingManualShowsPathAndDoesNotLaunch()
    {
        var manual = Path.Combine(AppContext.BaseDirectory, "マニュアル.md");
        var retained = manual + "." + Guid.NewGuid().ToString("N");
        var launched = new ConcurrentQueue<string>();
        File.Move(manual, retained);
        try {
            // The title-bar commands load a layout pass after the view; a click must wait for them like a user would.
            await Ui.Ready<Button>("PlanHelp");
            await Ui.Run(() => { view.OpenDocument = path => launched.Enqueue(path); Ui.Click("PlanHelp"); });
            await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.StartsWith("マニュアルが見つかりません:"));
            await Ui.Run(() => {
                var error = Ui.Find<TextBlock>("PlanError");
                Assert.That(error.Visibility, Is.EqualTo(Visibility.Visible));
                Assert.That(error.Text, Is.EqualTo("マニュアルが見つかりません: " + manual));
            });
            Assert.That(launched, Is.Empty);
        }
        finally { File.Move(retained, manual); }
    }

    [Test, Category("Manual")]
    public async Task FailedManualLaunchShowsExistingErrorPresentation()
    {
        await Ui.Run(() => {
            view.OpenDocument = _ => throw new IOException("Cannot open the document.");
            Ui.Click("PlanHelp");
        });
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text == "Cannot open the document.");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.EqualTo("Cannot open the document."));
            Assert.That(Ui.Find<InfoBar>("PlanSaveFailure").IsOpen, Is.False);
        });
    }
}
