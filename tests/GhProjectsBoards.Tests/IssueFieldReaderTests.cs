using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;
using static GhProjectsBoards.Tests.ProjectReaderTests;

namespace GhProjectsBoards.Tests;

// #258: organization Issue Fields added to a Project are read like Project fields, with the organization field
// ID and options that only the organization field reports.
[TestFixture]
[Category("Integration")]
internal sealed class IssueFieldReaderTests
{
    private static readonly ConnectionScope Scope = new("github.com", 42);

    private static object IssueField(string id, string name, string dataType, string? issueType, object[]? options = null)
        => new
        {
            __typename = dataType is "SINGLE_SELECT" or "MULTI_SELECT" ? "ProjectV2SingleSelectField" : "ProjectV2Field",
            id, name, dataType, isIssueField = true, project = new { id = "P1" },
            // The Project side of an Issue Field never lists options.
            options = Array.Empty<object>(),
            issueField = issueType is null ? null : (object)new { __typename = issueType, id = "IF-" + id, options = options ?? [] }
        };
    private static object IssueValue(string field, object? inner)
        => new { __typename = "ProjectV2ItemIssueFieldValue", field = new { id = field, project = new { id = "P1" } }, issueFieldValue = inner };
    private static readonly object[] PriorityOptions = [new { id = "IFSSO_1", name = "High" }, new { id = "IFSSO_2", name = "Low" }];

    private static async Task<ProjectReadResult> Read(object[] fields, object[] values)
    {
        var boundary = new ProjectBoundary { Override = (query, _) =>
            query.Contains("ProjectFields") ? Response(Project("P1", "fields", Page(fields, fields.Length)))
            : query.Contains("ProjectItems") ? Response(Project("P1", "items", Page([Item("P1", values: Page(values, values.Length))], 1)))
            : null };
        var service = new GhConnectionService("gh.exe", Scope.Host, boundary.Runner);
        var context = (await service.ConnectAsync()).Context!;
        var result = await new ProjectReader(service).ReadAsync(context, new(Scope, "P1"));
        boundary.AssertQueriesOnly();
        return result;
    }

    [Test]
    public async Task SupportedIssueFieldsArePresentWithTheOrganizationIdAndItsOptions()
    {
        var result = await Read([
            Field("P1", "P1-status"),
            IssueField("start", "Start date", "DATE", "IssueFieldDate"),
            IssueField("effort", "Effort", "NUMBER", "IssueFieldNumber"),
            IssueField("note", "Note", "TEXT", "IssueFieldText"),
            IssueField("priority", "Priority", "SINGLE_SELECT", "IssueFieldSingleSelect", PriorityOptions),
            IssueField("areas", "Areas", "MULTI_SELECT", "IssueFieldMultiSelect"),
            IssueField("hidden", "Hidden", "DATE", null)], []);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        var fields = result.Project!.Fields.ToDictionary(f => f.Id.NodeId);
        Assert.Multiple(() =>
        {
            foreach (var id in new[] { "start", "effort", "note", "priority" })
            {
                Assert.That(fields[id].Availability, Is.EqualTo(ValueAvailability.Present), id);
                Assert.That(fields[id].IssueFieldId, Is.EqualTo("IF-" + id), id);
                Assert.That(fields[id].ValueOwner, Is.EqualTo(FieldOwner.Issue), id);
            }
            Assert.That(fields["priority"].Options, Is.EqualTo(new[] { new SelectOption("IFSSO_1", "High"), new SelectOption("IFSSO_2", "Low") }));
            // A multi-select field has no plan value and a field without its organization definition cannot be written.
            Assert.That(fields["areas"].Availability, Is.EqualTo(ValueAvailability.Unsupported));
            Assert.That(fields["hidden"].Availability, Is.EqualTo(ValueAvailability.Unsupported));
            Assert.That(fields["P1-status"].IssueFieldId, Is.Null);
        });
    }

    [TestCase("DATE", "IssueFieldDate", "IssueFieldDateValue", "\"2026-10-22\"", ValueAvailability.Present, "2026-10-22", null)]
    [TestCase("NUMBER", "IssueFieldNumber", "IssueFieldNumberValue", "3.50", ValueAvailability.Present, "3.5", null)]
    [TestCase("TEXT", "IssueFieldText", "IssueFieldTextValue", "\"メモ\"", ValueAvailability.Present, "メモ", null)]
    [TestCase("SINGLE_SELECT", "IssueFieldSingleSelect", "IssueFieldSingleSelectValue", "\"IFSSO_2\"", ValueAvailability.Present, null, "IFSSO_2")]
    [TestCase("SINGLE_SELECT", "IssueFieldSingleSelect", "IssueFieldSingleSelectValue", "\"IFSSO_9\"", ValueAvailability.Unavailable, null, null)]
    [TestCase("DATE", "IssueFieldDate", "IssueFieldDateValue", "\"22/10/2026\"", ValueAvailability.Unavailable, null, null)]
    [TestCase("DATE", "IssueFieldDate", "IssueFieldNumberValue", "3", ValueAvailability.Unavailable, null, null)]
    public async Task IssueFieldValuesReadFromTheValueInsideTheProjectItem(string dataType, string issueType, string valueType, string raw,
        ValueAvailability availability, string? scalar, string? option)
    {
        var inner = new System.Text.Json.Nodes.JsonObject { ["__typename"] = valueType, ["id"] = "IFV_1",
            [valueType == "IssueFieldSingleSelectValue" ? "optionId" : "value"] = System.Text.Json.Nodes.JsonNode.Parse(raw) };
        var result = await Read([Field("P1", "P1-status"), IssueField("f", "Field", dataType, issueType, PriorityOptions)], [IssueValue("f", inner)]);
        var value = result.Project!.Items.Single().Values.Single(v => v.FieldId!.NodeId == "f");
        Assert.Multiple(() =>
        {
            Assert.That((value.Availability, value.Scalar, value.OptionId), Is.EqualTo((availability, scalar, option)));
            Assert.That(value.ValueId, Is.EqualTo("IFV_1"), "The value ID is the inner value's; the wrapper has none.");
            Assert.That(result.Outcome, Is.EqualTo(availability == ValueAvailability.Present ? ProjectReadOutcome.Complete : ProjectReadOutcome.Partial));
        });
    }

