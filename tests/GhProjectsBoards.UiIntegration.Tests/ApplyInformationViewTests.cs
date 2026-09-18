using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

[TestFixture, NonParallelizable]
public sealed class ApplyInformationViewTests
{
    [Test]
    public async Task PartialSelectionDetailsAndRecheckKeepViewportFocusAndRowIdentity()
    {
        var p = ApplyInformationTests.ThreeFields(); var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var row in rows) foreach (var cell in row.Cells.Where(c => c.Key?.Kind == "Select")) w.Commit("P1", cell, "done", true);
        var selected = new HashSet<string>(); var visible = rows.Select(r => r.ItemId).ToArray();
        ApplyConfirmationTable table = null!;
        void Refresh(bool latest) => table.Update(ApplyConfirmationPresentation.Create(w, p, w.ApplyCandidates(p), visible, false, selected,
            latest ? w.ReviewApply(p, selected) : null, latest), selected);
        await Ui.Run(() =>
        {
            table = new() { Width = 1000, Height = 520 };
            table.SelectionUpdated = () => { selected = table.SelectedIds; Refresh(true); };
            Refresh(true);
        });
        await Ui.Mount(table);
        try
        {
            await Ui.Until(() => table.List.ContainerFromIndex(0) is FrameworkElement { IsLoaded: true });
            await Ui.Run(() =>
            {
                table.List.SelectedItems.Add(table.List.Items[10]);
                Assert.That(Ui.Find<CheckBox>("ApplySelectAll", table).IsChecked, Is.Null);
                Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", table));
                Assert.That(table.SelectedIds.Count, Is.EqualTo(23));
                Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", table));
                Assert.That(table.SelectedIds, Is.Empty);
                table.List.SelectedItems.Add(table.List.Items[10]);
                table.List.ScrollIntoView(table.List.Items[10], ScrollIntoViewAlignment.Leading);
            });
            await Ui.Until(() => table.List.ContainerFromIndex(10) is FrameworkElement { IsLoaded: true } row
                && Ui.Tree(table.List).OfType<ScrollViewer>().First().VerticalOffset > 0
                && row.TransformToVisual(table.List).TransformPoint(new()).Y >= 0
                && row.TransformToVisual(table.List).TransformPoint(new()).Y < table.List.ActualHeight);
            Button details = null!; double before = 0; ScrollViewer scroll = null!;
            await Ui.Run(() =>
            {
                scroll = Ui.Tree(table.List).OfType<ScrollViewer>().First();
                details = Ui.Find<Button>("ApplyDetails-P1T11", table);
                Assert.That(details.Focus(FocusState.Keyboard), Is.True);
                before = scroll.VerticalOffset;
                Assert.That(before, Is.GreaterThan(0));
                Ui.Click(details);
            });
            await Ui.Until(() => details.Flyout.IsOpen);
            await Ui.Run(() => { Refresh(false); Refresh(true); });
            await Ui.Until(() => Math.Abs(scroll.VerticalOffset - before) < 1);
            await Ui.Run(async () =>
            {
                Assert.That(table.SelectedIds, Is.EquivalentTo(new[] { "P1T11" }));
                Assert.That(details.Flyout.IsOpen, Is.True);
                Assert.That(Ui.Tree((DependencyObject)((Flyout)details.Flyout).Content).OfType<TextBlock>().Select(t => t.Text),
                    Has.Some.Contains("項目 ID P1T11"));
                await ApplyInformationEvidence.Capture(table, "partial-selection-after-recheck");
                details.Flyout.Hide();
            });
            await Ui.Until(() => !details.Flyout.IsOpen && ReferenceEquals(FocusManager.GetFocusedElement(table.XamlRoot), details));
            await Ui.Run(() =>
            {
                Assert.That(scroll.VerticalOffset, Is.EqualTo(before).Within(1));
                Assert.That(Ui.Find<TextBlock>("ApplyValue-P1T11-Field-F0", table).Text, Is.EqualTo("Backlog → Ready"));
                table.Width = 620;
            });
            await Ui.Until(() => table.ActualWidth == 620);
            await Ui.Run(() =>
            {
                Assert.That(table.SelectedIds, Is.EquivalentTo(new[] { "P1T11" }));
                Assert.That(details.IsLoaded, Is.True);
                Assert.That(FocusManager.GetFocusedElement(table.XamlRoot), Is.SameAs(details));
                Assert.That(w.Journal, Is.Empty);
            });
        }
        finally { await Ui.Unmount(table); }
    }

    [TestCase(620, 14), TestCase(1000, 21)]
    public async Task NarrowOrEnlargedTextKeepsFieldLabelsAndCompleteDetailsAccessible(double width, double fontSize)
    {
        var p = ApplyInformationTests.ThreeFields(2);
        var longValue = new string('長', 180) + "末尾の値";
        p = p with { Snapshot = p.Snapshot with { Fields = p.Snapshot.Fields.Select(f => f with { Name = "同名",
            Options = f.Options.Select(o => o.Id == "done" ? o with { Name = longValue } : o).ToArray() }).ToArray(),
            Issues = p.Snapshot.Issues.ToDictionary(x => x.Key, x => x.Value with { Number = 1,
                Repository = x.Value.Repository with { NameWithOwner = "owner/" + x.Key.NodeId },
                Title = new(ValueAvailability.Present, new string('題', 180) + "元の末尾") }) } };
        var w = new EditingWorkspace(p.Snapshot.Id.Scope); w.SetRegistrations([p]); var rows = w.Open(p);
        foreach (var row in rows)
        {
            w.Commit("P1", row.Cells[0], new string('題', 180) + "変更した末尾");
            foreach (var cell in row.Cells.Where(c => c.Key?.Kind == "Select")) w.Commit("P1", cell, "done", true);
        }
        var view = ApplyConfirmationPresentation.Create(w, p, w.ApplyCandidates(p), rows.Select(r => r.ItemId).ToArray(), false, new HashSet<string>(), null, false);
        ApplyConfirmationTable table = null!;
        await Ui.Run(() =>
        {
            table = new() { Width = width, Height = 520, FontSize = fontSize };
            // Exercise larger native text in the bounded host without changing the user's OS setting.
            table.List.ItemContainerStyle = new Style(typeof(ListViewItem)) { Setters = { new Setter(Control.FontSizeProperty, fontSize) } };
            table.Update(view, new HashSet<string>());
        });
        await Ui.Mount(table);
        try
        {
            await Ui.Until(() => table.List.ContainerFromIndex(0) is FrameworkElement { IsLoaded: true });
            Button details = null!;
            await Ui.Run(() =>
            {
                var row = (FrameworkElement)table.List.ContainerFromIndex(0);
                var text = Ui.Tree(row).OfType<TextBlock>().Where(t => t.Visibility == Visibility.Visible).ToArray();
                Assert.That(text.Select(t => t.Text), Has.Some.EqualTo("同名 [F1]"));
                Assert.That(text.Select(t => t.Text), Has.Some.EqualTo("タイトル変更"));
                var labels = text.Where(t => t.Text == "同名 [F1]").ToArray();
                Assert.That(labels.All(t => t.ActualWidth > 0 && t.FontSize >= fontSize), Is.True);
                details = Ui.Find<Button>("ApplyDetails-P1T1", table); Ui.Click(details);
            });
            await Ui.Until(() => details.Flyout.IsOpen && Ui.Tree((DependencyObject)((Flyout)details.Flyout).Content).OfType<TextBlock>().Any(t => t.IsLoaded && t.ActualHeight > 0));
            await Ui.Run(async () =>
            {
                var full = string.Join("\n", Ui.Tree((DependencyObject)((Flyout)details.Flyout).Content).OfType<TextBlock>().Select(t => t.Text));
                Assert.That(full, Does.Contain("owner/I1 #1").And.Contain("元の末尾").And.Contain("変更した末尾").And.Contain(longValue).And.Contain("F1"));
                await ApplyInformationEvidence.Capture(table, $"long-narrow-{width}-font-{fontSize}");
                await ApplyInformationEvidence.Capture((FrameworkElement)((Flyout)details.Flyout).Content, $"complete-details-{width}-font-{fontSize}");
                Assert.That(table.SelectedIds, Is.Empty);
                details.Flyout.Hide();
            });
            await Ui.Until(() => !details.Flyout.IsOpen);
        }
        finally { await Ui.Unmount(table); }
    }
}
