using System.Text.Json;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #246: a lost connection is a connection problem, not an authentication one. The fake gh answers the
// item read by ending without an HTTP response, which the transport classifies as a lost connection;
// both work-card failure bars must show the connection wording.
internal sealed partial class PlanWorkspaceHostedTests
{
    [Test, Category("ConnectionMessage")]
    public async Task RefreshThatLosesTheConnectionShowsTheConnectionMessage()
    {
        await Open();
        var before = workspace.Session!.Document;
        File.WriteAllText(Path.Combine(root, "scenario.json"),
            JsonSerializer.Serialize(new { planEditor = true, workspace = true, planFault = "readnetwork" }));
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen);
        await Ui.Ready<Button>("PlanRetryRefresh");
        await Ui.Run(() => {
            AssertFailure("PlanRefreshFailure", "最新の情報に更新できませんでした", "再試行");
            var message = Ui.Find<InfoBar>("PlanRefreshFailure").Message;
            Assert.That(message, Is.EqualTo("GitHub に接続できません。ネットワークを確認してください。"));
            Assert.That(message, Does.Not.Contain("認証またはアクセス権"));
        });
        Assert.That(workspace.Session!.Document, Is.EqualTo(before), "The failed read keeps the plan.");
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        await Ui.Run(() => Ui.Click("PlanRetryRefresh"));
        await Ui.Until(() => !Ui.Find<InfoBar>("PlanRefreshFailure").IsOpen);
    }

    [Test, Category("ConnectionMessage")]
    public async Task PublishThatLosesTheConnectionShowsOneConnectionMessage()
    {
        await Open();
        await CommitText("PlanCell1_Title", "発行予定");
        File.WriteAllText(Path.Combine(root, "scenario.json"),
            JsonSerializer.Serialize(new { planEditor = true, workspace = true, planFault = "readnetwork" }));
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Tree(view).OfType<TextBlock>().Any(t => t.Text.Contains("設計 → 発行予定")));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanPublishFailure").IsOpen && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Ready<Button>("PlanRetryPublish");
        await Ui.Run(() => {
            AssertFailure("PlanPublishFailure", "発行できませんでした", "再試行");
            var message = Ui.Find<InfoBar>("PlanPublishFailure").Message;
            Assert.That(message, Is.EqualTo("GitHub に接続できなくなったため、発行を中断しました。"
                + "接続が戻ったら「最新の情報に更新」で確認してから、もう一度発行してください。"));
            Assert.That(message, Does.Not.Contain("未確認"), "The connection message already says what to do.");
            Assert.That(message, Does.Not.Contain("認証またはアクセス権"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero, "Nothing is written when the pre-write read cannot reach GitHub.");
    }
}
