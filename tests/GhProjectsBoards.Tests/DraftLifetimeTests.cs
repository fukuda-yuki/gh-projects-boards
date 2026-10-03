using System.Text.Json;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class DraftLifetimeTests
{
    [Test]
    public async Task NewerInputDuringSnapshotWriteIsNotAnnouncedAsSavedBeforeItIsDurable()
    {
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-save-status-" + Guid.NewGuid()));
        var project = EditingTests.Registration(count: 1);
        var workspace = new EditingWorkspace(project.Snapshot.Id.Scope);
        var cell = workspace.Open(project)[0].Cells[0];
        workspace.SetBuffer(cell, "Earlier input");
        var session = new DraftSession(store, workspace, 0);
        var notifications = new List<(string Status, long Durable, long Current)>();
        session.Changed += () => notifications.Add((session.Status, session.DurableRevision, workspace.Revision));
        using var caller = new QueuedSaveContext();
        var originalContext = SynchronizationContext.Current;
        Task<bool> flush;
        try
        {
            SynchronizationContext.SetSynchronizationContext(caller);
            flush = session.FlushAsync();
            // The real store writes the detached earlier snapshot on its worker.
            // Hold its caller continuation so newer input arrives before acknowledgement.
            workspace.SetBuffer(cell, "Newer input");
            while (!flush.IsCompleted) caller.RunNext();
        }
        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }

        Assert.That(await flush, Is.True);
        var saved = (await store.LoadAsync(workspace.Scope))!;
        Assert.That(saved.Revision, Is.EqualTo(workspace.Revision));
        Assert.That(saved.Fields.Single(field => field.Key == cell.Key).Buffer, Is.EqualTo("Newer input"));
        Assert.That(saved.History, Is.Empty);
        TestContext.WriteLine(JsonSerializer.Serialize(notifications.Select(value => new {
            value.Status, value.Durable, value.Current })));
        Assert.That(notifications.Where(value => value.Status.StartsWith("ローカル保存済み"))
            .All(value => value.Durable == value.Current), Is.True,
            "A saved notification must acknowledge all current input, not only an older snapshot.");
        Assert.That(notifications.Last().Status, Does.StartWith("ローカル保存済み"));
    }

    [Test]
    public async Task RetryRequestedDuringFailedSavePersistsTheLatestBuffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-save-retry-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var store = new DraftStore(root); var p = EditingTests.Registration(count: 1);
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); var cell = w.Open(p)[0].Cells[0];
        var session = new DraftSession(store, w, 0);
        using var competingWriter = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<bool>? joined = null; var queued = false;
        session.Changed += () =>
        {
            if (session.Status.StartsWith("ローカル保存中") && !queued)
            {
                queued = true; w.SetBuffer(cell, "Later pending text"); joined = session.FlushAsync();
            }
            if (session.Status.StartsWith("ローカル保存失敗")) competingWriter.Dispose();
        };
        Assert.That(await session.FlushAsync(), Is.True, session.Status);
        Assert.That(await joined!, Is.True);
        var saved = (await store.LoadAsync(w.Scope))!;
        Assert.That(saved.Revision, Is.EqualTo(w.Revision));
        Assert.That(saved.Fields.Single(f => f.Key == cell.Key).Buffer, Is.EqualTo("Later pending text"));
        Assert.That(saved.History, Is.Empty);
    }

    [TestCase(false), TestCase(true)]
    public async Task WorkCreatedBySettledNotificationIsDurableBeforeSuccess(bool commit)
    {
        var store = new DraftStore(Path.Combine(Path.GetTempPath(), "ghpb-save-settled-" + Guid.NewGuid()));
        var p = EditingTests.Registration(count: 1); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        var session = new DraftSession(store, w, 0); var opened = false;
        session.Changed += () =>
        {
            if (opened || session.Status.StartsWith("ローカル保存中")) return;
            opened = true;
            var cell = session.Workspace.Open(p)[0].Cells[0];
            session.Workspace.SetBuffer(cell, "Opened after acknowledgement");
            _ = session.FlushAsync();
        };
        Assert.That(commit ? await session.CommitAsync(c => c, () => true) : await session.FlushAsync(), Is.True);
        Assert.That(session.DurableRevision, Is.EqualTo(session.Workspace.Revision));
        var saved = (await store.LoadAsync(w.Scope))!;
        Assert.That(saved.Revision, Is.EqualTo(session.Workspace.Revision));
        Assert.That(saved.Fields.Single(f => f.Key.Kind == "Title").Buffer, Is.EqualTo("Opened after acknowledgement"));
    }

    [Test]
    public void CapturedCheckpointDoesNotShareMutableCollectionsWithWorkspaceOrOtherSnapshots()
    {
        var p = PlanningPathTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        w.SetRegistrations([p]); w.Open(p); w.SetPlanning(PlanningPathTests.Plan(), 0);
        var first = w.Snapshot(); var captured = w.Snapshot(); var before = JsonSerializer.Serialize(captured);
        first.Planning![0].Fields[0] = first.Planning[0].Fields[0] with { FieldId = "changed" };
        first.Registrations![0].Snapshot.Issues[0] = first.Registrations[0].Snapshot.Issues[0] with { Number = 900 };
        first.Fields[0] = first.Fields[0] with { Buffer = "foreign mutation" };
        Assert.That(JsonSerializer.Serialize(captured), Is.EqualTo(before));
        Assert.That(JsonSerializer.Serialize(w.Snapshot()), Is.EqualTo(before));
    }

    private sealed class QueuedSaveContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add((callback, state));
        internal void RunNext()
        {
            Assert.That(callbacks.TryTake(out var next, TimeSpan.FromSeconds(10)), Is.True, "The real save must complete.");
            next.Callback(next.State);
        }
        public void Dispose() => callbacks.Dispose();
    }
}
