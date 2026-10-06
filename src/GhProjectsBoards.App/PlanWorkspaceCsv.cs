using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class PlanWorkspaceView
{
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
            if (!ReferenceEquals(workspace.Session, session)) throw new InvalidOperationException("Projectが変更されました。CSVを選び直してください。");
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
