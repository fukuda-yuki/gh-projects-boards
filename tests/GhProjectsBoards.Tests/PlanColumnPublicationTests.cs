using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

internal sealed partial class PlanPublisherTests
{
    [Test]
    public async Task RecoveryCopyPublishesMembershipsWhenRepositoryHasNoProjectIssues()
    {
        await Start(1, state => state with {
            Issues = state.Issues.Select(i => i with { Row = i.Row with { Labels = ["bug"], MilestoneTitle = "Release" } }).ToImmutableArray(),
            Labels = [new("acme/repo", "L1", "bug")], Milestones = [new("M1", "acme/repo", "Release", Today)]
        });
        var original = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, original with { Issues = original.Issues.Select(i => i with { Added = false }).ToImmutableArray() });
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
        Assert.That(session.Document.Sync.Unavailable, Is.EqualTo(new[] { "I1" }));
        Assert.That((await session.ResolveUnavailable("I1", true, Today)).Succeeded, Is.True);
        var published = await publisher.PublishAsync(session, Today);
        Assert.That(published.Succeeded, Is.True, published.Error);
        var remote = FakePlanEditor.Load(root);
        var created = remote.Issues.Single(i => i.Added);
        Assert.That(created.Row.Identity, Is.Not.EqualTo("I1"));
        Assert.That(created.Row.Labels, Is.EqualTo(new[] { "bug" }));
        Assert.That(created.Row.MilestoneTitle, Is.EqualTo("Release"));
        Assert.That(remote.Issues.Single(i => i.Row.Identity == "I1"), Is.EqualTo(original.Issues.Single() with { Added = false }));
        Assert.That(session.Changes(Today).TaskCount, Is.Zero);
    }

    [Test]
    public async Task OpenReadsHiddenValuesLabelsAndMappedFieldsWithoutDependingOnTheirNames()
    {
        Scenario();
        var row = new PlanRow("I1","Task","acme/repo") { Start = Today, Labels = ["bug"],
            ExtraValues = ImmutableDictionary<string,PlanExtraValue>.Empty.Add("Project:X",new(ValueAvailability.Present,"\"hidden\"")) };
        FakePlanEditor.Save(root,new([new(row,"",true)],2) {
            ExtraFields = [new("X","Hidden","TEXT")], Labels = [new("acme/repo","L1","bug")] });
        runner = new(new GhProcessRunner(new Dictionary<string,string?> { ["GH_CONFIG_DIR"] = root }));
        var service = new GhConnectionService(GhProcessTests.FakeExecutable,"github.com",runner);
        var context = new ConnectionContext("github.com",42,"fixture-user",GhProcessTests.FakeExecutable);
        using var lease = await service.BeginOperationAsync(context,default,mutation:false);
        var read = await PlanSnapshot.ReadConsistentAsync(service,lease,context,Project,default);
        var snapshot = PlanSnapshot.From(read,Settings);
        Assert.That(snapshot.Baseline.Rows[0].Start, Is.EqualTo(Today));
        Assert.That(snapshot.Baseline.Rows[0].Labels, Is.EqualTo(new[] {"bug"}));
        Assert.That(PlanValues.Get(snapshot.Baseline.Rows[0],"Project:X"), Is.EqualTo("\"hidden\""));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test]
    public async Task LabelsReadWithoutVisibilityArePresentRatherThanAssumedEmpty()
    {
        Scenario();
        FakePlanEditor.Save(root,new([new(new("I1","Task","acme/repo") { Labels=["bug"] },"",true)],2) {
            Labels=[new("acme/repo","L1","bug")] });
        runner = new(new GhProcessRunner(new Dictionary<string,string?> { ["GH_CONFIG_DIR"] = root }));
        var service = new GhConnectionService(GhProcessTests.FakeExecutable,"github.com",runner);
        var read = await new ProjectReader(service).ReadAsync(new("github.com",42,"fixture-user",GhProcessTests.FakeExecutable),Project);
        Assert.That(read.Outcome,Is.EqualTo(ProjectReadOutcome.Complete));
        Assert.That(read.Project!.Issues.Values.Single().Labels.Availability,Is.EqualTo(ValueAvailability.Present));
        Assert.That(read.Project.Issues.Values.Single().Labels.Value!.Single().Name,Is.EqualTo("bug"));
    }

    [TestCase(2), TestCase(1040)]
    public async Task AllExtraValuesUsePagedReadsWithoutPerRowRequests(int count)
    {
        Scenario();
        var rows=Enumerable.Range(1,count).Select(n=>new PlanFakeIssue(new("I"+n,"Task","acme/repo") {
            ExtraValues=ImmutableDictionary<string,PlanExtraValue>.Empty.Add("Project:X-T",new(ValueAvailability.Present,"\"text\""))
                .Add("Project:X-N",new(ValueAvailability.Present,"-2")).Add("Project:X-H",new(ValueAvailability.Present,"\"secret\"")), Labels=["bug"] },"",true)).ToImmutableArray();
        FakePlanEditor.Save(root,new(rows,count+1) {
            ExtraFields=[new("X-T","Notes","TEXT"),new("X-N","Risk","NUMBER"),new("X-S","Priority","SINGLE_SELECT") {Options=["High","Low"]},new("X-H","Hidden","TEXT")],
            Labels=[new("acme/repo","L1","bug")] });
        runner=new(new GhProcessRunner(new Dictionary<string,string?> { ["GH_CONFIG_DIR"]=root }));
        var service=new GhConnectionService(GhProcessTests.FakeExecutable,"github.com",runner);
        var result=await new ProjectReader(service)
            .ReadAsync(new("github.com",42,"fixture-user",GhProcessTests.FakeExecutable),Project);
        Assert.That(result.Outcome,Is.EqualTo(ProjectReadOutcome.Complete),string.Join(",",result.Problems));
        var snapshot=PlanSnapshot.From(result,Settings);
        Assert.That(snapshot.Baseline.Rows.Length,Is.EqualTo(count));
        Assert.That(snapshot.Baseline.Rows.All(r=>r.ExtraValues.Keys.Count(k=>k.StartsWith("Project:X-",StringComparison.Ordinal))==4 && r.ExtraValues["Project:X-S"].Availability==ValueAvailability.Empty && r.Labels.SequenceEqual(new[]{"bug"})),Is.True);
        Assert.That(PlanValues.Get(snapshot.Baseline.Rows[0],"Project:X-N"),Is.EqualTo("-2"));
        Assert.That(runner.Queries.Count(q=>q.Contains("ProjectItems")),Is.EqualTo((count+99)/100));
        Assert.That(runner.Queries.Count,Is.EqualTo((count+99)/100+3));
        Assert.That(PlanValues.Get(snapshot.Baseline.Rows[0],"Project:X-H"),Is.EqualTo("\"secret\""));
        Assert.That(FakePlanEditor.Load(root).MutationBatches,Is.Zero);
    }

    [Test]
    public async Task LargeIssueLabelConnectionsPageInBatchesAndKeepCompleteMembership()
    {
        Scenario();
        var labels=Enumerable.Range(1,101).Select(n=>new PlanRepositoryOption("acme/repo","L"+n,"label"+n)).ToImmutableArray();
        var issues=Enumerable.Range(1,21).Select(n=>new PlanFakeIssue(new("I"+n,"Task","acme/repo") {Labels=labels.Select(l=>l.Name).ToImmutableArray()},"",true)).ToImmutableArray();
        FakePlanEditor.Save(root,new(issues,22) {Labels=labels});
        runner=new(new GhProcessRunner(new Dictionary<string,string?> { ["GH_CONFIG_DIR"]=root }));
        var service=new GhConnectionService(GhProcessTests.FakeExecutable,"github.com",runner);
        var result=await new ProjectReader(service)
            .ReadAsync(new("github.com",42,"fixture-user",GhProcessTests.FakeExecutable),Project);
        Assert.That(result.Outcome,Is.EqualTo(ProjectReadOutcome.Complete),string.Join(",",result.Problems));
        Assert.That(result.Project!.Issues.Values.All(i=>i.Labels.Value?.Length==101),Is.True);
        Assert.That(result.Project.Labels.Count,Is.EqualTo(101));
        Assert.That(runner.Queries.Count(q=>q.Contains("ProjectIssueLabels")),Is.EqualTo(2));
        Assert.That(FakePlanEditor.Load(root).MutationBatches,Is.Zero);
    }
    [Test]
    public async Task ConfiguredExtraColumnsRefreshPublishVerifyAndHideWithoutWrites()
    {
        await Start(2, state => state with {
            ExtraFields = [new("X-T","Notes","TEXT"),new("X-N","Risk","NUMBER"),new("X-S","Priority","SINGLE_SELECT") { Options=["High","Low"] }],
            Labels=[new("acme/repo","L-bug","bug")],
            Milestones=[new("M-release","acme/repo","Release",Today)]
        });
        Assert.That((await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { SheetColumns=[
            new("Project:X-T",true,null),new("Project:X-N",true,null),new("Project:X-S",true,null),new("Labels",true,null),new("MilestoneTitle",true,null)] }),Today)).Succeeded,Is.True);
        Assert.That((await publisher.RefreshAsync(session,Today)).Succeeded,Is.True);
        Assert.That(session.Document.State.Rows[0].ExtraValues.Keys.Count(k=>k.StartsWith("Project:X-",StringComparison.Ordinal)),Is.EqualTo(3));
        Assert.That((await session.Execute(new EditPlanCells(PlanOperationKind.Paste,[new("I1","Project:X-T","Memo"),new("I1","Project:X-N",-2m),
            new("I1","Project:X-S","high"),new("I1",PlanField.Labels,new[]{"bug"}),new("I1",PlanField.MilestoneTitle,"Release")]),Today)).Succeeded,Is.True);
        Assert.That((await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {SheetColumns=[]}),Today)).Succeeded,Is.True);
        var result=await publisher.PublishAsync(session,Today);
        Assert.That(result.Succeeded,Is.True,result.Error);
        Assert.That(session.Changes(Today).TaskCount,Is.Zero);
        var observed=FakePlanEditor.Load(root).Issues.Single(i=>i.Row.Identity=="I1").Row;
        Assert.That(PlanValues.Get(observed,"Project:X-T"),Is.EqualTo("\"Memo\""));
        Assert.That(observed.MilestoneTitle,Is.EqualTo("Release"));
        Assert.That(observed.Labels,Is.EqualTo(new[]{"bug"}));
        var writes=File.ReadAllText(Path.Combine(root,"plan-mutations.jsonl"));
        Assert.That(writes,Does.Not.Contain("createLabel").And.Not.Contain("createMilestone").And.Not.Contain("transferIssue"));
        var batches=FakePlanEditor.Load(root).MutationBatches;
        Assert.That((await publisher.PublishAsync(session,Today)).Succeeded,Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches,Is.EqualTo(batches));
    }
}
