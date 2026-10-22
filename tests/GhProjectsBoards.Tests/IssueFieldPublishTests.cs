using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #258: organization Issue Field columns are written on the Issue with setIssueFieldValue and go through the
// same ledger, verification and conflict handling as Project field writes.
internal sealed partial class PlanPublisherTests
{
    private static ProjectPlanSettings IssueFieldSettings => Settings with
    {
        Columns = Settings.Columns.Where(c => c.Role is not (PlanField.Estimate or PlanField.Start or PlanField.End))
            .Concat([new(PlanField.Estimate, "X-N", "Effort", "NUMBER"), new(PlanField.Start, "X-S", "Start date", "DATE"),
                new(PlanField.End, "X-E", "Target date", "DATE"), new(PlanField.Phase, "X-P", "Priority", "SINGLE_SELECT")]).ToImmutableArray()
    };
    private Task StartWithIssueFields() => Start(2, state => state with
    {
        ExtraFields = [new("X-N", "Effort", "NUMBER") { IssueField = true }, new("X-S", "Start date", "DATE") { IssueField = true },
            new("X-E", "Target date", "DATE") { IssueField = true }, new("X-T", "Note", "TEXT") { IssueField = true },
            new("X-P", "Priority", "SINGLE_SELECT") { IssueField = true, Options = ["High", "Low"] },
            new("X-M", "Areas", "MULTI_SELECT") { IssueField = true }],
        Issues = state.Issues.Select(i => i.Row.Identity != "I1" ? i : i with { Row = i.Row with { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty
            .Add("Project:X-P", new(ValueAvailability.Present, "\"Low\"")).Add("Project:X-N", new(ValueAvailability.Present, "2")) } }).ToImmutableArray()
    }, IssueFieldSettings);
    private IReadOnlyList<(string Mutation, JsonElement Input)> SentWrites() => !File.Exists(Path.Combine(root, "plan-mutations.jsonl")) ? []
        : File.ReadAllLines(Path.Combine(root, "plan-mutations.jsonl")).SelectMany(line =>
        {
            var payload = JsonDocument.Parse(line).RootElement;
            var query = payload.GetProperty("query").GetString()!;
            return System.Text.RegularExpressions.Regex.Matches(query, @"w\d+:(\w+)\(input:\$(v\d+)\)")
                .Select(m => (m.Groups[1].Value, payload.GetProperty("variables").GetProperty(m.Groups[2].Value)));
        }).ToArray();
    private PlanRow Remote(string identity) => FakePlanEditor.Load(root).Issues.Single(i => i.Row.Identity == identity).Row;