    [TestCase(true), TestCase(false)]
    public async Task AnIssueWithoutAnIssueFieldValueReadsAsEmpty(bool wrapperReturned)
    {
        var result = await Read([Field("P1", "P1-status"), IssueField("start", "Start date", "DATE", "IssueFieldDate")],
            wrapperReturned ? [IssueValue("start", null)] : []);
        Assert.That(result.Outcome, Is.EqualTo(ProjectReadOutcome.Complete), result.ToString());
        Assert.That(result.Project!.Items.Single().Values.Single(v => v.FieldId!.NodeId == "start").Availability, Is.EqualTo(ValueAvailability.Empty));
    }

    [Test]
    public async Task MappedIssueFieldsFillThePlanAndOnlyReadableFieldsAreOffered()
    {
        object Inner(string type, string key, object value) => new System.Text.Json.Nodes.JsonObject { ["__typename"] = type, ["id"] = "IFV-" + key,
            [type == "IssueFieldSingleSelectValue" ? "optionId" : "value"] = JsonSerializer.SerializeToNode(value) };
        var result = await Read([
                Field("P1", "P1-status"),
                IssueField("start", "Start date", "DATE", "IssueFieldDate"),
                IssueField("target", "Target date", "DATE", "IssueFieldDate"),
                IssueField("priority", "Priority", "SINGLE_SELECT", "IssueFieldSingleSelect", PriorityOptions),
                IssueField("areas", "Areas", "MULTI_SELECT", "IssueFieldMultiSelect")],
            [IssueValue("start", Inner("IssueFieldDateValue", "s", "2026-10-20")), IssueValue("priority", Inner("IssueFieldSingleSelectValue", "p", "IFSSO_1"))]);
        var settings = new ProjectPlanSettings { Columns = [new(PlanField.Start, "start", "Start date", "DATE"),
            new(PlanField.End, "target", "Target date", "DATE"), new(PlanField.Phase, "priority", "Priority", "SINGLE_SELECT")] };
        var snapshot = PlanSnapshot.From(result, settings);
        var row = snapshot.Baseline.Rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That((row.Start, row.End, row.Phase), Is.EqualTo(((DateOnly?)new DateOnly(2026, 10, 20), (DateOnly?)null, "High")));
            Assert.That(snapshot.Baseline.Columns.Where(c => c.IssueField).Select(c => c.Id), Is.EqualTo(new[] { "start", "target", "priority" }),
                "A field the app cannot read is never offered as a column or a mapping.");
            Assert.That(snapshot.Baseline.Columns.Single(c => c.Id == "priority").Options, Is.EqualTo(new[] { "High", "Low" }));
            Assert.That(snapshot.Baseline.Columns.Single(c => c.Id == "P1-status").IssueField, Is.False);
        });
    }

    [Test]
    public async Task OpeningAProjectAutoMapsIssueFieldsByNameAndLeavesAmbiguousNamesUnmapped()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-issue-field-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The workspace fixture's Project fields are named Start date and Target date, so an Issue Field named
            // Start date makes that name ambiguous, while 工程 exists only as an Issue Field.
            File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
            FakePlanEditor.Save(root, new([new(new("I1", "Task", "acme/repo") { ExtraValues = ImmutableDictionary<string, PlanExtraValue>.Empty
                .Add("Project:IF-phase", new(ValueAvailability.Present, "\"設計\"")) }, "", true)], 2) {
                ExtraFields = [new("IF-start", "Start date", "DATE") { IssueField = true },
                    new("IF-phase", "工程", "SINGLE_SELECT") { IssueField = true, Options = ["設計", "実装"] }] });
            var workspace = new PlanWorkspace(new(root), () => new(2026, 10, 22));
            await workspace.Connect(new(GhProcessTests.FakeExecutable, "github.com", new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root })));
            await workspace.Open((await workspace.Listings("fixture-user"))[0].Choice);
            var document = workspace.Session!.Document;
            Assert.Multiple(() =>
            {
                Assert.That(document.State.Settings.Columns.SingleOrDefault(c => c.Role == PlanField.Phase)?.FieldId, Is.EqualTo("IF-phase"));
                Assert.That(document.State.Settings.Columns.Any(c => c.Role == PlanField.Start), Is.False, "Two fields named Start date are ambiguous.");
                Assert.That(document.State.Settings.Columns.SingleOrDefault(c => c.Role == PlanField.End)?.FieldId, Is.EqualTo("F-End"));
                Assert.That(document.State.Rows.Single().Phase, Is.EqualTo("設計"));
            });
        }
        finally { Directory.Delete(root, true); }
    }
}
