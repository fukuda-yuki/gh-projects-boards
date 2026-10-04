using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class GridLifetimeHostedTests
{
    [Test, Category("GridLifetime")]
    public async Task UnmountedReplacementKeepsCurrentSavePresentationThenMountedRetryPersistsBothProjects()
    {
        var first = EditingTests.Registration(count: 2);
        var second = EditingTests.Registration("P2", count: 2);
        var work = new EditingWorkspace(first.Snapshot.Id.Scope);
        work.SetRegistrations([first, second]);
        var firstCell = work.Open(first)[0].Cells[1];
        work.SetBuffer(firstCell, "first Project unfinished");
        var directory = Path.Combine(Path.GetTempPath(), "ghpb-grid-lifetime-" + Guid.NewGuid().ToString("N"));
        var store = new DraftStore(directory);
        var session = new DraftSession(store, work, 0);
        EditingGrid current = null!, replacement = null!;
        var lifetime = new List<string>();
        TestContext.Out.WriteLine($"Store: {directory}");
        try
        {
            await Ui.Run(() => current = new(first, session, () => Task.FromResult(true)));
            await Ui.Mount(current);
            await Ui.Until(() => session.DurableRevision == work.Revision);
            TextBlock oldStatus = null!; string savedText = "";
            await Ui.Run(() => {
                oldStatus = Ui.Find<TextBlock>("WorkspaceSaveStatus", current);
                savedText = oldStatus.Text;
                Assert.That(savedText, Does.Contain("ローカル保存済み"));
                current.Unloaded += (_, _) => lifetime.Add("previous-unloaded");
            });
            FieldKey secondKey;
            using (var held = new FileStream(Path.Combine(directory, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await Ui.Run(() => {
                    replacement = new(second, session, () => Task.FromResult(true));
                    replacement.Loaded += (_, _) => lifetime.Add("replacement-loaded");
                    Assert.That(current.IsLoaded, Is.True);
                    Assert.That(replacement.IsLoaded, Is.False);
                    Assert.That(work.Revision, Is.GreaterThan(session.DurableRevision), "Opening the second Project initialized fields requiring a later save.");
                    Assert.That(oldStatus.Text, Is.EqualTo(savedText), "Preparing an unmounted view must leave the usable current table's save presentation unchanged.");
                    Assert.That(session.Status, Does.Contain("ローカル保存済み"));
                    var secondCell = work.Open(second)[0].Cells[1];
                    work.SetBuffer(secondCell, "second Project unfinished");
                });
                secondKey = work.Open(second)[0].Cells[1].Key!;
                // Use the same native child replacement as RegistrationPanel.
                await Ui.Run(() => { Ui.Root.Children.Clear(); Ui.Root.Children.Add(replacement); });
                await Ui.Until(() => !current.IsLoaded && replacement.IsLoaded && session.Status.Contains("失敗"));
                await Ui.Run(() => {
                    Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus", replacement).Text, Does.Contain("ローカル保存失敗"));
                    Assert.That(Ui.Find<Button>("WorkspaceSaveRetry", replacement).Visibility, Is.EqualTo(Visibility.Visible));
                    Assert.That(oldStatus.Text, Is.EqualTo(savedText), "The replaced table must not present the replacement's saving or failure.");
                    Assert.That(work.Buffer(firstCell), Is.EqualTo("first Project unfinished"));
                    Assert.That(work.Fields.Single(field => field.Key == secondKey).Buffer, Is.EqualTo("second Project unfinished"));
                });
                var beforeRetry = (await store.LoadAsync(work.Scope))!;
                Assert.That(beforeRetry.Fields.Any(field => field.Key == secondKey), Is.False, "A failed opening save must not be presented as durable.");
            }
            await Ui.Run(() => Ui.Click(Ui.Find<Button>("WorkspaceSaveRetry", replacement)));
            await Ui.Until(() => session.DurableRevision == work.Revision && !session.Status.Contains("失敗"));
            await Ui.Run(() => {
                Assert.That(Ui.Find<TextBlock>("WorkspaceSaveStatus", replacement).Text, Does.Contain("ローカル保存済み"));
                Assert.That(Ui.Find<Button>("WorkspaceSaveRetry", replacement).Visibility, Is.EqualTo(Visibility.Collapsed));
                Assert.That(oldStatus.Text, Is.EqualTo(savedText));
                Assert.That(work.Journal, Is.Empty);
            });
            var restored = (await store.LoadAsync(work.Scope))!;
            Assert.That(restored.Fields.Single(field => field.Key == firstCell.Key).Buffer, Is.EqualTo("first Project unfinished"));
            Assert.That(restored.Fields.Single(field => field.Key == secondKey).Buffer, Is.EqualTo("second Project unfinished"));
            Assert.That(restored.Journal, Is.Empty);
        }
        finally
        {
            TestContext.Out.WriteLine("Native replacement events: " + string.Join(" -> ", lifetime));
            if (replacement is not null) await Ui.Unmount(replacement);
            if (current is not null) await Ui.Unmount(current);
            await Ui.Run(async () => Assert.That(await session.FlushAsync(), Is.True));
            await Ui.Idle();
        }
    }
}
