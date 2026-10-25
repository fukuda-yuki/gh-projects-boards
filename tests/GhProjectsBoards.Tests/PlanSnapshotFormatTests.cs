using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanSnapshotFormatTests
{
    [Test]
    public void PublisherSnapshotUsesTheCurrentStrictDocumentFormat()
    {
        using var stream = typeof(PlanSnapshotFormatTests).Assembly.GetManifestResourceStream("GhProjectsBoards.Tests.Fixtures.Phase9Snapshot2.json")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var document = PlanJson.Read<PlanDocument>(buffer.ToArray());
        Assert.That(document.State.Rows, Is.Not.Empty);
        Assert.That(PlanJson.Read<PlanDocument>(System.Text.Encoding.UTF8.GetBytes(PlanJson.Text(document))).State.Rows.Length, Is.EqualTo(document.State.Rows.Length));
    }
    [Test]
    public void ASnapshotSavedBeforeTheReadObservationsOpensWithNone()
    {
        // #238 keeps the last read's observations with the plan. A snapshot the previous version wrote has
        // none, so the strip shows no read result and judges no 未更新 instead of failing the strict format.
        using var stream = typeof(PlanSnapshotFormatTests).Assembly.GetManifestResourceStream("GhProjectsBoards.Tests.Fixtures.Phase9Snapshot2.json")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var document = PlanJson.Read<PlanDocument>(buffer.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(document.Sync.ProgressUpdatedAt, Is.Empty);
            Assert.That(document.Sync.LastRead, Is.Null);
            Assert.That(PlanJson.Text(document), Does.Contain("progressUpdatedAt"));
        });
    }
}
