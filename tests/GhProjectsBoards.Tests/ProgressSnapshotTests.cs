using System.Collections.Immutable;
using System.Text;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Reports;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture, Category("Integration"), NonParallelizable]
internal sealed class ProgressSnapshotTests
{
    private string root = "";
    private PlanWorkspace workspace = null!;
    private EvaluationFixture.VersionPlan plan = null!;
    private static readonly DateTimeOffset ReadAt = new(2027, 1, 13, 21, 5, 0, TimeSpan.Zero);

    private async Task OpenEvaluation(string version)
    {
        root = Path.Combine(Path.GetTempPath(), "ghpb-progress-" + Guid.NewGuid().ToString("N"));
        await EvaluationFixture.Create(root, version);
        plan = EvaluationFixture.ReadPlan(version);
        workspace = new PlanWorkspace(new PlanStore(root), () => plan.StatusDate);
        await workspace.Connect(Service());
        await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
    }
    private GhConnectionService Service() => new(GhProcessTests.FakeExecutable, "github.com",
        new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = Path.Combine(root, "fake-gh") }));
    private async Task<ProjectPlanSettings> ExportedSettings()
    {
        var path = Path.Combine(root, "settings.json");
        await workspace.Session!.ExportSettingsAsync(path);
        return ProgressSnapshotReader.ReadSettings(await File.ReadAllBytesAsync(path));
    }
    private Task<ProgressSnapshot> Snapshot(ProjectPlanSettings settings) => ProgressSnapshotReader.ReadAsync(Service(),
        workspace.Selected!.Url, settings, plan.StatusDate, TimeZoneInfo.Local, () => ReadAt);
    private string[] Mutations() => File.ReadAllLines(Path.Combine(root, "fake-gh", "calls.jsonl"))
        .Where(line => line.Contains("\"mutation\":true", StringComparison.Ordinal)).ToArray();

    [TearDown]
    public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestCase("2027.04"), TestCase("2027.10")]
    public async Task SnapshotFromGitHubAndExportedSettingsEqualsTheAppSummary(string version)
    {
        await OpenEvaluation(version);
        var document = workspace.Session!.Document;
        var scheduled = PlanOperations.Schedule(document, plan.StatusDate);
        var summary = PlanSummary.Calculate(document, scheduled, plan.StatusDate);
        var issue = document.Sync.IssueLinks.ToDictionary(p => p.Key, p => p.Value.Caption);
        string[] Issues(IEnumerable<string> identities) => identities.Select(id => issue[id]).Order(StringComparer.Ordinal).ToArray();

        var snapshot = await Snapshot(await ExportedSettings());

        Assert.That(summary.Attention.Late, Is.Not.Empty, "The evaluation has late work to compare.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Tasks.Select(t => (t.Issue, t.ForecastEnd)),
                Is.EquivalentTo(scheduled.Where(t => !t.IsSummary).Select(t => (issue[t.Input.Identity], t.ForecastEnd))));
            Assert.That(snapshot.Lateness.Late.Order(StringComparer.Ordinal), Is.EqualTo(Issues(summary.Attention.Late)));
            Assert.That(snapshot.Lateness.Cause.Order(StringComparer.Ordinal), Is.EqualTo(Issues(summary.Attention.Cause)));
            Assert.That(snapshot.Lateness.OverEstimate.Order(StringComparer.Ordinal), Is.EqualTo(Issues(summary.Attention.OverEstimate)));
            Assert.That(snapshot.ForecastFinish, Is.EqualTo(summary.ForecastFinish));
            Assert.That(snapshot.FinishDifference, Is.EqualTo(summary.FinishDifference));
            Assert.That(snapshot.Phases.Select(p => (p.Phase, p.Target, p.ForecastFinish, p.Difference, p.Effort.Tasks, p.Effort.Remaining)),
                Is.EqualTo(summary.Phases.Where(p => !p.IsTotal).Select(p => (p.Phase, p.Target, p.Tasks.Finish, p.Difference, p.Tasks.Count, p.Tasks.Remaining.Value))));
            Assert.That(snapshot.Requirements, Has.Length.EqualTo(40));
            Assert.That(snapshot.StatusDate, Is.EqualTo(plan.StatusDate));
            Assert.That(snapshot.Project.Title, Is.EqualTo(plan.Title));
        }
        Assert.That(Mutations(), Is.Empty, "The snapshot only reads GitHub.");
    }

    [Test]
    public async Task EachLateTaskKeepsWhichCaseApplies()
    {
        await OpenEvaluation("2027.04");
        var snapshot = await Snapshot(await ExportedSettings());
        var late = snapshot.Tasks.Where(t => t.Late is not null).ToArray();
        Assert.That(late.Select(t => t.Issue), Is.EquivalentTo(snapshot.Lateness.Late));
        foreach (var task in late)
        {
            if (task.Late!.Days is { } days)
                Assert.That((days, task.ForecastEnd > task.End), Is.EqualTo((Math.Max(days, 1), true)), "days counts how far 見込終了 is past 終了日.");
            switch (task.Late.Kind)
            {
                case "finish": Assert.That(task.Late.Date, Is.EqualTo(task.End).And.LessThan(snapshot.StatusDate)); break;
                case "start": Assert.That((task.Late.Date, task.Actual ?? 0), Is.EqualTo((task.Start, 0m))); break;
                case "forecast":
                    Assert.That(task.Late.Date, Is.EqualTo(task.ForecastEnd).And.GreaterThan(task.End!.Value));
                    Assert.That(task.Late.Days, Is.Positive);
                    break;
                default: Assert.Fail("Unknown kind " + task.Late.Kind); break;
            }
        }
        Assert.That(late.Select(t => t.Late!.Kind).Distinct(), Does.Contain("finish").And.Contain("forecast"));
        Assert.That(late.Where(t => t.Late!.Kind != "forecast").Select(t => t.Late!.Days), Has.Some.Not.Null,
            "An overdue task also states how far its 見込終了 is past its 終了日, the order of 遅れの原因.");
    }

    [Test]
    public async Task SettingsMappingAFieldTheProjectLacksFailsWithoutWriting()
    {
        await OpenEvaluation("2027.04");
        var settings = await ExportedSettings();
        var missing = settings with { Columns = settings.Columns.Add(new PlanColumnMapping("Extra", "F-missing", "存在しない列", "NUMBER")) };

        var failure = Assert.ThrowsAsync<InvalidOperationException>(() => Snapshot(missing));

        Assert.That(failure!.Message, Does.Contain("存在しない列"));
        Assert.That(Mutations(), Is.Empty);
    }

    [Test]
    public async Task SnapshotJsonRoundTripsUnescapedAndRejectsOtherSchemas()
    {
        await OpenEvaluation("2027.04");
        var snapshot = await Snapshot(await ExportedSettings());

        var json = snapshot.ToJson();
        var text = Encoding.UTF8.GetString(json);
        var read = ProgressSnapshot.Read(json);

        Assert.That(text, Does.Contain(plan.Title).And.Contain("\"statusDate\": \"2027-01-13\"").And.Contain("\"readAt\": \"2027-01-13T21:05:00+00:00\""));
        Assert.That(text, Does.Not.Contain("PVT_").And.Not.Contain("\"rate\""), "Node IDs and rates stay out of the snapshot.");
        Assert.That(read.ToJson(), Is.EqualTo(json));
        Assert.That(() => ProgressSnapshot.Read(Encoding.UTF8.GetBytes(text.Replace(ProgressSnapshot.SchemaV1, "ghpb-progress-snapshot/2"))),
            Throws.ArgumentException.With.Message.Contains("ghpb-progress-snapshot/2"));
        Assert.That(() => ProgressSnapshot.Read(Encoding.UTF8.GetBytes(text.Replace("\"schema\":", "\"extra\": 1, \"schema\":"))),
            Throws.InstanceOf<System.Text.Json.JsonException>());
    }
}
