using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #248: a plan another window stored must neither cost this window's edits nor trap the window.
[TestFixture]
internal sealed class PlanStoredPlanConflictTests
{
    private static readonly DateOnly Today = PlanCsvImportTests.Today;
    private static PlanDocument Document()
    {
        var rows = ImmutableArray.Create(
            new PlanRow("I1", "first", "acme/repo") { Estimate = 8, Remaining = 8 },
            new PlanRow("I2", "second", "acme/repo") { Estimate = 8, Remaining = 8 },
            new PlanRow("I3", "third", "acme/repo") { Estimate = 8, Remaining = 8 });
        return PlanCsvImportTests.Empty() with { Baseline = new(rows, []), State = new(rows, new() { DefaultRepository = "acme/repo" }) };
    }
    private static string Root(string name) => Path.Combine(TestContext.CurrentContext.WorkDirectory, name + "-" + Guid.NewGuid().ToString("N"));

    [Test, Category("PlanStoredPlanConflict")]
    public async Task RefusedSaveNamesTheOtherWriterAsTheCause()
    {
        var root = Root("conflict");
        try
        {
            var store = new PlanStore(root);
            var document = Document();
            var mine = await PlanSession.CreateAsync(store, document, Today);
            var other = (await PlanSession.OpenAsync(store, document.Project, Today)).Session!;
            Assert.That((await other.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 9m)]), Today)).Succeeded, Is.True);
            var save = await mine.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 25m)]), Today);
            Assert.That(save.Succeeded, Is.False);
            Assert.That(save.Failure, Is.EqualTo(PlanSaveFailure.ChangedFile));
            Assert.That(save.Error, Does.Contain("別のウィンドウまたは別のアプリ"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test, Category("PlanStoredPlanConflict")]
    public async Task ReapplyingTheStoredPlanKeepsBothWritersEdits()
    {
        var root = Root("reapply");
        try
        {
            var store = new PlanStore(root);
            var document = Document();
            var mine = await PlanSession.CreateAsync(store, document, Today);
            var other = (await PlanSession.OpenAsync(store, document.Project, Today)).Session!;
            Assert.That((await other.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 9m)]), Today)).Succeeded, Is.True);
            Assert.That((await mine.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 25m)]), Today)).Succeeded, Is.False);
            var undo = mine.UndoCount;
            var reconciled = await mine.ReapplyStoredAsync(Today);
            Assert.That(reconciled.Succeeded, Is.True, reconciled.Error);
            Assert.That(mine.Document.State.Rows.Single(r => r.Identity == "I1").Estimate, Is.EqualTo(9m), "the other writer's edit survives");
            Assert.That(mine.Document.State.Rows.Single(r => r.Identity == "I2").Estimate, Is.EqualTo(25m), "this window's edit is reapplied");
            Assert.That(mine.UndoCount, Is.EqualTo(undo), "the window keeps its own history");
            var reopened = (await PlanSession.OpenAsync(store, document.Project, Today)).Session!;
            Assert.That(reopened.Document.State.Rows.Single(r => r.Identity == "I1").Estimate, Is.EqualTo(9m));
            Assert.That(reopened.Document.State.Rows.Single(r => r.Identity == "I2").Estimate, Is.EqualTo(25m));
            Assert.That(reopened.Document.State.Settings.DefaultRepository, Is.EqualTo("acme/repo"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test, Category("PlanStoredPlanConflict")]
    public async Task SavingACopyKeepsThisWindowsVersionAndLeavesTheStoredPlan()
    {
        var root = Root("copy");
        var copyRoot = Root("copy-out");
        try
        {
            var store = new PlanStore(root);
            var document = Document();
            var mine = await PlanSession.CreateAsync(store, document, Today);
            var other = (await PlanSession.OpenAsync(store, document.Project, Today)).Session!;
            Assert.That((await other.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 9m)]), Today)).Succeeded, Is.True);
            Assert.That((await mine.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 25m)]), Today)).Succeeded, Is.False);
            var path = Path.Combine(copyRoot, "kept-plan.json");
            Directory.CreateDirectory(copyRoot);
            var copy = await mine.SaveCopyAsync(path);
            Assert.That(copy.Succeeded, Is.True, copy.Error);
            var kept = PlanJson.Read<PlanCheckpoint>(await File.ReadAllBytesAsync(path));
            Assert.That(kept.Document.State.Rows.Single(r => r.Identity == "I1").Estimate, Is.EqualTo(8m), "the copy is this window's version");
            Assert.That(kept.Document.State.Rows.Single(r => r.Identity == "I2").Estimate, Is.EqualTo(25m));
            var reopened = (await PlanSession.OpenAsync(store, document.Project, Today)).Session!;
            Assert.That(reopened.Document.State.Rows.Single(r => r.Identity == "I1").Estimate, Is.EqualTo(9m), "the stored plan is untouched");
            Assert.That(reopened.Document.State.Rows.Single(r => r.Identity == "I2").Estimate, Is.EqualTo(8m));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(copyRoot)) Directory.Delete(copyRoot, true);
        }
    }

    [Test, Category("PlanStoredPlanConflict")]
    public async Task UnreadableStoredPlanRefusesReapplyAndKeepsTheEdit()
    {
        var root = Root("unreadable");
        try
        {
            var store = new PlanStore(root);
            var document = Document();
            var mine = await PlanSession.CreateAsync(store, document, Today);
            var other = (await PlanSession.OpenAsync(store, document.Project, Today)).Session!;
            Assert.That((await other.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Estimate, 9m)]), Today)).Succeeded, Is.True);
            Assert.That((await mine.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Estimate, 25m)]), Today)).Succeeded, Is.False);
            File.Delete(store.FileFor(document.Project));
            var before = mine.Document; var undo = mine.UndoCount;
            var reconciled = await mine.ReapplyStoredAsync(Today);
            Assert.That(reconciled.Succeeded, Is.False);
            Assert.That(reconciled.Error, Does.Contain("読み込めません"));
            Assert.That(mine.Document, Is.SameAs(before));
            Assert.That(mine.UndoCount, Is.EqualTo(undo));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