    [Test]
    public async Task IssueFieldColumnsReadPublishOnTheIssueAndClearWithDelete()
    {
        await StartWithIssueFields();
        var row = session.Document.State.Rows.Single(r => r.Identity == "I1");
        Assert.That((row.Phase, row.Estimate), Is.EqualTo(("Low", (decimal?)2m)), "Mapped Issue Field values are read into the plan.");
        await session.Execute(new EditPlanCells(PlanOperationKind.Paste, [new("I1", PlanField.Phase, "High"), new("I1", PlanField.Estimate, 8m),
            new("I1", "Project:X-T", "メモ")]), Today);
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.True, result.Error);
        Assert.Multiple(() =>
        {
            Assert.That(PlanValues.Get(Remote("I1"), "Project:X-P"), Is.EqualTo("\"High\""));
            Assert.That(PlanValues.Get(Remote("I1"), "Project:X-N"), Is.EqualTo("8"));
            Assert.That(JsonSerializer.Deserialize<string>(PlanValues.Get(Remote("I1"), "Project:X-T")), Is.EqualTo("メモ"));
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
        });
        var writes = SentWrites();
        var issueWrites = writes.Where(w => w.Mutation == "setIssueFieldValue").ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(issueWrites.Select(w => w.Input.GetProperty("issueFields")[0].GetProperty("fieldId").GetString()),
                Is.SupersetOf(new[] { "IF-X-P", "IF-X-N", "IF-X-T" }));
            Assert.That(issueWrites.All(w => w.Input.GetProperty("issueId").GetString() == "I1" || w.Input.GetProperty("issueId").GetString() == "I2"), Is.True);
            Assert.That(issueWrites.Single(w => w.Input.GetProperty("issueFields")[0].GetProperty("fieldId").GetString() == "IF-X-P")
                .Input.GetProperty("issueFields")[0].GetProperty("singleSelectOptionId").GetString(), Is.EqualTo("IFO-High"));
            Assert.That(writes.Where(w => w.Mutation.Contains("ProjectV2ItemFieldValue")).Select(w => w.Input.GetProperty("fieldId").GetString()),
                Has.None.StartsWith("X-"), "An Issue Field is never written as a Project item field.");
        });

        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, null)]), Today);
        var sent = SentWrites().Count;
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        var clear = SentWrites().Skip(sent).Single();
        Assert.That(clear.Mutation, Is.EqualTo("setIssueFieldValue"));
        Assert.That(clear.Input.GetProperty("issueFields")[0].GetProperty("delete").GetBoolean(), Is.True);
        Assert.That(PlanValues.Get(Remote("I1"), "Project:X-P"), Is.EqualTo("null"));

        var batches = FakePlanEditor.Load(root).MutationBatches;
        Assert.That((await publisher.RefreshAsync(session, Today)).Succeeded, Is.True);
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(batches), "Nothing remains to publish after the read-back.");
    }

    [Test]
    public async Task AnUnknownIssueFieldOutcomeIsVerifiedByTheNextReadAndNotResent()
    {
        await StartWithIssueFields();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "High")]), Today);
        Scenario("after");
        var first = await publisher.PublishAsync(session, Today);
        Assert.That(first.Succeeded, Is.False);
        Assert.That(PlanValues.Get(Remote("I1"), "Project:X-P"), Is.EqualTo("\"High\""), "GitHub applied the write although its response was lost.");
        Scenario();
        var sent = SentWrites().Count;
        var refresh = await publisher.RefreshAsync(session, Today);
        Assert.That(refresh.Succeeded, Is.True, refresh.Error);
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Sync.Unverified, Is.Empty);
            Assert.That(session.Document.Sync.Publish, Is.Null);
            Assert.That(session.Changes(Today).TaskCount, Is.Zero);
            Assert.That(session.Document.State.Rows.Single(r => r.Identity == "I1").Phase, Is.EqualTo("High"));
        });
        Assert.That((await publisher.PublishAsync(session, Today)).Succeeded, Is.True);
        Assert.That(SentWrites().Count, Is.EqualTo(sent), "A verified write is not sent again.");
    }

    [Test]
    public async Task AnExternalIssueFieldChangeIsAConflictAndIsNotOverwritten()
    {
        await StartWithIssueFields();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "High")]), Today);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity != "I1" ? i
            : i with { Row = PlanValues.Set(i.Row, "Project:X-P", "null") }).ToImmutableArray() });
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(PlanOperations.BlockingConflicts(session.Document).Select(c => (c.Identity, c.Field)), Is.EqualTo(new[] { ("I1", PlanField.Phase) }));
        Assert.That(PlanValues.Get(Remote("I1"), "Project:X-P"), Is.EqualTo("null"));
        Assert.That(SentWrites().Where(w => w.Mutation == "setIssueFieldValue"), Is.Empty);
    }

    [Test]
    public async Task AnIssueWhoseFieldsTheViewerCannotSetStopsThePublishBeforeAnyWrite()
    {
        await StartWithIssueFields();
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "High")]), Today);
        Scenario("no-set-fields");
        var result = await publisher.PublishAsync(session, Today);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Does.Contain("「Task 1」の Issue フィールドを変更する権限がありません"));
        Assert.That(SentWrites(), Is.Empty);
        Assert.That(session.Changes(Today).TaskCount, Is.EqualTo(1), "The edit stays unpublished for the PMO to undo.");
    }

    [Test]
    public async Task EveryFieldOfferedForAPlanColumnReadsBack()
    {
        await StartWithIssueFields();
        // The same choices the settings drop-down lists: the Project's readable fields of the role's data type.
        foreach (var role in PlanColumnMatching.Roles)
            foreach (var choice in session.Document.Baseline.Columns.Where(c => c.DataType == role.Type))
            {
                var columns = session.Document.State.Settings.Columns.Where(c => c.Role != role.Role && c.FieldId != choice.Id)
                    .Append(new PlanColumnMapping(role.Role, choice.Id, choice.Name, choice.DataType)).ToImmutableArray();
                Assert.That((await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { Columns = columns }), Today)).Succeeded, Is.True);
                var refresh = await publisher.RefreshAsync(session, Today);
                Assert.That(refresh.Succeeded, Is.True, $"{role.Role} → {choice.Name}: {refresh.Error}");
            }
        Assert.That(session.Document.Baseline.Columns.Select(c => c.Id), Does.Not.Contain("X-M"), "A multi-select Issue Field is never offered.");
        Assert.That(SentWrites(), Is.Empty);
    }
}
