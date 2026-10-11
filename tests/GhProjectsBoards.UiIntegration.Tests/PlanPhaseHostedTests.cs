using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;
namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test]
    public async Task PhaseIsInitiallyVisibleReadOnlyWhenUnmappedAndEditableWhenMapped()
    {
        await Ui.Ready<PlanSheetCell>("PlanCell1_Phase");
        await Ui.Run(() => {
            var phase = Ui.Find<PlanSheetCell>("PlanCell1_Phase");
            Assert.That(phase.IsReadOnly, Is.True);
            Assert.That(phase.Text, Is.Empty);
            Assert.That(sheet.VisibleColumns.Select(c => c.Label).Take(5), Is.EqualTo(new[] { "ID", "状態", "タスク名", "工程", "担当者" }));
        });
        var baseline = session.Document.Baseline with { Columns = [new("F-Phase", "工程", "SINGLE_SELECT") { Options = ["SA", "UI"] }] };
        await session.AcceptRefresh(new(baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0), Today, refresh: false);
        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {
            Columns = session.Document.State.Settings.Columns.Add(new(PlanField.Phase, "F-Phase", "工程", "SINGLE_SELECT")) }), Today);
        await Ui.Run(() => sheet.Refresh()); await Ui.Idle();
        await Edit(1, PlanField.Phase, " sa ");
        Assert.That(session.Document.State.Rows[0].Phase, Is.EqualTo("SA"));
        await Ui.Run(() => Assert.That(ToolTipService.GetToolTip(Ui.Find<PlanSheetCell>("PlanCell1_Phase")), Is.EqualTo("SA")));
        await Select(1, PlanField.Phase); await Select(2, PlanField.Phase, extend: true);
        await Ui.ClickCommand("PlanSheetFillDown"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[1].Phase, Is.EqualTo("SA"));
        await Select(2, PlanField.Phase); clipboard = new("ui", null);
        await Ui.ClickCommand("PlanSheetPaste"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[1].Phase, Is.EqualTo("UI"));
        await Ui.ClickCommand("PlanSheetClear"); await Ui.Idle();
        Assert.That(session.Document.State.Rows[1].Phase, Is.Null);
        await Edit(1, PlanField.Phase, "unknown");
        await Ui.Run(() => {
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell1_Phase").Text, Is.EqualTo("unknown"));
            Assert.That(sheet.Problems[("I1", PlanField.Phase)], Does.Contain("SA, UI"));
        });
        await Edit(1, PlanField.Phase, "SA");
    }
}
