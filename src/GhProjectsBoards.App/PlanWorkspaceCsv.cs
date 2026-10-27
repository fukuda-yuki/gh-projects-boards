using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
    private async Task ExportCsv(PlanSession session)
    {
        if (await SelectFile("csv-export") is not { } path) return;
        var token = OperationToken;
        token.ThrowIfCancellationRequested();
        var document = session.Document;
        var today = Today; var marks = workspace.Display.Marks;
        csvExportResult.IsOpen = false;
        try { await Task.Run(async () => await File.WriteAllBytesAsync(path, PlanCsvExport.Create(document, today, marks), token), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An export failure must not offer recovery for the plan's own durable save.
            throw new InvalidOperationException("CSVを書き出せませんでした。 " + ex.Message, ex);
        }
        csvExportResult.Message = $"{document.State.Rows.Length.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"))} 行を書き出しました。";
        csvExportResult.IsOpen = true;
    }

    // #324: the import template is a header-only file generated from the open Project's column settings. The
    // sheet command runs through the ordinary command gate, so pending cell input is committed before this runs.
    private async Task WriteTemplateCsv(PlanSession session)
    {
        if (await SelectTemplateFile() is not { } path) return;
        var token = OperationToken;
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(workspace.Session, session)) throw new InvalidOperationException("プロジェクトが変更されました。CSV を選び直してください。");
        // The template is one header row; only the file write must stay off the UI thread, as the export does.
        var template = workspace.CsvTemplate();
        csvExportResult.IsOpen = false;
        try { await Task.Run(async () => await File.WriteAllBytesAsync(path, template, token), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A template failure must not offer recovery for the plan's own durable save.
            throw new InvalidOperationException("取り込み用 CSV を書き出せませんでした。 " + ex.Message, ex);
        }
        csvExportResult.Message = "取り込み用 CSV を書き出しました。";
        csvExportResult.IsOpen = true;
    }

    // The suggested name of the template. A hosted UI test reads it while the picker is substituted, so the
    // name the PMO sees is verifiable without opening a native save dialog.
    internal string? CsvTemplateSuggestedFileName { get; private set; }

    // The save picker for the template. It is separate from SelectFile because its suggested name carries the
    // purpose of the file; a UI test substitutes the picker through PickFile with the same purpose.
    private async Task<string?> SelectTemplateFile()
    {
        CsvTemplateSuggestedFileName = TemplateFileName(workspace.Selected?.Title);
        if (PickFile is { } pick) return await pick("csv-template");
        var picker = new FileSavePicker { SuggestedFileName = CsvTemplateSuggestedFileName };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // 「<Project title> 取り込み用.csv」, with the characters a file name cannot hold replaced as the export
    // does. A title that is empty after trimming falls back to Project.
    internal static string TemplateFileName(string? projectTitle)
    {
        var title = string.Concat((string.IsNullOrWhiteSpace(projectTitle) ? "Project" : projectTitle)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        return (title.Length == 0 ? "Project" : title) + " 取り込み用.csv";
    }

    private async Task ImportCsv(PlanSession session)
    {
        var token = OperationToken;
        while (true)
        {
            if (await SelectFile("csv") is not { } path) return;
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            var parsed = PlanCsvImport.Read(file.Length <= 5 * 1024 * 1024 ? await File.ReadAllBytesAsync(path, token) : new byte[5 * 1024 * 1024 + 1]);
            var preview = await workspace.PreviewCsv(parsed, token);
            if (!preview.Errors.IsEmpty)
            {
                var list = new ListView { MaxHeight = 320, SelectionMode = ListViewSelectionMode.None,
                    ItemsSource = preview.Errors.Select(e => $"{e.Line}行: {e.Reason}").ToArray() };
                var dialog = Id(new ContentDialog { XamlRoot = XamlRoot, Title = "CSVを確認してください", Content = list,
                    PrimaryButtonText = "CSVを選び直す", CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Primary }, "PlanCsvErrors");
                if (await ShowCsvDialog(dialog, token) == ContentDialogResult.Primary) continue;
                return;
            }
            var command = preview.Command!;
            if (preview.IsDuplicate)
            {
                var dialog = Id(new ContentDialog { XamlRoot = XamlRoot, Title = "同じCSVは追加済みです", Content = "重複するタスクを追加しますか？",
                    PrimaryButtonText = "重複して追加", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close }, "PlanCsvDuplicate");
                if (await ShowCsvDialog(dialog, token) != ContentDialogResult.Primary) return;
                command = command with { AllowDuplicateCsv = true };
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(workspace.Session, session)) throw new InvalidOperationException("プロジェクトが変更されました。CSVを選び直してください。");
            var save = await session.Execute(command, Today);
            RenderTasks();
            sheet!.RevealAddedRow(command.Rows[0].Identity);
            sheet!.Check(save);
            return;
        }
    }
    private async Task<ContentDialogResult> ShowCsvDialog(ContentDialog dialog, CancellationToken token)
    {
        using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        var result = await dialog.ShowAsync();
        token.ThrowIfCancellationRequested();
        return result;
    }
}
