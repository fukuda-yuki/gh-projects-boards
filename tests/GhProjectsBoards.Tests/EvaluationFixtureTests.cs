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
            Assert.That(session.Document.State.Rows.Length, Is.EqualTo(1040), "40 requirement Issues are additional to the 1,000 executable tasks.");
            Assert.That(session.Document.State.Settings.People.Length, Is.EqualTo(20));
            Assert.That(session.Changes(EvaluationFixture.Today).TaskCount, Is.Zero);
            Assert.That(session.Document.Sync.NativeOrders["I1"], Is.EqualTo(session.Document.State.Rows.Where(r => r.Parent == "I1").Select(r => r.Identity)));
            var publisher = new PlanPublisher(workspace.Service!, workspace.Context!);
            Assert.That((await publisher.RefreshAsync(session, EvaluationFixture.Today)).Succeeded, Is.True);
            Assert.That(session.Changes(EvaluationFixture.Today).TaskCount, Is.Zero);
            Assert.That(session.Document.Sync.NativeOrders["I1"], Is.EqualTo(session.Document.State.Rows.Where(r => r.Parent == "I1").Select(r => r.Identity)));
            var rows = session.Document.State.Rows;
            var requirements = rows.Where(r => r.Parent is null).ToArray();
            Assert.That(requirements.Length, Is.EqualTo(40));
            Assert.That(requirements.All(p => rows.Count(r => r.Parent == p.Identity) == 25), Is.True);
            Assert.That(rows.Where(r => r.Parent is not null).All(r => r.Assignees.Length == 1 && r.Estimate > 0), Is.True);
            var horizon = PlanPeople.Calculate(session.Document, EvaluationFixture.Today, EvaluationFixture.Today, PlanPeriodScale.Day, 210);
            var people = horizon.People.Where(p => p.Identity.StartsWith("U")).ToArray();
            var days = people.SelectMany(p => p.Periods).Where(p => p.Capacity > 0 && p.Planned > 0).ToArray();
            Assert.That(people.Any(p => p.Difference < 0), Is.True, "At least one person exceeds their total allowance.");
            Assert.That(people.Any(p => p.Difference >= 0 && p.Periods.Count(d => d.Overloaded) == 1), Is.True);
            Assert.That(people.SelectMany(p => p.Periods.Select((d, n) => (d, n))).Any(x => x.n >= 60 && x.d.Planned > 0), Is.True);
            Assert.That(people.All(p => p.Missing.Count == 0 && p.Unallocated.Count == 0), Is.True);
            var report = PlanPeople.Calculate(session.Document, EvaluationFixture.Today, new(2026, 10, 14), PlanPeriodScale.Day, 1);
            var person = report.People.Single(p => p.Identity == "U1");
            Assert.That(person.Periods[0].Overloaded, Is.True);
            Assert.That(person.Difference, Is.GreaterThanOrEqualTo(0));
            Assert.That(rows.Any(r => r.Parent != null) && rows.Any(r => !r.Predecessors.IsEmpty), Is.True);
            var weeks = PlanPeople.Calculate(session.Document, EvaluationFixture.Today, new(2026, 10, 12), PlanPeriodScale.Week, 29);
            var ratios = weeks.Periods.Select((period, n) => new {
                period.Start, Planned = weeks.People.Sum(p => p.Periods[n].Planned ?? 0), Capacity = weeks.People.Sum(p => p.Periods[n].Capacity ?? 0)
            }).ToArray();
            foreach (var week in ratios) TestContext.Out.WriteLine($"{week.Start}: {week.Planned}/{week.Capacity} hours ({week.Planned / week.Capacity:P1})");
            Assert.That(ratios.Count(w => w.Planned >= w.Capacity * .8m && w.Planned <= w.Capacity), Is.GreaterThanOrEqualTo(22), "Most of the 29 displayed weeks must exercise near-capacity planning; report the entire tail too.");
            Assert.That(weeks.People.Single(p => p.Identity == "U1").Periods[0].Percent, Is.LessThanOrEqualTo(100), "Daily overload must remain meaningful when the weekly average is not overloaded.");
            TestContext.Out.WriteLine($"Overloaded active working person-days: {days.Count(p => p.Overloaded)}/{days.Length}; over-allowance people: {people.Count(p => p.Difference < 0)}; single-day overload within allowance: {people.Count(p => p.Difference >= 0 && p.Periods.Count(d => d.Overloaded) == 1)}");
            await workspace.Flush();
            Assert.ThrowsAsync<IOException>(async () => await EvaluationFixture.Create(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
