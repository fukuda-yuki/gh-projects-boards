using System.Collections.Immutable;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanPeopleOverload")]
internal sealed class PlanPeopleOverloadHostedTests
{
    private string root = null!;
    private PlanSession session = null!;
    private PlanPeopleView view = null!;
    private static readonly DateOnly Day = new(2026, 10, 5);

    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghpb-people-" + Guid.NewGuid().ToString("N"));
        var rows = Enumerable.Range(1, 3).Select(i => new PlanRow("I" + i, "作業" + i, "acme/repo") {
            Assignees = ["U1"], Estimate = 8, Remaining = 8, Actual = 0, StartNoEarlierThan = i == 3 ? Day.AddDays(1) : Day
        }).ToImmutableArray();
        var document = new PlanDocument(new(new("github.com", 1), "P1"), new(rows, []), new(rows, new() {
            StatusDate = Day, People = [new("U1", "alice", 100, null, []), new("U2", "bob", 100, null, [])]
        }));
        session = await PlanSession.CreateAsync(new(root), document, Day);
        await Ui.Run(() => view = new(session) { Width = 1280, Height = 720 });
        await Ui.Mount(view);
        await Ui.Ready<ComboBox>("PeopleScale");
    }

    [TearDown]
    public async Task Cleanup()
    {
        try {
            if (view is not null) await Ui.Unmount(view, check: false);
            if (session is not null) await session.FlushAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        } finally { Ui.EndTest(); }
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task AverageShowsDailyWarningAndDateDrillDownCorrectsOnlyTheSelectedTask(int scale)
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PeopleScale").SelectedIndex = scale);
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PeopleLoad_U1_0");
        await Ui.Run(() => {
            var label = Ui.Find<TextBlock>("PeopleLoad_U1_0");
            Assert.That(label.Text, Does.Contain("日超過1日").And.Contain("最大200%"));
            Assert.That(label.IsTextTrimmed, Is.False);
            Assert.That(AutomationProperties.GetName(Ui.Find<Button>("PeopleLoadOpen_U1_0")), Does.Contain(label.Text));
            Ui.Click("PeopleLoadOpen_U1_0");
        });
        await Ui.Ready<Button>("PeopleOverloadDay_U1_20261005");
        await Ui.Run(() => Ui.Click("PeopleOverloadDay_U1_20261005"));
        await Ui.Idle();
        await Ui.Ready<ComboBox>("PeopleTask_I1_Assignees");
        await Ui.Run(() => {
            Assert.That(Ui.Tree(view).OfType<ComboBox>().Select(AutomationProperties.GetAutomationId),
                Does.Contain("PeopleTask_I1_Assignees").And.Contain("PeopleTask_I2_Assignees").And.Not.Contain("PeopleTask_I3_Assignees"));
            var choice = Ui.Find<ComboBox>("PeopleTask_I2_Assignees");
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Tag == "U2");
        });
        await Ui.Until(() => session.Document.State.Rows[1].Assignees.SequenceEqual(["U2"]));
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PeopleLoad_U1_0");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PeopleLoad_U1_0").Text, Does.Not.Contain("日超過")));
        Assert.That(session.UndoCount, Is.EqualTo(1));
        Assert.That(session.Document.State.Rows[0].Assignees, Is.EqualTo(new[] { "U1" }));
        await session.Undo(Day);
        await Ui.Run(() => view.Refresh());
        await Ui.Idle();
        await Ui.Ready<TextBlock>("PeopleLoad_U1_0");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PeopleLoad_U1_0").Text, Does.Contain("日超過1日")));
    }
}
