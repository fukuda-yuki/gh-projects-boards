using System.Text;
using GhProjectsBoards.App;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

// #324: 取り込み用 CSV を書き出す… writes the header-only template from the open Project's column settings.
internal sealed partial class PlanWorkspaceHostedTests
{
    [Test]
    public async Task TemplateCsvWritesAHeaderOnlyFileWithTheProjectNameWithoutGitHubWrites()
    {
        await Open();
        var path = Path.Combine(root, "template.csv");
        await Ui.Run(() => view.PickFile = purpose => {
            Assert.That(purpose, Is.EqualTo("csv-template"));
            return Task.FromResult<string?>(path);
        });
        await Ui.ClickCommand("PlanSheetCsvTemplate");
        await Ui.Until(() => Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.That(Encoding.UTF8.GetString(bytes),
            Is.EqualTo("\uFEFFキー,親,タスク名,工程,担当者,見積 h,残 h,実績 h,開始日,終了日,先行,リポジトリ\r\n"),
            "Only the shown, editable columns are written, between キー/親 and リポジトリ.");
        await Ui.Run(() => {
            Assert.That(Ui.Find<InfoBar>("PlanCsvExportResult").Message, Is.EqualTo("取り込み用 CSV を書き出しました。"));
            Assert.That(view.CsvTemplateSuggestedFileName, Is.EqualTo(PlanWorkspaceView.TemplateFileName(workspace.Selected!.Title)));
            Assert.That(view.CsvTemplateSuggestedFileName, Does.EndWith(" 取り込み用.csv"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task TemplateCsvCancellationLeavesFilesAndDocumentUntouched()
    {
        await Open();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var before = workspace.Session!.Document;
        var picked = false;
        await Ui.Run(() => view.PickFile = purpose => {
            Assert.That(purpose, Is.EqualTo("csv-template"));
            picked = true;
            return Task.FromResult<string?>(null);
        });
        await Ui.ClickCommand("PlanSheetCsvTemplate");
        await Ui.Until(() => picked);
        await Ui.Idle();
        Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(), Is.EqualTo(files));
        Assert.That(workspace.Session!.Document, Is.SameAs(before));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen, Is.False));
    }

    [Test]
    public async Task TemplateCsvWriteFailureShowsItsReasonWithoutSuccessOrGitHubWrites()
    {
        await Open();
        await Ui.Run(() => view.PickFile = _ => Task.FromResult<string?>(root));
        await Ui.ClickCommand("PlanSheetCsvTemplate");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text,
                Does.StartWith("取り込み用 CSV を書き出せませんでした。 ").And.Contain(root));
            Assert.That(Ui.Find<InfoBar>("PlanSaveFailure").IsOpen, Is.False);
            Assert.That(Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen, Is.False);
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}
