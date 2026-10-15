using System.Collections.Immutable;
using System.Text.Json;
using GhProjectsBoards.App;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable, Category("PlanPublishReview")]
internal sealed class PlanPublishReviewHostedTests
{
    private DateOnly readDay;
    private string? previousToday;
    private string root = null!;
    private PlanWorkspace workspace = null!;
    private PlanWorkspaceView view = null!;
    private static DateOnly Today => new(2026, 10, 7);
    private static string FakeExecutable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GHPB_UI_FAKE_GH_PATH") is { Length: > 0 } path) return path;
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GhProjectsBoards.sln"))) directory = directory.Parent;
            return Path.Combine(directory!.FullName, "tests", "GhProjectsBoards.Tests", "bin", "Release", "net10.0-windows", "GhProjectsBoards.Tests.exe");
        }
    }
    [SetUp]
    public async Task Setup()
    {
        await Ui.BeginTest();
        previousToday = Environment.GetEnvironmentVariable("GHPB_TODAY");
        Environment.SetEnvironmentVariable("GHPB_TODAY", "2026-10-07");
        readDay = Today;
        root = Path.Combine(Path.GetTempPath(), "ghpb-publish-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true}");
        workspace = new(new(root), () => readDay);
        await Ui.Run(() => view = new(workspace, (_, host) => new(FakeExecutable, host,
            new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root }))));
        await Ui.Mount(view);
    }
    private async Task Open(int count, DateOnly? publishedDay = null)
    {
        FakePlanEditor.Save(root, new(Enumerable.Range(1, count).Select(i => new PlanFakeIssue(
            new("I" + i, "要求タスク " + i, "acme/repo") { Estimate = 8, Remaining = 8, Actual = 0, Start = publishedDay, End = publishedDay }, "", true)).ToImmutableArray(), count + 1));
        await Ui.Run(() => Ui.Click("PlanConnect"));
        await Ui.Until(() => workspace.Available.Count == 2 || Ui.Find<TextBlock>("PlanError").Text.Length > 0);
        await Ui.Run(() => { Assert.That(Ui.Find<TextBlock>("PlanError").Text, Is.Empty); Ui.Find<ListView>("AvailableProjects").SelectedIndex = 0; });
        await Ui.Until(() => workspace.Session is not null);
        await Ui.Ready<PlanSheetCell>("PlanCell1_Title");
        await Ui.Idle();
    }
    [TearDown]
    public async Task Cleanup()
    {
        try
        {
            try { await Ui.Run(async () => await view.StopAsync()); }
            finally
            {
                try { await Ui.Unmount(view, check: false); await Ui.Idle(); }
                finally { await workspace.Flush(); Directory.Delete(root, true); }
            }
        }
        finally { Environment.SetEnvironmentVariable("GHPB_TODAY", previousToday); Ui.EndTest(); }
    }
    [Test, Category("PublishOutcome")]
    public async Task OrderReviewNamesOnlyTheMovedRowsAndCountsTheirTasks()
    {
        // Published dates leave the order as the only difference.
        readDay = new(2026, 10, 6);
        await Open(6, readDay);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new MovePlanRows(["I1", "I2"], null), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(1) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            Assert.That(list.Items.Count, Is.EqualTo(2));
            var names = Enumerable.Range(0, 2).Select(i =>
                new Microsoft.UI.Xaml.Automation.Peers.ListViewItemAutomationPeer((ListViewItem)list.ContainerFromIndex(i)).GetName()).ToArray();
            Assert.That(names[0], Does.Contain("要求タスク 1").And.Contain("表示順  1 → 5"));
            Assert.That(names[1], Does.Contain("要求タスク 2").And.Contain("表示順  2 → 6"));
            Assert.That(Ui.Find<TextBlock>("PlanPublishSummary").Text, Does.Contain("の 2 タスクを GitHub に書き込みます"));
        });
        Assert.That(workspace.Session!.Changes(Today).TaskCount, Is.EqualTo(2));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PublishOutcome")]
    public async Task ReviewNamesDestinationAndChangesAndClosingKeepsWork()
    {
        await Open(2);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste,
                [new("I1", PlanField.Title, "変更したタスク"), new("I1", PlanField.Actual, 3m), new("I2", PlanField.Actual, 2m)]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(1) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => {
            var project = workspace.Selected!;
            Assert.That(Ui.Find<TextBlock>("PlanPublishSummary").Text, Is.EqualTo($"{project.Title} ({project.OwnerLogin} / プロジェクト {project.Number}) の 2 タスクを GitHub に書き込みます"));
            var list = Ui.Find<ListView>("PlanPublishLines");
            Assert.That(list.Items.Count, Is.EqualTo(2));
            var name = new Microsoft.UI.Xaml.Automation.Peers.ListViewItemAutomationPeer((ListViewItem)list.ContainerFromIndex(0)).GetName();
            Assert.That(name, Does.Contain("変更したタスク").And.Contain("要求タスク 1 → 変更したタスク").And.Contain("実績工数  0h → 3h").And.Not.Contain("PlanPublishReviewGroup"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").Style, Is.SameAs(Application.Current.Resources["AccentButtonStyle"]));
            Assert.That(Ui.Find<Button>("PlanPublish").Style, Is.Not.SameAs(Application.Current.Resources["AccentButtonStyle"]));
            Assert.That(Ui.Find<Button>("PlanPublishClose").Content, Is.EqualTo("閉じる（変更は保持）"));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanPublish").Style, Is.SameAs(Application.Current.Resources["AccentButtonStyle"])));
        Assert.That(workspace.Session!.Changes(Today).TaskCount, Is.EqualTo(2));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase("edit"), TestCase("refresh"), TestCase("reset"), TestCase("project"), Category("PublishOutcome")]
    public async Task SuccessDisablesPublicationAndRetainsResultUntilNextWork(string next)
    {
        await Open(1);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False));
        await Ui.Run(async () => {
            Ui.Click("PlanPublishClose");
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "発行したタスク")]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "issue138-review"));
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<TextBlock>("PlanPublishResult").Visibility == Visibility.Visible && Ui.Find<Button>("PlanPublish").IsEnabled);
        string result = "";
        await Ui.Run(async () => {
            result = Ui.Find<TextBlock>("PlanPublishResult").Text;
            Assert.That(result, Does.Match(@"^1 タスクを発行しました（\d{1,2}:\d{2}）$"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False);
            await RenderedEvidence.Capture(view, "issue138-result");
            Ui.Click("PlanPublishClose");
        });
        await Ui.Run(() => Ui.Click("PlanPublish")); await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanPublishResult").Text, Is.EqualTo(result));
            Ui.Click("PlanPublishClose");
        });
        await Ui.Idle();
        if (next == "edit")
        {
            await Ui.Run(() => { var title = Ui.Find<TextBox>("PlanCell1_Title"); title.Focus(FocusState.Programmatic); title.Text = "次の変更"; Ui.Click("PlanPublish"); });
            await Ui.Until(() => workspace.Session!.Document.State.Rows[0].Title == "次の変更");
        }
        else if (next == "project")
        {
            await Ui.Run(() => Ui.Click("PlanProjectPicker"));
            await Ui.Ready<Button>("PlanChooseProject");
            await Ui.Run(() => Ui.Click("PlanChooseProject"));
            await Ui.Ready<ListView>("AvailableProjects");
            await Ui.Run(() => Ui.Find<ListView>("AvailableProjects").SelectedIndex = 1);
            await Ui.Until(() => workspace.Selected!.Id == workspace.Available[1].Id && Ui.Find<Button>("PlanPublish").IsEnabled);
        }
        else
        {
            await Ui.Run(() => {
                if (next == "refresh") Ui.Click("PlanRefresh");
                else ((Microsoft.UI.Xaml.Automation.Provider.IExpandCollapseProvider)Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(Ui.Find<SplitButton>("PlanRefresh"))
                    .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.ExpandCollapse)).Expand();
            });
            if (next == "reset")
            {
                await Ui.Until(() => Ui.Popup<MenuFlyoutItem>("PlanReset") is { IsLoaded: true, IsEnabled: true });
                await Ui.Run(() => {
                    var item = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                        .SelectMany(p => Ui.Tree(p.Child)).OfType<MenuFlyoutItem>().Single(i => AutomationProperties.GetAutomationId(i) == "PlanReset");
                    ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(item)
                        .GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                });
                await Ui.DialogReady("PlanResetConfirm");
                await Ui.Run(() => Ui.DialogButton("PlanResetConfirm", "PrimaryButton"));
            }
            await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled
                && Ui.Find<TextBlock>("PlanPublishResult").Visibility == Visibility.Collapsed);
        }
        await Ui.Run(() => Ui.Click("PlanPublish")); await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanPublishResult").Visibility, Is.EqualTo(Visibility.Collapsed)));
    }

    [TestCase(false, true), TestCase(true, true), TestCase(false, false), TestCase(true, false), Category("PublishOutcome")]
    public async Task InformationalGroupsDoNotCountAsWritableTasks(bool edit, bool datesAlreadyPublished)
    {
        readDay = new(2026, 10, 6);
        await Open(2, datesAlreadyPublished ? readDay : null);
        await Ui.Run(async () => {
            var session = workspace.Session!;
            var baseline = session.Document.Baseline;
            baseline = baseline with { Columns = baseline.Columns.Add(new("DeletedNote", "Review notes", "TEXT")),
                Rows = baseline.Rows.Select(r => PlanValues.Set(r, "Project:DeletedNote", "\"original\"")).ToImmutableArray() };
            await session.AcceptRefresh(new(baseline, ImmutableDictionary<string, string>.Empty, [], 0, 0), Today, refresh: false);
            await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", "Project:DeletedNote", "保持するメモ")]), Today);
            var deleted = baseline with { Columns = baseline.Columns.RemoveAt(baseline.Columns.Length - 1),
                Rows = baseline.Rows.Select(r => r with { ExtraValues = r.ExtraValues.Remove("Project:DeletedNote") }).ToImmutableArray() };
            await session.AcceptRefresh(new(deleted, ImmutableDictionary<string, string>.Empty, [], 0, 0), Today, refresh: false);
            if (edit) await session.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I1", PlanField.Title, "書き込み対象")]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroupI2");
        await Ui.Run(() => {
            var expectedTasks = datesAlreadyPublished ? (edit ? 1 : 0) : 2;
            Assert.That(Ui.Find<TextBlock>("PlanPublishSummary").Text, Does.Contain($"の {expectedTasks} タスクを GitHub に書き込みます"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.EqualTo(expectedTasks > 0));
            var text = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroupI2")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("GitHub で削除されたフィールド"));
            Assert.That(text, datesAlreadyPublished ? Does.Not.Contain("開始日") : Does.Contain("開始日").And.Contain("終了日"));
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PublishOutcome")]
    public async Task ClosedTaskRemainingZeroIsReviewedAndPublishable()
    {
        readDay = new(2026, 10, 6);
        await Open(2, readDay);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I1"
            ? i with { Row = i.Row with { Closed = true, CloseDate = readDay, Status = "Done" } } : i).ToImmutableArray() });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled && workspace.Session!.Document.State.Rows[0].Closed);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell, [new("I2", PlanField.Title, "別の変更")]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroupI1");
        var expected = workspace.Session!.Changes(Today).TaskCount;
        Assert.That(expected, Is.EqualTo(2), "Precondition: Core publishes the closed task's Remaining 0.");
        await Ui.Run(() => {
            var text = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroupI1")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("見込み残存工数  8h → 0h"));
            Assert.That(Ui.Find<TextBlock>("PlanPublishSummary").Text, Does.Contain($"の {expected} タスクを GitHub に書き込みます"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
        });
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [Test, Category("PublishOutcome")]
    public async Task PartialFailureKeepsTaskFailureAndDoesNotClaimSuccess()
    {
        await Open(2);
        File.WriteAllText(Path.Combine(root, "scenario.json"), "{\"planEditor\":true,\"workspace\":true,\"planFault\":\"partial\"}");
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste,
                [new("I1", PlanField.Title, "変更1"), new("I2", PlanField.Title, "変更2")]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
        await Ui.Until(() => Ui.Find<InfoBar>("PlanPublishFailure").IsOpen && Ui.Find<Button>("PlanPublish").IsEnabled);
        await Ui.Until(() => Ui.Tree(Ui.Find<ListView>("PlanPublishLines")).OfType<TextBlock>()
            .Any(text => text.IsLoaded && text.ActualHeight > 0 && text.Text.Contains("発行失敗")));
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBlock>("PlanPublishResult").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(string.Join(" ", Ui.Tree(Ui.Find<ListView>("PlanPublishLines")).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("発行失敗").And.Contain("Synthetic failure"));
        });
    }

    [TestCase(false, -1), TestCase(true, -1), TestCase(false, 0), TestCase(true, 0), TestCase(false, 1), Category("PlanReadDate")]
    public async Task ReviewNamesCalculationDateAndFirstTakeInOrigin(bool refresh, int dayOffset)
    {
        readDay = Today.AddDays(dayOffset);
        await Open(1);
        Assert.That(workspace.Session!.Document.State.Settings.StatusDate, Is.EqualTo(readDay));
        Assert.That(workspace.Session.Document.Dates.Held, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
        await Ui.Run(async () => {
            await workspace.Session.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = readDay }), Today);
            var title = Ui.Find<TextBox>("PlanCell1_Title");
            title.Focus(FocusState.Programmatic); title.Text = "Edited";
            Ui.Click("PlanPublish");
        });
        await Ui.Ready<TextBlock>("PlanPublishStatusDate");
        // This persistent TextBlock is Loaded even while the review is hidden.
        // Invoke and the pending title commit complete after the calling UI turn.
        await Ui.Until(() => Ui.Find<TextBlock>("PlanPublishStatusDate").Text.Length > 0);
        await Ui.Idle();
        Assert.That(workspace.Session.Document.State.Rows[0].Title, Is.EqualTo("Edited"));
        Assert.That(workspace.Session.Document.Dates.Recalculated, Is.EqualTo(PlanDateHoldOrigin.FirstTakeIn));
        if (refresh)
        {
            var beforeRefresh = "";
            await Ui.Run(() => beforeRefresh = Ui.Find<TextBlock>("PlanPublishStatusDate").Text);
            await Ui.Run(() => { Ui.Click("PlanPublishClose"); Ui.Click("PlanRefresh"); });
            await Ui.Until(() => Ui.Find<SplitButton>("PlanRefresh").IsEnabled && workspace.Session!.Document.Dates.Recalculated is null);
            await Ui.Run(() => Ui.Click("PlanPublish"));
            await Ui.Ready<TextBlock>("PlanPublishStatusDate");
            await Ui.Until(() => Ui.Find<TextBlock>("PlanPublishStatusDate").Text != beforeRefresh);
            await Ui.Idle();
        }
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanPublishStatusDate").Text,
            Is.EqualTo(DateMessage(dayOffset) + (refresh ? "" : "（最初の取り込みのあとの再計算）"))));
        await Ui.Run(async () => await RenderedEvidence.Capture(view, "issue130-publish-date-" + refresh));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    [TestCase(-1), TestCase(0), Category("PlanReset")]
    public async Task ReviewNamesResetOriginAfterTheNextPlanningEdit(int dayOffset)
    {
        readDay = Today.AddDays(dayOffset);
        await Open(1);
        await workspace.Reset();
        Assert.That(workspace.Session!.Changes(Today).TaskCount, Is.Zero);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new ReplacePlanSettings(workspace.Session.Document.State.Settings with { StatusDate = readDay }), Today);
            await workspace.Session.Execute(new EditPlanCells(PlanOperationKind.Cell,
                [new("I1", PlanField.Title, "Edited after reset")]), Today);
        });
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Ready<TextBlock>("PlanPublishStatusDate");
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanPublishStatusDate").Text,
            Is.EqualTo(DateMessage(dayOffset) + "（GitHub の状態に戻したあとの再計算）")));
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
    }

    private static string DateMessage(int dayOffset) => dayOffset switch {
        -1 => "注意: 基準日 2026/10/06 (火) は今日より前です。「最新の情報に更新」で今日の進捗を取り込めます。",
        0 => "日付は基準日 2026/10/07 (水) で計算したものです。",
        1 => "日付は基準日 2026/10/08 (木) で計算したものです。",
        _ => throw new ArgumentOutOfRangeException(nameof(dayOffset))
    };

    [Test, Category("PlanReadDate")]
    public async Task PastStatusDateWarnsAndPublishesTheSameMutationsAsToday()
    {
        await Open(1);
        var original = FakePlanEditor.Load(root);
        string[]? pastMutations = null;
        foreach (var dayOffset in new[] { -1, 0 })
        {
            if (dayOffset == 0)
            {
                await Ui.Run(() => Ui.Click("PlanPublishClose"));
                FakePlanEditor.Save(root, original);
                await Ui.Run(async () => await workspace.Reset());
            }
            await Ui.Run(async () => {
                var session = workspace.Session!;
                await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with {
                    StatusDate = Today.AddDays(dayOffset), ProjectStart = new(2026, 10, 13)
                }), Today);
                await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
                    [new("I1", PlanField.Title, "Edited")]), Today);
                Ui.Click("PlanPublish");
            });
            await Ui.Until(() => Ui.Find<TextBlock>("PlanPublishStatusDate").Text.StartsWith(DateMessage(dayOffset)));
            await Ui.Run(async () => {
                var line = Ui.Find<TextBlock>("PlanPublishStatusDate");
                Assert.That(AutomationProperties.GetName(line), Is.EqualTo(line.Text));
                Assert.That(line.Foreground, Is.EqualTo(PlanSheetView.Brush(dayOffset < 0 ? "SheetChangedMarkBrush" : "TextFillColorPrimaryBrush")));
                Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
                await RenderedEvidence.Capture(view, "issue166-status-date-" + dayOffset);
            });
            var log = Path.Combine(root, "plan-mutations.jsonl");
            var before = File.Exists(log) ? File.ReadAllLines(log).Length : 0;
            await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
            await Ui.Until(() => Ui.Find<TextBlock>("PlanUnpublished").Text == "未発行 0 タスク" &&
                Ui.Find<TextBlock>("PlanPublishResult").Visibility == Visibility.Visible && Ui.Find<Button>("PlanPublish").IsEnabled);
            var mutations = File.ReadAllLines(log).Skip(before).Select(line => {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.GetProperty("query").GetString() + "\n" +
                    json.RootElement.GetProperty("variables").GetRawText();
            }).ToArray();
            Assert.That(mutations, Is.Not.Empty);
            Assert.That(FakePlanEditor.Load(root).Issues[0].Row.Title, Is.EqualTo("Edited"));
            Assert.That(workspace.Session!.Document.Sync.Failures, Is.Empty);
            Assert.That(workspace.Session.Document.Sync.Unverified, Is.Empty);
            if (dayOffset < 0) pastMutations = mutations;
            else Assert.That(mutations, Is.EqualTo(pastMutations));
        }
    }

    [Test, Category("PlanSheetFollowup")]
    public async Task ReviewDatesKeepTheYearInBeforeAfterAndGitHubValues()
    {
        await Open(1);
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i with { Row = i.Row with { Start = new(2027, 10, 5), End = new(2027, 10, 5), Remaining = 0, Actual = 8 } }).ToImmutableArray() });
        await Ui.Run(() => Ui.Click("PlanRefresh")); await Ui.Idle();
        await Ui.Run(async () => await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste, [
            new("I1", PlanField.End, new DateOnly(2032, 10, 5))]), Today));
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Ready<PlanPublishGroupView>("PlanPublishGroupI1");
        await Ui.Run(() => {
            var text = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroupI1")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("終了日  2027/10/5 (火) → 2032/10/5 (火)"));
            Ui.Click("PlanPublishClose");
        });
        remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i with { Row = i.Row with { End = new(2038, 10, 5) } }).ToImmutableArray() });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.Any(c => c.Field == PlanField.End));
        await Ui.Until(() => Ui.Find<Button>("PlanPublish").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Ready<Button>("PlanResolveI1_End_True");
        await Ui.Run(async () => {
            var text = string.Join(" ", Ui.Tree(Ui.Find<PlanPublishGroupView>("PlanPublishGroupI1")).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("終了日  2038/10/5 (火) → 2032/10/5 (火)").And.Contain("GitHub: 2038/10/5 (火)"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "followup-review-years");
        });
    }
    [Test]
    public async Task SeveralFieldDifferencesShareTheirIssueAndFullLongValuesRemainReadable()
    {
        await Open(2);
        var longTitle = "要求タスク 1 の変更内容を確認するための長いタイトル・末尾まで表示";
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste, [
                new("I1", PlanField.Title, longTitle), new("I1", PlanField.Actual, 3m), new("I1", PlanField.Remaining, 5m),
                new("I2", PlanField.Actual, 2m)]), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            Assert.That(list.Items.Count, Is.EqualTo(2), "One review group must contain every difference for its Issue.");
            var first = (ListViewItem)list.ContainerFromIndex(0);
            var text = string.Join("\n", Ui.Tree(first).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(text, Does.Contain("acme/repo#1").And.Contain("要求タスク 1 → " + longTitle)
                .And.Contain("実績工数  0h → 3h").And.Contain("見込み残存工数  8h → 5h"));
            var comparison = Ui.Tree(first).OfType<TextBlock>().Single(t => t.Text.Contains("実績工数  0h → 3h"));
            Assert.That(comparison.Text, Does.Contain("見込み残存工数  8h → 5h"));
            Assert.That(comparison.TextWrapping, Is.EqualTo(TextWrapping.Wrap));
            Assert.That(comparison.TextTrimming, Is.EqualTo(TextTrimming.None));
            Assert.That(first.ActualWidth, Is.LessThanOrEqualTo(list.ActualWidth));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "publish-grouped-full-values");
        });
    }
    [Test]
    public async Task VersionReviewVirtualizesIssueGroupsAndReachesFinalDifferencesWithoutWriting()
    {
        await Open(1040);
        await Ui.Run(async () => {
            await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste, workspace.Session.Document.State.Rows
                .SelectMany(r => new[] { new PlanCellChange(r.Identity, PlanField.Estimate, 16m),
                    new PlanCellChange(r.Identity, PlanField.Actual, 3m), new PlanCellChange(r.Identity, PlanField.Remaining, 13m) }).ToImmutableArray()), Today);
            Ui.Click("PlanPublish");
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            Assert.That(list.Items.Count, Is.EqualTo(1040));
            Assert.That(list.ItemsSource, Is.Not.Null);
            Assert.That(list.ContainerFromIndex(1039), Is.Null, "Offscreen groups must remain virtualized.");
            var first = (ListViewItem)list.ContainerFromIndex(0);
            Assert.That(first.ActualHeight, Is.LessThan(100), "Three short differences must not consume three repeated Issue rows.");
            await RenderedEvidence.Capture(view, "publish-grouped-version-start");
            list.ScrollIntoView(list.Items[1039], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(1039) is ListViewItem { IsLoaded: true } item &&
            Ui.Tree(item).OfType<TextBlock>().Any(t => t.Text == "1040 要求タスク 1040"));
        await Ui.Run(async () => {
            var list = Ui.Find<ListView>("PlanPublishLines");
            var last = (ListViewItem)list.ContainerFromIndex(1039);
            var text = string.Join("\n", Ui.Tree(last).OfType<TextBlock>().Select(t => t.Text));
            Assert.That(AutomationProperties.GetName(last), Does.Contain("要求タスク 1040").And.Contain("見積工数  8h → 16h").And.Not.Contain("1 要求タスク 1\n"));
            Assert.That(text, Does.Contain("要求タスク 1040").And.Contain("見積工数  8h → 16h")
                .And.Contain("実績工数  0h → 3h").And.Contain("見込み残存工数  8h → 13h"));
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.True);
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "publish-grouped-version-end");
        });
    }
    [Test]
    public async Task RecycledConflictKeepsItsIssueAndResolutionReturnsToThatIssueWithoutWriting()
    {
        await Open(60);
        await Ui.Run(async () => await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Paste,
            workspace.Session.Document.State.Rows.SelectMany(r => new[] {
                new PlanCellChange(r.Identity, PlanField.Actual, 3m), new PlanCellChange(r.Identity, PlanField.Remaining, 5m) }).ToImmutableArray()), Today));
        var remote = FakePlanEditor.Load(root);
        FakePlanEditor.Save(root, remote with { Issues = remote.Issues.Select(i => i.Row.Identity == "I60" ? i with { Row = i.Row with { Actual = 2 } } : i).ToImmutableArray() });
        await Ui.Run(() => Ui.Click("PlanRefresh"));
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.Length == 1);
        await Ui.Until(() => Ui.Find<Button>("PlanPublish").IsEnabled);
        await Ui.Run(() => Ui.Click("PlanPublish"));
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(() => {
            Assert.That(Ui.Find<Button>("PlanPublishConfirm").IsEnabled, Is.False);
            var list = Ui.Find<ListView>("PlanPublishLines"); list.ScrollIntoView(list.Items[59], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Ready<Button>("PlanResolveI60_Actual_True");
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanPublishLines"); list.ScrollIntoView(list.Items[0], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Until(() => Ui.Find<ListView>("PlanPublishLines").ContainerFromIndex(0) is ListViewItem { IsLoaded: true, ActualHeight: > 0 });
        await Ui.Run(() => {
            var list = Ui.Find<ListView>("PlanPublishLines"); list.ScrollIntoView(list.Items[59], ScrollIntoViewAlignment.Leading);
        });
        await Ui.Ready<Button>("PlanResolveI60_Actual_True");
        await Ui.Run(() => {
            var group = Ui.Find<PlanPublishGroupView>("PlanPublishGroupI60");
            Assert.That(string.Join(" ", Ui.Tree(group).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("要求タスク 60").And.Contain("競合  GitHub: 2"));
            Ui.Click("PlanResolveI60_Actual_True");
        });
        await Ui.Until(() => workspace.Session!.Document.Sync.Conflicts.IsEmpty && Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
        try { await Ui.Until(() => {
            var group = Ui.Tree(view).OfType<PlanPublishGroupView>().FirstOrDefault(g =>
                AutomationProperties.GetAutomationId(g) == "PlanPublishGroupI60" && g.IsLoaded && g.ActualHeight > 0);
            if (group is null) return false;
            var list = Ui.Find<ListView>("PlanPublishLines");
            var bounds = group.TransformToVisual(list).TransformBounds(new(0, 0, group.ActualWidth, group.ActualHeight));
            return bounds.Bottom > 0 && bounds.Top < list.ActualHeight;
        }); }
        catch {
            await Ui.Run(async () => {
                var list = Ui.Find<ListView>("PlanPublishLines");
                TestContext.Out.WriteLine("Review realized groups: " + string.Join(", ", Ui.Tree(list).OfType<PlanPublishGroupView>().Select(g =>
                    $"{AutomationProperties.GetAutomationId(g)}:{g.TransformToVisual(list).TransformBounds(new(0, 0, g.ActualWidth, g.ActualHeight))}")));
                await RenderedEvidence.Capture(view, "publish-position-failure");
            });
            throw;
        }
        await Ui.Run(async () => {
            Assert.That(workspace.Session!.Document.State.Rows.Single(r => r.Identity == "I60").Actual, Is.EqualTo(2));
            var group = Ui.Find<PlanPublishGroupView>("PlanPublishGroupI60");
            var list = Ui.Find<ListView>("PlanPublishLines");
            var bounds = group.TransformToVisual(list).TransformBounds(new(0, 0, group.ActualWidth, group.ActualHeight));
            Assert.That(bounds.Bottom, Is.GreaterThan(0));
            Assert.That(bounds.Top, Is.LessThan(list.ActualHeight), "The resolved Issue must remain in the review viewport.");
            Assert.That(string.Join(" ", Ui.Tree(group).OfType<TextBlock>().Select(t => t.Text)), Does.Contain("見込み残存工数  8h → 5h").And.Not.Contain("競合"));
            Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.Zero);
            await RenderedEvidence.Capture(view, "publish-recycled-conflict-resolved");
        });
    }
}
