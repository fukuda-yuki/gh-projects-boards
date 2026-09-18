using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using NUnit.Framework;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace GhProjectsBoards.UiIntegration.Tests;

internal static class ApplyInformationEvidence
{
    internal static async Task Capture(FrameworkElement view, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(view);
        Assert.That(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, Is.True);
        var buffer = await bitmap.GetPixelsAsync(); using var reader = DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels); Assert.That(pixels.Any(b => b != 0), Is.True);
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-review-" + TestContext.CurrentContext.Test.ID + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = await StorageFolder.GetFolderFromPathAsync(folder);
        var file = await destination.CreateFileAsync(name + ".png", CreationCollisionOption.FailIfExists);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var dpi = 96 * view.XamlRoot.RasterizationScale;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync(); Console.WriteLine("Rendered review evidence: " + file.Path);
    }
}
