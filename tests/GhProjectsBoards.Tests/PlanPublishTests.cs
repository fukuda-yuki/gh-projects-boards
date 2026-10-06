using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;
[TestFixture]
internal sealed class PlanPublishTests
{
    [TestCase("Done"), TestCase(null)]
    public void StatusReviewUsesOptionIdentityAndVerifiesObservedName(string? status)
    {
        var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var field = new ProjectFieldDefinition(new(scope, "F-status"), project, "Status", "ProjectV2SingleSelectField", "SINGLE_SELECT", FieldOwner.ProjectItem,
            [new("option-done", "Done")], ValueAvailability.Present);
        var before = new PlanRow("I1", "Task", "acme/repo") { Status = "Todo" };
        var row = before with { Status = status }; var baseline = new PlanBaseline([before], []);
        var settings = new ProjectPlanSettings();
        var remote = new PlanRemoteSnapshot(baseline, ImmutableDictionary<string,string>.Empty, [field], 0, 0);
        var review = PlanPublishPlan.Build(new(project, baseline, new([row], settings)), remote, new(2026,10,5), Guid.NewGuid().ToString("N"));
        var write = review.Writes.Single();
        Assert.That(review.Changes.Single().Field, Is.EqualTo(PlanField.Status));
        Assert.That(write.Stage, Is.EqualTo(PlanPublishStage.Fields));
        Assert.That(write.Mutation, Is.EqualTo(status is null ? "clearProjectV2ItemFieldValue" : "updateProjectV2ItemFieldValue"));
        if (status is not null) Assert.That(write.Input, Does.Contain("option-done"));
        var progress = new PlanPublishProgress(Guid.NewGuid().ToString("N"), review.Writes);
        Assert.That(PlanVerification.Verify(write, progress, remote with { Baseline = new([row], []) }, settings), Is.True);
        Assert.That(PlanVerification.Verify(write, progress, remote, settings), Is.False);
    }
    [Test, SetCulture("th-TH")]
    public void GitHubDatesAndNumbersRemainGregorianAndInvariant()
    {
        var day = new DateOnly(2026, 10, 6); var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var roles = new[] { PlanField.Start, PlanField.End, PlanField.StartNoEarlierThan, PlanField.Actual };
        var fields = roles.Select(f => new ProjectFieldDefinition(new(scope, "F-" + f), project, f.ToString(), "ProjectV2Field",
            f == PlanField.Actual ? "NUMBER" : "DATE", FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray();
        var settings = new ProjectPlanSettings { Columns = fields.Select((f, i) => new PlanColumnMapping(roles[i], f.Id.NodeId, f.Name, f.DataType)).ToImmutableArray() };
        var before = new PlanRow("I1", "Task", "acme/repo");
        var row = before with { Start = day, End = day, StartNoEarlierThan = day, Fixed = true, Actual = 12.5m };
        var baseline = new PlanBaseline([before], []);
        var review = PlanPublishPlan.Build(new(project, baseline, new([row], settings)), new(baseline, ImmutableDictionary<string, string>.Empty, fields, 0, 0), day, Guid.NewGuid().ToString("N"));
        var verified = new PlanRemoteSnapshot(new([row], []), ImmutableDictionary<string, string>.Empty, fields, 0, 0);
        Assert.That(review.Writes.All(w => PlanVerification.Verify(w, new(Guid.NewGuid().ToString("N"), review.Writes), verified, settings)), Is.True);
        var values = review.Writes.Select(w => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(w.Input)).ToArray();
        Assert.That(values.Where(v => v.GetProperty("value").TryGetProperty("date", out _)).Select(v => v.GetProperty("value").GetProperty("date").GetString()), Is.EqualTo(new[] { "2026-10-06", "2026-10-06", "2026-10-06" }));
        Assert.That(values.Single(v => v.GetProperty("value").TryGetProperty("number", out _)).GetProperty("value").GetProperty("number").GetRawText(), Is.EqualTo("12.5"));
    }
    [Test]
    public void CreationPacingOverlapsRequestTimeAndBoundsEveryRollingMinuteAcrossMixedBatches()
    {
        var origin = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        ImmutableArray<PlanCreationStart> starts = [];
        Assert.That(PlanCreationPacing.NextStart(starts, 10, origin), Is.EqualTo(origin));
        starts = PlanCreationPacing.Reserve(starts, 10, origin);
        Assert.That(PlanCreationPacing.NextStart(starts, 1, origin.AddSeconds(3)), Is.EqualTo(origin.AddSeconds(10)));
        var all = new List<PlanCreationStart>(starts); var random = new Random(5901); var now = origin.AddSeconds(3);
        for (var i = 0; i < 2000; i++)
        {
            var count = random.Next(1, 11);
            now = PlanCreationPacing.NextStart(starts, count, now);
            Assert.That(all.Where(s => s.Started > now.AddMinutes(-1)).Sum(s => s.Issues) + count, Is.LessThanOrEqualTo(60));
            Assert.That(now, Is.GreaterThanOrEqualTo(all[^1].Started.AddSeconds(all[^1].Issues)));
            starts = PlanCreationPacing.Reserve(starts, count, now);
            // The exact persisted representation, including varying batch sizes, is used after reopen.
            starts = PlanJson.Read<ImmutableArray<PlanCreationStart>>(System.Text.Encoding.UTF8.GetBytes(PlanJson.Text(starts)));
            all.Add(new(now, count)); now = now.AddSeconds(random.Next(0, 15));
        }
    }
    [TestCase(null)]
    [TestCase("I2")]
    public async Task RecordedCreationIdentityCannotBeForgottenOrRebound(string? replacement)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-binding-" + Guid.NewGuid().ToString("N"));
        var day = new DateOnly(2026, 10, 5); var project = new ScopedId(new("github.com", 42), "P1");
        var row = PlanRow.New("Created", "acme/repo");
        var session = await PlanSession.CreateAsync(new(root), new(project, new([], []), new([row], new())), day);
        var write = new PlanWrite("create", row.Identity, PlanPublishStage.Create, "createIssue", "CreateIssueInput",
            PlanJson.Text(new { repositoryId = "R1", title = "Created", body = "marker" }), "issue { id }") { State = PlanWriteState.Succeeded, ResultId = "I1" };
        var attempt = new PlanPublishProgress(Guid.NewGuid().ToString("N"), [write]);
        try
        {
            await session.SaveSync(session.Document.Sync with { Publish = attempt });
            Assert.ThrowsAsync<ArgumentException>(async () => await session.SaveSync(session.Document.Sync with { Publish = attempt with
            { Writes = [write with { ResultId = replacement, State = replacement is null ? PlanWriteState.Pending : PlanWriteState.Succeeded }] } }));
            var reopened = await PlanSession.OpenAsync(new(root), project, day);
            Assert.That(reopened.Status, Is.EqualTo(PlanLoadStatus.Loaded));
            Assert.That(reopened.Session!.Document.Sync.Publish!.Writes.Single().ResultId, Is.EqualTo("I1"));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
    [TestCase(PlanField.Estimate)]
    [TestCase(PlanField.Remaining)]
    [TestCase(PlanField.Actual)]
    public void SummaryOwnEffortIsNeverAnUnpublishedDifference(PlanField field)
    {
        var day = new DateOnly(2026, 10, 5);
        var parent = new PlanRow("P", "Parent", "acme/repo"); var child = new PlanRow("C", "Child", "acme/repo") { Parent = "P" };
        var edited = PlanValues.Set(parent, field, "8");
        var document = new PlanDocument(new(new("github.com", 42), "P1"), new([parent, child], []), new([edited, child], new()));
        Assert.That(PlanOperations.Changes(document, day).TaskCount, Is.Zero);
    }
    [Test]
    public void LiveBudgetBoundsIssueCreationAndReportsTheWholeRun()
    {
        var budget = PlanPublisherLive.Estimate();
        Assert.That(budget.CreatedIssues, Is.EqualTo(250));
        Assert.That(budget.CreateRequests, Is.EqualTo(25));
        Assert.That(budget.PlannedMutationRequests, Is.EqualTo(480));
        Assert.That(() => PlanPublisherLive.Estimate(100, 3, 67), Throws.InvalidOperationException);
        Assert.That(() => PlanPublisherLive.Estimate(101), Throws.InvalidOperationException);
    }
    [TestCase(1, 1, 7, 7, false, false)]
    [TestCase(1, 1, 7, 7, false)]
    [TestCase(1, 4, 1, 4, false)]
    [TestCase(1, 4, 4, 4, false)]
    [TestCase(1, 4, 7, 4, true)]
    public async Task VerificationMergesUnpublishedFields(int baseline, int local, int remote, int expected, bool conflict, bool verifiedTitle = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-review-" + Guid.NewGuid().ToString("N"));
        var day = new DateOnly(2026, 10, 5); var project = new ScopedId(new("github.com", 42), "P1");
        var row = new PlanRow("I1", "Before", "acme/repo") { Actual = baseline };
        var session = await PlanSession.CreateAsync(new(root), new(project, new([row], []), new([row with { Actual = local }], new())), day);
        try
        {
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "Published")]), day);
            var write = new PlanWrite("0", "I1", PlanPublishStage.Fields, "updateIssue", "UpdateIssueInput", "{\"id\":\"I1\",\"title\":\"Published\"}", "issue { id }") { State = PlanWriteState.Succeeded };
            await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), [write]) });
            await session.AcceptPublished(new(new([row with { Title = verifiedTitle ? "Published" : "Before", Actual = remote }], []), ImmutableDictionary<string,string>.Empty.Add("I1", "T1"), [], 0, 0), day);
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Published"));
            Assert.That(session.Document.State.Rows[0].Actual, Is.EqualTo(expected));
            Assert.That(session.Document.Sync.Conflicts.Any(c => c.Field == PlanField.Actual), Is.EqualTo(conflict));
            await session.Undo(day);
            Assert.That(session.Document.State.Rows[0].Title, Is.EqualTo("Before"));
            Assert.That(session.Document.State.Rows[0].Actual, Is.EqualTo(expected));
            Assert.That((await PlanSession.OpenAsync(new(root), project, day)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task DiscoveredRowsSurviveMoveUndoAndReopen(bool refresh)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-review-" + Guid.NewGuid().ToString("N"));
        var day = new DateOnly(2026, 10, 5); var project = new ScopedId(new("github.com", 42), "P1");
        var rows = new[] { "A", "B" }.Select(id => new PlanRow(id, id, "acme/repo")).ToImmutableArray();
        var session = await PlanSession.CreateAsync(new(root), new(project, new(rows, []), new(rows, new())), day);
        try
        {
            await session.Execute(new MovePlanRows(["B"], "A"), day);
            var remote = new PlanRemoteSnapshot(new(rows.Add(new("C", "C", "acme/repo")), []), ImmutableDictionary<string,string>.Empty, [], 0, 0);
            if (refresh) await session.AcceptRefresh(remote, day);
            else { await session.SaveSync(session.Document.Sync with { Publish = new(Guid.NewGuid().ToString("N"), []) }); await session.AcceptPublished(remote, day); }
            var opened = await PlanSession.OpenAsync(new(root), project, day);
            Assert.That(opened.Status, Is.EqualTo(PlanLoadStatus.Loaded), opened.Error);
            session = opened.Session!;
            await session.Undo(day);
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "A", "B", "C" }));
            Assert.That((await PlanSession.OpenAsync(new(root), project, day)).Status, Is.EqualTo(PlanLoadStatus.Loaded));
            await session.Redo(day);
            Assert.That(session.Document.State.Rows.Select(r => r.Identity), Is.EqualTo(new[] { "B", "A", "C" }));
        }
        finally { await session.FlushAsync(); Directory.Delete(root, true); }
    }
    [Test]
    public void ReviewIncludesCalculatedSummaryDatesButNeverRolledUpEffort()
    {
        var day = new DateOnly(2026, 10, 5);
        var scope = new ConnectionScope("github.com", 42); var project = new ScopedId(scope, "P1");
        var parent = new PlanRow("P", "Parent", "acme/repo") { Estimate = 99, Remaining = 99 };
        var child = new PlanRow("C", "Child", "acme/repo") { Parent = "P", Estimate = 8, Remaining = 8 };
        var mappings = new[] { PlanField.Estimate, PlanField.Remaining, PlanField.Start, PlanField.End }
            .Select(f => new PlanColumnMapping(f, "F" + f, f.ToString(), f is PlanField.Start or PlanField.End ? "DATE" : "NUMBER")).ToImmutableArray();
        var settings = new ProjectPlanSettings { StatusDate = day, Columns = mappings };
        var baseline = new PlanBaseline([parent, child], mappings.Select(m => new PlanColumnDefinition(m.FieldId, m.Name, m.DataType)).ToImmutableArray());
        var document = new PlanDocument(project, baseline, new(baseline.Rows, settings));
        var remote = new PlanRemoteSnapshot(baseline, ImmutableDictionary<string, string>.Empty.Add("P", "TP").Add("C", "TC"),
            mappings.Select(m => new ProjectFieldDefinition(new(scope, m.FieldId), project, m.Name, "ProjectV2Field", m.DataType, FieldOwner.ProjectItem, [], ValueAvailability.Present)).ToImmutableArray(), 0, 0) { SubIssueOrders = ImmutableDictionary<string, ImmutableArray<string>>.Empty.Add("P", ["C"]) };
        var plan = PlanPublishPlan.Build(document, remote, day, "run");
        Assert.That(plan.Changes.Where(c => c.Identity == "P").Select(c => c.Field), Is.EquivalentTo(new[] { PlanField.Start, PlanField.End }));
        Assert.That(plan.Writes, Has.Length.EqualTo(4));
        Assert.That(plan.Changes.All(c => c.After == "\"2026-10-05\""), Is.True);
    }
    [TestCase(PlanPublishStage.Fields, 49, 1, "number")]
    [TestCase(PlanPublishStage.Fields, 50, 1, "number")]
    [TestCase(PlanPublishStage.Fields, 51, 2, "number")]
    [TestCase(PlanPublishStage.Fields, 51, 2, "date")]
    [TestCase(PlanPublishStage.Fields, 11, 2, "mixed")]
    [TestCase(PlanPublishStage.Create, 9, 1, "")]
    [TestCase(PlanPublishStage.Create, 10, 1, "")]
    [TestCase(PlanPublishStage.Create, 11, 2, "")]
    [TestCase(PlanPublishStage.Add, 11, 2, "")]
    public void BatchesRespectThePublishedLimits(PlanPublishStage stage, int count, int batches, string values)
    {
        var writes = Enumerable.Range(0, count).Select(i => new PlanWrite(i.ToString(), "I" + i, stage,
            stage == PlanPublishStage.Fields ? "updateProjectV2ItemFieldValue" : stage == PlanPublishStage.Create ? "createIssue" : "addProjectV2ItemById", "Input",
            stage != PlanPublishStage.Fields ? "{}" : values == "date" ? "{\"value\":{\"date\":\"2026-10-07\"}}" :
            values == "mixed" && i % 2 == 1 ? "{\"value\":{\"singleSelectOptionId\":\"done\"}}" : "{\"value\":{\"number\":8}}", "id")).ToArray();
        var actual = PlanPublishPlan.Batches(writes).ToArray();
        Assert.That(actual.Length, Is.EqualTo(batches));
        Assert.That(actual.SelectMany(b => b), Is.EqualTo(writes));
    }
}
