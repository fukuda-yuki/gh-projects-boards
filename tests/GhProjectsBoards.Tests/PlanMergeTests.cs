using System.Collections.Immutable;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanMergeTests
{
    [TestCase("PCGXYZ", "PCGXYZAB", "PCGABXYZ", "AB")]
    [TestCase("PXCYGZUV", "PXCYGZUVAB", "PXCYGABZUV", "AB")]
    [TestCase("PCGXYZ", "PCGAXYZB", "PCGABXYZ", "B")]
    [TestCase("PCGXYZ", "UPCGXYZ", "UPCGXYZ", "")]
    [TestCase("PCGWXYZ", "PCGWXYZABQ", "PCGAQBWXYZ", "ABQ")]
    [TestCase("PCGWXYZ", "PCGWXYZQBA", "PCGBAQWXYZ", "AQB")]
    [TestCase("PCGXYZ", "XYZPCG", "XYZPCG", "")]
    [TestCase("PCX", "PCXAB", "PCABX", "X")]
    public void NewlyFoundChildrenFollowTheWholeSubtree(string original, string incoming, string expected, string moved)
    {
        PlanRow Row(char id) => new(id.ToString(), id.ToString(), "acme/repo") {
            Parent = id switch { 'C' => "P", 'G' => "C", 'A' or 'B' => "P", 'Q' => "A", _ => null }
        };
        var rows = original.Select(Row).ToImmutableArray();
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new(rows, []), new(rows, new()));
        var remote = new PlanBaseline(incoming.Select(Row).ToImmutableArray(), []);
        var result = PlanMerge.Merge(document, remote);
        Assert.That(string.Concat(result.State.Rows.Select(r => r.Identity)), Is.EqualTo(expected));
        Assert.That(PlanOperations.Changes(result, new(2026, 10, 18)).Fields
            .Where(p => p.Value.Contains(PlanField.Order)).Select(p => p.Key), Is.EquivalentTo(moved.Select(c => c.ToString())));
        Assert.That(PlanMerge.Merge(result, remote).State.Rows, Is.EqualTo(result.State.Rows));
    }

    [Test]
    public void RefreshAddedIdentitiesFollowPlanOrderAndExcludeKnownAndCreatedIssues()
    {
        PlanRow Row(string id) => new(id, id, "acme/repo");
        var before = new PlanDocument(new(new("github.com", 42), "P1"), new([Row("B")], []), new([Row("B"), Row("L")], new())) {
            Sync = new() { Publish = new("run", [new("key", "local:new", PlanPublishStage.Create, "createIssue", "CreateIssueInput!", "{}", "") { ResultId = "C" }]) }
        };
        var after = before with { State = before.State with { Rows = [Row("N2"), Row("B"), Row("N1"), Row("L"), Row("C")] } };
        Assert.That(PlanRefreshResult.FindAdded(before, after), Is.EqualTo(new[] { "N2", "N1" }));
        Assert.That(new PlanRefreshResult(new(2026, 10, 18), new(2026, 10, 18), []).AddedTasks, Is.Empty);
    }

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
    [Test]
    public void DivergentGitHubOrdersDoNotConflictOrAdoptARemoteOnlyReorder()
    {
        // The plan read A,B,C while GitHub's native order was already B,A,C: a later native reorder is
        // neither a local change nor a conflict, and it does not move the plan's rows.
        var rows = new[] { "P", "A", "B", "C" }.Select(id => new PlanRow(id, id, "acme/repo") { Parent = id == "P" ? null : "P" }).ToImmutableArray();
        var baseline = new PlanBaseline(rows, []);
        var document = new PlanDocument(new(new("github.com", 42), "P1"), baseline, new(rows, new()))
            { Sync = new() { NativeOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["B", "A", "C"]) } };
        var result = PlanMerge.NativeOrder(document, PlanMerge.Merge(document, baseline),
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["C", "A", "B"]));
        Assert.That(result.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "A", "B", "C" }));
        Assert.That(result.Sync.NativeOrders["P"], Is.EqualTo(new[] { "C", "A", "B" }));
        Assert.That(result.Sync.Conflicts.Any(c => c.Field == PlanField.SubIssueOrder), Is.False);
    }
    // #231 C3: a verified archive this plan remembers is an Issue it already knew. When the read
    // returns it, it keeps the position the read reports; #245's tail placement is only for an Issue
    // the plan meets for the first time.
    [Test]
    public void ReturningArchivedIdentityKeepsTheReadPositionAndAsksForNoRewrite()
    {
        var p = new PlanRow("P", "P", "acme/repo");
        var a = new PlanRow("A", "A", "acme/repo") { Parent = "P" };
        var b = new PlanRow("B", "B", "acme/repo") { Parent = "P" };
        var c = new PlanRow("C", "C", "acme/repo") { Parent = "P" };
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([p, a, c], []),
            new([p, a, c], new()) { ArchivedIdentities = ["B"] });
        var merged = PlanMerge.NativeOrder(document, PlanMerge.Merge(document, new PlanBaseline([p, a, b, c], [])),
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["A", "B", "C"]));
        Assert.Multiple(() =>
        {
            Assert.That(merged.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "A", "B", "C" }),
                "The returning Issue keeps the read position instead of the parent's subtree tail.");
            Assert.That(merged.State.ArchivedIdentities, Is.Empty, "The remembered archive is pruned when the Issue returns.");
            Assert.That(PlanOperations.SiblingOrderChange(merged, "P"), Is.Null, "The review has no sub-issue-order line.");
            Assert.That(merged.Sync.Conflicts, Is.Empty);
            Assert.That(PlanOperations.Changes(merged, new(2026, 10, 21)).Fields.Values.SelectMany(fields => fields),
                Has.None.EqualTo(PlanField.SubIssueOrder));
            Assert.That(PlanOperations.Changes(merged, new(2026, 10, 21)).Fields.Values.SelectMany(fields => fields),
                Has.None.EqualTo(PlanField.Order));
        });
    }
    [Test]
    public void ReturningArchivedIdentityKeepsTheReadOrderWhenGitHubOrdersDiffer()
    {
        // The Project item returns at the parent's tail while GitHub's native sub-issue order still has
        // its old slot. The plan adopts the read order and asks for no write, because a difference
        // between GitHub's own two orders is not a local change (#167).
        var p = new PlanRow("P", "P", "acme/repo");
        var a = new PlanRow("A", "A", "acme/repo") { Parent = "P" };
        var b = new PlanRow("B", "B", "acme/repo") { Parent = "P" };
        var c = new PlanRow("C", "C", "acme/repo") { Parent = "P" };
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([p, a, c], []),
            new([p, a, c], new()) { ArchivedIdentities = ["B"] });
        var merged = PlanMerge.NativeOrder(document, PlanMerge.Merge(document, new PlanBaseline([p, a, c, b], [])),
            ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["A", "B", "C"]));
        Assert.Multiple(() =>
        {
            Assert.That(merged.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "A", "C", "B" }), "The read order stands.");
            Assert.That(merged.State.ArchivedIdentities, Is.Empty);
            Assert.That(PlanOperations.SiblingOrderChange(merged, "P"), Is.Null, "No line for a difference the PMO did not make.");
            Assert.That(merged.Sync.Conflicts.Any(c => c.Field == PlanField.SubIssueOrder), Is.False,
                "The membership difference a return creates is not a local order change.");
        });
    }
    [Test]
    public void MultipleReturningArchivedChildrenKeepTheReadOrder()
    {
        var p = new PlanRow("P", "P", "acme/repo");
        PlanRow Child(string id) => new(id, id, "acme/repo") { Parent = "P" };
        var a = Child("A"); var b = Child("B"); var c = Child("C"); var d = Child("D");
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([p, a, d], []),
            new([p, a, d], new()) { ArchivedIdentities = ["B", "C"] });
        var merged = PlanMerge.Merge(document, new PlanBaseline([p, a, b, d, c], []));
        Assert.Multiple(() =>
        {
            Assert.That(merged.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "A", "B", "D", "C" }));
            Assert.That(merged.State.ArchivedIdentities, Is.Empty);
        });
    }
    [Test]
    public void GenuinelyNewIssueStillFollowsTheParentTailAlongsideReturningRows()
    {
        // Only the remembered Issue keeps its read slot; an Issue the plan has never seen keeps #245.
        var p = new PlanRow("P", "P", "acme/repo");
        PlanRow Child(string id) => new(id, id, "acme/repo") { Parent = "P" };
        var a = Child("A"); var b = Child("B"); var c = Child("C"); var n = Child("N");
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([p, a, c], []),
            new([p, a, c], new()) { ArchivedIdentities = ["B"] });
        var merged = PlanMerge.Merge(document, new PlanBaseline([p, a, b, c, n], []));
        Assert.That(merged.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "P", "A", "B", "C", "N" }),
            "The returning Issue stays at B's read slot while the new Issue is imported at the parent's tail.");
        Assert.That(merged.State.ArchivedIdentities, Is.Empty);
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
