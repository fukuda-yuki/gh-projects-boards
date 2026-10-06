using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NUnit.Framework;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GhProjectsBoards.UiIntegration.Tests;

internal static class RenderedEvidence
{
    internal static async Task Capture(FrameworkElement view, string name)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Ui.Trace($"[CAPTURE start] {name} size={view.ActualWidth}x{view.ActualHeight}");
        if (!view.IsLoaded || view.XamlRoot is null) throw new InvalidOperationException($"Capture target unloaded: {name}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Let native item entrance animations settle before pixel evidence is captured.
        // This delay is not used to establish behavior; tests assert their state separately.
        await Task.Delay(1000, deadline.Token);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        EventHandler<object> rendering = (_, _) => { if (++frames >= 2) rendered.TrySetResult(); };
        CompositionTarget.Rendering += rendering;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token); }
        finally { CompositionTarget.Rendering -= rendering; }
        deadline.Token.ThrowIfCancellationRequested();
        if (!view.IsLoaded || view.XamlRoot is null) throw new InvalidOperationException($"Capture target unloaded: {name}");
        var bitmap = new RenderTargetBitmap();
        Ui.Trace($"[CAPTURE render] {name} milliseconds={timer.Elapsed.TotalMilliseconds:F1}");
        await bitmap.RenderAsync(view).AsTask(deadline.Token);
        Ui.Trace($"[CAPTURE rendered] {name} milliseconds={timer.Elapsed.TotalMilliseconds:F1} pixels={bitmap.PixelWidth}x{bitmap.PixelHeight}");
        Assert.That(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, Is.True);
        var buffer = await bitmap.GetPixelsAsync().AsTask(deadline.Token); using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels); Assert.That(pixels.Any(b => b != 0), Is.True);
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-review-" + TestContext.CurrentContext.Test.ID + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = await StorageFolder.GetFolderFromPathAsync(folder).AsTask(deadline.Token);
        var file = await destination.CreateFileAsync(name + ".png", CreationCollisionOption.FailIfExists).AsTask(deadline.Token);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite).AsTask(deadline.Token);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(deadline.Token);
        var dpi = 96 * view.XamlRoot.RasterizationScale;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync().AsTask(deadline.Token); Console.WriteLine("Rendered review evidence: " + file.Path);
    }
}
