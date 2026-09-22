using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Category("GridIme")]
    public void RecycledSheetPublicRealizationAndDirectImeRetainPendingHostsAtThousandTasks()
    {
        Assert.That(Environment.GetEnvironmentVariable("GHPB_RECYCLED_PRESENTATION"), Is.EqualTo("1"), "Gate A candidate must be explicitly selected.");
        using var f = new Fixture();
        var seed = new ProcessStartInfo(Environment.GetEnvironmentVariable("GHPB_E2E_FAKE_GH_PATH")!) { UseShellExecute = false, CreateNoWindow = true };
        seed.ArgumentList.Add("--seed-gantt"); seed.ArgumentList.Add(f.Data);
        using (var process = Process.Start(seed)!) { Assert.That(process.WaitForExit(30000), Is.True); Assert.That(process.ExitCode, Is.Zero); }
        f.Run(w =>
        {
            w.Patterns.Transform.Pattern.Resize(1080, 760); w.Move(0, 0);
            OpenSaved(w, "P1", profile: true); WorkspaceUi.CloseProjectNavigation(w);
            Wait(() => Element(w, "ToggleProjectNavigation").Name == "Project一覧を表示");
            Thread.Sleep(400); // Existing navigation animation setup; outside all measured input.
            var initial = Activate(0, 0);
            Keyboard.TypeVirtualKeyCode(0x1A); Key(VirtualKeyShort.KEY_P);
            Wait(() => initial.Text == "p");
            var originalRuntime = initial.Properties.RuntimeId.Value;
            var list = Element(w, "ProjectItems");
            Assert.That(list.Patterns.ItemContainer.IsSupported, Is.True, "Native offscreen discovery is required.");
            var lastItem = list.Patterns.ItemContainer.Pattern.FindItemByProperty(null!, list.Automation.PropertyLibrary.Element.Name,
                "行 1000 #1000  owner/repo 作業 1000");
            Assert.That(lastItem, Is.Not.Null, "Discover the last data item without enumerating 1,000 native editors.");
            if (lastItem!.Patterns.VirtualizedItem.IsSupported) lastItem.Patterns.VirtualizedItem.Pattern.Realize();
            lastItem.Patterns.ScrollItem.Pattern.ScrollIntoView();
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell999_0"));
            var last = Activate(999, 0);
            var lastRuntime = last.Properties.RuntimeId.Value;
            try
            {
                Keyboard.TypeVirtualKeyCode(0x16);
                Key(VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_I, VirtualKeyShort.KEY_H, VirtualKeyShort.KEY_O, VirtualKeyShort.KEY_N, VirtualKeyShort.KEY_G, VirtualKeyShort.KEY_O);
                Wait(() => last.Text == "にほんご");
                list.Patterns.Scroll.Pattern.SetScrollPercent(100, 0);
                Wait(() => list.Patterns.Scroll.Pattern.VerticalScrollPercent.Value < 1);
                Assert.That(last.Properties.HasKeyboardFocus.Value, Is.True);
                Assert.That(last.Text, Is.EqualTo("にほんご"));
                Key(VirtualKeyShort.SPACE); Wait(() => last.Text == "日本語");
                Key(VirtualKeyShort.RETURN); // Confirms only native composition.
                Assert.That(last.Text, Is.EqualTo("日本語"));
                Assert.That(last.Properties.RuntimeId.Value, Is.EqualTo(lastRuntime));
                list.Patterns.Scroll.Pattern.SetScrollPercent(0, 100);
                Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell999_0"));
                Capture(w, f.Root, "last-direct-ime-return");
            }
            finally { Keyboard.TypeVirtualKeyCode(0x1A); }
            list.Patterns.Scroll.Pattern.SetScrollPercent(0, 0);
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell0_0"));
            var returned = Activate(0, 0);
            Assert.That(returned.Properties.RuntimeId.Value, Is.EqualTo(originalRuntime));
            Assert.That(returned.Text, Is.EqualTo("p"));
            Capture(w, f.Root, "original-pending-return");
            Assert.That(f.Calls(), Is.Empty);

            TextBox Activate(int row, int column)
            {
                var id = $"GridCell{row}_{column}";
                Element(w, id).Focus();
                Wait(() => Element(w, id).Properties.HasKeyboardFocus.Value);
                // Reacquire the stable identity after realization/activation. The
                // recycled presentation peer is not the protected native input peer.
                return Element(w, id).AsTextBox();
            }
        });
        var fields = Durable(f).GetProperty("Fields").EnumerateArray().ToArray();
        foreach (var (id, text) in new[] { ("I1", "p"), ("I1000", "日本語") })
        {
            var field = fields.Single(field => field.GetProperty("Key").GetProperty("Kind").GetString() == "Title" && field.GetProperty("Key").GetProperty("NodeId").GetString() == id);
            Assert.That(field.GetProperty("Buffer").GetString(), Is.EqualTo(text));
            Assert.That(field.GetProperty("Change").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        Assert.That(fields.Single(field => field.GetProperty("Key").GetProperty("NodeId").GetString() == "P1T1000"
            && field.GetProperty("Key").GetProperty("FieldId").GetString() == "F-Estimate").GetProperty("Buffer").GetString(), Is.EqualTo("24未確定"));
        File.WriteAllText(Path.Combine(f.Root, "gate-a-native.json"), JsonSerializer.Serialize(new {
            tasks = 1000, people = 20, directIme = "physical NIHONGO, conversion, confirmation with original native peer retained across both axes",
            discovery = "native ItemContainer, VirtualizedItem, ScrollItem, focused native ValuePattern",
            endpoint = "normal process close and independent checkpoint readback", humanAcceptance = "not_run" }));
    }
}
