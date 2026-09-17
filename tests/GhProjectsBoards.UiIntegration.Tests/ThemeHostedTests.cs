using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NUnit.Framework;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class ThemeHostedTests
{
    [TestCase(ElementTheme.Light), TestCase(ElementTheme.Dark)]
    public async Task StateMarkersRangeAndPendingEditorRemainDistinctAcrossTheme(ElementTheme theme)
    {
        var p = EditingTests.Registration(count: 6);
        p = p with { Snapshot = p.Snapshot with { Issues = p.Snapshot.Issues.ToDictionary(pair => pair.Key,
            pair => pair.Value.Number == 6 ? pair.Value with { Capability = new(false, p.RetrievedAt) } : pair.Value) } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var index in new[] { 1, 2, 4 }) w.Commit("P1", rows[index].Cells[1], "done", true);
        var record = w.Snapshot();
        w = EditingWorkspace.Restore(record with { Fields = record.Fields.Select(f => f.Key == rows[4].Cells[1].Key
            ? f with { Conflict = true, Observation = new("theme-observation", p.Snapshot.Id, p.RetrievedAt, "dup1", ValueAvailability.Present, null, rows[4].Cells[1].Options) } : f).ToArray() });
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-states-" + Guid.NewGuid().ToString("N"));
        var session = new DraftSession(new DraftStore(folder), w, 0);
        ElementTheme old = default; Grid surface = null!; EditingGrid grid = null!;
        string selectedCapture = "", pendingCapture = "", conditions = "";
        await Ui.Run(() => {
            old = Ui.Root.RequestedTheme; Ui.Root.RequestedTheme = theme;
            surface = (Grid)XamlReader.Load("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"/>");
            grid = new(p, session, () => Task.FromResult(true)); surface.Children.Add(grid);
        });
        try
        {
            await Ui.Mount(surface); await Ui.Ready<Button>("GridCell0_1");
            await SheetNativeInput.Click("GridCell0_1"); await SheetNativeInput.Click("GridCell1_1", Windows.System.VirtualKey.Shift);
            await Ui.Run(async () => {
                Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Does.Contain("2行・2セル"));
                Assert.That(Ui.Find<TextBlock>("GridMarker1_1").Text, Is.EqualTo("◆"));
                Assert.That(Ui.Find<TextBlock>("GridMarker2_1").Text, Is.EqualTo("◆"));
                Assert.That(Ui.Find<TextBlock>("GridMarker4_1").Text, Is.EqualTo("!"));
                Assert.That(Ui.Find<TextBox>("GridCell5_0").IsReadOnly, Is.True);
                Assert.That(Ui.Find<TextBlock>("GridMarker5_0").Text, Is.EqualTo("▧"));
                selectedCapture = await CaptureThemeAsync(Path.Combine(folder, "selected"), theme, surface);
            });
            await SheetNativeInput.Click("GridCell3_0"); await SheetNativeInput.Press(Windows.System.VirtualKey.F2);
            await SheetNativeInput.Press(Windows.System.VirtualKey.X);
            await Ui.Until(() => w.Buffer(rows[3].Cells[0]) is not null);
            await Ui.Run(async () => {
                Assert.That(w.Field(rows[3].Cells[0])!.Change, Is.Null);
                Assert.That(Ui.Find<TextBlock>("GridMarker3_0").Visibility, Is.EqualTo(Visibility.Collapsed));
                pendingCapture = await CaptureThemeAsync(Path.Combine(folder, "pending"), theme, surface);
                conditions = $"State matrix: requested={theme}; actual={grid.ActualTheme}; highContrast={new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast}; scale={Ui.Root.XamlRoot.RasterizationScale}; source={typeof(EditingGrid).Module.ModuleVersionId}";
            });
            TestContext.AddTestAttachment(selectedCapture); TestContext.AddTestAttachment(pendingCapture); TestContext.Out.WriteLine(conditions);
        }
        finally { await Ui.Unmount(surface); await Ui.Run(async () => { Ui.Root.RequestedTheme = old; Assert.That(await session.FlushAsync(), Is.True); }); await Ui.Idle(); }
    }

    [TestCase(ElementTheme.Light)]
    [TestCase(ElementTheme.Dark)]
    public async Task ChangingWorkspaceThemeKeepsFocusedSelectedPendingTitle(ElementTheme requestedTheme)
    {
        var registration = EditingTests.Registration(count: 6);
        var work = new EditingWorkspace(registration.Snapshot.Id.Scope);
        work.SetRegistrations([registration]); work.Open(registration);
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-theme-" + Guid.NewGuid().ToString("N"));
        var session = new DraftSession(new DraftStore(folder), work, 0);
        ElementTheme originalTheme = default;
        EditingGrid grid = null!;
        Grid surface = null!;
        TextBox editor = null!;
        await Ui.Run(() =>
        {
            originalTheme = Ui.Root.RequestedTheme;
            Ui.Root.RequestedTheme = requestedTheme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            grid = new EditingGrid(registration, session, () => Task.FromResult(true));
            // Match the ordinary MainWindow's native themed background; RenderTargetBitmap
            // captures this hosted XAML surface, not the window compositor's background.
            surface = (Grid)XamlReader.Load("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"/>");
            surface.Children.Add(grid);
        });
        try
        {
            await Ui.Mount(surface);
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                editor = Ui.Find<TextBox>("GridCell0_0");
                Ui.Click("GridDetails");
                Assert.That(editor.Focus(FocusState.Programmatic), Is.True);
                editor.Text = "テーマ変更後も保持する未確定文字";
                Ui.Root.RequestedTheme = requestedTheme;
            });
            await Ui.Until(() => grid.ActualTheme == requestedTheme && editor.ActualTheme == requestedTheme
                && Ui.Find<TextBlock>("SelectedCellDetails").Text.Contains(editor.Text));
            await Ui.Idle();
            string screenshot = "";
            await Ui.Run(async () =>
            {
                surface.UpdateLayout();
                await RenderFramesAsync();
                Assert.That(grid.IsLoaded && grid.ActualWidth > 0 && grid.ActualHeight > 0, Is.True);
                Assert.That(Ui.Find<TextBox>("GridCell0_0"), Is.SameAs(editor));
                Assert.That(FocusManager.GetFocusedElement(Ui.Root.XamlRoot), Is.SameAs(editor));
                Assert.That(editor.Text, Is.EqualTo("テーマ変更後も保持する未確定文字"));
                Assert.That(Ui.Find<TextBlock>("GridSelection").Text, Is.EqualTo("タイトル：1行・1セル"));
                Assert.That(Ui.Find<Grid>("SheetHeader").ActualTheme, Is.EqualTo(requestedTheme));
                Assert.That(session.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo(editor.Text));
                Assert.That(session.Workspace.DifferenceCount, Is.Zero);
                screenshot = await CaptureThemeAsync(folder, requestedTheme, surface);
            });
            TestContext.Out.WriteLine($"Theme: {requestedTheme}; hosted actual EditingGrid; render: {screenshot}");
            TestContext.AddTestAttachment(screenshot, $"Hosted workspace after switching to {requestedTheme}; pending selection retained.");
            await Ui.Run(() =>
            {
                var header = Ui.Find<Grid>("SheetHeader");
                var row = (Grid)((ListViewItem)Ui.Find<ListView>("ProjectItems").Items[0]).Content;
                Assert.That(header.TransformToVisual(grid).TransformPoint(new(0, 0)).X,
                    Is.EqualTo(row.TransformToVisual(grid).TransformPoint(new(0, 0)).X).Within(1),
                    "Header and row column boundaries must align when the sheet fits its viewport.");
            });
        }
        finally
        {
            await Ui.Unmount(surface);
            await Ui.Run(async () =>
            {
                Ui.Root.RequestedTheme = originalTheme;
                Assert.That(await session.FlushAsync(), Is.True);
            });
            await Ui.Idle();
        }
    }

    private static async Task RenderFramesAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        EventHandler<object>? rendering = null;
        rendering = (_, _) => { if (++frames >= 2) completion.TrySetResult(); };
        CompositionTarget.Rendering += rendering;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= rendering; }
    }
    private static async Task<string> CaptureThemeAsync(string folder, ElementTheme theme, Grid surface)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(surface);
        Assert.That(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, Is.True, "Theme evidence must contain the rendered workspace.");
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        Assert.That(pixels.Any(b => b != 0), Is.True, "A blank render is not theme evidence.");
        Directory.CreateDirectory(folder);
        var target = await StorageFolder.GetFolderFromPathAsync(folder);
        var file = await target.CreateFileAsync($"workspace-{theme.ToString().ToLowerInvariant()}.png", CreationCollisionOption.FailIfExists);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = 96 * Ui.Root.XamlRoot.RasterizationScale;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync();
        return file.Path;
    }
}
