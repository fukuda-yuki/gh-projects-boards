using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #319: 種類 is a standard column whose value belongs to the Issue, and the app's own columns cannot be renamed.
[TestFixture]
internal sealed class PlanIssueTypeColumnTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);

    private static PlanDocument Document(params PlanRow[] rows)
    {
        var document = new PlanDocument(new(new("github.com", 1), "P1"), new([.. rows], []), new([.. rows], new() { StatusDate = Day }));
        return document.Baseline.IssueTypes.IsDefaultOrEmpty
            ? document with { Baseline = document.Baseline with {
                IssueTypes = [new("IT-1", "Task"), new("IT-2", "Bug"), new("IT-3", "Feature")],
                IssueTypeRepositories = ["acme/repo"] } }
            : document;
    }

    [Test]
    public void IssueTypeFollowsPhaseAndIsShownByDefaultOnlyWithAnOrganizationRepository()
    {
        var personal = Document(new PlanRow("I1", "Task", "acme/repo"));
        personal = personal with { Baseline = personal.Baseline with { IssueTypes = [], IssueTypeRepositories = [] } };
        var organization = Document(new PlanRow("I1", "Task", "acme/repo"));
        var keys = PlanSheetColumns.All(organization).Select(c => c.Key).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(keys.Skip(Array.IndexOf(keys, PlanField.Phase) + 1).First(), Is.EqualTo(PlanField.IssueType));
            var column = PlanSheetColumns.All(organization).Single(c => c.Key == PlanField.IssueType);
            Assert.That((column.Name, column.Type, column.GitHub, column.Editability, column.Visible),
                Is.EqualTo(("種類", "単一選択", "—", "編集できる", true)));
            Assert.That(PlanSheetColumns.All(personal).Single(c => c.Key == PlanField.IssueType).Visible, Is.False);
        });
    }

    [Test]
    public void StoredDisplayNamesOfAppColumnsAreIgnoredAndRenamingOneIsRefused()
    {
        var d = Document(new PlanRow("I1", "Task", "acme/repo"));
        d = d with { State = d.State with { Settings = d.State.Settings with { SheetColumns = [
            new("Title", true, "作業"), new("Phase", true, "工程名")] } } };
        Assert.Multiple(() =>
        {
            Assert.That(PlanSheetColumns.All(d).Single(c => c.Key == "Title").Name, Is.EqualTo("タスク名"));
            Assert.That(PlanSheetColumns.All(d).Single(c => c.Key == "Phase").Name, Is.EqualTo("工程"));
            Assert.That(() => PlanSheetColumns.Change(d, "Title", name: "作業", rename: true),
                Throws.ArgumentException.With.Message.EqualTo("この列の表示名は変更できません。"));
            Assert.That(() => PlanSheetColumns.Change(d, PlanField.IssueType, name: "型", rename: true),
                Throws.ArgumentException.With.Message.EqualTo("この列の表示名は変更できません。"));
        });
    }

    [Test]
    public void AProjectFieldKeepsItsConfiguredDisplayName()
    {
        var row = new PlanRow("I1", "Task", "acme/repo");
        var d = new PlanDocument(new(new("github.com", 1), "P1"), new([row], [new("F1", "Notes", "TEXT")]), new([row], new()));
        var settings = PlanSheetColumns.Change(d, "Project:F1", name: "メモ", rename: true);
        var named = d with { State = d.State with { Settings = settings } };
        Assert.That(PlanSheetColumns.All(named).Single(c => c.Key == "Project:F1").Name, Is.EqualTo("メモ"));
        var restored = PlanSheetColumns.Change(named, "Project:F1", name: "", rename: true);
        Assert.That(PlanSheetColumns.All(named with { State = named.State with { Settings = restored } }).Single(c => c.Key == "Project:F1").Name,
            Is.EqualTo("Notes"));
    }

    [TestCase(" task ", "Task")]
    [TestCase("BUG", "Bug")]
    [TestCase("ｆｅａｔｕｒｅ", "Feature")]
    public void TypedNamesMatchTheProjectsTypesIgnoringCase(string input, string expected)
        => Assert.That(PlanSheetEditing.Parse(Document(new PlanRow("I1", "Task", "acme/repo")), PlanField.IssueType, input, Day, "I1"),
            Is.EqualTo(expected));

    [Test]
    public void EmptyInputClearsTheType()
        => Assert.That(PlanSheetEditing.Parse(Document(new PlanRow("I1", "Task", "acme/repo")), PlanField.IssueType, "", Day, "I1"),
            Is.Null);

    [Test]
    public void UnknownNameListsTheProjectTypes()
        => Assert.That(() => PlanSheetEditing.Parse(Document(new PlanRow("I1", "Task", "acme/repo")), PlanField.IssueType, "Epic", Day, "I1"),
            Throws.ArgumentException.With.Message.EqualTo("種類は Task, Bug, Feature のどれかを入力してください。"));

    [Test]
    public void ARepositoryWithoutIssueTypesRefusesTheEdit()
    {
        var d = Document(new PlanRow("I1", "Task", "acme/personal"));
        d = d with { Baseline = d.Baseline with { IssueTypeRepositories = [] , IssueTypes = [] } };
        Assert.That(() => PlanSheetEditing.Parse(d, PlanField.IssueType, "Task", Day, "I1"),
            Throws.ArgumentException.With.Message.EqualTo("このリポジトリには Issue の種類がありません"));
    }

    [Test]
    public void EditingSetsTheTypeAndClearRemovesIt()
    {
        var d = Document(new PlanRow("I1", "Task", "acme/repo"));
        var edited = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.IssueType, PlanSheetEditing.Parse(d, PlanField.IssueType, "bug", Day, "I1"))]), Day).State;
        Assert.That(edited.Rows.Single().IssueType, Is.EqualTo("Bug"));
        Assert.That(PlanOperations.Changes(d with { State = edited }, Day).Fields["I1"], Does.Contain(PlanField.IssueType));
        var cleared = PlanOperations.Apply(d with { State = edited }, new ClearPlanCells(["I1"], [PlanField.IssueType]), Day).State;
        Assert.That(cleared.Rows.Single().IssueType, Is.Null);
        Assert.That(PlanOperations.Replay(edited, PlanOperations.Difference(d.State, edited, PlanOperationKind.Cell), false).Rows,
            Is.EqualTo(d.State.Rows));
    }

    [Test]
    public void RequirementRowsShowTheBaselineTypeAndRefuseEdits()
    {
        var parent = new PlanRow("P", "Parent", "acme/repo") { IssueType = "Task" };
        var child = new PlanRow("C", "Child", "acme/repo") { Parent = "P" };
        var d = Document(parent, child);
        Assert.That(PlanOperations.EffectiveRows(d).Single(r => r.Identity == "P").IssueType, Is.EqualTo("Task"));
        Assert.That(() => PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("P", PlanField.IssueType, "Bug")]), Day),
            Throws.ArgumentException.With.Message.EqualTo("要求事項の行では編集できません"));
    }

    [Test]
    public void ValuesRoundTripThroughTheOrdinaryFieldContract()
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { IssueType = "Bug" };
        var d = Document(row);
        Assert.That(PlanValues.Get(row, PlanField.IssueType), Is.EqualTo("\"Bug\""));
        var set = PlanValues.Set(PlanValues.Set(row, PlanField.IssueType, "null"), PlanField.IssueType, "\"Task\"");
        Assert.That(set.IssueType, Is.EqualTo("Task"));
        Assert.That(PlanValues.Fields(), Does.Contain(PlanField.IssueType));
    }

    [Test]
    public void APlanSavedBeforeTheColumnOpensWithNoTypeOrCatalog()
    {
        var d = Document(new PlanRow("I1", "Task", "acme/repo") { IssueType = "Bug" });
        var json = JsonNode.Parse(PlanJson.Text(d))!;
        Strip(json);
        var reopened = PlanJson.Read<PlanDocument>(Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Multiple(() =>
        {
            Assert.That(reopened.State.Rows.Single().IssueType, Is.Null);
            Assert.That(reopened.Baseline.IssueTypes, Is.Empty);
            Assert.That(reopened.Baseline.IssueTypeRepositories, Is.Empty);
        });

        static void Strip(JsonNode? node)
        {
            if (node is JsonObject item)
            {
                if (item.ContainsKey("rows") && item.ContainsKey("columns")) { item.Remove("issueTypes"); item.Remove("issueTypeRepositories"); }
                if (item.ContainsKey("identity") && item.ContainsKey("title")) item.Remove("issueType");
                foreach (var (_, child) in item.ToArray()) Strip(child);
            }
            else if (node is JsonArray list)
                foreach (var child in list.ToArray()) Strip(child);
        }
    }

    [Test]
    public void DuplicateTypeIdentitiesAreRejected()
    {
        var d = Document(new PlanRow("I1", "Task", "acme/repo"));
        var duplicate = d with { Baseline = d.Baseline with { IssueTypes = [new("IT-1", "Task"), new("IT-1", "Bug")] } };
        Assert.That(() => PlanOperations.ValidateDocument(duplicate, Day), Throws.ArgumentException);
        Assert.That(() => PlanOperations.ValidateDocument(d, Day), Throws.Nothing);
    }
}
