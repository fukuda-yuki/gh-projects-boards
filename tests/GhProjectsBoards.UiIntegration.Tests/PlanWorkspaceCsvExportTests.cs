using System.Text;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    [Test]
    public async Task CsvExportWritesWholeProjectDespiteFilterAndFoldWithoutGitHubWrites()
    {
        await Open();
        var path = Path.Combine(root, "analysis.csv");
        await Ui.Run(async () => {
            var session = workspace.Session!;
            var parent = PlanRow.New("要求事項", "acme/repo");
            await session.Execute(new InsertPlanRows([parent, PlanRow.New("隠れた子", "acme/repo") with { Parent = parent.Identity }], Kind: PlanOperationKind.CsvImport), readDay);
            var sheet = Ui.Tree(view).OfType<PlanSheetView>().Single();
            sheet.Refresh();
            view.PickFile = purpose => { Assert.That(purpose, Is.EqualTo("csv-export")); return Task.FromResult<string?>(path); };
        });
        await Ui.ClickCommand("PlanSheetCollapseAll");
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "設計");
        await Ui.Until(() => Ui.Tree(view).OfType<PlanSheetView>().Single().RowIds.Count == 2);
        await Ui.ClickCommand("PlanSheetCsvExport");
        await Ui.Until(() => Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.That(bytes.Take(3), Is.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
        var lines = Encoding.UTF8.GetString(bytes).Split("\r\n");
        Assert.That(lines[0], Is.EqualTo("\uFEFFID,状態,タスク名,工程,担当者,見積 h,残 h,実績 h,開始日,終了日,先行,要求事項,リポジトリ,Issue 番号,URL,未発行"));
        Assert.That(lines[1], Does.StartWith("1,").And.Contain(",設計,").And.Contain("https://github.com/acme/repo/issues/1"));
        Assert.That(lines[2], Does.Contain(",要求事項,").And.EndsWith(",新規"));
        Assert.That(lines[3], Does.Contain(",隠れた子,").And.EndsWith(",新規"));
        Assert.That(lines, Has.Length.EqualTo(5));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanCsvExportResult").Message, Is.EqualTo("3 行を書き出しました。")));
    }

    [Test]
    public async Task CsvExportCancellationLeavesFilesAndDocumentUntouched()
    {
        await Open();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var before = workspace.Session!.Document;
        var picked = false;
        await Ui.Run(() => view.PickFile = purpose => { Assert.That(purpose, Is.EqualTo("csv-export")); picked = true; return Task.FromResult<string?>(null); });
        await Ui.ClickCommand("PlanSheetCsvExport");
        await Ui.Until(() => picked);
        await Ui.Idle();
        Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(), Is.EqualTo(files));
        Assert.That(workspace.Session!.Document, Is.SameAs(before));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await Ui.Run(() => Assert.That(Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen, Is.False));
    }

    [Test]
    public async Task CsvExportRefusesInvalidInputBeforePickerAndExportsCorrectedInput()
    {
        await Open();
        var path = Path.Combine(root, "corrected.csv");
        var picked = false;
        await Ui.Run(() => {
            view.PickFile = _ => { picked = true; return Task.FromResult<string?>(path); };
            var cell = Ui.Find<TextBox>("PlanCell1_Estimate");
            cell.Focus(FocusState.Programmatic); cell.Text = "invalid";
        });
        await Ui.ClickCommand("PlanSheetCsvExport");
        await Ui.Idle();
        Assert.That(picked, Is.False);
        Assert.That(File.Exists(path), Is.False);
        await Ui.Run(() => Ui.Find<TextBox>("PlanCell1_Estimate").Text = "16");
        await Ui.ClickCommand("PlanSheetCsvExport");
        await Ui.Until(() => Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen);
        Assert.That(await File.ReadAllTextAsync(path), Does.Contain(",16,"));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task CsvExportWriteFailureShowsReasonWithoutSuccessOrGitHubWrites()
    {
        await Open();
        await Ui.Run(() => view.PickFile = _ => Task.FromResult<string?>(root));
        await Ui.ClickCommand("PlanSheetCsvExport");
        await Ui.Until(() => Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanError").Text, Does.StartWith("CSVを書き出せませんでした。 ").And.Contain(root));
            Assert.That(Ui.Find<InfoBar>("PlanSaveFailure").IsOpen, Is.False);
            Assert.That(Ui.Find<InfoBar>("PlanCsvExportResult").IsOpen, Is.False);
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }
}
