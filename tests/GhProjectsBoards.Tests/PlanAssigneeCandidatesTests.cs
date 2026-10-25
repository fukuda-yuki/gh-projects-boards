using System.Collections.Immutable;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #225: the 担当者 cell offers the Project's known people while it is edited. The source is the stored
// people plus the assignees the last read returned (#253); it is local, so it costs no GitHub request, and a
// chosen person is accepted by the same typed-login rule that the cell already uses.
[TestFixture]
internal sealed class PlanAssigneeCandidatesTests
{
    private static readonly DateOnly Day = new(2026, 10, 13);

    private static PlanDocument Document(params PlanResource[] people) => new(new(new("github.com", 1), "P1"),
        new([], []), new([], new() { People = people.ToImmutableArray() }));

    private static PlanDocument Logins(PlanDocument document, params (string Identity, string Login)[] logins)
        => document with { Sync = document.Sync with { PeopleNames = logins.ToImmutableDictionary(l => l.Identity, l => l.Login) } };

    private static PlanDocument PeopleAndLogins()
        => Logins(Document(new("U1", "alice", 100, null), new("U2", "福田 有輝", 100, null)),
            ("U1", "alice"), ("U2", "fukuda-yuki"), ("U3", "person-U3"));

    [Test]
    public void EachKnownPersonIsOfferedOnceWithTheDisplayNameAndTheLogin()
    {
        var choices = PlanAssigneeChoices.All(PeopleAndLogins());
        Assert.Multiple(() =>
        {
            Assert.That(choices.Select(c => c.Identity), Is.EqualTo(new[] { "U1", "U2", "U3" }), "The stored people come once, then the read-only assignees.");
            Assert.That(choices.Select(c => c.Label), Is.EqualTo(new[] { "alice", "福田 有輝 · fukuda-yuki", "person-U3" }),
                "A candidate shows the configured display name and the retrieved login; one name stands alone when they are the same.");
            Assert.That(choices.Select(c => c.Login), Is.EqualTo(new[] { "alice", "fukuda-yuki", "person-U3" }),
                "The login is the value the cell commits, so a displayed name never has to be typed.");
        });
    }

    [Test]
    public void EveryOfferedLoginCommitsAsItsOwnPerson()
    {
        var document = PeopleAndLogins();
        foreach (var choice in PlanAssigneeChoices.All(document))
            Assert.That(PlanSheetEditing.Parse(document, PlanField.Assignees, choice.Login, Day),
                Is.EqualTo(new[] { choice.Identity }), choice.Label);
    }

    [TestCase("", "alice | 福田 有輝 · fukuda-yuki | person-U3")]
    [TestCase("PER", "person-U3")]
    [TestCase("ｐｅｒ", "person-U3", TestName = "MatchingFindsFullWidthSearchText")]
    [TestCase("福田", "福田 有輝 · fukuda-yuki")]
    [TestCase("fuku", "福田 有輝 · fukuda-yuki")]
    [TestCase("alice; per", "person-U3", TestName = "MatchingSearchesTheValueAfterTheLastSeparator")]
    [TestCase("alice;", "alice | 福田 有輝 · fukuda-yuki | person-U3", TestName = "MatchingOffersEveryoneAfterASeparatorWithoutAValue")]
    public void MatchingFiltersOnLoginAndDisplayName(string text, string expected)
        => Assert.That(Labels(PlanAssigneeChoices.Matching(PlanAssigneeChoices.All(PeopleAndLogins()), text)), Is.EqualTo(expected));

