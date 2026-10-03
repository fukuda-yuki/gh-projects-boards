using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [Test, Category("GanttReasons")]
    public async Task OptionalPredecessorCauseLinksToItsTaskAndExplainsTheAdoptedWeekendHolidayGap()
    {
        await PrepareGanttReasonOrigins();
        var adopted = Work.PlanFor(Workspace.Selected!);
        Assert.That(adopted.Tasks.Single(task => task.Id == "I1").Finish, Is.EqualTo(new DateTime(2026, 10, 13, 12, 0, 0)),
            "The UI fixture must contain the adopted predecessor finish that controls the successor.");
        Assert.That(adopted.Tasks.Single(task => task.Id == "I2").Start, Is.EqualTo(new DateTime(2026, 10, 14, 9, 0, 0)));
        Assert.That(adopted.Tasks.Single(task => task.Id == "I2").Warnings, Does.Contain("先行 I1 に警告があります。"),
            "The recorded engine warning remains available even when its optional cause is presented separately.");
        var before = JsonSerializer.Serialize(Work.Snapshot());
        await Ui.Run(() => Ui.Find<ListView>("GanttTasks").SelectedIndex = 1);
        await Ui.Until(() => Ui.Find<GanttView>("GanttView").SelectedRowId == "P1-T2");
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is { IsLoaded: true });
        await Ui.Run(async () => {
            var details = Ui.Popup<StackPanel>("GanttTaskDetails")!;
            Assert.That(Ui.Find<TextBlock>("GanttState-P1-T2").Text, Does.Not.Contain("注意"));
            Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.Not.Contain("日程の注意"));
            Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Is.Empty,
                "An optional unallocated breakdown must not occupy the ongoing task's attention notice.");
            var schedule = Ui.Tree(details).OfType<TextBlock>().SingleOrDefault(text => AutomationProperties.GetAutomationId(text) == "GanttScheduleWarnings");
            Assert.That(schedule?.Text ?? "", Is.Empty);
            var optional = Ui.Find<TextBlock>("GanttEffortWarnings", details);
            Assert.That(optional.IsLoaded && optional.Visibility == Visibility.Visible, Is.True);
            Assert.That(optional.Text, Does.Contain("任意").And.Contain("sample-user/first #1").And.Contain("見積の未割当")
                .And.Contain("残時間の未割当").And.Contain("2026-10-13 12:00"));
            Assert.That(optional.Text, Does.Not.Contain("先行 I1 に警告があります"), "Show the actual cause and readable origin rather than the generic propagation message.");
            Assert.That(Ui.Find<TextBlock>("GanttCalendarExplanation", details).Text,
                Does.Contain("Owner 2").And.Contain("個人例外").And.Contain("2026-10-13").And.Contain("10:00–12:00")
                    .And.Contain("2026-10-14 09:00"));
            var origins = Ui.Tree(details).OfType<HyperlinkButton>()
                .Where(link => AutomationProperties.GetAutomationId(link).StartsWith("GanttWarningOrigin-", StringComparison.Ordinal)).ToArray();
            Assert.That(origins, Has.Length.EqualTo(1), "Both optional causes share one exact origin action.");
            Assert.That(AutomationProperties.GetAutomationId(origins[0]), Is.EqualTo("GanttWarningOrigin-I1"));
            Assert.That(origins[0].Content?.ToString(), Does.Contain("sample-user/first #1"));
            await ApplyInformationEvidence.Capture(details, "gantt-optional-upstream-cause");
            Assert.That(origins[0].Focus(FocusState.Keyboard), Is.True);
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(origins[0]).GetPattern(PatternInterface.Invoke)).Invoke();
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is null
            && Ui.Find<GanttView>("GanttView").SelectedRowId == "P1-T1");
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is { IsLoaded: true });
        await Ui.Run(() => {
            var details = Ui.Popup<StackPanel>("GanttTaskDetails")!;
            Assert.That(string.Join("\n", Ui.Tree(details).OfType<TextBlock>().Select(text => text.Text)), Does.Contain("sample-user/first #1"));
            var explanation = Ui.Find<TextBlock>("GanttCalendarExplanation", details);
            Assert.That(explanation.Text, Does.Contain("2026-10-09 18:00").And.Contain("2026-10-13 09:00"));
            var days = explanation.Text.Split('\n');
            Assert.That(days.Any(line => line.Contains("2026-10-10") && line.Contains("週末")), Is.True);
            Assert.That(days.Any(line => line.Contains("10-11") && line.Contains("週末")), Is.True,
                "Both weekend days remain explained when consecutive identical days share one displayed range.");
            Assert.That(days.Any(line => line.Contains("2026-10-12") && line.Contains("スポーツの日")), Is.True);
            var records = Ui.Tree(details).OfType<Expander>().Single(expander => (string)expander.Header == "計算の記録");
            Assert.That(records.IsExpanded, Is.False);
            ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(records).GetPattern(PatternInterface.ExpandCollapse)).Expand();
        });
        await Ui.Until(() => Ui.Popup<TextBlock>("GanttCalendarRecord") is { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(() => Ui.Popup<TextBlock>("GanttCalendarRecord")!.StartBringIntoView());
        await SheetNativeInput.Rendered();
        await Ui.Run(async () => {
            var record = Ui.Popup<TextBlock>("GanttCalendarRecord")!;
            var calendar = adopted.Configuration!.Calendar;
            Assert.That(record.Text, Does.Contain(calendar.Revision).And.Contain(calendar.Holidays.Version)
                .And.Contain(calendar.Holidays.Source).And.Contain(calendar.Holidays.SourceSha256)
                .And.Contain("タスクID: I1").And.Contain("採用計画リビジョン: " + adopted.SourceRevision));
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(before));
            Assert.That(h.Writes, Is.Empty); Assert.That(h.Existing.Writes, Is.Empty); Assert.That(Work.Journal, Is.Empty);
            await ApplyInformationEvidence.Capture(Ui.Popup<StackPanel>("GanttTaskDetails")!, "gantt-origin-weekend-holiday-record");
            Ui.Find<AppBarButton>("GanttDetails").Flyout.Hide();
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is null);
    }

    [Test, Category("GanttReasons")]
    public async Task UnavailableManualPredecessorCauseKeepsItsVisibleIdentityAndOriginAction()
    {
        await Ui.Unmount(panel);
        const string problem = "取得した工数の値を確認できません。";
        var first = new PlanningTask("I1", PlanningMode.Manual, "U1",
            ManualStart: new(2026, 10, 5, 9, 0, 0), ManualFinish: new(2026, 10, 5, 10, 0, 0),
            Contributions: [new("U1", 4, null)], Assignment: new(["U1"], true));
        var second = new PlanningTask("I2", PlanningMode.Auto, "U1", Contributions: [new("U1", 1, null)], Assignment: new(["U1"], true));
        PlanningInput[] inputs = [new(first, 4, null, ["U1"], [], SourceProblem: problem), new(second, 1, null, ["U1"], [new("I1")])];
        var adopted = PlanningEngine.Calculate(PlanningPathTests.Plan() with { Version = 3, Tasks = [first, second] }, inputs, 31);
        Assert.That(adopted.Tasks.Single(task => task.Id == "I1").Problem, Is.EqualTo(problem));
        Assert.That(adopted.Tasks.Single(task => task.Id == "I1").Resolved, Is.True, "Manual dates are retained while the source problem remains explicit.");
        Assert.That(adopted.Tasks.Single(task => task.Id == "I2").Warnings, Does.Contain("先行 I1 に警告があります。"));
        var projection = new GanttProjection(inputs.Select((input, index) => {
            var result = adopted.Tasks.Single(task => task.Id == input.Task.Id);
            return new GanttRow("source-row-" + input.Task.Id, input.Task.Id, "Issue " + (index + 1), "owner/repo #" + (index + 1),
                result, input, GanttProjection.StateFor(result, adopted.SourceRevision), false);
        }).ToArray(), adopted);
        var beforePlan = JsonSerializer.Serialize(adopted);
        var beforeWork = JsonSerializer.Serialize(Work.Snapshot());
        GanttView view = null!;
        await Ui.Run(() => { view = new GanttView { Width = 1100, Height = 700 }; view.Present(projection, "source-row-I2"); });
        await Ui.Mount(view); await Ui.Ready<TextBlock>("GanttState-source-row-I2");
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is { IsLoaded: true });
        await Ui.Run(async () => {
            var details = Ui.Popup<StackPanel>("GanttTaskDetails")!;
            var warnings = Ui.Find<TextBlock>("GanttScheduleWarnings", details);
            Assert.That(warnings.Text, Does.Contain(problem).And.Contain("owner/repo #1"));
            Assert.That(Ui.Find<TextBlock>("GanttState-source-row-I2").Text, Does.Contain("注意"));
            Assert.That(Ui.Find<TextBlock>("GanttNotice").Text, Does.Contain("日程の注意"));
            Assert.That(Ui.Tree(details).OfType<TextBlock>().Any(text => AutomationProperties.GetAutomationId(text) == "GanttEffortWarnings"), Is.False);
            var origin = Ui.Find<HyperlinkButton>("GanttWarningOrigin-I1", details);
            Assert.That(origin.IsLoaded && origin.IsEnabled, Is.True);
            Assert.That(origin.Content?.ToString(), Does.Contain("owner/repo #1"));
            await ApplyInformationEvidence.Capture(details, "gantt-unavailable-upstream-origin");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(origin).GetPattern(PatternInterface.Invoke)).Invoke();
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is null && view.SelectedRowId == "source-row-I1");
        await Ui.Run(() => {
            Assert.That(JsonSerializer.Serialize(adopted), Is.EqualTo(beforePlan));
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(beforeWork));
            Assert.That(h.Writes, Is.Empty); Assert.That(h.Existing.Writes, Is.Empty); Assert.That(Work.Journal, Is.Empty);
        });
        await Ui.Unmount(view);
    }

    [Test, Category("GanttReasons")]
    public async Task ActualReportAndInconsistentContributionsRemainActionableWithoutBecomingScheduleProblems()
    {
        await Ui.Unmount(panel);
        var first = new PlanningTask("I1", PlanningMode.Auto, "U1",
            Contributions: [new("U1", 5, null)], Assignment: new(["U1"], true));
        var second = new PlanningTask("I2", PlanningMode.Auto, "U1",
            Contributions: [new("U1", 1, null)], Assignment: new(["U1"], true));
        PlanningInput[] inputs = [new(first, 4, null, ["U1"], [], ActualTotal: 2), new(second, 1, null, ["U1"], [new("I1")])];
        var adopted = PlanningEngine.Calculate(PlanningPathTests.Plan() with { Version = 3, Tasks = [first, second] }, inputs, 32);
        Assert.That(adopted.Tasks.All(task => task.Resolved && task.Problem is null), Is.True,
            "Missing report provenance and excess optional contributions do not invalidate these dates.");
        var projection = new GanttProjection(inputs.Select((input, index) => {
            var result = adopted.Tasks.Single(task => task.Id == input.Task.Id);
            return new GanttRow("record-row-" + input.Task.Id, input.Task.Id, "Issue " + (index + 1), "owner/repo #" + (index + 1),
                result, input, GanttProjection.StateFor(result, adopted.SourceRevision), false);
        }).ToArray(), adopted);
        var beforePlan = JsonSerializer.Serialize(adopted);
        var beforeWork = JsonSerializer.Serialize(Work.Snapshot());
        GanttView view = null!;
        await Ui.Run(() => { view = new GanttView { Width = 1100, Height = 700 }; view.Present(projection, "record-row-I2"); });
        await Ui.Mount(view); await Ui.Ready<TextBlock>("GanttState-record-row-I2");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("GanttState-record-row-I2").Text,
                Does.Contain("実績を確認").And.Contain("内訳を確認").And.Not.Contain("注意"));
            Assert.That(AutomationProperties.GetName(Ui.Find<ListViewItem>("GanttRow-record-row-I2")),
                Does.Contain("owner/repo #2").And.Contain("Issue 2").And.Contain("実績を確認")
                    .And.Contain("内訳を確認").And.Not.Contain("注意"));
            Assert.That(Ui.Find<TextBlock>("GanttNotice").Text,
                Does.Contain("実績の反映前").And.Contain("工数内訳の合計").And.Not.Contain("日程の注意").And.Not.Contain("任意"));
        });
        await Ui.ClickCommand("GanttDetails");
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is { IsLoaded: true });
        await Ui.Run(async () => {
            var details = Ui.Popup<StackPanel>("GanttTaskDetails")!;
            Assert.That(Ui.Find<TextBlock>("GanttActualReportWarnings", details).Text,
                Does.Contain("GitHubへ反映する前").And.Contain("owner/repo #1").And.Contain("報告対象日").And.Not.Contain("任意"));
            Assert.That(Ui.Find<TextBlock>("GanttContributionWarnings", details).Text,
                Does.Contain("owner/repo #1").And.Contain("合計を超え").And.Contain("訂正"));
            Assert.That(Ui.Tree(details).OfType<TextBlock>().Any(text => AutomationProperties.GetAutomationId(text)
                is "GanttEffortWarnings" or "GanttScheduleWarnings"), Is.False,
                "Neither actionable record issue belongs to the optional or schedule-problem group.");
            var origin = Ui.Find<HyperlinkButton>("GanttWarningOrigin-I1", details);
            Assert.That(origin.IsLoaded && origin.IsEnabled, Is.True);
            Assert.That(Ui.Tree(details).OfType<HyperlinkButton>().Count(link =>
                AutomationProperties.GetAutomationId(link).StartsWith("GanttWarningOrigin-", StringComparison.Ordinal)), Is.EqualTo(1));
            await ApplyInformationEvidence.Capture(details, "gantt-report-and-contribution-origin");
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(origin).GetPattern(PatternInterface.Invoke)).Invoke();
        });
        await Ui.Until(() => Ui.Popup<StackPanel>("GanttTaskDetails") is null && view.SelectedRowId == "record-row-I1");
        await Ui.Run(() => {
            Assert.That(JsonSerializer.Serialize(adopted), Is.EqualTo(beforePlan));
            Assert.That(JsonSerializer.Serialize(Work.Snapshot()), Is.EqualTo(beforeWork));
            Assert.That(h.Writes, Is.Empty); Assert.That(h.Existing.Writes, Is.Empty); Assert.That(Work.Journal, Is.Empty);
        });
        await Ui.Unmount(view);
    }

    private async Task PrepareGanttReasonOrigins()
    {
        await Ui.Unmount(panel);
        await Ui.Run(async () => { await Workspace.StopAsync(); Assert.That(await Workspace.FlushDraftsAsync(), Is.True); });
        h = await CreationHarness.Create(2, planning: true);
        h.Existing.ChangeResponse = (query, response) => {
            if (!query.Contains("ProjectItems")) return;
            foreach (var item in response["data"]!["node"]!["items"]!["nodes"]!.AsArray())
            {
                var issue = item!["content"]!;
                var assignee = issue["id"]!.ToString() == "I1" ? "U1" : "U2";
                issue["assignees"] = JsonSerializer.SerializeToNode(ProjectReaderTests.Page([new { id = assignee, login = "worker-" + assignee }], 1));
            }
        };
        var choice = await new ProjectDiscovery(h.Existing.Service).ResolveAsync(h.Existing.Context,
            "https://github.com/users/sample-user/projects/1", default);
        await Workspace.RegisterAsync(choice, null, true);
        var project = Workspace.Selected!;
        Assert.That(project.Snapshot.Issues.Values.SelectMany(issue => issue.Native!.Assignees).Select(person => person.Id.NodeId),
            Is.EquivalentTo(new[] { "U1", "U2" }));
        var plan = PlanningPathTests.Plan() with {
            Version = 3, Cutoff = new(2026, 10, 9, 18, 0, 0),
            People = [new("U1", "Owner 1", 100), new("U2", "Owner 2", 100)],
            Tasks = [new("I1", PlanningMode.Auto, "U1", Progress: PlanningProgress.InProgress,
                ActualStart: new(2026, 10, 9, 9, 0, 0), Actuals: [new("U1", 1, new(2026, 10, 9))], Assignment: new(["U1"], true)),
                new("I2", PlanningMode.Auto, "U2", Contributions: [new("U2", 4, null)], LocalLinks: [new("I1")], Assignment: new(["U2"], true))]
        };
        plan = plan with { Calendar = plan.Calendar with { Exceptions = [new(new(2026, 10, 13), "U2", [new(600, 720)])] } };
        Work.CommitPlanning(project, plan, Work.Revision,
            [new("P1-T1", "Estimate", "8"), new("P1-T1", "Remaining", "3"), new("P1-T2", "Estimate", "4")]);
        await Ui.Run(() => { panel.Width = 1100; panel.Height = 700; panel.Initialize(Workspace); });
        await Ui.Mount(panel); await OpenNavigation(SplitViewDisplayMode.Inline);
        await Ui.Run(() => Ui.Find<SelectorBar>("ProjectViews").SelectedItem = Ui.Find<SelectorBar>("ProjectViews").Items[1]);
        await Ui.Ready<ListView>("GanttTasks");
        await Ui.Ready<TextBlock>("GanttState-P1-T2");
    }
}
