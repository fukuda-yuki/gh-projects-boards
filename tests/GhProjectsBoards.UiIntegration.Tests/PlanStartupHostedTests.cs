using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanStartup")]
public sealed class PlanStartupHostedTests
{
    private FrameworkElement? view;
    [SetUp] public Task Mount() => Ui.BeginTest();
    [TearDown]
    public async Task Cleanup()
    {
        try { if (view is not null) await Ui.Unmount(view, check: false); }
        finally { Ui.EndTest(); }
    }

    [Test]
    public async Task DataRootFailureNamesTheFolderAndAnotherFailureCanBeCopied()
    {
        var root = PlanStartupNotice.For(new PlanDataRootException(@"D:\missing\plans"));
        var general = PlanStartupNotice.For(new InvalidOperationException("forced workspace failure"));
        // A TextBox belongs to the window thread. Creating it on the test thread is RPC_E_WRONG_THREAD.
        var rootPanel = default(FrameworkElement)!;
        await Ui.Run(() => rootPanel = PlanStartupView.Create(root));
        view = rootPanel;
        await Ui.Mount(rootPanel);
        await Ui.Run(() =>
        {
            var box = Ui.Find<TextBox>("PlanStartupDataRoot");
            Assert.That(box.IsReadOnly, Is.True);
            Assert.That(box.Text.ReplaceLineEndings("\n"), Is.EqualTo(root.Text.ReplaceLineEndings("\n")));
            Assert.That(box.Text, Does.Contain(@"D:\missing\plans"));
            Assert.That(box.Text, Does.Contain("保存先を利用できません。"));
            Assert.That(Ui.Find<Button>("PlanStartupCopy").IsEnabled, Is.True);
        });
        await Ui.Run(() => RenderedEvidence.Capture(rootPanel, "startup-data-root"));
        await Ui.Unmount(view, check: false);
        var failurePanel = default(FrameworkElement)!;
        await Ui.Run(() => failurePanel = PlanStartupView.Create(general));
        view = failurePanel;
        await Ui.Mount(failurePanel);
        string copied = "";
        await Ui.Run(async () =>
        {
            var box = Ui.Find<TextBox>("PlanStartupFailure");
            Assert.That(box.Text.ReplaceLineEndings("\n"), Is.EqualTo(general.Text.ReplaceLineEndings("\n")));
            Assert.That(box.Text, Does.Contain("起動できませんでした。"));
            Assert.That(box.Text, Does.Contain("forced workspace failure"));
            Assert.That(box.Text, Does.Not.Contain("保存先を利用できません"));
            Ui.Click("PlanStartupCopy");
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            Assert.That(content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text), Is.True);
            copied = await content.GetTextAsync();
        });
        Assert.That(copied, Is.EqualTo(general.Text));
        await Ui.Run(() => RenderedEvidence.Capture(failurePanel, "startup-general"));
    }
}
