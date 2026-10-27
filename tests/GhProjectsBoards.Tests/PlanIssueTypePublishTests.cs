using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #319: 種類 is the Issue's own type, so its publish is one Issue-level write in the field phase, verified by
// reading the type back, and it follows the ordinary conflict, refresh and Undo rules. These cases run the real
// publisher against the fake gh executable, so the mutation and read-back are observed at the adapter boundary.
internal sealed partial class PlanPublisherTests
{
    private static Func<PlanFakeState, PlanFakeState> OrganizationTypes(params string?[] byRow) => state => state with
    {
        OrganizationRepositories = true,
        IssueTypes = [new("IT1", "Task"), new("IT2", "Bug"), new("IT3", "Feature", false)],
        Issues = state.Issues.Select((issue, index) => index < byRow.Length && byRow[index] is { } type
            ? issue with { Row = issue.Row with { IssueType = type } } : issue).ToImmutableArray()
    };

    [Test]
    public async Task IssueTypeIsOneIssueLevelWriteInTheFieldPhaseAndIsVerified()
    {
        await Start(2, OrganizationTypes("Task", null));
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Baseline.IssueTypes, Is.EqualTo(new[] { new PlanIssueType("IT1", "Task"), new PlanIssueType("IT2", "Bug") }),
                "Only enabled types are offered, with the catalog IDs the publish targets.");
            Assert.That(session.Document.Baseline.IssueTypeRepositories, Is.EqualTo(new[] { "acme/repo" }));
            Assert.That(session.Document.Baseline.Rows.Select(r => r.IssueType), Is.EqualTo(new[] { "Task", null }));
        });
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, "Bug")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        var sent = File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl"));
        Assert.Multiple(() =>
        {
            Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.IssueType, Is.EqualTo("Bug"));
            Assert.That(sent, Does.Contain("updateIssueIssueType"));
            Assert.That(sent, Does.Contain("IT2"), "The catalog ID observed with the Project is the write target.");
            Assert.That(sent, Does.Not.Contain("setIssueFieldValue"), "The type is not an organization Issue Field.");
            Assert.That(session.Changes(Today).TaskCount, Is.Zero, "The verified publish leaves nothing unpublished.");
            Assert.That(result.Review!.Changes.Single(c => c.Field == PlanField.IssueType),
                Is.EqualTo(new PlanReviewChange("I1", PlanField.IssueType, PlanJson.Text("Task"), PlanJson.Text("Bug"))));
            // The scoped publish read keeps the catalog and the observed value for the next edit.
            Assert.That(session.Document.Baseline.IssueTypes, Is.EqualTo(new[] { new PlanIssueType("IT1", "Task"), new PlanIssueType("IT2", "Bug") }));
            Assert.That(session.Document.Baseline.Rows.Single(r => r.Identity == "I1").IssueType, Is.EqualTo("Bug"));
        });
    }

    [Test]
    public async Task ClearingTheIssueTypeSendsANullTypeIdAndVerifiesTheClear()
    {
        await Start(1, OrganizationTypes("Task"));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, null)]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.Multiple(() =>
        {
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.IssueType, Is.Null);
            Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Contain("\"issueTypeId\":null"));
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        });
    }

    [Test]
    public async Task IssueTypeOnANewTaskIsPublishedAfterCreation()
    {
        // The catalog comes from the Project's repositories, so the fixture has one Issue to make them known.
        await Start(1, OrganizationTypes("Task"));
        await session.Execute(new InsertPlanRows([PlanRow.New("New task", "acme/repo") with { IssueType = "Bug" }]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.Multiple(() =>
        {
            Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Title == "New task").Row.IssueType, Is.EqualTo("Bug"));
            Assert.That(File.ReadAllText(Path.Combine(root, "plan-mutations.jsonl")), Does.Contain("updateIssueIssueType"));
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        });
    }

    [Test]
    public async Task AnIssueTypeTheReadBackDoesNotConfirmStaysPendingAndIsResent()
    {
        await Start(1, OrganizationTypes("Task"));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, "Bug")]), Today);
        Scenario("type-mismatch");
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Sync.Failures.Single().Reason, Is.EqualTo("VerificationMismatch"));
            Assert.That(session.Document.Sync.Failures.Single().Field, Is.EqualTo(PlanField.IssueType));
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.IssueType, Is.EqualTo("Task"));
        });
        Scenario();
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.IssueType, Is.EqualTo("Bug"));
    }

    [Test]
    public async Task IssueTypeChangeThatGitHubAlsoChangedStaysAConflictAndWritesNothing()
    {
        await Start(1, OrganizationTypes("Task"));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, "Bug")]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues
            .Select(i => i with { Row = i.Row with { IssueType = "Feature" } }).ToImmutableArray() });
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        var conflict = session.Document.Sync.Conflicts.Single(c => c.Identity == "I1" && c.Field == PlanField.IssueType);
        Assert.Multiple(() =>
        {
            Assert.That((conflict.Baseline, conflict.Local, conflict.Remote),
                Is.EqualTo((PlanJson.Text("Task"), PlanJson.Text("Bug"), PlanJson.Text("Feature"))));
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.IssueType, Is.EqualTo("Feature"), "A conflict is never overwritten.");
            Assert.That(File.Exists(Path.Combine(root, "plan-mutations.jsonl")), Is.False);
        });
        // Choosing GitHub's value resolves the conflict through the ordinary conflict path.
        Assert.That((await session.ResolveConflict("I1", PlanField.IssueType, true, Today)).Succeeded, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.State.Rows.Single().IssueType, Is.EqualTo("Feature"));
            Assert.That(session.Document.Sync.Conflicts, Is.Empty);
            Assert.That(session.Changes(Today).TaskCount, Is.Zero, "Choosing GitHub's type leaves nothing to publish.");
        });
    }

    [Test]
    public async Task RefreshKeepsALocalIssueTypeEditGitHubDidNotChange()
    {
        await Start(1, OrganizationTypes("Task"));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, "Bug")]), Today);
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.State.Rows.Single().IssueType, Is.EqualTo("Bug"), "An unchanged GitHub value preserves the local edit.");
            Assert.That(session.Document.Baseline.Rows.Single().IssueType, Is.EqualTo("Task"));
            Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RefreshAdoptsGitHubsIssueType()
    {
        await Start(1, OrganizationTypes("Task"));
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues
            .Select(i => i with { Row = i.Row with { IssueType = "Feature" } }).ToImmutableArray() });
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.State.Rows.Single().IssueType, Is.EqualTo("Feature"));
            Assert.That(session.Document.Baseline.Rows.Single().IssueType, Is.EqualTo("Feature"));
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        });
    }

    [Test]
    public async Task UndoAfterPublishingTheIssueTypeRestoresTheLocalValueAndSurvivesReopen()
    {
        await Start(1, OrganizationTypes("Task"));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, "Bug")]), Today);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.IssueType, Is.EqualTo("Bug"));
        await session.Undo(Today);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.State.Rows.Single().IssueType, Is.EqualTo("Task"), "Undo changes only the local plan.");
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.IssueType, Is.EqualTo("Bug"), "The published Issue type is not undone on GitHub.");
        });
        await Reopen();
        Assert.That(session.Document.State.Rows.Single().IssueType, Is.EqualTo("Task"));
    }

    [Test]
    public async Task AnIncompleteIssueTypeCatalogCannotReplaceTheBaseline()
    {
        await Start(1, OrganizationTypes("Task"));
        var before = PlanJson.Text(session.Document);
        Scenario("issue-types-incomplete");
        var result = await publisher.RefreshAsync(session, Today);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False, "A read whose catalog could not be proven must not be adopted.");
            Assert.That(PlanJson.Text(session.Document), Is.EqualTo(before));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        });
    }

    [Test]
    public async Task IssueTypeChangeStopsBeforeDispatchWhenTheReadObservesNoUpdatePermission()
    {
        await Start(2, OrganizationTypes("Task", null));
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.IssueType, "Bug")]), Today);
        Scenario("no-update-permission");
        var result = await publisher.PublishAsync(session, Today);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("「Task 1」の種類を変更する権限がありません"));
            Assert.That(FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == "I1").Row.IssueType, Is.EqualTo("Task"));
            Assert.That(File.Exists(Path.Combine(root, "plan-mutations.jsonl")), Is.False);
        });
    }

    [Test]
    public void TheReviewNamesIssueTypeAndCarriesTheIssueDestinationNote()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PlanSheetColumns.ReviewName(PlanField.IssueType), Is.EqualTo("種類"));
            Assert.That(PlanPublishText.IssueDestinationNote("種類", 2),
                Is.EqualTo("「種類」の値（2 タスク）は Issue 自体に書き込みます。この Issue を含むほかのプロジェクトにも同じ値が表示されます。"));
            Assert.That(PlanPublishText.IssueDestinationNote("Issue フィールド", 1),
                Is.EqualTo("「Issue フィールド」の値（1 タスク）は Issue 自体に書き込みます。この Issue を含むほかのプロジェクトにも同じ値が表示されます。"),
                "#258's sentence keeps its wording through the same helper.");
        });
    }

    [Test]
    public void ValidateAcceptsOnlyTheIssueTypeWriteContract()
    {
        var row = new PlanRow("I1", "Task 1", "acme/repo") { IssueType = "Bug" };
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([row], []), new([row], new()));
        PlanWrite Write(object input) => new("0", "I1", PlanPublishStage.Fields, "updateIssueIssueType",
            "UpdateIssueIssueTypeInput", PlanJson.Text(input), "issue { id }") { Field = PlanField.IssueType, State = PlanWriteState.Pending };
        var runId = Guid.NewGuid().ToString("N");
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => PlanPublishPlan.Validate(new(runId, [Write(new { issueId = "I1", issueTypeId = "IT2" })]), document));
            Assert.DoesNotThrow(() => PlanPublishPlan.Validate(new(runId, [Write(new { issueId = "I1", issueTypeId = (string?)null })]), document));
            Assert.Throws<ArgumentException>(() => PlanPublishPlan.Validate(
                new(runId, [Write(new { issueId = "I1", issueTypeId = "IT2" }) with { Selection = "item { id }" }]), document));
            Assert.Throws<ArgumentException>(() => PlanPublishPlan.Validate(
                new(runId, [Write(new { issueId = "I1", issueTypeId = "IT2" }) with { Identity = "I2" }]), document),
                "The write target must be the row it publishes.");
        });
    }
}
