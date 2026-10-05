using GhProjectsBoards.App.Prototypes.WinUi;
using GhProjectsBoards.App.Prototypes.WebView;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using NUnit.Framework;
using Windows.System;
namespace GhProjectsBoards.UiIntegration.Tests;
[TestFixture, NonParallelizable]
public sealed class PrototypeHostedTests
{
    [TestCase("16", "2026-10-07")]
    [TestCase("8", "2026-10-06")]
    public async Task NativeInvalidInputSurvivesAnotherCommitUntilCorrected(string correction, string expectedDate)
    {
        NativePrototype view = null!;
        await Ui.Run(() => view = new()); await Ui.Mount(view);
        try
        {
            await Ui.Ready<TextBox>("PrototypeCell0_1");
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_1").Focus(FocusState.Programmatic));
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_1").Text = "abc");
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell1_0").Focus(FocusState.Programmatic));
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell1_0").Text = "Correct title");
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell2_0").Focus(FocusState.Programmatic));
            await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBox>("PrototypeCell0_1").Text, Is.EqualTo("abc"));
                Assert.That(Ui.Find<TextBlock>("PrototypeError").Text, Is.Not.Empty);
                Assert.That(view.Plan.Rows[0].Remaining, Is.EqualTo(8));
                Assert.That(view.Plan.Rows[1].Title, Is.EqualTo("Correct title"));
            });
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_1").Focus(FocusState.Programmatic));
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_1").Text = correction);
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_0").Focus(FocusState.Programmatic));
            await Ui.Until(() => Ui.Find<TextBlock>("PrototypeEnd0").Text == expectedDate && Ui.Find<TextBlock>("PrototypeError").Text.Length == 0);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("PrototypeError").Text, Is.Empty);
                Assert.That(Ui.Find<TextBox>("PrototypeCell0_1").Text, Is.EqualTo(correction));
                Assert.That(Ui.Find<TextBox>("PrototypeCell1_2").Text, Is.EqualTo(expectedDate));
            });
        }
        finally { await Ui.Unmount(view); }
    }
    [Test]
    public async Task NativeRemainingFocusCommitChangesDisplayedSuccessorDateAndBar()
    {
        NativePrototype view = null!;
        await Ui.Run(() => view = new()); await Ui.Mount(view);
        try
        {
            await Ui.Ready<TextBox>("PrototypeCell0_1");
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_1").Focus(FocusState.Programmatic));
            await Ui.Run(() => { var editor = Ui.Find<TextBox>("PrototypeCell0_1"); editor.SelectAll(); editor.SelectedText = "16"; });
            await Ui.Run(() => Ui.Find<TextBox>("PrototypeCell0_0").Focus(FocusState.Programmatic));
            await Ui.Until(() => Ui.Find<TextBlock>("PrototypeEnd0").Text == "2026-10-07");
            await SheetNativeInput.Rendered();
            await CaptureNative(view);
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBox>("PrototypeCell1_2").Text, Is.EqualTo("2026-10-07"));
                Assert.That(Ui.Find<Rectangle>("PrototypeBar0").ActualWidth, Is.EqualTo(48));
                Assert.That(Canvas.GetLeft(Ui.Find<Rectangle>("PrototypeBar1")), Is.EqualTo(48));
                foreach (var row in view.Visible.Where(r => r.Index >= 0))
                    Assert.That(Ui.Find<TextBox>($"PrototypeCell{row.Index}_2").Text, Is.EqualTo(view.Plan.Rows[row.Index].Start), $"Rendered start for row {row.Index + 1}");
            });
        }
        finally { await Ui.Unmount(view); }
    }
    [Test]
    public async Task WebRemainingEnterChangesDisplayedSuccessorDateAndBar()
    {
        WebPrototype view = null!;
        await Ui.Run(() => view = new()); await Ui.Mount(view);
        try
        {
            try { await Ui.Until(() => { Assert.That(view.Failure, Is.Empty); return view.Ready; }); }
            catch (Exception ex) { string stage = ""; await Ui.Run(() => stage = view.Stage); throw new InvalidOperationException("Web initialization: " + stage, ex); }
            await Ui.Run(async () => await view.Browser.ExecuteScriptAsync("const input=document.getElementById('cell-0-1');input.focus();input.value='16';input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));"));
            string result = "";
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Ui.Run(async () => result = await view.Browser.ExecuteScriptAsync("[document.getElementById('end-0').textContent,document.getElementById('cell-1-2').value,document.getElementById('bar-0').getBoundingClientRect().width,document.getElementById('bar-1').getAttribute('x')].join('|')"));
                if (result == "\"2026-10-07|2026-10-07|48|48\"") break;
                await Task.Delay(20);
            }
            Assert.That(result, Is.EqualTo("\"2026-10-07|2026-10-07|48|48\""));
        }
        finally { await Ui.Unmount(view); }
    }
    private static async Task CaptureNative(FrameworkElement view)
    {
        var folder = Environment.GetEnvironmentVariable("GHPB_PROTOTYPE_CAPTURE");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        await Ui.Run(async () => {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(view);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
            var directory = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder);
            var file = await directory.CreateFileAsync("winui.png", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        });
    }
}

