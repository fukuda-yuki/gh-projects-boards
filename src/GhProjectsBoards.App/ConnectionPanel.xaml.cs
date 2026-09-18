using System.ComponentModel;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace GhProjectsBoards.App;

public sealed partial class ConnectionPanel : UserControl
{
    private ConnectionViewModel model = new();
    private RegistrationWorkspace? workspace;
    private Task? operation;
    private bool rendering, pickerOpen, closingRequested, checking;
    internal IntPtr WindowHandle { get; set; }
    public event EventHandler? ReturnRequested;
    public ConnectionPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { model.PropertyChanged += ModelChanged; Render(); };
        Unloaded += (_, _) => model.PropertyChanged -= ModelChanged;
        ExecutableInput.TextChanged += (_, _) => { if (!rendering && model.ExecutablePath != ExecutableInput.Text) { workspace?.SuspendConnection(); model.ExecutablePath = ExecutableInput.Text; } };
        HostInput.TextChanged += (_, _) => { if (!rendering && model.Host != HostInput.Text) { workspace?.SuspendConnection(); model.Host = HostInput.Text; } };
    }
    internal void Initialize(RegistrationWorkspace? owner, ConnectionViewModel? value = null)
    {
        workspace = owner;
        if (value is not null) { model.PropertyChanged -= ModelChanged; model = value; if (IsLoaded) model.PropertyChanged += ModelChanged; }
        rendering = true;
        ExecutableInput.Text = model.ExecutablePath; HostInput.Text = model.Host;
        rendering = false;
        Render();
    }
    internal void ShowProblem(string text) => UiMessage.Text = text;
    internal async Task StopAsync()
    {
        closingRequested = true; model.Cancel();
        if (operation is not null) await operation;
    }
    internal void ResumeAfterFailedClose() { closingRequested = false; Render(); }
    private async void GoBack(object sender, RoutedEventArgs e)
    {
        model.Cancel();
        if (operation is not null) await operation;
        ReturnRequested?.Invoke(this, EventArgs.Empty);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!IsLoaded) return;
        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        if (!IsLoaded) return;
        rendering = true;
        try
        {
            // TextChanged can arrive after another control's notification. Never write
            // model snapshots back over newer input while rendering diagnostic state.
            var enabled = model.CanCheck && !checking && !closingRequested && !pickerOpen;
            ExecutableInput.IsEnabled = HostInput.IsEnabled = enabled;
            DetectButton.IsEnabled = BrowseButton.IsEnabled = CheckButton.IsEnabled = enabled;
            SwitchButton.IsEnabled = enabled && model.CanSwitch;
            CancelButton.IsEnabled = model.IsBusy && !closingRequested;
            StatusValue.Text = model.StatusText;
            EnvironmentValue.Text = model.EnvironmentText;
            VersionValue.Text = model.VersionText;
            AccountValue.Text = model.AccountText;
            StorageValue.Text = model.StorageText;
            ScopeValue.Text = model.ScopeText;
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
        workspace?.SuspendConnection();
        if (closingRequested) return;
        UiMessage.Text = "";
        model.ExecutablePath = ExecutableInput.Text;
        model.Host = HostInput.Text;
        model.IssueUrl = "";
        model.ProjectUrl = "";
        await model.CheckAsync(newConnection);
        if (workspace is not null && !closingRequested && model.Connection is { IsConnected: true })
            await workspace.BindAsync(model.Connection.Context, model.Service);
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
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeFilter.Add(".exe");
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !closingRequested) ExecutableInput.Text = file.Path;
        }
        catch (Exception)
        {
            if (IsLoaded) UiMessage.Text = "ファイルを選択できませんでした。gh.exe のパスを入力してください。";
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

}
