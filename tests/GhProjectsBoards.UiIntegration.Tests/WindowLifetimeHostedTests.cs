using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class WindowLifetimeHostedTests
{
    [Test, Category("CloseRecovery")]
    public async Task FailedStopKeepsTheWindowUsableAndRetryClosesAfterSavingItsInput()
    {
        Ui.Check();
        var root = Path.Combine(Path.GetTempPath(), "ghpb-close-recovery-" + Guid.NewGuid().ToString("N"));
        var project = EditingTests.Registration(count: 1);
        await new RegistrationStore(root).SaveAsync(project);
        TestContext.Out.WriteLine($"Store: {root}; production: {typeof(MainWindow).Assembly.Location}");
        var runner = GhConnectionTests.ConnectedRunner();
        var service = new GhConnectionService("explicit-gh.exe", "github.com", runner);
        var context = (await service.ConnectAsync()).Context!;
        var hostWindow = Ui.Window;
        MainWindow? window = null;
        RegistrationPanel projects = null!;
        var closed = false;
        var previousRoot = Environment.GetEnvironmentVariable("GHPB_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", root);
            try
            {
                await Ui.Run(() => {
                    window = new MainWindow();
                    window.Closed += (_, _) => closed = true;
                    window.Activate();
                    projects = Ui.Tree(window.Content).OfType<RegistrationPanel>().Single();
                    Ui.Window = window;
                });
            }
            finally { Environment.SetEnvironmentVariable("GHPB_DATA_ROOT", previousRoot); }
            await Ui.Until(() => projects.IsLoaded && projects.Workspace.Registrations.Count == 1);
            var workspace = projects.Workspace;
            await Ui.Run(async () => {
                await workspace.BindAsync(context, service);
                Assert.That(await workspace.SelectAsync(project.Snapshot.Id), Is.True);
            });
            await Ui.Until(() => Ui.Tree(projects).OfType<EditingGrid>().Any(grid => grid.IsLoaded));
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0", projects)).SetFocus());
            await Ui.Until(() => Ui.Tree(projects).OfType<TextBox>().Any(input => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(input) == "GridCell0_0" && input.IsLoaded));
            await Ui.Run(async () => {
                Ui.Find<TextBox>("GridCell0_0", projects).Text = "Input before failed close";
                var failure = new InvalidOperationException("Controlled settled observer failure");
                void FailSettledObserver() { if (!workspace.IsBusy) throw failure; }
                workspace.Changed += FailSettledObserver;
                try
                {
                    Exception? observed = null;
                    try { await workspace.DiscoverAsync((_, _, _) => Task.CompletedTask); }
                    catch (InvalidOperationException error) { observed = error; }
                    Assert.That(observed, Is.SameAs(failure));
                }
                finally { workspace.Changed -= FailSettledObserver; }
            });

            // Window.Close destroys directly; the system close affordance exercises
            // the cancellable AppWindow.Closing route used by the title-bar button.
            await SheetNativeInput.ActivateWindow();
            await SheetNativeInput.Press(VirtualKey.F4, VirtualKey.Menu);
            await Ui.Until(() => closed || Ui.Find<TextBlock>("RegistrationStatus", projects).Text.Contains("終了できませんでした"));
            await Ui.Run(() => {
                Assert.That(closed, Is.False);
                Assert.That(projects.IsEnabled, Is.True);
                Assert.That(Ui.Find<Button>("ConnectionPageButton", projects).IsEnabled, Is.True);
                Assert.That(Ui.Find<TextBlock>("RegistrationStatus", projects).Text, Does.Contain("入力は保持しています").And.Contain("もう一度閉じて"));
                Assert.That(workspace.Drafts!.Workspace.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("Input before failed close"));
                Ui.Click(Ui.Find<Button>("ConnectionPageButton", projects));
            });
            var connection = await ConnectionIn(window!);
            await Ui.Until(() => connection.Visibility == Visibility.Visible && Ui.Tree(connection).OfType<Button>().Any(button =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(button) == "CheckConnectionButton" && button.IsLoaded));
            await Ui.Run(() => {
                Assert.That(Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled, Is.True);
                Assert.That(Ui.Find<TextBox>("HostInput", connection).IsEnabled, Is.True);
                Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection));
            });
            await Ui.Until(() => projects.Visibility == Visibility.Visible);
            await Ui.Run(() => FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<FrameworkElement>("GridCell0_0", projects)).SetFocus());
            await Ui.Until(() => Ui.Tree(projects).OfType<TextBox>().Any(input => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(input) == "GridCell0_0" && input.IsLoaded));
            await Ui.Run(() => Ui.Find<TextBox>("GridCell0_0", projects).Text = "Continued work after failed close");
            await SheetNativeInput.ActivateWindow();
            await SheetNativeInput.Press(VirtualKey.F4, VirtualKey.Menu);
            await Ui.Until(() => closed);
            var saved = await new DraftStore(root).LoadAsync(project.Snapshot.Id.Scope);
            Assert.That(saved!.Fields.Single(f => f.Key == new FieldKey("Title", "I1")).Buffer, Is.EqualTo("Continued work after failed close"));
        }
        finally
        {
            await Ui.Run(() => {
                if (window is not null && !closed) window.Close();
                Ui.Window = hostWindow;
                hostWindow.Activate();
                return Task.CompletedTask;
            }, check: false);
        }
        await Ui.Idle();

        static async Task<ConnectionPanel> ConnectionIn(MainWindow owner)
        {
            ConnectionPanel result = null!;
            await Ui.Run(() => result = Ui.Tree(owner.Content).OfType<ConnectionPanel>().Single());
            return result;
        }
    }
}
