using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using System.Collections.Immutable;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanPhaseTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);
    private static PlanDocument Document() => new(new(new("github.com", 1), "P1"),
        new([new("I1", "Task", "acme/repo"), new("I2", "Other", "acme/repo")], [new("F", "工程", "SINGLE_SELECT") { Options = ["SA", "UI"] }]),
        new([new("I1", "Task", "acme/repo"), new("I2", "Other", "acme/repo")],
            new() { Columns = [new(PlanField.Phase, "F", "工程", "SINGLE_SELECT")] }));

    [TestCase(" sa ", "SA"), TestCase("ui", "UI"), TestCase(" ", null)]
    [TestCase("ｓａ", "SA"), TestCase("ＵＩ", "UI"), TestCase("　ｓａ　", "SA")]
    public void PhaseInputNormalizesNamesAndWhitespace(string text, string? expected)
        => Assert.That(PlanSheetEditing.Parse(Document(), PlanField.Phase, text, Day), Is.EqualTo(expected));

    [Test]
    public void UnknownPhaseExplainsOptionsAndUnmappedPhaseCannotBeCleared()
    {
        var d = Document();
        Assert.That(() => PlanSheetEditing.Parse(d, PlanField.Phase, "SS", Day), Throws.ArgumentException.With.Message.EqualTo("工程は SA, UI のどれかを入力してください。"));
        Assert.That(() => PlanSheetEditing.Parse(d, PlanField.Phase, "ｘｙ", Day), Throws.ArgumentException.With.Message.EqualTo("工程は SA, UI のどれかを入力してください。"));
        Assert.That(() => PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, 12m)]), Day), Throws.ArgumentException);
        var unmapped = d with { State = d.State with { Settings = new() } };
        // #236 part B: 工程-unmapped is a supported state, so a range clear skips the column instead of
        // refusing every editable cell beside it; an explicit edit of the column is still refused.
        var cleared = PlanOperations.Apply(unmapped, new ClearPlanCells(["I1"], [PlanField.Phase]), Day).State;
        Assert.That(cleared.Rows, Is.EqualTo(unmapped.State.Rows));
        Assert.That(() => PlanOperations.Apply(unmapped, new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "SA")]), Day), Throws.ArgumentException.With.Message.EqualTo("工程のフィールドが未設定です。"));
    }

    [Test]
    public void PhasePasteFillClearAndUndoPreserveOtherFields()
    {
        var d = Document();
        var pasted = PlanOperations.Apply(d, PlanSheetEditing.Paste(d, ["I1"], [PlanField.Phase], new(0, 0), "sa", Day), Day).State;
        var filled = PlanOperations.Apply(d with { State = pasted }, new FillPlanCells(PlanOperationKind.CtrlD, "I1", ["I2"], [PlanField.Phase]), Day).State;
        Assert.That(filled.Rows.Select(r => r.Phase), Is.EqualTo(new[] { "SA", "SA" }));
        var patch = PlanOperations.Difference(pasted, filled, PlanOperationKind.CtrlD);
        Assert.That(PlanOperations.Replay(filled, patch, false).Rows[1].Phase, Is.Null);
        var cleared = PlanOperations.Apply(d with { State = filled }, new ClearPlanCells(["I1"], [PlanField.Phase]), Day).State;
        Assert.That(cleared.Rows[0], Is.EqualTo(d.State.Rows[0]));
    }

    [Test]
    public void SummaryPhaseUsesBaselineAndRejectsEditing()
    {
        var d = Document();
        d = d with { Baseline = d.Baseline with { Rows = [d.Baseline.Rows[0] with { Phase = "SA" }] },
            State = d.State with { Rows = [d.State.Rows[0] with { Phase = "UI" }, d.State.Rows[1] with { Parent = "I1" }] } };
        Assert.That(PlanOperations.EffectiveRows(d)[0].Phase, Is.EqualTo("SA"));
        Assert.That(() => PlanOperations.Apply(d, new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Phase, "UI")]), Day), Throws.ArgumentException);
        Assert.That(PlanOperations.IsSummaryPublishExcluded(true, PlanField.Phase), Is.True);
    }

    [Test]
    public void ReversedPeriodIsRejectedAndEndDoesNotConstrainScheduling()
    {
        var d = Document();
        Assert.That(() => PlanOperations.Apply(d, new ReplacePlanSettings(d.State.Settings with { ProjectStart = Day, ProjectEnd = Day.AddDays(-1) }), Day), Throws.ArgumentException);
        var state = PlanOperations.Apply(d, new ReplacePlanSettings(d.State.Settings with { ProjectEnd = Day.AddDays(-1) }), Day).State;
        Assert.That(PlanOperations.Schedule(d with { State = state }, Day).Select(t => (t.Start, t.End)),
            Is.EqualTo(PlanOperations.Schedule(d, Day).Select(t => (t.Start, t.End))));
        Assert.That(PlanJson.Read<ProjectPlanSettings>(System.Text.Encoding.UTF8.GetBytes(PlanJson.Text(state.Settings))).ProjectEnd, Is.EqualTo(Day.AddDays(-1)));
    }

    [TestCase("UI", "updateProjectV2ItemFieldValue"), TestCase(null, "clearProjectV2ItemFieldValue")]
    public void PhaseReviewUsesNamesAndWritesAndVerifiesCurrentOptionIdentity(string? phase, string mutation)
    {
        var d = Document();
        d = d with { Baseline = d.Baseline with { Rows = d.Baseline.Rows.SetItem(0, d.Baseline.Rows[0] with { Phase = "SA" }) },
            State = d.State with { Rows = d.State.Rows.SetItem(0, d.State.Rows[0] with { Phase = phase }) } };
        var remote = new PlanRemoteSnapshot(d.Baseline, ImmutableDictionary<string, string>.Empty,
            [new(new(d.Project.Scope, "F"), d.Project, "工程", "ProjectV2SingleSelectField", "SINGLE_SELECT", FieldOwner.ProjectItem,
                [new("option-sa", "SA"), new("option-ui", "UI")], ValueAvailability.Present)], 0, 0);
        var review = PlanPublishPlan.Build(d, remote, Day, "phase");
        var change = review.Changes.Single();
        Assert.That((change.Field, change.Before, change.After), Is.EqualTo((PlanField.Phase, "\"SA\"", PlanJson.Text(phase))));
        var write = review.Writes.Single();
        Assert.That(write.Mutation, Is.EqualTo(mutation));
        if (phase is not null) Assert.That(write.Input, Does.Contain("option-ui"));
        Assert.That(PlanVerification.Field(write, d.State.Settings), Is.EqualTo(PlanField.Phase));
        Assert.That(PlanVerification.Verify(write, new("phase", review.Writes), remote with { Baseline = remote.Baseline with { Rows = d.State.Rows } }, d.State.Settings), Is.True);
        Assert.That(PlanVerification.Verify(write, new("phase", review.Writes), remote, d.State.Settings), Is.False);
    }

    [Test]
    public void DivergentPhaseRefreshConflictsWhileUnchangedLocalAdoptsRemote()
    {
        var d = Document();
        var remote = d.Baseline with { Rows = d.Baseline.Rows.SetItem(0, d.Baseline.Rows[0] with { Phase = "UI" }) };
        Assert.That(PlanMerge.Merge(d, remote).State.Rows[0].Phase, Is.EqualTo("UI"));
        var local = d with { State = d.State with { Rows = d.State.Rows.SetItem(0, d.State.Rows[0] with { Phase = "SA" }) } };
        var merged = PlanMerge.Merge(local, remote);
        Assert.That(merged.State.Rows[0].Phase, Is.EqualTo("SA"));
        Assert.That(merged.Sync.Conflicts.Single().Field, Is.EqualTo(PlanField.Phase));
    }
    [Test]
    public void ExactPhaseSingleSelectMapsButAmbiguousAndWrongTypeDoNot()
    {
        var mapping = PlanColumnMatching.Match([new("F-Phase", "工程", "SINGLE_SELECT")]);
        Assert.That(mapping.Select(m => m.Role.ToString()), Is.EqualTo(new[] { "Phase" }));
        Assert.That(PlanColumnMatching.Match([new("A", "工程", "SINGLE_SELECT"), new("B", "工程", "SINGLE_SELECT")]), Is.Empty);
        Assert.That(PlanColumnMatching.Match([new("A", "工程", "TEXT")]), Is.Empty);
    }
}
