using System.Text.Json;
using GhProjectsBoards.Core.Prototypes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Web.WebView2.Core;
namespace GhProjectsBoards.App.Prototypes.WebView;
public sealed class WebPrototype : Grid
{
    internal readonly WebView2 Browser = new();
    private readonly PrototypePlan plan = new();
    private readonly PrototypeMetrics metrics = new("webview");
    private readonly TextBlock error = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private string documentUri = "";
    private int pending;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal string Stage { get; private set; } = "constructed";
    internal string Failure => error.Text;
    internal bool Ready { get; private set; }
    public WebPrototype()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(error, 0); Grid.SetRow(Browser, 1);
        AutomationProperties.SetAutomationId(error, "PrototypeWebError");
        AutomationProperties.SetName(Browser, "計画表とガント");
        Children.Add(error); Children.Add(Browser);
        Loaded += Initialize;
        Unloaded += (_, _) => { metrics.End(pending, "unloaded-before-frame"); Browser.Close(); };
    }
    private void ShowError(string text)
    {
        error.Text = text;
        error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        try
        {
            Stage = "creating environment";
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(AppContext.BaseDirectory, "PrototypeWebProfile"), null);
            Stage = "ensuring control";
            await Browser.EnsureCoreWebView2Async(env);
            Stage = "control ready";
            Browser.CoreWebView2.ProcessFailed += (_, args) => { ShowError("WebView2: " + args.ProcessFailedKind); Stage += " -> " + args.ProcessFailedKind; };
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            Browser.CoreWebView2.NavigationStarting += (_, args) => { Stage = "local navigation"; if (!string.Equals(args.Uri, documentUri, StringComparison.OrdinalIgnoreCase)) args.Cancel = true; };
            Browser.CoreWebView2.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            Browser.CoreWebView2.WebResourceRequested += (_, args) => args.Response = env.CreateWebResourceResponse(null, 403, "Offline prototype", "");
            Browser.CoreWebView2.WebMessageReceived += Message;
            Browser.CoreWebView2.NavigationCompleted += (_, args) => { Stage += " -> navigation completed " + args.IsSuccess + " " + args.WebErrorStatus; if (args.IsSuccess) Send(0); else ShowError("ページを開始できません: " + args.WebErrorStatus); };
            using var stream = typeof(WebPrototype).Assembly.GetManifestResourceStream("GhProjectsBoards.App.Prototypes.WebView.prototype.html")!;
            var file = Path.Combine(AppContext.BaseDirectory, "PrototypeWebProfile", "prototype.html");
            await File.WriteAllTextAsync(file, await new StreamReader(stream).ReadToEndAsync());
            documentUri = new Uri(file).AbsoluteUri;
            Browser.CoreWebView2.Navigate(documentUri);
        }
        catch (Exception ex) { ShowError("WebView2 を開始できません: " + ex.Message); }
    }
    private void Send(int revision, double? started = null) => Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { revision, started, rows = plan.Rows }, Json));
    private void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Stage = "local message";
        if (!string.Equals(e.Source, documentUri, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson); var root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "rendered")
            { Ready = true; metrics.End(root.GetProperty("revision").GetInt32(), "web-second-animation-frame", root.TryGetProperty("elapsedMs", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number ? elapsed.GetDouble() : null); return; }
            var revision = metrics.Begin(); pending = revision;
            try { plan.Edit(root.GetProperty("index").GetInt32(), root.GetProperty("column").GetInt32(), root.GetProperty("value").GetString()!); Send(revision, root.GetProperty("started").GetDouble()); }
            catch (ArgumentException) { metrics.End(revision, "invalid"); Browser.CoreWebView2.PostWebMessageAsJson("{\"error\":\"入力値を確認してください\"}"); }
        }
        catch (JsonException) { ShowError("入力メッセージを確認できません"); }
    }
}








