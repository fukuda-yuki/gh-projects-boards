using GhProjectsBoards.App;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Tests;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using NUnit.Framework;
using Windows.Storage.Streams;

namespace GhProjectsBoards.UiIntegration.Tests;

internal sealed partial class PlanWorkspaceHostedTests
{
    [TestCase(false), TestCase(true), Category("EndDatePaint")]
    public async Task UnchangedTaskEndRepaintsRedAcrossStatusDateAndPublication(bool publish)
    {
        readDay = new(2026, 10, 5);
        var calculatedEnd = new DateOnly(2026, 10, 14);
        FakePlanEditor.Save(root, new([
            new(new("I1", "設計", "acme/repo") {
                Estimate = 8, Remaining = 8, Assignees = ["U1"],
                Start = readDay, End = readDay.AddDays(1)
            }, "", true)
        ], 2));
        await Open();
        // The PMO moves the task to 10/14; publishing it later writes that plan end.
        await workspace.Session!.Execute(new EditPlanCells(PlanOperationKind.Cell,
            [new("I1", PlanField.Start, calculatedEnd), new("I1", PlanField.End, calculatedEnd)]), readDay);
        // Move the 基準日 back and forth through the same picker used for the round trip: a 基準日 change
        // moves no plan date (#236), and the retained end must return to its ordinary colour.
        await SetEndColorStatusDate(readDay.AddDays(-1));
        await SetEndColorStatusDate(readDay);
        PlanSheetCell end = null!;
        string text = "";
        await Ui.Ready<PlanSheetCell>("PlanCell1_End");
        await Ui.Run(() => {
            end = Ui.Find<PlanSheetCell>("PlanCell1_End");
            text = end.Text;
            Assert.That(text, Is.EqualTo("10/14 (水)"));
            Assert.That(workspace.Session!.Document.State.Rows.Single().Parent, Is.Null);
        });
        await AssertEndColorPixels(end, text, false, "normal");

        // Dates are plan values (#236): the end turns red only once the 基準日 passes it.
        await SetEndColorStatusDate(calculatedEnd.AddDays(1));
        await AssertEndColorPixels(end, text, true, "overdue");
        if (publish)
        {
            // Use the existing hosted publication path with fake gh and real readback/adoption.
            await Ui.Run(() => Ui.Click("PlanPublish"));
            await Ui.Ready<Button>("PlanPublishConfirm");
            await Ui.Until(() => Ui.Find<Button>("PlanPublishConfirm").IsEnabled);
            await Ui.Run(() => Ui.Click("PlanPublishConfirm"));
            // After a successful publication the review keeps 発行する disabled and shows the result line (#138).
            await Ui.Until(() => workspace.Session!.Document.Baseline.Rows.Single().End == calculatedEnd
                && Ui.Find<TextBlock>("PlanPublishResult").Visibility == Microsoft.UI.Xaml.Visibility.Visible && Ui.Find<Button>("PlanPublish").IsEnabled);
            await Ui.Run(() => Ui.Click("PlanPublishClose"));
            await Ui.Idle();
            Assert.That(FakePlanEditor.Load(root).Issues.Single().Row.End, Is.EqualTo(calculatedEnd));
            // Publishing does not change lateness: the retained cell stays red after the publication repaint.
            await AssertEndColorPixels(end, text, true, "published");
        }
        var batches = FakePlanEditor.Load(root).MutationBatches;
        await SetEndColorStatusDate(readDay);
        Assert.That(FakePlanEditor.Load(root).MutationBatches, Is.EqualTo(batches));
        await AssertEndColorPixels(end, text, false, "status-date-restored");
    }

    private async Task SetEndColorStatusDate(DateOnly date)
    {
        await Ui.Run(() => Ui.Find<CalendarDatePicker>("PlanStatusDate").Date =
            new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)));
        await Ui.Until(() => workspace.Session!.Document.State.Settings.StatusDate == date);
        await Ui.Idle();
        var saved = await workspace.Session!.FlushAsync();
        Assert.That(saved.Succeeded, Is.True, "Status-date save must succeed before checking paint: " + saved.Error);
    }

    private static async Task AssertEndColorPixels(PlanSheetCell end, string text, bool critical, string state)
    {
        await Ui.Idle();
        await Ui.Run(async () => {
            Assert.That(Ui.Find<PlanSheetCell>("PlanCell1_End"), Is.SameAs(end), "Exercise the retained native TextBox.");
            Assert.That(end.Text, Is.EqualTo(text), state);
            Assert.That(end.IsLoaded && end.ActualWidth > 0 && end.ActualHeight > 0, Is.True);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(end);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(buffer);
            var pixels = new byte[buffer.Length];
            reader.ReadBytes(pixels);
            var red = 0;
            var neutral = 0;
            // Capture only the TextBox: adjacent lateness icons and unpublished corners
            // must not satisfy the text-color assertion. Allow antialiasing in BGRA pixels.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a < 80) continue;
                if (r > g + 40 && r > b + 40) red++;
                if (Math.Abs(r - g) < 12 && Math.Abs(r - b) < 12 && Math.Max(r, Math.Max(g, b)) < 180) neutral++;
            }
            TestContext.Out.WriteLine($"{state}: {bitmap.PixelWidth}x{bitmap.PixelHeight}, red={red}, neutral={neutral}, text={end.Text}");
            await RenderedEvidence.Capture(end, "end-" + state);
            Assert.That(red, critical ? Is.GreaterThan(10) : Is.Zero, state + ": rendered critical text");
            if (!critical) Assert.That(neutral, Is.GreaterThan(10), state + ": ordinary text must remain visible");
        });
    }
}
