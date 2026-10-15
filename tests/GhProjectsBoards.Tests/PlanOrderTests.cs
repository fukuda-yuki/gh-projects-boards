using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanOrderTests
{
    private static readonly DateOnly Today = new(2026, 10, 15);

    [TestCase("ABCDEF", "ABCDEF", "")]
    [TestCase("ABCDEF", "CDEFAB", "AB")]
    [TestCase("ABCDEF", "EFABCD", "EF")]
    [TestCase("ABCD", "CDAB", "AB")]
    [TestCase("ABCDEFGH", "BACDFEGH", "AE")]
    [TestCase("ABCD", "AXB CYD", "")]
    [TestCase("ABCDE", "ACE", "")]
    [TestCase("", "XY", "")]
    public void OnlyMinimumMovedRowsHaveOrderChanges(string baseline, string current, string moved)
    {
        var document = Document(baseline, current.Replace(" ", ""));
        Assert.That(PlanOrder.Moved(baseline.Select(c => c.ToString()), current.Replace(" ", "").Select(c => c.ToString())),
            Is.EquivalentTo(moved.Select(c => c.ToString())));
        Assert.That(PlanOperations.Changes(document, Today).Fields.Where(p => p.Value.Contains(PlanField.Order)).Select(p => p.Key),
            Is.EquivalentTo(moved.Select(c => c.ToString())));
    }

    [TestCase(2)]
    [TestCase(3)]
    public void MovingRequirementDownCountsOnlyIts26Rows(int blocks)
    {
        var rows = Enumerable.Range(0, 40).SelectMany(b => Enumerable.Range(0, 26).Select(i =>
            new PlanRow($"R{b:D2}-{i}", "Task", "acme/repo") { Parent = i == 0 ? null : $"R{b:D2}-0" })).ToImmutableArray();
        var start = 6 * 26;
        var current = rows.Take(start).Concat(rows.Skip(start + 26).Take((blocks - 1) * 26))
            .Concat(rows.Skip(start).Take(26)).Concat(rows.Skip(start + blocks * 26)).ToImmutableArray();
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new(rows, []), new(current, new()));
        var changes = PlanOperations.Changes(document, Today);
        Assert.That(changes.Fields.Where(p => p.Value.Contains(PlanField.Order)).Select(p => p.Key),
            Is.EquivalentTo(rows.Skip(start).Take(26).Select(r => r.Identity)));
        Assert.That(changes.TaskCount, Is.EqualTo(26));
        Assert.That(changes.EnteredTaskCount, Is.EqualTo(26));
        Assert.That(changes.RecalculatedTaskCount, Is.Zero);
        var csvRows = System.Text.Encoding.UTF8.GetString(PlanCsvExport.Create(document, Today)).Split('\n').Skip(1)
            .Where(line => line.Contains("表示順")).ToArray();
        Assert.That(csvRows, Has.Length.EqualTo(26));
    }

    [TestCase("ABCDEF", "CDEFAB", "AB")]
    [TestCase("ABCDEF", "EFABCD", "EF")]
    [TestCase("ABCD", "CDAB", "AB")]
    [TestCase("ABCDEFGH", "BACDFEGH", "AE")]
    [TestCase("ABCD", "XC DABY", "XABY")]
    public void PositionWritesAndReviewAgreeAndReachPlanOrder(string baseline, string current, string moved)
    {
        current = current.Replace(" ", "");
        var document = Document(baseline, current);
        var remote = new PlanRemoteSnapshot(document.Baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0);
        var review = PlanPublishPlan.Build(document, remote, Today, Guid.NewGuid().ToString("N"));
        var writes = review.Writes.Where(w => w.Mutation == "updateProjectV2ItemPosition").ToArray();
        Assert.That(writes.Select(w => w.Identity), Is.EqualTo(moved.Select(c => c.ToString())));
        Assert.That(review.Changes.Where(c => c.Field == PlanField.Order).Select(c => c.Identity), Is.EqualTo(writes.Select(w => w.Identity)));
        foreach (var change in review.Changes.Where(c => c.Field == PlanField.Order))
        {
            var before = baseline.IndexOf(change.Identity, StringComparison.Ordinal);
            Assert.That(change.Before, Is.EqualTo(before < 0 ? null : before.ToString()));
            Assert.That(change.After, Is.EqualTo(current.IndexOf(change.Identity, StringComparison.Ordinal).ToString()));
        }
        var order = baseline.Select(c => c.ToString()).Concat(current.Except(baseline).Select(c => c.ToString())).ToList();
        foreach (var write in writes)
        {
            using var input = JsonDocument.Parse(write.Input);
            var index = current.IndexOf(write.Identity, StringComparison.Ordinal);
            var after = input.RootElement.GetProperty("afterId").GetString();
            Assert.That(after, Is.EqualTo(index == 0 ? null : "item:" + current[index - 1]));
            order.Remove(write.Identity);
            order.Insert(after is null ? 0 : order.IndexOf(after[5..]) + 1, write.Identity);
        }
        Assert.That(order, Is.EqualTo(current.Select(c => c.ToString())));
        Assert.That(PlanPublishPlan.Batches(writes).All(b => b.Length == 1), Is.True);
        PlanPublishPlan.Validate(new(Guid.NewGuid().ToString("N"), review.Writes), document);
    }

    private static PlanDocument Document(string baseline, string current)
    {
        PlanRow Row(char id) => new(id.ToString(), id.ToString(), "acme/repo");
        return new(new(new("github.com", 42), "P1"), new(baseline.Select(Row).ToImmutableArray(), []),
            new(current.Select(Row).ToImmutableArray(), new()));
    }
}
