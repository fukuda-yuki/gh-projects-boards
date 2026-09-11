using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GhProjectsBoards.App;

public partial class MainWindow : Window
{
    private readonly ConnectionViewModel model = new();
    private Task? operation;
    private bool closingRequested;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = model;
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync(false);
    private async void Switch_Click(object sender, RoutedEventArgs e) => await CheckAsync(true);
    private async Task CheckAsync(bool newConnection)
    {
        if (operation is { IsCompleted: false }) return;
        operation = model.CheckAsync(newConnection);
        await operation;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => model.Cancel();
    private void OpenGridPrototype_Click(object sender, RoutedEventArgs e)
        => new GridPrototype.GridPrototypeWindow { Owner = this }.ShowDialog();
    private void Detect_Click(object sender, RoutedEventArgs e) => model.ExecutablePath = ConnectionViewModel.FindGh();
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "実行ファイル (*.exe)|*.exe", CheckFileExists = true, Title = "gh.exe を選択" };
        if (dialog.ShowDialog(this) == true) model.ExecutablePath = dialog.FileName;
    }
    private void CopyLogin_Click(object sender, RoutedEventArgs e) => Copy(model.LoginCommand);
    private void CopyRefresh_Click(object sender, RoutedEventArgs e) => Copy(model.RefreshCommand);
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (ExternalException) { model.ShowClipboardFailure(); }
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || operation is not { IsCompleted: false }) return;
        e.Cancel = true;
        if (closingRequested) return;
        closingRequested = true;
        model.Cancel();
        // Let the owned gh process stop before shutting down the WPF dispatcher.
        await operation;
        Close();
    }
}
