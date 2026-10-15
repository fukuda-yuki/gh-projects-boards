using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.Assist;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// Pure behavior here; persistence, history and revision are exercised with real storage separately.
[TestFixture]
internal sealed class AssistRulesTests
{
    internal static readonly DateOnly Today = new(2026, 10, 15);
    internal static PlanDocument Document()
    {
        ImmutableArray<PlanRow> rows = [
            new("R", "要件", "acme/work"),
            new("A", "PS-004 設計", "acme/work") { Parent = "R", Phase = "PS", Assignees = ["p1"], Estimate = 8, Remaining = 16, Actual = 4, Start = Today.AddDays(-3), End = Today.AddDays(-1) },
            new("B", "IT 検証", "acme/work") { Parent = "R", Phase = "IT", Predecessors = ["A"], Assignees = ["p2"], Estimate = 8, Remaining = 8 }
        ];
        return new(new ScopedId(new("github.com", 42), "P1"),
            new(rows, [new("phase", "工程", "SINGLE_SELECT") { Options = ["PS", "IT"] }]),
            new(rows, new() { StatusDate = Today, ProjectStart = Today.AddDays(-5), ProjectEnd = Today.AddDays(20),
                People = [new("p1", "田中", 100, 8), new("p2", "鈴木", 100, null)],
                Columns = [new(PlanField.Phase, "phase", "工程", "SINGLE_SELECT")] }));
    }
    [Test]
    public void ReadToolsReturnLocalScheduleRelationsAndBoundedPagesWithoutChangingDocument()
    {
        var document = Document(); var before = PlanJson.Text(document);
        var reader = new AssistPlanReader(document, Today);
        var page = reader.FindTasks(new() { Limit = 1 });
        Assert.That(page.Total, Is.EqualTo(3)); Assert.That(page.Complete, Is.False);
        var task = reader.GetTasks(["A"]).Items.Single();
        Assert.Multiple(() => {
            Assert.That(task.Task, Is.EqualTo(new AssistTaskReference("A", 2, "PS-004 設計")));
            Assert.That(task.Requirement!.Identity, Is.EqualTo("R"));
            Assert.That(task.Phase, Is.EqualTo("PS")); Assert.That(task.Assignees.Single().Name, Is.EqualTo("田中"));
            Assert.That(task.Estimate, Is.EqualTo(8)); Assert.That(task.Actual, Is.EqualTo(4));
            Assert.That(task.Remaining, Is.EqualTo(16)); Assert.That(task.Forecast, Is.EqualTo(20));
            Assert.That(task.End.Origin, Is.EqualTo(DateOrigin.Calculated));
            Assert.That(task.StartReason, Is.Not.Empty); Assert.That(task.EndReason, Is.Not.Empty);
            Assert.That(task.Successors.Single().Identity, Is.EqualTo("B"));
            Assert.That(task.PublishedDifferences.Any(d => d.Field == PlanField.End), Is.True);
            Assert.That(task.Lateness.Level, Is.EqualTo(PlanLatenessLevel.Overdue));
        });
        var summary = reader.SummarizePlan();
        Assert.That(summary.Measures.Total.Count, Is.EqualTo(2));
        Assert.That(summary.Overloads.Single().Identity, Is.EqualTo("p1"));
        Assert.That(summary.StatusDate, Is.EqualTo(Today));
        Assert.That(PlanJson.Text(document), Is.EqualTo(before));
    }
    [TestCase("open", 3), TestCase("closed", 0), TestCase("overdue", 1), TestCase("missing-estimate", 2), TestCase("missing-remaining", 1)]
    public void FindFiltersHaveExplicitMeaning(string state, int expected)
    {
        var d = Document(); d = d with { State = d.State with { Rows = d.State.Rows.SetItem(2, d.State.Rows[2] with { Estimate = null }) } };
        Assert.That(new AssistPlanReader(d, Today).FindTasks(new() { State = state }).Total, Is.EqualTo(expected));
    }
    [Test]
    public void UnknownIdentitiesAndUnboundedRequestsAreRejected()
    {
        var reader = new AssistPlanReader(Document(), Today);
        Assert.Throws<ArgumentException>(() => reader.GetTasks(["elsewhere"]));
        Assert.Throws<ArgumentException>(() => reader.GetTasks(Enumerable.Repeat("A", 21).ToArray()));
        Assert.Throws<ArgumentException>(() => reader.FindTasks(new() { Requirement = "elsewhere" }));
        Assert.Throws<ArgumentException>(() => reader.FindTasks(new() { State = "invented" }));
        Assert.Throws<ArgumentException>(() => reader.FindTasks(new() { Limit = 101 }));
    }
    [Test]
    public void CombinedSearchUsesLocalValuesStatusDateAndPublishedLateness()
    {
        var d = Document(); d = d with { State = d.State with { Rows = d.State.Rows.SetItem(1, d.State.Rows[1] with { Title = "更新した設計" }) } };
        var reader = new AssistPlanReader(d, Today.AddDays(10));
        var filter = new AssistFindTasks { Title = "更新", Phase = "PS", Assignee = "田中", Requirement = "R", From = Today, To = Today, State = "unpublished" };
        Assert.That(reader.FindTasks(filter).Items.Select(t => t.Identity), Is.EqualTo(new[] { "A" }));
        Assert.That(reader.FindTasks(filter with { State = "later-than-published" }).Items.Select(t => t.Identity), Is.EqualTo(new[] { "A" }));
        Assert.That(reader.SummarizePlan().StatusDate, Is.EqualTo(Today));
        Assert.That(reader.GetTasks(["A"]).Items.Single().PublishedDifferences.Single(v => v.Field == PlanField.Title).Published, Is.EqualTo("PS-004 設計"));
    }
    [Test]
    public void ExistingExternalRelationsRemainWarningsWithoutExposingOutOfPlanTaskReferences()
    {
        var d = Document();
        var rows = d.State.Rows.SetItem(1, d.State.Rows[1] with { Parent = "external-parent", Predecessors = ["external-predecessor"] });
        d = d with { State = d.State with { Rows = rows }, Baseline = d.Baseline with { Rows = rows } };
        var reader = new AssistPlanReader(d, Today);
        Assert.That(reader.FindTasks(new() { Requirement = "R" }).Items.Select(r => r.Identity), Is.EqualTo(new[] { "B" }));
        var detail = reader.GetTasks(["A"]).Items.Single();
        Assert.That(detail.Requirement, Is.Null); Assert.That(detail.Predecessors, Is.Empty); Assert.That(detail.Warnings, Is.Not.Empty);
    }
    [Test]
    public void ManualSearchReturnsSourceMarkdownAndPages()
    {
        var result = AssistManual.Read("# 操作\n説明\n## 工数\n**残**を編集\n## 日程\n残を再計算", "残", 0, 1);
        Assert.That(result.Total, Is.EqualTo(2)); Assert.That(result.Complete, Is.False);
        Assert.That(result.Items.Single().Markdown, Does.Contain("**残**"));
        Assert.That(result.Items.Single().Markdown, Does.Not.Contain("<html"));
    }
    [Test]
    public void ProposalValidatesOneCandidateAndReportsScheduleEffectsWithoutMutation()
    {
        var d = Document(); var before = PlanJson.Text(d);
        var result = AssistProposal.Create(d, 7, Today, [new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(40m))]);
        Assert.That(result.Errors, Is.Empty); var proposal = result.Proposal!;
        Assert.That(proposal.Revision, Is.EqualTo(7));
        Assert.That(proposal.Changes.Single().Current, Is.EqualTo("16"));
        Assert.That(proposal.Changes.Single().Proposed, Is.EqualTo("40"));
        Assert.That(proposal.ScheduleEffect.Total, Is.EqualTo(2));
        Assert.That(proposal.ScheduleEffect.Items.Select(t => t.Task.Identity), Is.EquivalentTo(new[] { "A", "B" }));
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
    }
    [TestCase("A", "Remaining", "-1"), TestCase("A", "Title", "\"bad\\nline\""), TestCase("A", "Remaining", "\"16\""),
     TestCase("A", "Parent", "\"B\""), TestCase("elsewhere", "Title", "\"x\""),
     TestCase("A", "Predecessors", "[\"elsewhere\"]"), TestCase("A", "Predecessors", "[\"B\"]"),
     TestCase("A", "Assignees", "[\"unknown-person\"]"), TestCase("A", "Phase", "\"unknown\"")]
    public void InvalidProposalHasErrorsAndNoCandidate(string id, string field, string json)
    {
        var d = Document(); var before = PlanJson.Text(d);
        var result = AssistProposal.Create(d, 0, Today, [new(id, field, JsonDocument.Parse(json).RootElement.Clone())]);
        Assert.That(result.Proposal, Is.Null); Assert.That(result.Errors, Is.Not.Empty);
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
    }
    [Test]
    public void HeldDatePreviewMatchesTheOrdinaryEditRelease()
    {
        var d = Document() with { Dates = new(PlanDateHoldOrigin.FirstTakeIn) };
        var result = AssistProposal.Create(d, 0, Today, [new("A", PlanField.Title, JsonSerializer.SerializeToElement("変更"))]);
        Assert.That(result.Proposal!.ScheduleEffect.Total, Is.GreaterThan(0));
    }
    [Test]
    public void ProposalShowsScheduledCurrentDateRatherThanTheOldPublishedInput()
    {
        var d = Document();
        var proposal = AssistProposal.Create(d, 0, Today, [new("B", PlanField.Start, JsonSerializer.SerializeToElement("2026-10-26"))]).Proposal!;
        Assert.That(proposal.Changes.Single().Current, Is.EqualTo("2026-10-19"));
    }
    [Test]
    public void TypedProposalAcceptsTheSameWholeOperationAsSheetInputIncludingDateAndAssigneeChanges()
    {
        var d = Document();
        AssistEdit Change(string field, object? value) => new("B", field, JsonSerializer.SerializeToElement(value));
        var result = AssistProposal.Create(d, 3, Today, [Change(PlanField.Assignees, new[] { "p1" }), Change(PlanField.Phase, "PS"),
            Change(PlanField.Estimate, 40m), Change(PlanField.Actual, 8m), Change(PlanField.Remaining, 32m),
            Change(PlanField.Start, "2026-10-26"), Change(PlanField.End, "2026-10-30"), Change(PlanField.Fixed, true),
            Change(PlanField.Predecessors, new[] { "A" }), Change(PlanField.Title, "再計画")]);
        Assert.That(result.Errors, Is.Empty);
        var after = PlanOperations.Apply(d, result.Proposal!.Command, Today).State.Rows.Single(r => r.Identity == "B");
        Assert.Multiple(() => {
            Assert.That(after.Assignees, Is.EqualTo(new[] { "p1" })); Assert.That(after.Phase, Is.EqualTo("PS"));
            Assert.That(after.Estimate, Is.EqualTo(40)); Assert.That(after.Actual, Is.EqualTo(8)); Assert.That(after.Remaining, Is.EqualTo(32));
            Assert.That(after.StartNoEarlierThan, Is.EqualTo(new DateOnly(2026, 10, 26))); Assert.That(after.Fixed, Is.True);
            Assert.That(after.End, Is.EqualTo(new DateOnly(2026, 10, 30))); Assert.That(after.Predecessors, Is.EqualTo(new[] { "A" }));
            Assert.That(after.Title, Is.EqualTo("再計画"));
        });
    }
    [Test]
    public void ScheduleEffectReportsFullCountButOnlyFirstTenAffectedTasks()
    {
        var d = Document();
        var rows = d.State.Rows.AddRange(Enumerable.Range(1, 15).Select(n => new PlanRow("C" + n, "後続" + n, "acme/work") {
            Parent = "R", Estimate = 8, Remaining = 8, Assignees = ["p2"], Predecessors = ["A"] }));
        d = d with { State = d.State with { Rows = rows }, Baseline = d.Baseline with { Rows = rows } };
        var effect = AssistProposal.Create(d, 0, Today, [new("A", PlanField.Remaining, JsonSerializer.SerializeToElement(40m))]).Proposal!.ScheduleEffect;
        Assert.That(effect.Total, Is.EqualTo(17)); Assert.That(effect.Items.Count, Is.EqualTo(10)); Assert.That(effect.Complete, Is.False);
    }
    [Test]
    public void RuntimeEnvironmentRemovesCredentialsAndOverridesWithoutChangingSource()
    {
        var source = new Dictionary<string, string> { ["PATH"] = "tools", ["GH_CONFIG_DIR"] = "gh-config", ["GH_TOKEN"] = "sentinel",
            ["GITHUB_ENTERPRISE_TOKEN"] = "sentinel", ["OPENCODE_GO_API_KEY"] = "sentinel", ["my_api_key_override"] = "sentinel",
            ["COPILOT_CLI_PATH"] = "injected", ["COPILOT_API_URL"] = "injected", ["OPENAI_BASE_URL"] = "injected", ["ANTHROPIC_AUTH_TOKEN"] = "sentinel",
            ["NODE_OPTIONS"] = "injected", ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "injected" };
        var result = AssistEnvironment.Curate(source);
        Assert.That(result.Keys, Is.EquivalentTo(new[] { "PATH", "GH_CONFIG_DIR" }));
        Assert.That(source.Count, Is.EqualTo(12));
    }
    [TestCase("gh-cli", "github.com", "pmo", true), TestCase("token", "github.com", "pmo", false),
     TestCase("gh-cli", "other.example", "pmo", false), TestCase("gh-cli", "github.com", "other", false)]
    public void CopilotIdentityMustMatchConnectedViewer(string type, string host, string login, bool accepted)
    {
        var failure = AssistAuthentication.Validate(true, type, host, login, "github.com", "pmo");
        Assert.That(failure is null, Is.EqualTo(accepted));
    }
}
