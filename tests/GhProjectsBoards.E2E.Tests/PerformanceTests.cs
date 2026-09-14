using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Explicit("Opt-in measured runner only"), Category("Performance")]
    public void MeasureOrdinaryHundredItemInteraction()
    {
        var output = Environment.GetEnvironmentVariable("GHPB_PERFORMANCE_UI_ROOT") ?? throw new InvalidOperationException("Use Test-Performance.ps1 -Mode Desktop");
        File.WriteAllText(Path.Combine(output, "plan.json"), JsonSerializer.Serialize(new { items = 100, warmup = 1, measured = 5,
            boundary = "UI Automation invocation through visible completion; includes polling overhead, excludes fixture setup and user review", frequency = Stopwatch.Frequency }));
        for (var sample = -1; sample < 5; sample++)
        {
            using var f = new Fixture();
            File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new { registration = true, apply = true, itemCount = 100 }));
            f.Run(w =>
            {
                Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
                var timer = Stopwatch.StartNew();
                Edit(w, 0, "Measured title");
                Assert.That(CellText(w, 0), Is.EqualTo("Measured title"));
                var localMs = timer.Elapsed.TotalMilliseconds;
                Invoke(w, "ReviewApplyButton"); Element(w, "ApplyTargetRows").AsListBox().Items[0].Select();
                timer.Restart(); Invoke(w, "PrimaryButton");
                Wait(() => w.FindFirstDescendant(cf => cf.ByAutomationId("ApplyReviewDialog")) is not null);
                var prepareMs = timer.Elapsed.TotalMilliseconds;
                timer.Restart(); Invoke(w, "PrimaryButton");
                Wait(() => Text(w, "RegistrationStatus").Contains("Apply処理を停止"));
                var executeMs = timer.Elapsed.TotalMilliseconds;
                Assert.That(Text(w, "DraftStatus"), Does.Contain("変更フィールド 0"));
                Assert.That(File.ReadAllLines(Path.Combine(f.Root, "apply-requests.jsonl")), Has.Length.EqualTo(1));
                File.WriteAllText(Path.Combine(output, sample < 0 ? "warmup.json" : $"sample-{sample}.json"),
                    JsonSerializer.Serialize(new { sample, localMs, prepareMs, executeMs, fixture = f.Root }));
            });
        }
    }
}
