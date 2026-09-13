using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using GhProjectsBoards.Core.Projects;

namespace GhProjectsBoards.App;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionViewModel model = new();
    private Task? operation;
    private bool rendering;
    private bool pickerOpen;
    private bool closingRequested;
    private bool closed;
    private bool closeReady;
    private bool checking;
    private RegistrationWorkspace? workspace;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            workspace = new RegistrationWorkspace(RegistrationStore.ForUser());
            ProjectsPage.Initialize(workspace);
            operation = RestoreAsync();
        }
        catch (Exception) { UiMessage.Text = "保存先を利用できません。GHPB_DATA_ROOTは絶対パスを指定してください。実データへの代替保存はしません。"; }
        AppWindow.Resize(new SizeInt32(1080, 960));
        ExecutableInput.Text = model.ExecutablePath;
        HostInput.Text = model.Host;
        IssueInput.Text = model.IssueUrl;
        ProjectInput.Text = model.ProjectUrl;
        ExecutableInput.TextChanged += (_, _) => { if (!rendering && model.ExecutablePath != ExecutableInput.Text) { workspace?.InvalidateConnection(); model.ExecutablePath = ExecutableInput.Text; } };
        HostInput.TextChanged += (_, _) => { if (!rendering && model.Host != HostInput.Text) { workspace?.InvalidateConnection(); model.Host = HostInput.Text; } };
        IssueInput.TextChanged += (_, _) => { if (!rendering) model.IssueUrl = IssueInput.Text; };
        ProjectInput.TextChanged += (_, _) => { if (!rendering) model.ProjectUrl = ProjectInput.Text; };
        model.PropertyChanged += ModelChanged;
        AppWindow.Closing += Closing;
        Closed += (_, _) =>
        {
            closed = true;
            closingRequested = true;
            model.PropertyChanged -= ModelChanged;
        };
        Render();
    }

    private async Task RestoreAsync()
    {
        try { await workspace!.RestoreAsync(); }
        catch (Exception) { UiMessage.Text = "保存データを読み込めません。保存先を確認してください。自動削除はしていません。"; }
    }
    private void ShowConnection(object sender, RoutedEventArgs args) { ConnectionPage.Visibility = Visibility.Visible; ProjectsPage.Visibility = Visibility.Collapsed; }
    private void ShowProjects(object sender, RoutedEventArgs args)
    {
        if (workspace is null) return;
        ConnectionPage.Visibility = Visibility.Collapsed; ProjectsPage.Visibility = Visibility.Visible; ProjectsPage.Update();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (closed) return;
        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        if (closed) return;
        rendering = true;
        try
        {
            // TextChanged can arrive after another control's notification. Never write
            // model snapshots back over newer input while rendering diagnostic state.
            var enabled = model.CanCheck && !checking && !closingRequested && !pickerOpen;
            ExecutableInput.IsEnabled = HostInput.IsEnabled = IssueInput.IsEnabled = ProjectInput.IsEnabled = enabled;
            DetectButton.IsEnabled = BrowseButton.IsEnabled = CheckButton.IsEnabled = enabled;
            SwitchButton.IsEnabled = enabled && model.CanSwitch;
            CancelButton.IsEnabled = model.IsBusy && !closingRequested;
            StatusValue.Text = model.StatusText;
            EnvironmentValue.Text = model.EnvironmentText;
            VersionValue.Text = model.VersionText;
            AccountValue.Text = model.AccountText;
            StorageValue.Text = model.StorageText;
            ScopeValue.Text = model.ScopeText;
            IssueValue.Text = model.IssueText;
            ProjectValue.Text = model.ProjectText;
            LoginCommandValue.Text = model.LoginCommand;
            RefreshCommandValue.Text = model.RefreshCommand;
        }
        finally { rendering = false; }
    }

    private async void Check_Click(object sender, RoutedEventArgs args) => await CheckAsync(false);
    private async void Switch_Click(object sender, RoutedEventArgs args) => await CheckAsync(true);
    private async Task CheckAsync(bool newConnection)
    {
        if (closingRequested || checking || operation is { IsCompleted: false }) return;
        checking = true;
        operation = CheckTransitionAsync(newConnection);
        Render();
        try { await operation; }
        finally { checking = false; Render(); }
    }
    private async Task CheckTransitionAsync(bool newConnection)
    {
        await Task.Yield();
        if (workspace is not null) await workspace.BindAsync(null);
        if (closingRequested) return;
        UiMessage.Text = "";
        model.ExecutablePath = ExecutableInput.Text;
        model.Host = HostInput.Text;
        model.IssueUrl = IssueInput.Text;
        model.ProjectUrl = ProjectInput.Text;
        await model.CheckAsync(newConnection);
        if (workspace is not null && !closingRequested)
            await workspace.BindAsync(model.Connection is { IsConnected: true } connected ? connected.Context : null);
    }
    private void Cancel_Click(object sender, RoutedEventArgs args) => model.Cancel();
    private void Detect_Click(object sender, RoutedEventArgs args) => ExecutableInput.Text = ConnectionViewModel.FindGh();

    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        if (pickerOpen || closingRequested) return;
        pickerOpen = true;
        Render();
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !closingRequested) ExecutableInput.Text = file.Path;
        }
        catch (Exception)
        {
            if (!closed) UiMessage.Text = "ファイルを選択できませんでした。gh.exe のパスを入力してください。";
        }
        finally { pickerOpen = false; Render(); }
    }

    private void CopyLogin_Click(object sender, RoutedEventArgs args) => Copy(model.LoginCommand);
    private void CopyRefresh_Click(object sender, RoutedEventArgs args) => Copy(model.RefreshCommand);
    private void Copy(string text)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            Clipboard.Flush();
        }
        catch (Exception) { model.ShowClipboardFailure(); }
    }

    private async void Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeReady)
        {
            closingRequested = true;
            return;
        }
        args.Cancel = true;
        if (closingRequested) return;
        closingRequested = true;
        workspace?.CancelPendingEdits();
        ProjectsPage.IsEnabled = false;
        ConnectionTab.Focus(FocusState.Programmatic);
        Render();
        model.Cancel();
        if (workspace is not null) await workspace.StopAsync();
        // Keep the UI dispatcher alive until the owned gh operation has stopped.
        if (operation is not null) await operation;
        if (workspace is not null && !await workspace.FlushDraftsAsync()) { closingRequested = false; ProjectsPage.IsEnabled = true; Render(); return; }
        closeReady = true;
        DispatcherQueue.TryEnqueue(() => { if (!closed) Close(); });
    }
}
