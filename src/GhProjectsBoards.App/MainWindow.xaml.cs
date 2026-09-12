using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace GhProjectsBoards.App;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionViewModel model = new();
    private Task? operation;
    private bool rendering;
    private bool pickerOpen;
    private bool closingRequested;
    private bool closed;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1080, 960));
        ExecutableInput.Text = model.ExecutablePath;
        HostInput.Text = model.Host;
        IssueInput.Text = model.IssueUrl;
        ProjectInput.Text = model.ProjectUrl;
        ExecutableInput.TextChanged += (_, _) => { if (!rendering) model.ExecutablePath = ExecutableInput.Text; };
        HostInput.TextChanged += (_, _) => { if (!rendering) model.Host = HostInput.Text; };
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
            var enabled = model.CanCheck && !closingRequested && !pickerOpen;
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
        if (closingRequested || operation is { IsCompleted: false }) return;
        UiMessage.Text = "";
        model.ExecutablePath = ExecutableInput.Text;
        model.Host = HostInput.Text;
        model.IssueUrl = IssueInput.Text;
        model.ProjectUrl = ProjectInput.Text;
        operation = model.CheckAsync(newConnection);
        await operation;
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
        if (operation is not { IsCompleted: false })
        {
            closingRequested = true;
            return;
        }
        args.Cancel = true;
        if (closingRequested) return;
        closingRequested = true;
        Render();
        model.Cancel();
        // Keep the UI dispatcher alive until the owned gh operation has stopped.
        await operation;
        DispatcherQueue.TryEnqueue(() => { if (!closed) Close(); });
    }
}
