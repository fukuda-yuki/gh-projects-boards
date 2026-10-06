using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture, Category("Integration")]
internal sealed class EvaluationFixtureTests
{
    [Test]
    public async Task FreshEvaluationOpensAndRefreshesWithNoUnpublishedTasksAndDailyOverload()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-evaluation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await EvaluationFixture.Create(root);
            var workspace = new PlanWorkspace(new PlanStore(root));
            var runner = new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = Path.Combine(root, "fake-gh") });
            await workspace.Connect(new GhConnectionService(GhProcessTests.FakeExecutable, "github.com", runner));
            await workspace.Open(workspace.Available.First());
            var session = workspace.Session!;
            Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1000));
            Assert.That(session.Document.State.Settings.People.Length, Is.EqualTo(20));
            Assert.That(session.Changes(EvaluationFixture.Today).TaskCount, Is.Zero);
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            Assert.That((await publisher.RefreshAsync(session, EvaluationFixture.Today)).Succeeded, Is.True);
            Assert.That(session.Changes(EvaluationFixture.Today).TaskCount, Is.Zero);
            var report = PlanPeople.Calculate(session.Document, EvaluationFixture.Today, EvaluationFixture.Today, PlanPeriodScale.Day, 1);
            var person = report.People.Single(p => p.Identity == "U1");
            Assert.That(person.Periods[0].Overloaded, Is.True);
            Assert.That(person.Difference, Is.GreaterThanOrEqualTo(0));
            var rows = session.Document.State.Rows;
            Assert.That(rows.Any(r => r.Parent != null) && rows.Any(r => !r.Predecessors.IsEmpty), Is.True);
            Assert.That(rows.Any(r => r.Fixed) && rows.Any(r => r.StartNoEarlierThan != null) && rows.Any(r => r.Closed), Is.True);
            Assert.That(rows.Any(r => r.Assignees.IsEmpty) && rows.Any(r => r.Assignees.Length > 1), Is.True);
            await workspace.Flush();
            Assert.ThrowsAsync<IOException>(async () => await EvaluationFixture.Create(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
