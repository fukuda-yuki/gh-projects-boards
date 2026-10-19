using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #231 slice B: the publish review names each row-state operation in plain words and keeps 状態 as a
// reviewed field, so the PMO reads the GitHub consequence before confirming.
internal sealed partial class PlanPublishReviewHostedTests
{
    private async Task<string> ReviewContainerName(int index)
    {
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(index) is ListViewItem { IsLoaded: true });
        var name = "";
        await Ui.Run(() => name = new ListViewItemAutomationPeer(
            (ListViewItem)Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(index)).GetName());
        return name;
    }

    [Test, Category("PublishOutcome")]
    public async Task ReviewNamesAPendingCompletionInPlainWords()
    {
        await Open(1);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new ClosePlanRows(["I1"]), Today);
            Ui.Click("PlanPublish");
        });
        var name = await ReviewContainerName(0);
        Assert.That(name, Does.Contain("GitHub: Issue を完了として閉じます"));
        Assert.That(name, Does.Contain("状態"));
        Assert.That(name, Does.Contain("完了"));
    }

    [Test, Category("PublishOutcome")]
    public async Task ReviewNamesAPendingCancellationAndAReopen()
    {
        await Open(2);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new ClosePlanRows(["I1"], Cancelled: true), Today);
            await workspace.Session!.Execute(new ClosePlanRows(["I2"]), Today);
            Ui.Click("PlanPublish");
        });
        var names = new List<string>();
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(1) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => {
            for (var i = 0; i < 2; i++)
                names.Add(new ListViewItemAutomationPeer((ListViewItem)Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(i)).GetName());
        });
        Assert.That(names, Has.Some.Contains("GitHub: Issue を完了としてではなく閉じます（中止）"));
        Assert.That(names, Has.Some.Contains("GitHub: Issue を完了として閉じます"));
    }
}