    [Test]
    public void ClosestKeepsTheMatchingPeopleAndOtherwiseRanksTheClosestLoginOrName()
    {
        var document = Logins(Document([new("U4", "福田 有輝", 100, null)]),
            ("U1", "person-U1"), ("U2", "person-U2"), ("U3", "fukuda-yuki"), ("U4", "fukuda-yuki2"));
        var choices = PlanAssigneeChoices.All(document);
        Assert.Multiple(() =>
        {
            Assert.That(Labels(PlanAssigneeChoices.Closest(choices, "person-U8")),
                Is.EqualTo("person-U1 | person-U2 | fukuda-yuki | 福田 有輝 · fukuda-yuki2"),
                "A typo keeps the list open with the nearest logins first, sharing the most leading characters.");
            Assert.That(Labels(PlanAssigneeChoices.Closest(choices, "福田さ")), Does.StartWith("福田 有輝 · fukuda-yuki"),
                "A display name the PMO is typing ranks its person first.");
            Assert.That(Labels(PlanAssigneeChoices.Closest(choices, "person-U1x")), Does.StartWith("person-U1"),
                "The login the typed text extends is the closest person.");
            Assert.That(Labels(PlanAssigneeChoices.Closest(choices, "person-U1")), Is.EqualTo("person-U1"),
                "A query that matches keeps the ordinary filtered list rather than the whole ranking.");
        });
    }

    [TestCase("", "alice", "alice", TestName = "ComposeFillsAnEmptyCell")]
    [TestCase("alice", "bob", "bob", TestName = "ComposeReplacesTheValueBeingTyped")]
    [TestCase("alice;", "bob", "alice; bob", TestName = "ComposeAppendsAfterASeparator")]
    [TestCase("alice; ", "bob", "alice; bob", TestName = "ComposeKeepsASpaceAfterTheSeparator")]
    [TestCase("alice;bob", "carol", "alice; carol", TestName = "ComposeReplacesOnlyTheValueAfterTheSeparator")]
    [TestCase("alice, bob", "carol", "alice, carol", TestName = "ComposeKeepsTheTypedSeparator")]
    [TestCase("alice、", "bob", "alice、 bob", TestName = "ComposeKeepsTheJapaneseSeparator")]
    public void ComposeKeepsTheEarlierValuesAndTheTypedSeparator(string text, string login, string expected)
        => Assert.That(PlanAssigneeChoices.Compose(text, login), Is.EqualTo(expected));

    [TestCase("", "")]
    [TestCase(" alice ", "alice")]
    [TestCase("alice, ", "")]
    [TestCase("alice; per", "per")]
    [TestCase("alice、bob", "bob")]
    public void QueryIsTheValueBeingTyped(string text, string expected)
        => Assert.That(PlanAssigneeChoices.Query(text), Is.EqualTo(expected));

    [Test]
    public void SemicolonSeparatesAssignmentValuesLikeTheOtherSeparators()
    {
        var document = Logins(Document(new("U1", "alice", 100, null), new("U2", "bob", 100, null)), ("U1", "alice"), ("U2", "bob"));
        Assert.Multiple(() =>
        {
            Assert.That(PlanSheetEditing.Parse(document, PlanField.Assignees, "alice; bob", Day), Is.EqualTo(new[] { "U1", "U2" }));
            Assert.That(PlanSheetEditing.UnresolvedAssignees(document, "alice; late-user"), Is.EqualTo(new[] { "late-user" }),
                "Only the typed value without a local match needs a lookup.");
        });
    }

    [Test]
    public async Task BuildingTheCandidateListSendsNoGitHubRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-assignee-choices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), """{"planEditor":true,"workspace":true}""");
            FakePlanEditor.Save(root, new([
                new(new("I1", "Assigned", "acme/repo") { Assignees = ["U1"] }, "", true),
                new(new("I2", "Also assigned", "acme/repo") { Assignees = ["U2"] }, "", true)], 3));
            var workspace = new PlanWorkspace(new PlanStore(root), () => Day);
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com",
                new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            File.WriteAllText(Path.Combine(root, "calls.jsonl"), "");
            var choices = PlanAssigneeChoices.All(workspace.Session!.Document);
            Assert.Multiple(() =>
            {
                Assert.That(choices.Select(c => c.Login), Is.EqualTo(new[] { "person-U1", "person-U2" }),
                    "The list is the people the Project already knows, not a repository's assignable users.");
                Assert.That(File.ReadAllLines(Path.Combine(root, "calls.jsonl")).Where(line => line.Length > 0), Is.Empty,
                    "Building the list sends no request; opening the Project never read the assignable-user catalog.");
            });
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Labels(IEnumerable<PlanAssigneeChoice> choices) => string.Join(" | ", choices.Select(c => c.Label));
}
