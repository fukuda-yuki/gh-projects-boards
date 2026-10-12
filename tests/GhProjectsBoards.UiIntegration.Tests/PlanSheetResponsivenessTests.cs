using System.Diagnostics;
using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NUnit.Framework;
using Windows.System;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanSheetHostedTests
{
    [Test]
    public async Task MovingAndContractingSelectionClearsOldBordersAndFillHandles()
    {
        var before = PlanJson.Text(session.Document.State);
        async Task CheckSelection(int first, int last, string[] fields, int active, string end)
        {
            await Ui.Run(() => {
                for (var number = 1; number <= 6; number++) {
                    Assert.That(Ui.Find<Border>($"PlanSelectionAccent{number}").Visibility,
                        Is.EqualTo(number == active ? Visibility.Visible : Visibility.Collapsed));
                    foreach (var field in new[] { PlanField.Estimate, PlanField.Remaining }) {
                        var selected = number >= first && number <= last && fields.Contains(field);
                        var id = $"PlanCell{number}_{field}";
                        Assert.That(SelectProvider(id).IsSelected, Is.EqualTo(selected));
                        var cell = Ui.Find<TextBox>(id);
                        var frame = (Border)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(cell));
                        Assert.That(frame.BorderThickness.Left, Is.EqualTo(selected ? 2 : 0));
                        Assert.That(cell.Text, Is.EqualTo("8h"));
                    }
                }
                var handles = Ui.Tree(sheet).OfType<Button>().Where(button => button.Visibility == Visibility.Visible
                    && AutomationProperties.GetAutomationId(button).StartsWith("PlanFillHandle", StringComparison.Ordinal));
                Assert.That(handles.Select(AutomationProperties.GetAutomationId), Is.EqualTo(new[] { $"PlanFillHandle{active}_{end}" }));
            });
        }
        for (var number = 1; number <= 6; number++) await Ui.Ready<TextBox>($"PlanCell{number}_Remaining");
        await SelectFocusedCell(1, PlanField.Estimate);
        await SelectFocusedCell(4, PlanField.Remaining, true);
        await CheckSelection(1, 4, [PlanField.Estimate, PlanField.Remaining], 4, PlanField.Remaining);
        await SelectFocusedCell(2, PlanField.Estimate, true);
        await CheckSelection(1, 2, [PlanField.Estimate], 2, PlanField.Estimate);
        await SelectFocusedCell(6, PlanField.Remaining);
        await CheckSelection(6, 6, [PlanField.Remaining], 6, PlanField.Remaining);
        Assert.That(PlanJson.Text(session.Document.State), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task ScrollMeasurementTargetsRowsDespiteNestedEditorScrollViewers()
    {
        ScrollViewer vertical = null!;
        await Ui.Run(() => {
            Assert.That(Ui.Tree(sheet.List).OfType<ScrollViewer>().Count(), Is.GreaterThan(1));
            vertical = ScrollMeasurementViewport();
            vertical.ChangeView(null, 1120, null, true);
        });
        await Ui.Until(() => Math.Abs(vertical.VerticalOffset - 1120) < 1
            && sheet.List.ContainerFromIndex(40) is ListViewItem { ContentTemplateRoot: PlanSheetRow { IsLoaded: true, Identity: "I41" } });
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            var row = (PlanSheetRow)((ListViewItem)sheet.List.ContainerFromIndex(40)).ContentTemplateRoot;
            Assert.That(row.Identity, Is.EqualTo("I41"));
            Assert.That(row.TransformToVisual(vertical).TransformPoint(new()).Y, Is.EqualTo(0).Within(2));
            Assert.That(session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public async Task FilterTypingKeepsRowsUntilPauseAndShowsAcceptedMatchCountWithoutEditingPlan()
    {
        var before = session.Document.State;
        var undo = session.UndoCount;
        await Ui.Run(async () => {
            var filter = Ui.Find<TextBox>("PlanSheetFilter");
            filter.Focus(FocusState.Programmatic);
            filter.Text = "Task 1";
            Assert.That(filter.Text, Is.EqualTo("Task 1"));
            Assert.That(sheet.List.Items.Count, Is.EqualTo(101));
            await Task.Delay(180);
            Assert.That(sheet.List.Items.Count, Is.EqualTo(101));
            filter.Text = "Task 20";
            await Task.Delay(180);
            Assert.That(sheet.List.Items.Count, Is.EqualTo(101), "Each change restarts the pause.");
        });
        await Ui.Until(() => sheet.List.Items.Count == 2);
        await Ui.Run(() => {
            Assert.That(sheet.List.Items[0], Is.EqualTo("I20"));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("1 件"));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Visibility, Is.EqualTo(Visibility.Visible));
        });
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [Test]
    public async Task NarrowingAndClearingFilterRetainsTheMatchingNativeEditorAndSelection()
    {
        await Select(1, PlanField.Title);
        TextBox original = null!;
        await Ui.Run(() => {
            original = Ui.Find<TextBox>("PlanCell1_Title");
            Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic);
            Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 1";
        });
        await Ui.Until(() => sheet.List.Items.Count == 13);
        await Ui.Ready<TextBox>("PlanCell1_Title");
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title"), Is.SameAs(original), "A matching row keeps its native editor when other rows disappear.");
            Assert.That(original.Text, Is.EqualTo("Task 1"));
            Assert.That(SelectProvider("PlanCell1_Title").IsSelected, Is.True);
            Ui.Find<TextBox>("PlanSheetFilter").Text = "";
        });
        await Ui.Until(() => sheet.List.Items.Count == 101);
        await Ui.Ready<TextBox>("PlanCell1_Title");
        for (var number = 2; number <= 9; number++) await Ui.Ready<TextBox>($"PlanCell{number}_Title");
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Title"), Is.SameAs(original));
            Assert.That(original.Text, Is.EqualTo("Task 1"));
            for (var number = 2; number <= 9; number++)
                Assert.That(Ui.Find<TextBox>($"PlanCell{number}_Title").Text, Is.EqualTo($"Task {number}"), "Restored rows must display their current task rather than a recycled row's content.");
            Assert.That(SelectProvider("PlanCell1_Title").IsSelected, Is.True);
            Assert.That(session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public async Task DeepFilteredDependencyRemainsCoherentWhenClearedFoldedAndUndone()
    {
        var before = PlanJson.Text(session.Document.State);
        await session.Execute(new EditPlanCells(PlanOperationKind.Cell,
            [new("I100", PlanField.Parent, "I99"), new("I100", PlanField.Predecessors, System.Collections.Immutable.ImmutableArray.Create("I1"))]), Today);
        await Ui.Run(() => { sheet.Refresh(); sheet.List.ScrollIntoView("I100"); });
        await Ui.Ready<TextBox>("PlanCell100_Title");
        await Ui.Run(() => {
            Ui.Find<TextBox>("PlanSheetFilter").Focus(FocusState.Programmatic);
            Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 100";
        });
        await Ui.Until(() => sheet.List.Items.Count == 2);
        await Ui.Ready<TextBox>("PlanCell100_End");
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(sheet.List.Items.Cast<string>(), Is.EqualTo(new[] { "I100", "" }));
            Assert.That(Ui.Find<TextBox>("PlanCell100_End").Text, Is.EqualTo("10/6 (火)"));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("1 件"));
            Ui.Find<TextBox>("PlanSheetFilter").Text = "";
        });
        await Ui.Until(() => sheet.List.Items.Count == 101);
        await Ui.Run(() => sheet.List.ScrollIntoView("I99"));
        await Ui.Ready<Button>("PlanFold99");
        await Ui.Run(() => Ui.Click("PlanFold99"));
        await Ui.Until(() => !sheet.List.Items.Contains("I100"));
        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Z));
        await Ui.Until(() => sheet.List.Items.Count == 101);
        Assert.That(PlanJson.Text(session.Document.State), Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.Zero);
    }

    [Test]
    public async Task FilterAfterCalendarChangeAndUndoKeepsCurrentDatesAndCalendarShading()
    {
        await Ui.Run(() => Ui.Find<ComboBox>("PlanGanttZoom").SelectedItem = "日");
        await Ui.Idle();
        bool Shaded() => Ui.Tree(sheet).OfType<FrameworkElement>().Any(element =>
            AutomationProperties.GetAutomationId(element) == "PlanNonWorking20261005");
        await Ui.Run(() => Assert.That(Shaded(), Is.False));

        await session.Execute(new ReplacePlanSettings(session.Document.State.Settings with { CompanyDaysOff = [Today] }), Today);
        await Ui.Run(() => { sheet.Refresh(); Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 1"; });
        await Ui.Until(() => sheet.List.Items.Count == 13);
        await Ui.Ready<TextBox>("PlanCell1_Start");
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("10/6 (火)"));
            Assert.That(Shaded(), Is.True);
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("12 件"));
            Assert.That(session.UndoCount, Is.EqualTo(1), "Filtering adds no operation to Undo.");
        });

        await Ui.Run(() => sheet.KeyboardCommand(VirtualKey.Z));
        await Ui.Idle();
        await Ui.Run(() => {
            Assert.That(Ui.Find<TextBox>("PlanCell1_Start").Text, Is.EqualTo("10/5 (月)"));
            Assert.That(Shaded(), Is.False, "Undo must also refresh a calendar whose date range did not change.");
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("12 件"));
            Assert.That(session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public async Task EmptyFilterCancelsPendingTextAndRestoresRowsWithoutWaitingForPause()
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "no match");
        await Ui.Until(() => sheet.List.Items.Count == 1);
        await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("0 件")));
        var elapsed = new Stopwatch();
        var applied = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        Windows.Foundation.Collections.VectorChangedEventHandler<object> observer = (_, args) => {
            if (elapsed.IsRunning && args.CollectionChange == Windows.Foundation.Collections.CollectionChange.ItemInserted)
                applied.TrySetResult(elapsed.Elapsed.TotalMilliseconds);
        };
        await Ui.Run(() => sheet.List.Items.VectorChanged += observer);
        try {
            await Ui.Run(() => {
                var filter = Ui.Find<TextBox>("PlanSheetFilter");
                filter.Text = "Task 20";
                elapsed.Restart();
                filter.Text = "";
            });
            var clearToRows = await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Out.WriteLine($"Clear to first restored row: {clearToRows:F3} ms");
            Assert.That(clearToRows, Is.LessThan(300), "Clear must start restoring rows without the debounce; full layout has a separate performance measurement.");
            await Ui.Until(() => sheet.List.Items.Count == 101);
        } finally {
            await Ui.Run(() => sheet.List.Items.VectorChanged -= observer);
        }
        await Task.Delay(350);
        await Ui.Run(() => {
            Assert.That(sheet.List.Items.Count, Is.EqualTo(101));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
    }

    [Test, Category("PlanSheetNative")]
    public async Task EnterAppliesFilterBeforePauseThroughNativeKeyRoute()
    {
        await SheetNativeInput.ActivateWindow();
        TextBox filter = null!;
        var elapsed = new Stopwatch();
        var applied = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        Windows.Foundation.Collections.VectorChangedEventHandler<object> observer = (_, _) => {
            if (sheet.List.Items.Cast<string>().SequenceEqual(new[] { "I20", "" }))
                applied.TrySetResult(elapsed.Elapsed.TotalMilliseconds);
        };
        Microsoft.UI.Xaml.Input.KeyEventHandler pressed = (_, args) => {
            if (args.OriginalKey != VirtualKey.Enter) return;
            // Supply fresh text at native key delivery, after activation/focus and input-driver setup.
            // The real KeyDown route must apply it without waiting for the newly started debounce.
            filter.Text = "Task 20";
            elapsed.Restart();
        };
        await Ui.Run(() => {
            filter = Ui.Find<TextBox>("PlanSheetFilter");
            Assert.That(filter.Focus(FocusState.Programmatic), Is.True);
        });
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            filter.AddHandler(UIElement.PreviewKeyDownEvent, pressed, true);
            sheet.List.Items.VectorChanged += observer;
        });
        try {
            await SheetNativeInput.Press(VirtualKey.Enter);
            var keyToRows = await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Out.WriteLine($"Native Enter to accepted rows: {keyToRows:F3} ms");
            Assert.That(elapsed.IsRunning, Is.True, "The native preview-key route must run.");
            Assert.That(keyToRows, Is.LessThan(300), "Enter must bypass the debounce, excluding activation and driver setup.");
            await Ui.Run(() => Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("1 件")));
        } finally {
            await Ui.Run(() => {
                filter.RemoveHandler(UIElement.PreviewKeyDownEvent, pressed);
                sheet.List.Items.VectorChanged -= observer;
            });
        }
    }

    [TestCase("Task 20"), TestCase("")]
    public async Task RejectedFilterRestoresAcceptedTextCountAndRowsWithoutLosingInvalidInput(string proposed)
    {
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 1");
        await Ui.Until(() => sheet.List.Items.Count == 13);
        await Edit(1, PlanField.Remaining, "invalid");
        var before = session.Document.State;
        var undo = session.UndoCount;
        await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = proposed);
        await Ui.Until(() => Ui.Find<TextBox>("PlanSheetFilter").Text == "Task 1");
        await Ui.Run(() => {
            Assert.That(sheet.List.Items.Count, Is.EqualTo(13));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("12 件"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("invalid"));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Assert.That(Ui.Popup<Border>("SheetInputProblem"), Is.Not.Null);
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [Test]
    public async Task FilterCountExcludesRetainedNonmatchingPendingRows()
    {
        await PrepareSheetPendingExit();
        await Ui.Run(() => {
            Assert.That(sheet.List.Items.Contains("I1"), Is.True, "The invalid input remains reachable.");
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("11 件"));
        });
    }

    [Test]
    public async Task UnloadingCancelsUnacceptedFilterAndRemountRestoresAcceptedText()
    {
        var before = session.Document.State;
        var undo = session.UndoCount;
        TextBox filter = null!;
        await Ui.Run(() => { filter = Ui.Find<TextBox>("PlanSheetFilter"); filter.Text = "Task 20"; });
        await Ui.Unmount(sheetHost);
        await Task.Delay(350);
        await Ui.Run(() => Assert.That(sheet.List.Items.Count, Is.EqualTo(101)));
        await Ui.Mount(sheetHost);
        await Ui.Run(() => {
            Assert.That(filter.Text, Is.Empty);
            Assert.That(sheet.List.Items.Count, Is.EqualTo(101));
            Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Visibility, Is.EqualTo(Visibility.Collapsed));
        });
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [Test]
    public async Task NewerFilterSupersedesTextQueuedBehindClipboardRead()
    {
        var content = new TaskCompletionSource<PlanClipboardContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clipboardReader = () => { started.TrySetResult(); return content.Task; };
        var before = session.Document.State;
        var undo = session.UndoCount;
        await Select(1, PlanField.Title);
        await Ui.ClickCommand("PlanSheetPaste");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var staleResultShown = false;
        EventHandler<RenderedEventArgs> observer = (_, _) => {
            var rows = sheet.List.Items.Cast<string>();
            if (rows.Contains("I1") && !rows.Contains("I2")) staleResultShown = true;
        };
        await Ui.Run(() => CompositionTarget.Rendered += observer);
        try {
            await Ui.Run(() => Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 1");
            await Task.Delay(350);
            await Ui.Run(() => {
                Assert.That(sheet.List.Items.Count, Is.EqualTo(101));
                Ui.Find<TextBox>("PlanSheetFilter").Text = "Task 20";
            });
            content.TrySetResult(new("", null));
            await Ui.Until(() => sheet.List.Items.Count == 2);
            await Ui.Run(() => {
                Assert.That(staleResultShown, Is.False, "The superseded filter must never replace the visible result.");
                Assert.That(sheet.List.Items[0], Is.EqualTo("I20"));
                Assert.That(Ui.Find<TextBlock>("PlanSheetFilterCount").Text, Is.EqualTo("1 件"));
            });
        } finally {
            content.TrySetResult(new("", null));
            await Ui.Run(() => CompositionTarget.Rendered -= observer);
        }
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [TestCase(false), TestCase(true)]
    public async Task HorizontalScrollKeepsPendingEditorCaretAndSelectionAndAlignsContent(bool gantt)
    {
        var before = session.Document.State;
        var undo = session.UndoCount;
        TextBox cell = null!;
        ScrollViewer scroll = null!;
        double cellX = 0, barX = 0, offset = 0, target = 0;
        string selection = "";
        await Ui.Run(() => {
            var divider = Ui.Find<PlanSheetDivider>("PlanSheetDivider");
            var provider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(divider).GetPattern(PatternInterface.RangeValue);
            provider.SetValue(500);
        });
        await Ui.Until(() => Math.Abs(Ui.Find<ScrollViewer>("PlanSheetHorizontal").ActualWidth - 500) < 1);
        await SheetNativeInput.Rendered();
        await Select(1, PlanField.Title);
        // Selection reveals its column again at low priority; let that finish before scrolling.
        await Ui.LowPriorityIdle();
        await Ui.Run(() => {
            cell = Ui.Find<TextBox>("PlanCell1_Title");
            Assert.That(cell.Focus(FocusState.Programmatic), Is.True);
            cell.Text = "Pending title"; cell.Select(3, 2);
            selection = Ui.Find<TextBlock>("PlanSheetSelection").Text;
            scroll = Ui.Find<ScrollViewer>(gantt ? "PlanGanttHorizontal" : "PlanSheetHorizontal");
            cellX = cell.TransformToVisual(sheet).TransformPoint(new()).X;
            barX = Ui.Find<FrameworkElement>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X;
            offset = scroll.HorizontalOffset;
            target = offset + 120 <= scroll.ScrollableWidth ? offset + 120 : Math.Max(0, offset - 120);
            Assert.That(target, Is.Not.EqualTo(offset));
            scroll.ChangeView(offset + (target - offset) / 2, null, null, true);
            scroll.ChangeView(target, null, null, true);
        });
        await Ui.Until(() => Math.Abs(scroll.HorizontalOffset - target) < 1);
        await SheetNativeInput.Rendered();
        await Ui.Run(() => {
            Assert.That(cell.Text, Is.EqualTo("Pending title"));
            Assert.That(cell.FocusState, Is.Not.EqualTo(FocusState.Unfocused));
            Assert.That(cell.SelectionStart, Is.EqualTo(3));
            Assert.That(cell.SelectionLength, Is.EqualTo(2));
            Assert.That(Ui.Find<TextBlock>("PlanSheetSelection").Text, Is.EqualTo(selection));
            Assert.That(cell.TransformToVisual(sheet).TransformPoint(new()).X,
                Is.EqualTo(cellX - (gantt ? 0 : target - offset)).Within(1));
            Assert.That(Ui.Find<FrameworkElement>("PlanBar1").TransformToVisual(sheet).TransformPoint(new()).X,
                Is.EqualTo(barX - (gantt ? target - offset : 0)).Within(1));
        });
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
    }

    [Test]
    public async Task RejectedCellShowsAnchoredAnnouncedMessageWithoutMovingVisibleRows()
    {
        Dictionary<string, double> positions = null!;
        await Ui.Run(() => positions = sheet.Realized.Where(r => r.IsLoaded).ToDictionary(r => r.Identity,
            r => r.TransformToVisual(sheet).TransformPoint(new()).Y));
        var before = session.Document.State;
        var undo = session.UndoCount;
        await Edit(1, PlanField.Remaining, "invalid");
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is not null);
        await Ui.Run(() => {
            sheet.Refresh();
            sheet.Select("I1", PlanField.Title, false);
            sheet.Select("I1", PlanField.Remaining, false);
            sheet.UpdateLayout();
            foreach (var row in sheet.Realized.Where(r => positions.ContainsKey(r.Identity)))
                Assert.That(row.TransformToVisual(sheet).TransformPoint(new()).Y, Is.EqualTo(positions[row.Identity]));
            var message = (TextBlock)Ui.Popup<Border>("SheetInputProblem")!.Child;
            Assert.That(message.Text, Is.Not.Empty);
            Assert.That(AutomationProperties.GetLiveSetting(message), Is.EqualTo(Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive));
            Assert.That(Ui.Find<TextBlock>("PlanSheetError").Visibility, Is.EqualTo(Visibility.Collapsed));
            Assert.That(Ui.Find<TextBox>("PlanCell1_Remaining").Text, Is.EqualTo("invalid"));
        });
        Assert.That(session.Document.State, Is.EqualTo(before));
        Assert.That(session.UndoCount, Is.EqualTo(undo));
        await Edit(1, PlanField.Remaining, "4");
        await Ui.Until(() => Ui.Popup<Border>("SheetInputProblem") is null);
    }

    [Test]
    public async Task SheetListDoesNotAnimateItems()
    {
        await Ui.Run(() => AssertQuietList(sheet.List));
    }

    [Test]
    public async Task SheetFlyoutsAndPredecessorCandidatesHaveNoTransitions()
    {
        await Select(2, PlanField.Title);
        await Ui.ClickCommand("PlanSheetPredecessorAdd");
        await Ui.Until(() => Ui.Popup<ListView>("PlanPredecessorCandidates")?.ContainerFromIndex(0) is ListViewItem { IsLoaded: true });
        await Ui.Run(() => {
            AssertQuietList(Ui.Popup<ListView>("PlanPredecessorCandidates")!);
            AssertQuietPresenter();
            Ui.Click(Ui.Popup<Button>("PlanPredecessorCancel")!);
        });

        void AssertQuietPresenter()
        {
            var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(sheet.XamlRoot)
                .Where(p => p.IsOpen).SelectMany(p => Ui.Tree(p.Child)).OfType<FlyoutPresenter>().Single();
            Assert.That(presenter.ContentTransitions, Is.Null.Or.Empty);
            Assert.That(presenter.Transitions, Is.Null.Or.Empty);
        }
    }

    internal static void AssertQuietList(ListView list)
    {
        Assert.That(list.ItemContainerTransitions, Is.Null.Or.Empty);
        Assert.That(list.Transitions, Is.Null.Or.Empty);
        foreach (var item in Ui.Tree(list).OfType<ListViewItem>()) {
            Assert.That(item.ContentTransitions, Is.Null.Or.Empty);
            Assert.That(item.Transitions, Is.Null.Or.Empty);
        }
    }
}
