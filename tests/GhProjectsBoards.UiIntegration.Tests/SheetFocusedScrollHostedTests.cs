using System.Diagnostics;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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
public sealed class SheetFocusedScrollHostedTests
{
    [Test]
    public async Task ScrollingAwayFromAnEditedMiddleRowDoesNotSelectTheFirstRowOrLoseTheNextInput()
    {
        var registration = EditingTests.Registration(count: 1000);
        var work = new EditingWorkspace(registration.Snapshot.Id.Scope); work.SetRegistrations([registration]);
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-middle-scroll-" + Guid.NewGuid().ToString("N")));
        var session = new DraftSession(store, work, 0);
        EditingGrid grid = null!;
        await Ui.Run(() => { Ui.Window.AppWindow.Resize(new(1100, 800)); grid = new(registration, session, () => Task.FromResult(true)); });
        try
        {
            await Ui.Mount(grid); await Ui.Ready<FrameworkElement>("GridCell5_0");
            await SheetNativeInput.Click("GridCell5_0"); await SheetNativeInput.Press(Windows.System.VirtualKey.Number7);
            await Ui.Until(() => Ui.Find<TextBox>("GridCell5_0").Text == "7");
            TextBox original = null!; ScrollViewer viewport = null!; IScrollProvider scroll = null!;
            await Ui.Run(() => {
                original = Ui.Find<TextBox>("GridCell5_0");
                Assert.That(original.Text, Is.EqualTo("7"));
                var list = Ui.Find<ListView>("ProjectItems"); viewport = Ui.Tree(list).OfType<ScrollViewer>().First();
                scroll = (IScrollProvider)FrameworkElementAutomationPeer.CreatePeerForElement(list).GetPattern(PatternInterface.Scroll);
                scroll.SetScrollPercent(-1, 2.4);
            });
            await SheetNativeInput.Rendered();
            await Task.Delay(300); // Observe an unwanted focus/caret reveal after the scroll settles; not a performance claim.
            await Ui.Run(() => {
                Assert.That(viewport.VerticalOffset, Is.GreaterThan(400), "Passive scroll must not snap back to the first row.");
                Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T6"));
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(original));
                FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell25_0")).SetFocus();
            });
            await SheetNativeInput.Press(Windows.System.VirtualKey.Number8);
            await Ui.Until(() => work.Buffer(work.Open(registration)[25].Cells[0]) == "8");
            await Ui.Run(() => scroll.SetScrollPercent(-1, 0)); await SheetNativeInput.Rendered();
            await Ui.Run(() => {
                FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell5_0")).SetFocus();
                Assert.That(Ui.Find<TextBox>("GridCell5_0"), Is.SameAs(original));
                Assert.That(original.Text, Is.EqualTo("7"));
                Assert.That(work.Journal, Is.Empty);
            });
        }
        finally { await Ui.Unmount(grid); Assert.That(await session.FlushAsync(), Is.True); }
    }

    [Test]
    public async Task LongTitleCaretScrollsInsideCellWithoutMovingSheetOrCommitting()
    {
        var registration = EditingTests.Registration(count: 6);
        var work = new EditingWorkspace(registration.Snapshot.Id.Scope);
        work.SetRegistrations([registration]); work.Open(registration);
        var session = new DraftSession(new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-long-title-" + Guid.NewGuid().ToString("N"))), work, 0);
        EditingGrid grid = null!;
        Grid surface = null!;
        TextBox editor = null!;
        ScrollViewer textScroll = null!, sheetScroll = null!;
        var text = string.Concat(Enumerable.Repeat("長いタイトル native caret scrolling 0123456789 ", 8));
        var key = new FieldKey("Title", "I1");
        await Ui.Run(() =>
        {
            grid = new EditingGrid(registration, session, () => Task.FromResult(true));
            surface = new Grid { Width = 640, Height = 440 };
            surface.Children.Add(grid);
        });
        try
        {
            await Ui.Mount(surface);
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                editor = Ui.Find<TextBox>("GridCell0_0");
                Assert.That(editor.Focus(FocusState.Keyboard), Is.True);
                sheetScroll = Ui.Tree(Ui.Find<ListView>("ProjectItems")).OfType<ScrollViewer>().First();
                textScroll = Ui.Tree(editor).OfType<ScrollViewer>().First();
            });
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), editor)
                && editor.SelectionLength == editor.Text.Length);
            await Ui.Run(() => { editor.SelectedText = text; editor.Select(text.Length, 0); });
            await Ui.Until(() => textScroll.HorizontalOffset > 0);
            await Ui.Run(() =>
            {
                Assert.That(editor.SelectionStart, Is.EqualTo(text.Length));
                Assert.That(editor.Text, Is.EqualTo(text));
                Assert.That(sheetScroll.HorizontalOffset, Is.Zero);
                Assert.That(sheetScroll.VerticalOffset, Is.Zero);
                Assert.That(work.Fields.Single(f => f.Key == key).Buffer, Is.EqualTo(text));
                Assert.That(work.Fields.Single(f => f.Key == key).Change, Is.Null);
                editor.Select(0, 0);
            });
            await Ui.Until(() => textScroll.HorizontalOffset < 1);
            await Ui.Run(() =>
            {
                Assert.That(FocusManager.GetFocusedElement(grid.XamlRoot), Is.SameAs(editor));
                Assert.That(editor.SelectionStart, Is.Zero);
                Assert.That(editor.Text, Is.EqualTo(text));
                Assert.That(sheetScroll.HorizontalOffset, Is.Zero);
                Assert.That(work.Journal, Is.Empty);
            });
        }
        finally { await Ui.Unmount(surface); await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True)); }
    }

    [TestCase(false), TestCase(true)]
    public async Task FocusedTitleScrollRoundtripRetainsPendingIdentityAndCaret(bool pending)
    {
        var registration = EditingTests.Registration(count: 101);
        var work = new EditingWorkspace(registration.Snapshot.Id.Scope);
        work.SetRegistrations([registration]); work.Open(registration);
        var folder = Path.Combine(Path.GetTempPath(), "ghpb-focused-scroll-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(Path.Combine(folder, "data"));
        var session = new DraftSession(store, work, 0);
        var firstKey = new FieldKey("Title", "I1");
        var secondKey = new FieldKey("Title", "I2");
        var observations = new List<object>();
        var captures = new List<string>();
        var artifactErrors = new List<string>();
        Exception? behaviorError = null;
        Exception? teardownError = null;
        var started = Stopwatch.GetTimestamp();
        EditingGrid grid = null!;
        Grid surface = null!;
        ScrollViewer scroll = null!;
        TextBox editor = null!;
        ListView list = null!;
        var mounted = false;
        var initialSelectionStart = 0;
        var initialSelectionLength = 0;
        var firstText = pending ? "pending-first-title" : "Issue 1";

        void Observe(string phase)
        {
            var focused = FocusManager.GetFocusedElement(grid.XamlRoot);
            observations.Add(new
            {
                phase, ticks = Stopwatch.GetTimestamp() - started,
                horizontal = scroll.HorizontalOffset, vertical = scroll.VerticalOffset,
                maximumHorizontal = scroll.ScrollableWidth, maximumVertical = scroll.ScrollableHeight,
                viewportWidth = scroll.ViewportWidth, viewportHeight = scroll.ViewportHeight,
                focusedId = focused is DependencyObject element ? AutomationProperties.GetAutomationId(element) : null,
                activeItem = grid.SelectionIdentity?.Item, activeField = grid.SelectionIdentity?.Field,
                originalLoaded = editor.IsLoaded, originalText = editor.Text,
                caret = editor.SelectionStart, selectedLength = editor.SelectionLength,
                originalBuffer = work.Fields.Single(f => f.Key == firstKey).Buffer,
                originalChange = work.Fields.Single(f => f.Key == firstKey).Change
            });
        }
        void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => Observe(args.IsIntermediate ? "view-intermediate" : "view-final");

        async Task Capture(string phase)
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(surface);
            Assert.That(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, Is.True);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(buffer);
            var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
            Assert.That(pixels.Any(value => value != 0), Is.True, "Empty render cannot support pixel inspection.");
            Directory.CreateDirectory(folder);
            var destination = await StorageFolder.GetFolderFromPathAsync(folder);
            var file = await destination.CreateFileAsync(phase + ".png", CreationCollisionOption.FailIfExists);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var dpi = 96 * surface.XamlRoot.RasterizationScale;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
            await encoder.FlushAsync();
            captures.Add(file.Path);
        }

        void AssertFirstEditor()
        {
            var focused = FocusManager.GetFocusedElement(grid.XamlRoot) as TextBox;
            Assert.That(focused, Is.Not.Null, "Scrolling must not move focus from the active title to a toolbar/list/another row.");
            Assert.That(AutomationProperties.GetAutomationId(focused!), Is.EqualTo("GridCell0_0"));
            Assert.That(grid.SelectionIdentity?.Item, Is.EqualTo("P1T1"));
            Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(firstKey));
            Assert.That(focused!.Text, Is.EqualTo(firstText));
            Assert.That(focused.SelectionStart, Is.EqualTo(initialSelectionStart));
            Assert.That(focused.SelectionLength, Is.EqualTo(initialSelectionLength));
            var field = work.Fields.Single(f => f.Key == firstKey);
            Assert.That(field.Buffer, Is.EqualTo(pending ? firstText : null));
            Assert.That(field.Change, Is.Null);
            Assert.That(work.DifferenceCount, Is.Zero);
            Assert.That(work.LocalRows, Is.Empty);
            Assert.That(work.Journal, Is.Empty);
        }

        async Task MoveAndObserve(string phase, bool bottom, bool right)
        {
            await Ui.Run(() =>
            {
                Observe(phase + "-before");
                scroll.ChangeView(right ? scroll.ScrollableWidth : 0, bottom ? scroll.ScrollableHeight : 0, null, true);
            });
            await Ui.Until(() => Math.Abs(scroll.VerticalOffset - (bottom ? scroll.ScrollableHeight : 0)) < 1
                && Math.Abs(scroll.HorizontalOffset - (right ? scroll.ScrollableWidth : 0)) < 1);
            await Ui.Run(async () =>
            {
                await RenderFrames();
                // Observe a quiet interval after reaching the endpoint. This does not
                // send more input or move focus to make a transient endpoint pass.
                var quiet = Stopwatch.StartNew();
                do
                {
                    Observe(phase + "-quiet");
                    AssertFirstEditor();
                    Assert.That(scroll.VerticalOffset, Is.EqualTo(bottom ? scroll.ScrollableHeight : 0).Within(1));
                    Assert.That(scroll.HorizontalOffset, Is.EqualTo(right ? scroll.ScrollableWidth : 0).Within(1));
                    await Task.Delay(25);
                } while (quiet.Elapsed < TimeSpan.FromMilliseconds(300));
                await Capture(phase);
                Assert.That(scroll.VerticalOffset, Is.EqualTo(bottom ? scroll.ScrollableHeight : 0).Within(1),
                    "The settled viewport must not return to the old focused row without user input.");
                Assert.That(scroll.HorizontalOffset, Is.EqualTo(right ? scroll.ScrollableWidth : 0).Within(1));
                AssertFirstEditor();
                var rowIndex = bottom ? 100 : 0;
                var row = (Grid)((ListViewItem)list.Items[rowIndex]).Content;
                Assert.That(row.IsLoaded, Is.True);
                Assert.That(Ui.Find<Grid>("SheetHeader").TransformToVisual(grid).TransformPoint(new(0, 0)).X,
                    Is.EqualTo(row.TransformToVisual(grid).TransformPoint(new(0, 0)).X).Within(1),
                    "Header and visible row boundaries must retain their column context.");
            });
        }

        try
        {
            await Ui.Run(() =>
            {
                grid = new EditingGrid(registration, session, () => Task.FromResult(true));
                surface = (Grid)XamlReader.Load("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"/>");
                surface.Width = 640; surface.Height = 440; surface.Children.Add(grid);
            });
            await Ui.Mount(surface); mounted = true;
            await Ui.Ready<TextBox>("GridCell0_0");
            await Ui.Run(() =>
            {
                list = Ui.Find<ListView>("ProjectItems", grid);
                scroll = Ui.Tree(list).OfType<ScrollViewer>().First();
                editor = Ui.Find<TextBox>("GridCell0_0", grid);
                Assert.That(editor.Focus(FocusState.Keyboard), Is.True);
            });
            await Ui.Until(() => ReferenceEquals(FocusManager.GetFocusedElement(grid.XamlRoot), editor));
            await Ui.Until(() => editor.SelectionStart == 0 && editor.SelectionLength == editor.Text.Length);
            await Ui.Run(async () =>
            {
                if (pending) { editor.SelectedText = firstText; editor.Select(3, 0); }
                initialSelectionStart = editor.SelectionStart; initialSelectionLength = editor.SelectionLength;
                Assert.That(scroll.ScrollableHeight, Is.GreaterThan(0));
                Assert.That(scroll.ScrollableWidth, Is.GreaterThan(0));
                scroll.ViewChanged += ViewChanged;
                Observe("focused-start"); await Capture("01-focused-start");
                AssertFirstEditor();
            });
            await MoveAndObserve("02-bottom-left", bottom: true, right: false);
            await MoveAndObserve("03-bottom-right", bottom: true, right: true);
            await MoveAndObserve("04-return-top-left", bottom: false, right: false);
            await MoveAndObserve("05-repeat-bottom-right", bottom: true, right: true);
            await MoveAndObserve("06-repeat-return", bottom: false, right: false);

            await Ui.Run(() => Assert.That(Ui.Find<TextBox>("GridCell1_0").Focus(FocusState.Keyboard), Is.True));
            await Ui.Until(() => (FocusManager.GetFocusedElement(grid.XamlRoot) as DependencyObject) is { } focus
                && AutomationProperties.GetAutomationId(focus) == "GridCell1_0");
            await Ui.Until(() =>
            {
                var second = Ui.Find<TextBox>("GridCell1_0");
                return second.SelectionStart == 0 && second.SelectionLength == second.Text.Length;
            });
            await Ui.Run(async () =>
            {
                var second = Ui.Find<TextBox>("GridCell1_0");
                second.SelectedText = "pending-second-title";
                Observe("second-target-input"); await Capture("07-second-target-input");
                Assert.That(grid.SelectionIdentity?.Field, Is.EqualTo(secondKey));
                Assert.That(work.Fields.Single(f => f.Key == firstKey).Buffer, Is.EqualTo(pending ? firstText : null));
                Assert.That(work.Fields.Single(f => f.Key == secondKey).Buffer, Is.EqualTo("pending-second-title"));
                Assert.That(work.Fields.Where(f => f.Buffer is not null).Select(f => f.Key),
                    Is.EquivalentTo(pending ? new[] { firstKey, secondKey } : new[] { secondKey }));
                Assert.That(work.DifferenceCount, Is.Zero);
                Assert.That(await session.FlushAsync(), Is.True);
            });
            var durable = await store.LoadAsync(work.Scope);
            Assert.That(durable, Is.Not.Null);
            Assert.That(durable!.Fields.Where(f => f.Buffer is not null).Select(f => f.Key),
                Is.EquivalentTo(pending ? new[] { firstKey, secondKey } : new[] { secondKey }));
            Assert.That(durable.Fields.Single(f => f.Key == secondKey).Buffer, Is.EqualTo("pending-second-title"));
            Assert.That(durable.Fields.Single(f => f.Key == firstKey).Buffer, Is.EqualTo(pending ? firstText : null));
            Assert.That(durable.Fields.Where(f => f.Change is not null), Is.Empty);
        }
        catch (Exception error)
        {
            behaviorError = error;
            throw;
        }
        finally
        {
            try
            {
                if (mounted)
                {
                    await Ui.Run(async () =>
                    {
                        if (scroll is not null)
                        {
                            scroll.ViewChanged -= ViewChanged;
                            Observe("final-observation");
                            try { await Capture("99-final-observation"); }
                            catch (Exception captureError) { artifactErrors.Add("Final diagnostic capture failed: " + captureError); }
                        }
                    });
                    await Ui.Unmount(surface);
                    await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
                    await Ui.Idle();
                }
            }
            catch (Exception error)
            {
                teardownError = error;
                if (behaviorError is null) throw;
                TestContext.Error.WriteLine("Teardown also failed; retaining the original behavior failure: " + error);
            }
            finally
            {
                Directory.CreateDirectory(folder);
                var record = Path.Combine(folder, "observations.json");
                await File.WriteAllTextAsync(record, JsonSerializer.Serialize(new
                {
                    scope = "Hosted real-control ChangeView/focus/pending/identity diagnosis; not physical wheel or readable-pixel acceptance.",
                    pending, rows = 101, selectFields = 1, stopwatchFrequency = Stopwatch.Frequency,
                    appAssembly = typeof(EditingGrid).Assembly.Location,
                    appSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(EditingGrid).Assembly.Location))),
                    behaviorError = behaviorError?.ToString(), teardownError = teardownError?.ToString(), artifactErrors,
                    observations, captures
                }, new JsonSerializerOptions { WriteIndented = true }));
                TestContext.AddTestAttachment(record, "Focused editor scroll observations; source and immutable captures.");
                foreach (var capture in captures) TestContext.AddTestAttachment(capture, "Actual hosted XAML render; inspect pixels separately.");
                foreach (var error in artifactErrors) TestContext.Error.WriteLine(error);
                TestContext.Out.WriteLine("Focused scroll evidence: " + folder);
            }
        }
    }

    private static async Task RenderFrames()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        EventHandler<object>? handler = null;
        handler = (_, _) => { if (++frames >= 2) completion.TrySetResult(); };
        CompositionTarget.Rendering += handler;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
}
