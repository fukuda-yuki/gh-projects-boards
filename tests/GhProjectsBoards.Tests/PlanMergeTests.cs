using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanMergeTests
{
    [Test]
    public void RefreshKeepsANewLocalRowAtItsInsertionAnchor()
    {
        var a = new PlanRow("A", "A", "acme/repo"); var b = new PlanRow("B", "B", "acme/repo"); var local = PlanRow.New("New", "acme/repo");
        var baseline = new PlanBaseline([a, b], []);
        var document = new PlanDocument(new(new("github.com", 42), "P1"), baseline, new([a, local, b], new()));
        Assert.That(PlanMerge.Merge(document, baseline).State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "A", local.Identity, "B" }));
    }
    [TestCase("A,B,C", "B,A,C", "B,A,C", false)]
    [TestCase("C,A,B", "A,B,C", "C,A,B", false)]
    [TestCase("C,A,B", "B,A,C", "C,A,B", true)]
    public void NativeSiblingOrderMergesWithoutReplacingTheOtherProjectRows(string local, string remote, string expected, bool conflict)
    {
        var rows = new[] { "P", "A", "B", "C" }.Select(id => new PlanRow(id, id, "acme/repo") { Parent = id == "P" ? null : "P" }).ToImmutableArray();
        var baseline = new PlanBaseline(rows, []);
        var state = new PlanState(new[] { "P" }.Concat(local.Split(',')).Select(id => rows.Single(r => r.Identity == id)).ToImmutableArray(), new());
        var document = new PlanDocument(new(new("github.com", 42), "P1"), baseline, state)
            { Sync = new() { NativeOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["A", "B", "C"]) } };
        var result = PlanMerge.NativeOrder(document, PlanMerge.Merge(document, baseline),
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", remote.Split(',').ToImmutableArray()));
        Assert.That(result.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P" }.Concat(expected.Split(','))));
        Assert.That(result.Sync.Conflicts.Any(c => c.Field == PlanField.SubIssueOrder), Is.EqualTo(conflict));
    }
    [TestCase("B", "B", "R", "R", false)]
    [TestCase("B", "L", "B", "L", false)]
    [TestCase("B", "L", "L", "L", false)]
    [TestCase("B", "L", "R", "L", true)]
    public void RefreshMergesEachFieldWithoutDiscardingLocalWork(string baseline, string local, string remote, string expected, bool conflict)
    {
        var b = new PlanRow("I1", baseline, "acme/repo");
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([b], []), new([b with { Title = local }], new()));
        var result = PlanMerge.Merge(document, new([b with { Title = remote }], []));
        Assert.Multiple(() =>
        {
            Assert.That(result.State.Rows.Single().Title, Is.EqualTo(expected));
            Assert.That(result.Sync.Conflicts.Any(c => c.Field == PlanField.Title), Is.EqualTo(conflict));
            Assert.That(result.Baseline.Rows.Single().Title, Is.EqualTo(remote));
        });
    }
}
