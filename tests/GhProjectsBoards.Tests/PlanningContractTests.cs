using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanningContractTests
{
    [Test]
    public void NewCheckpointVersionsPlanningWithoutInventingAPlan()
    {
        var work = new EditingWorkspace(new("github.com", 42));
        Assert.That(work.Snapshot().Version, Is.EqualTo(9));
        Assert.That(work.Snapshot().Planning, Is.Empty);
    }

    internal static ProjectPlanning Plan() => new(1, "P1", 0, At("2026-10-05 09:00"), At("2026-10-09 18:00"),
        [new("Estimate", "F-estimate", "NUMBER"), new("Start", "F-start", "DATE")],
        new("calendar-one", PlanningContract.BundledHolidays(), false, [new(new(2026, 10, 12), null, [new(540, 780)]), new(new(2026, 10, 12), "U1", [])]),
        [new("U1", "PMO", 80)],
        [new("I1", PlanningMode.Manual, "U1", At("2026-10-05 12:07"), At("2026-10-06 16:19"), PlanningProgress.InProgress,
            ActualStart: At("2026-10-05 09:17"), Actuals: [new("U1", 1.23456789m, new(2026, 10, 5)), new(null, 0m, new(2026, 10, 5))],
            Contributions: [new("U1", 16m, 3.125m)], LocalLinks: [new("external-I", "SS")])]);
    internal static DateTime At(string v) => DateTime.SpecifyKind(DateTime.ParseExact(v, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified);

    [Test]
    public async Task ActualCheckpointExportAndSecondRootRestorePreserveManualCalendarAttributionAndPendingWork()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "planning-restore-" + Guid.NewGuid().ToString("N"));
        var p = EditingTests.Registration(); var w = new EditingWorkspace(p.Snapshot.Id.Scope);
        w.SetRegistrations([p]); var rows = w.Open(p); w.Commit("P1", rows[0].Cells[0], "Changed title"); w.SetBuffer(rows[0].Cells[0], "pending 日本語");
        var local = w.AddRow(p); w.SetPlanning(Plan() with { Tasks = Plan().Tasks.Append(new(local)).ToArray() }, 0);
        var store = new DraftStore(root); var session = new DraftSession(store, w, 0);
        Assert.That(await session.FlushAsync(), Is.True);
        var baseline = await store.LoadAsync(w.Scope); var backup = Path.Combine(root, "portable.json");
        await store.ExportBackupAsync(w.Scope, backup);
        var destination = new DraftStore(root + "-second-profile"); await destination.RestoreBackupAsync(backup, w.Scope);
        var reopened = await destination.LoadAsync(w.Scope);
        Assert.That(JsonSerializer.Serialize(reopened), Is.EqualTo(JsonSerializer.Serialize(baseline)));
        var restored = EditingWorkspace.Restore(reopened!);
        Assert.That(restored.Planning("P1")!.Tasks[0].Mode, Is.EqualTo(PlanningMode.Manual));
        Assert.That(restored.Buffer(restored.Open(p)[0].Cells[0]), Is.EqualTo("pending 日本語"));
        Assert.That((await new RegistrationStore(root + "-second-profile").LoadAsync()).Registrations.Single().Snapshot.Id, Is.EqualTo(p.Snapshot.Id));
        Assert.ThrowsAsync<InvalidOperationException>(() => destination.RestoreBackupAsync(backup, w.Scope));
        Assert.ThrowsAsync<InvalidDataException>(() => new DraftStore(root + "-wrong-account").RestoreBackupAsync(backup, new("github.com", 99)));
        restored.SetPlanning(restored.Planning("P1")! with { Cutoff = At("2026-10-16 18:00") }, restored.Planning("P1")!.Stamp);
        await destination.SaveAsync(restored.Snapshot(), reopened!.Revision);
        Assert.ThrowsAsync<InvalidDataException>(() => destination.SaveAsync(reopened, reopened.Revision));
        Assert.That((await destination.LoadAsync(w.Scope))!.Planning![0].Cutoff, Is.EqualTo(At("2026-10-16 18:00")));
    }

    [Test]
    public async Task LegacyAndUnknownVersionsDoNotInventOrResetPlanning()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "planning-version-" + Guid.NewGuid().ToString("N"));
        var w = new EditingWorkspace(new("github.com", 42)); var store = new DraftStore(root);
        var legacy = w.Snapshot() with { Version = 7, Planning = null };
        await store.SaveAsync(legacy, 0);
        w = EditingWorkspace.Restore((await store.LoadAsync(w.Scope))!);
        Assert.That(w.Planning("P1"), Is.Null);
        w.SetPlanning(Plan(), 0); await store.SaveAsync(w.Snapshot(), 0);
        var bytes = await File.ReadAllBytesAsync(store.FileFor(w.Scope));
        Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(w.Snapshot() with { Planning = [Plan() with { Version = 2 }] }, w.Revision));
        Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(w.Snapshot() with { Version = 10 }, w.Revision));
        Assert.That(await File.ReadAllBytesAsync(store.FileFor(w.Scope)), Is.EqualTo(bytes));
        Assert.Throws<InvalidOperationException>(() => w.SetPlanning(Plan(), 0));
        foreach (var unknown in new[] { w.Snapshot() with { Version = 10 }, w.Snapshot() with { Planning = [Plan() with { Version = 2 }] } })
        {
            var raw = JsonSerializer.Serialize(unknown);
            await File.WriteAllTextAsync(store.FileFor(w.Scope), raw);
            Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(w.Scope));
            Assert.That((await store.CheckpointsAsync()).Problems.Single().Kind, Is.EqualTo("InvalidCheckpoint"));
            Assert.ThrowsAsync<InvalidDataException>(() => new DraftStore(root + "-unknown-restore").RestoreBackupAsync(store.FileFor(w.Scope), w.Scope));
            Assert.That(await File.ReadAllTextAsync(store.FileFor(w.Scope)), Is.EqualTo(raw));
        }
    }

    [TestCase("0", "0"), TestCase("16.000", "16"), TestCase("0.125", "0.125"), TestCase("1.23456789", "1.23456789")]
    public void RawHoursKeepExactValueAndProjection(string input, string expected)
        => Assert.That(PlanningContract.CanonicalHours(PlanningContract.ParseHours(input)), Is.EqualTo(expected));

    [TestCase("NaN"), TestCase("Infinity"), TestCase("-1"), TestCase("0.123456789"), TestCase("1000000001")]
    [TestCase("0.00000000000000000000000000001"), TestCase("16.00000000000000000000000000001")]
    public void InvalidHoursAreRejectedInsteadOfRounded(string input)
        => Assert.Throws<InvalidOperationException>(() => PlanningContract.ParseHours(input));

    [Test]
    public void ExcessiveFloatPrecisionRemainsRawAndCannotBePublishedLossily()
    {
        var hours = PlanningContract.ParseHours("999999999.12345678");
        Assert.That(hours, Is.EqualTo(999999999.12345678m));
        Assert.That(PlanningContract.CanPublishHours(hours), Is.False);
        Assert.That(PlanningContract.CanPublishHours(999999.12345678m), Is.True);
    }

    [Test]
    public void ChangedRemoteDateRequiresAnExplicitTimeDecisionAndDoesNotRewriteManual()
    {
        var task = Plan().Tasks[0];
        Assert.That(PlanningContract.RequiresDateDecision("2026-10-05", task.ManualStart, "2026-10-05"), Is.False);
        Assert.That(PlanningContract.RequiresDateDecision("2026-10-05", At("2026-10-06 12:07"), "2026-10-05"), Is.False, "Unpublished local dates are not remote changes.");
        Assert.That(PlanningContract.RequiresDateDecision("2026-10-05", task.ManualStart, "2026-10-06"), Is.True);
        Assert.That(PlanningContract.RequiresDateDecision("2026-10-05", task.ManualStart, null), Is.True);
        Assert.That(task.ManualStart, Is.EqualTo(At("2026-10-05 12:07")));
        Assert.That(task.Mode, Is.EqualTo(PlanningMode.Manual));
    }

    [Test]
    public void OfficialPresetKeepsEveryListedHolidayIncludingUnnamedSubstitutes()
    {
        var h = PlanningContract.BundledHolidays();
        Assert.That(h.Dates, Has.Length.EqualTo(54));
        Assert.That(h.Dates.Select(d => d.Date), Does.Contain(new DateOnly(2026, 9, 22)).And.Contain(new DateOnly(2026, 5, 6)));
        Assert.That(h.FirstYear, Is.EqualTo(2025)); Assert.That(h.LastYear, Is.EqualTo(2027));
        Assert.That(h.SourceSha256, Is.EqualTo("CEC37A743C96995CDB9CB52B685C9003634682A9B0E1A640A6B9B96881FE964A"));
    }

    [TestCase(true), TestCase(false)]
    public async Task DamagedMetadataIsDiagnosedWithoutCrashingOrResettingOtherProfiles(bool task)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "planning-damage-" + Guid.NewGuid().ToString("N"));
        var w = new EditingWorkspace(new("github.com", 42)); w.SetRegistrations([]); w.SetPlanning(Plan(), 0);
        var store = new DraftStore(root); await store.SaveAsync(w.Snapshot(), 0);
        var malformed = w.Snapshot() with { Planning = [task ? Plan() with { Tasks = [null!] } : Plan() with { Calendar = Plan().Calendar with { Exceptions = [null!] } }] };
        var bytes = JsonSerializer.Serialize(malformed); await File.WriteAllTextAsync(store.FileFor(w.Scope), bytes);
        var other = new EditingWorkspace(new("github.com", 99)); other.SetRegistrations([]); await store.SaveAsync(other.Snapshot(), 0);
        var loaded = await store.CheckpointsAsync();
        Assert.That(loaded.Problems.Single().Kind, Is.EqualTo("InvalidCheckpoint"));
        Assert.That(loaded.Records.Single().Scope, Is.EqualTo(other.Scope));
        Assert.That(await File.ReadAllTextAsync(store.FileFor(w.Scope)), Is.EqualTo(bytes));
    }

    [Test]
    public async Task BackupWithPlanningPreservesVerifiedAndUncertainCreationWithoutReplay()
    {
        var h = await CreationHarness.Create(); var known = h.Add(); await h.Apply(known);
        h.LoseCreate = true; var uncertain = h.Add(); await h.Apply(uncertain);
        var w = h.Session.Workspace; w.SetPlanning(Plan(), 0); Assert.That(await h.Session.FlushAsync(), Is.True);
        var before = w.Snapshot(); var backup = Path.Combine(h.Existing.Root, "planning-backup.json"); var store = new DraftStore(h.Existing.Root);
        await store.ExportBackupAsync(w.Scope, backup);
        var target = new DraftStore(h.Existing.Root + "-restored"); await target.RestoreBackupAsync(backup, w.Scope);
        var reopened = EditingWorkspace.Restore((await target.LoadAsync(w.Scope))!);
        Assert.That(reopened.Creations.Single(c => c.LocalId == known).Completed, Is.True);
        Assert.That(reopened.Creations.Single(c => c.LocalId == uncertain).Dispatched, Is.True);
        Assert.That(reopened.Creations.Single(c => c.LocalId == uncertain).Verified, Is.Null);
        Assert.That(reopened.CreationLocked(uncertain), Is.True);
        Assert.That(JsonSerializer.Serialize(reopened.Snapshot().Journal), Is.EqualTo(JsonSerializer.Serialize(before.Journal)));
    }

    [TestCase("Mode"), TestCase("WeightPercent"), TestCase("ManualStart")]
    public async Task MissingPersistedMetadataCannotUseNewInputDefaults(string missing)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "planning-missing-" + Guid.NewGuid().ToString("N"));
        var w = new EditingWorkspace(new("github.com", 42)); w.SetPlanning(Plan(), 0);
        var store = new DraftStore(root); await store.SaveAsync(w.Snapshot(), 0);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.FileFor(w.Scope)))!;
        var target = json["Planning"]![0]![missing == "WeightPercent" ? "People" : "Tasks"]![0]!.AsObject(); target.Remove(missing);
        var raw = json.ToJsonString(); await File.WriteAllTextAsync(store.FileFor(w.Scope), raw);
        Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(w.Scope));
        Assert.That((await store.CheckpointsAsync()).Problems.Single().Kind, Is.EqualTo("InvalidCheckpoint"));
        Assert.That(await File.ReadAllTextAsync(store.FileFor(w.Scope)), Is.EqualTo(raw));
    }
}
