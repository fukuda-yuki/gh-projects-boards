using GhProjectsBoards.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.Foundation;
using Windows.Graphics;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class ConnectionLayoutHostedTests
{
    [TestCase(1280), TestCase(960), Category("ConnectionLayout")]
    public async Task ConnectionInputsAndExpandedGuidanceFitTheViewport(int width)
    {
        ConnectionPanel connection = null!;
        SizeInt32 originalSize = default;
        await Ui.Run(() => {
            originalSize = Ui.Window.AppWindow.Size;
            var scale = Ui.Root.XamlRoot.RasterizationScale;
            Ui.Window.AppWindow.Resize(new((int)(width * scale), (int)(720 * scale)));
            connection = new ConnectionPanel();
        });
        await Ui.Mount(connection);
        try
        {
            await Ui.Ready<TextBox>("HostInput");
            await Ui.Run(() => {
                connection.UpdateLayout();
                AssertHorizontalBounds("HostInput", "ConnectionDetails", "LoginHelpExpander", "CheckConnectionButton", "CancelConnectionButton");
                var host = Ui.Find<TextBox>("HostInput", connection);
                var back = Ui.Find<Button>("ProjectsPageButton", connection);
                Assert.That(Bounds(host, connection).Left, Is.EqualTo(Bounds(back, connection).Left).Within(1),
                    "The form starts beneath the page controls, without an unexplained horizontal offset.");
                Ui.Find<Expander>("ConnectionDetails", connection).IsExpanded = true;
                Ui.Find<Expander>("LoginHelpExpander", connection).IsExpanded = true;
            });
            await Ui.Ready<TextBox>("LoginCommand");
            await Ui.Run(() => {
                connection.UpdateLayout();
                AssertHorizontalBounds("HostInput", "ConnectionDetails", "LoginHelpExpander", "ExecutablePath",
                    "LoginCommand", "RefreshCommand", "CopyLoginButton", "CopyRefreshButton");
                var viewport = Ui.Find<ScrollViewer>("ConnectionScreen", connection);
                viewport.ChangeView(null, viewport.ScrollableHeight, null, true);
            });
            await Ui.Until(() => {
                var viewport = Ui.Find<ScrollViewer>("ConnectionScreen", connection);
                var copy = Ui.Find<Button>("CopyRefreshButton", connection);
                var bounds = Bounds(copy, viewport);
                return bounds.Top >= -1 && bounds.Bottom <= viewport.ViewportHeight + 1;
            });
        }
        finally
        {
            await Ui.Unmount(connection);
            await Ui.Run(() => Ui.Window.AppWindow.Resize(originalSize));
            await Ui.Idle();
        }

        void AssertHorizontalBounds(params string[] ids)
        {
            var viewport = Ui.Find<ScrollViewer>("ConnectionScreen", connection);
            foreach (var id in ids)
            {
                var control = Ui.Find<FrameworkElement>(id, connection);
                var bounds = Bounds(control, viewport);
                TestContext.Out.WriteLine($"{width}: {id}={bounds}; viewport={viewport.ViewportWidth}x{viewport.ViewportHeight}");
                Assert.That(bounds.Width, Is.GreaterThan(0), id);
                Assert.That(bounds.Left, Is.GreaterThanOrEqualTo(-1), id);
                Assert.That(bounds.Right, Is.LessThanOrEqualTo(viewport.ViewportWidth + 1),
                    $"{id} must remain fully inside the horizontal viewport.");
            }
        }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
}
