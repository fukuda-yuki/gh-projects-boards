using System.Text;
using System.Collections.Immutable;
using GhProjectsBoards.Core.Projects;
using System.Text.Json.Nodes;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanColumnSettingsTests
{
    [Test]
    public void ColumnChoicesRoundTripThroughStrictSettingsAndOneUndoPatch()
    {
        var settings = new ProjectPlanSettings();
        var json = JsonNode.Parse(PlanJson.Text(settings))!;
        json["sheetColumns"] = JsonNode.Parse("""[{"key":"Title","visible":true,"displayName":"作業"},{"key":"Id","visible":true,"displayName":null}]""");
        var changed = PlanJson.Read<ProjectPlanSettings>(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var row = new PlanRow("I1", "Task", "acme/repo");
        var document = new PlanDocument(new(new("github.com", 1), "P1"), new([row], []), new([row], settings));
        var after = PlanOperations.Apply(document, new ReplacePlanSettings(changed), new(2026, 10, 12)).State;
        var patch = PlanOperations.Difference(document.State, after, PlanOperationKind.Settings);
        Assert.That(patch.Rows, Is.Empty);
        Assert.That(PlanJson.Read<ProjectPlanSettings>(Encoding.UTF8.GetBytes(PlanJson.Text(after.Settings))).SheetColumns[0].DisplayName, Is.EqualTo("作業"));
        Assert.That(PlanOperations.SettingsEqual(PlanOperations.Replay(after, patch, false).Settings, settings), Is.True);
        Assert.That(PlanOperations.SettingsEqual(PlanOperations.Replay(document.State, patch, true).Settings, changed), Is.True);
    }

    [Test]
    public void ExtraNumberPasteRejectsAtomicallyAndHiddenValuesStayUnpublished()
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { ExtraValues = ImmutableDictionary<string,PlanExtraValue>.Empty.Add("Project:N", new(ValueAvailability.Empty,"null")) };
        var d = new PlanDocument(new(new("github.com", 1), "P1"), new([row], [new("N", "Risk", "NUMBER")]), new([row], new()));
        var day = new DateOnly(2026, 10, 12);
        var pasted = PlanSheetEditing.Paste(d, ["I1"], ["Project:N"], new(0,0), "-2.5", day);
        var state = PlanOperations.Apply(d, pasted, day).State;
        var changed = d with { State = state };
        Assert.That(PlanValues.Get(state.Rows[0], "Project:N"), Is.EqualTo("-2.5"));
        Assert.That(PlanOperations.Changes(changed, day).Fields["I1"], Does.Contain("Project:N"));
        Assert.That(() => PlanSheetEditing.Paste(d, ["I1"], ["Project:N"], new(0,0), "bad", day), Throws.ArgumentException);
        Assert.That(PlanOperations.Replay(state, PlanOperations.Difference(d.State, state, PlanOperationKind.Paste), false).Rows, Is.EqualTo(d.State.Rows));
    }

    private static readonly DateOnly Day = new(2026,10,12);
    [Test]
    public void MappingRejectsUnpublishedExtraValuesWithoutChangingTheDocument()
    {
        var d = ExtraDocument();
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", "Project:N", 5m)]), Day).State };
        var before = PlanJson.Text(d);
        var settings = d.State.Settings with { Columns = [new(PlanField.Estimate, "N", "Risk", "NUMBER")] };
        Assert.That(() => PlanOperations.Apply(d, new ReplacePlanSettings(settings), Day),
            Throws.ArgumentException.With.Message.Contains("未発行"));
        Assert.That(PlanJson.Text(d), Is.EqualTo(before));
    }

    [Test]
    public void MappedFieldCannotBeEditedAsAnExtraAndPublishesOnlyThroughItsRole()
    {
        var d = ExtraDocument();
        var settings = d.State.Settings with { Columns = [new(PlanField.Estimate, "N", "Risk", "NUMBER")] };
        d = d with { State = PlanOperations.Apply(d, new ReplacePlanSettings(settings), Day).State };
        Assert.That(PlanSheetColumns.All(d).Any(c => c.Key == "Project:N"), Is.False);
        Assert.That(() => PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", "Project:N", 5m)]), Day), Throws.ArgumentException);
        d = d with { State = PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Estimate, 8m)]), Day).State };
        var snapshot = new PlanRemoteSnapshot(d.Baseline, ImmutableDictionary<string,string>.Empty.Add("I1","T1"),
            [new(new(d.Project.Scope,"N"),d.Project,"Risk","ProjectV2Field","NUMBER",FieldOwner.ProjectItem,[],ValueAvailability.Present)],0,0);
        var writes = PlanPublishPlan.Build(d,snapshot,Day,Guid.NewGuid().ToString("N")).Writes;
        Assert.That(writes, Has.Length.EqualTo(1));
        Assert.That(writes[0].Input, Does.Contain("\"fieldId\": \"N\"").And.Contain("\"number\": 8"));
    }
    private static PlanDocument ExtraDocument()
    {
        var row = new PlanRow("I1", "Task", "acme/repo") { ExtraValues = ImmutableDictionary<string,PlanExtraValue>.Empty
            .Add("Project:T",new(ValueAvailability.Empty,"null")).Add("Project:N",new(ValueAvailability.Empty,"null")) };
        return new(new(new("github.com",1),"P1"), new([row], [new("T","Notes","TEXT"),new("N","Risk","NUMBER"),
            new("D","Review","DATE"),new("S","Priority","SINGLE_SELECT") { Options = ["High","Low"] },new("I","Sprint","ITERATION")]) {
            RepositoryMilestones = [new("acme/repo","M1","Release"),new("acme/other","M2","Other")],
            RepositoryLabels = [new("acme/repo","L1","bug"),new("acme/repo","L2","urgent")] }, new([row], new()));
    }
    [TestCase("Id"), TestCase("Title")]
    public void RequiredColumnsCannotBeHidden(string key)
        => Assert.That(() => PlanOperations.Apply(ExtraDocument(), new ReplacePlanSettings(new() { SheetColumns = [new(key,false,null)] }),Day), Throws.ArgumentException);

    [TestCase("Project:S"," high ","High"), TestCase("MilestoneTitle"," release ","Release")]
    public void ExistingNamesMatchIgnoringCase(string key,string input,string expected)
        => Assert.That(PlanSheetEditing.Parse(ExtraDocument(),key,input,Day,"I1"),Is.EqualTo(expected));
    [TestCase("Project:S","Unknown"), TestCase("MilestoneTitle","Other"),TestCase("Labels","new")]
    public void UnknownOrOtherRepositoryNamesAreRejected(string key,string input)
        => Assert.That(() => PlanSheetEditing.Parse(ExtraDocument(),key,input,Day,"I1"),Throws.ArgumentException);
    [Test]
    public void TextNumberDateSelectUseTypedValuesAndIterationRejectsClear()
    {
        var d=ExtraDocument();
        Assert.That(PlanSheetEditing.Parse(d,"Project:T","日本語",Day),Is.EqualTo("日本語"));
        Assert.That(PlanSheetEditing.Parse(d,"Project:N","-4",Day),Is.EqualTo(-4m));
        Assert.That(PlanSheetEditing.Parse(d,"Project:D","10/14",Day),Is.EqualTo(new DateOnly(2026,10,14)));
        Assert.That(() => PlanOperations.Apply(d,new ClearPlanCells(["I1"],["Project:I"]),Day),Throws.ArgumentException);
    }
    [Test]
    public void LabelsNormalizeAndClearAndSummaryRejectsWholeRange()
    {
        var d=ExtraDocument();
        var value=PlanSheetEditing.Parse(d,PlanField.Labels," BUG, urgent,bug ",Day,"I1");
        var state=PlanOperations.Apply(d,new EditPlanCells(PlanOperationKind.Cell,[new("I1",PlanField.Labels,value)]),Day).State;
        Assert.That(state.Rows[0].Labels,Is.EqualTo(new[]{"bug","urgent"}));
        Assert.That(PlanOperations.Changes(d with { State=state },Day).TaskCount,Is.EqualTo(1));
        Assert.That(PlanOperations.Apply(d with {State=state},new ClearPlanCells(["I1"],[PlanField.Labels]),Day).State.Rows[0].Labels,Is.Empty);
        d=d with {State=d.State with { Rows=d.State.Rows.Add(new("local:"+Guid.NewGuid().ToString("N"),"Child","acme/repo") {Parent="I1"})}};
        Assert.That(() => PlanOperations.Apply(d,new EditPlanCells(PlanOperationKind.Paste,[new("I1",PlanField.Title,"Renamed"),new("I1","Project:T","text")]),Day),Throws.ArgumentException);
    }
    [Test]
    public void ExtraFieldsMergeIndependentlyAndKeepHiddenLocalEdits()
    {
        var d=ExtraDocument();
        var local=PlanValues.Set(d.State.Rows[0],"Project:T","\"local\"");
        var remote=PlanValues.Set(PlanValues.Set(d.Baseline.Rows[0],"Project:T","\"remote\""),"Project:N","3");
        var merged=PlanMerge.Merge(d with {State=d.State with {Rows=[local]}},d.Baseline with {Rows=[remote]});
        Assert.That(merged.Sync.Conflicts.Single().Field,Is.EqualTo("Project:T"));
        Assert.That(PlanValues.Get(merged.State.Rows[0],"Project:T"),Is.EqualTo("\"local\""));
        Assert.That(PlanValues.Get(merged.State.Rows[0],"Project:N"),Is.EqualTo("3"));
        Assert.That(merged.State.Settings.SheetColumns,Is.Empty,"Visibility does not control retained edits.");
    }
    [Test]
    public void ExtraPublicationUsesExistingMutationsAndReadbackWithoutCreationOrTransfer()
    {
        var d=ExtraDocument();
        var state=PlanOperations.Apply(d,new EditPlanCells(PlanOperationKind.Paste,[new("I1","Project:T","note"),
            new("I1","Project:N",-3m),new("I1",PlanField.MilestoneTitle,"release"),new("I1",PlanField.Labels,new[]{"BUG"})]),Day).State;
        d=d with {State=state};
        var fields=d.Baseline.Columns.Select(c => new ProjectFieldDefinition(new(d.Project.Scope,c.Id),d.Project,c.Name,"ProjectV2Field",c.DataType,
            FieldOwner.ProjectItem,c.Options.Select(o=>new SelectOption("option-"+o,o)).ToArray(),ValueAvailability.Present)).ToImmutableArray();
        var snapshot=new PlanRemoteSnapshot(d.Baseline,ImmutableDictionary<string,string>.Empty.Add("I1","T1"),fields,0,0);
        var review=PlanPublishPlan.Build(d,snapshot,Day,Guid.NewGuid().ToString("N"));
        Assert.That(review.Writes.Select(w=>w.Mutation),Is.EquivalentTo(new[]{"updateProjectV2ItemFieldValue","updateProjectV2ItemFieldValue","updateIssue","addLabelsToLabelable"}));
        var observed=snapshot with {Baseline=d.Baseline with {Rows=state.Rows}};
        foreach(var write in review.Writes) {
            Assert.That(PlanVerification.Verify(write,new("run",review.Writes),observed,state.Settings),Is.True,write.Input);
            Assert.That(PlanVerification.Verify(write,new("run",review.Writes),snapshot,state.Settings),Is.False,write.Input);
        }
        PlanPublishPlan.Validate(new(Guid.NewGuid().ToString("N"),review.Writes),d);
        Assert.That(PlanPublishPlan.Build(d with {Baseline=observed.Baseline},observed,Day,"none").Writes,Is.Empty);
    }
    [Test]
    public void DisplayChoicesReorderRenameResetWithoutDroppingInputs()
    {
        var d=ExtraDocument();
        var settings=PlanSheetColumns.Change(d,"Project:T",true,"メモ",true);
        d=d with {State=d.State with {Settings=settings}};
        d=d with {State=d.State with {Settings=PlanSheetColumns.Move(d,"Project:T",0)}};
        Assert.That(PlanSheetColumns.All(d)[0].Name,Is.EqualTo("メモ"));
        d=d with {State=d.State with {Settings=PlanSheetColumns.Change(d,"Project:T",name:"",rename:true)}};
        Assert.That(PlanSheetColumns.All(d)[0].Name,Is.EqualTo("Notes"));
        d=d with {State=d.State with {Settings=d.State.Settings with {SheetColumns=[]}}};
        Assert.That(PlanSheetColumns.All(d).Where(c=>c.Visible).Count(),Is.EqualTo(11));
        Assert.That(d.State.Rows[0].ExtraValues.Count,Is.EqualTo(2));
    }

    [Test]
    public void LocalMilestoneMembershipUpdatesTargetsWithoutChangingSchedule()
    {
        var d=ExtraDocument();
        d=d with {Baseline=d.Baseline with {Milestones=[new("Release",Day,false,false)]}};
        var schedule=PlanOperations.Schedule(d,Day);
        var state=PlanOperations.Apply(d,new EditPlanCells(PlanOperationKind.Cell,[new("I1",PlanField.MilestoneTitle,"Release")]),Day).State;
        var changed=d with {State=state};
        Assert.That(PlanSummary.CalculateMilestones(changed,PlanOperations.Schedule(changed,Day)).Single().Tasks,Is.EqualTo(new[]{"I1"}));
        Assert.That(PlanOperations.Schedule(changed,Day).Select(t=>(t.Start,t.End)),Is.EqualTo(schedule.Select(t=>(t.Start,t.End))));
        Assert.That(PlanSummary.CalculateMilestones(d,schedule).Single().Tasks,Is.Empty);
    }

    [Test]
    public async Task ColumnSettingsPersistAcrossReopenAndPortableImportWithOneUndo()
    {
        var root=Path.Combine(Path.GetTempPath(),"ghpb-columns-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var d=ExtraDocument() with {Dates=new(PlanDateHoldOrigin.FirstTakeIn)};
            var session=await PlanSession.CreateAsync(new(root),d,Day);
            var settings=PlanSheetColumns.Change(d,"Project:T",true,"メモ",true);
            var saved=await session.Execute(new ReplacePlanSettings(settings),Day);
            Assert.That(saved.Succeeded,Is.True,saved.Error);
            Assert.That(session.UndoCount,Is.EqualTo(1));
            Assert.That(session.Document.Dates.Held,Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
            var file=Path.Combine(root,"settings.json");
            await session.ExportSettingsAsync(file);
            var reopened=await PlanSession.OpenAsync(new(root),d.Project,Day);
            Assert.That(reopened.Status,Is.EqualTo(PlanLoadStatus.Loaded),reopened.Error);
            Assert.That(PlanSheetColumns.All(reopened.Session!.Document).Single(c=>c.Key=="Project:T").Name,Is.EqualTo("メモ"));
            await session.Undo(Day);
            var imported=await session.ImportSettingsAsync(file,Day);
            Assert.That(imported.Applied,Is.True,imported.Error);
            Assert.That(session.UndoCount,Is.EqualTo(1));
            Assert.That(session.Document.Dates.Held,Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
        } finally { Directory.Delete(root,true); }
    }
}
