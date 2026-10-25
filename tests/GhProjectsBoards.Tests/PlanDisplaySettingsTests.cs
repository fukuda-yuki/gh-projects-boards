using System.Collections.Immutable;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

// #251 D1: the lateness mark switches are this PC's display preference, carried over once from the first
// Project opened while none is stored; plans and settings files no longer hold them.
[TestFixture]
internal sealed class PlanDisplaySettingsTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly ScopedId First = new(new("github.com", 1), "P1"), Second = new(new("github.com", 1), "P2");
    private string root = "";
    [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ghpb-display-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TearDown] public void Cleanup() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private static PlanDocument Document(ScopedId project, params PlanRow[] rows) => new(project,
        new(rows.ToImmutableArray(), []), new(rows.ToImmutableArray(), new() { StatusDate = Day }));

    // Writes a stored plan as the earlier version did, with the switches inside the Project settings.
    private async Task StoreLegacyPlan(PlanStore store, ScopedId project, bool overdue, bool later, bool overEstimate)
    {
        var session = await PlanSession.CreateAsync(store, Document(project, new PlanRow("I1", "Task", "a/b") { Remaining = 8 }), Day);
        Assert.That((await session.FlushAsync()).Succeeded, Is.True);
        var path = store.FileFor(project);
        var json = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        var settings = json["document"]!["state"]!["settings"]!.AsObject();
        settings["showOverdueMark"] = overdue; settings["showLaterMark"] = later; settings["showOverEstimateMark"] = overEstimate;
        await File.WriteAllTextAsync(path, json.ToJsonString());
    }

    [Test]
    public async Task TheFirstOpenedProjectsSwitchesAreCarriedOverOnceAndThePlanStillOpens()
    {
        var store = new PlanStore(root);
        await StoreLegacyPlan(store, First, overdue: false, later: true, overEstimate: false);
        await StoreLegacyPlan(store, Second, overdue: true, later: false, overEstimate: true);
        var display = new PlanDisplaySettingsStore(store);

        var carried = await display.LoadAsync(First);
        Assert.That(carried.Marks, Is.EqualTo(new PlanMarkVisibility(Overdue: false, Later: true, OverEstimate: false)));
        Assert.That(File.Exists(display.FilePath), Is.True, "The carried-over preference is stored for every Project.");

        // A second Project's earlier switches no longer apply: the PC preference is already stored.
        Assert.That((await display.LoadAsync(Second)).Marks, Is.EqualTo(carried.Marks));
        Assert.That((await new PlanDisplaySettingsStore(new PlanStore(root)).LoadAsync(Second)).Marks, Is.EqualTo(carried.Marks), "It survives restart.");

        var loaded = await store.LoadAsync(Second);
        Assert.That(loaded.Status, Is.EqualTo(PlanLoadStatus.Loaded), "A plan that still holds the switches opens, without them.");
        Assert.That(loaded.Checkpoint!.Document.State.Rows.Single().Title, Is.EqualTo("Task"));
    }

    [Test]
    public async Task WithoutAStoredPlanOrPreferenceEveryMarkIsShownAndAChangeIsKept()
    {
        var store = new PlanStore(root);
        var display = new PlanDisplaySettingsStore(store);
        Assert.That((await display.LoadAsync(First)).Marks, Is.EqualTo(PlanMarkVisibility.All));

        var hidden = new PlanDisplaySettings(1, new(Later: false));
        await display.SaveAsync(hidden);
        Assert.That((await new PlanDisplaySettingsStore(store).LoadAsync(Second)), Is.EqualTo(hidden));
    }

    [Test]
    public async Task ASettingsFileThatStillHoldsTheSwitchesImportsWithoutThem()
    {
        var store = new PlanStore(root);
        var session = await PlanSession.CreateAsync(store, Document(First, new PlanRow("I1", "Task", "a/b")), Day);
        var file = Path.Combine(root, "settings.json");
        await session.ExportSettingsAsync(file);
        var json = JsonNode.Parse(await File.ReadAllBytesAsync(file))!;
        json["settings"]!["showLaterMark"] = false;
        json["settings"]!["companyDaysOff"] = new JsonArray("2026-10-06");
        await File.WriteAllTextAsync(file, json.ToJsonString());

        var imported = await session.ImportSettingsAsync(file, Day);

        Assert.That(imported.Applied, Is.True, imported.Error);
        Assert.That(session.Document.State.Settings.CompanyDaysOff, Is.EqualTo(new[] { new DateOnly(2026, 10, 6) }));
        Assert.That(await File.ReadAllTextAsync(file), Does.Contain("showLaterMark"));
        await session.ExportSettingsAsync(file);
        Assert.That(await File.ReadAllTextAsync(file), Does.Not.Contain("Mark"), "Project settings no longer carry the switches.");
        await session.FlushAsync();
    }

    [Test]
    public void TheSettingsPeopleTableCountsEachAssigneesOpenLeafTasksAndTheirRemaining()
    {
        var document = Document(First,
            new PlanRow("R", "Requirement", "a/b") { Assignees = ["U1"] },
            new PlanRow("T1", "Open", "a/b") { Parent = "R", Assignees = ["U1", "U2"], Remaining = 8 },
            new PlanRow("T2", "Open too", "a/b") { Parent = "R", Assignees = ["U1"], Remaining = 4 },
            new PlanRow("T3", "Done", "a/b") { Parent = "R", Assignees = ["U1"], Remaining = 0, Closed = true },
            new PlanRow("T4", "Cancelled", "a/b") { Parent = "R", Assignees = ["U1"], Remaining = 16, Cancelled = true, Closed = true },
            new PlanRow("T5", "No remaining", "a/b") { Parent = "R", Assignees = ["U2"] });

        var work = PlanPeople.Work(document, PlanOperations.Schedule(document, Day)).ToDictionary(w => w.Person.Identity);

        Assert.That((work["U1"].Tasks, work["U1"].Remaining), Is.EqualTo((2, 12m)), "A requirement, a completed and a cancelled task do not count.");
        Assert.That((work["U2"].Tasks, work["U2"].Remaining), Is.EqualTo((2, (decimal?)null)), "A shared task counts for each assignee; a missing 残 leaves the total unknown.");
    }
}
