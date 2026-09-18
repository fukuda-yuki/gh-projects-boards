using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [TestCase(false), TestCase(true)]
    public void TitleIdentityStaysConsistentAcrossFilteringEditingAndProjectSwitch(bool multiple)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Root, "scenario.json"), JsonSerializer.Serialize(new {
            registration = true, reviewInformation = !multiple, itemCount = 10 }));
        var identity = multiple ? "#1  sample-user/first" : "#1";
        f.Run(w =>
        {
            Connect(w); Invoke(w, "ProjectsPageButton"); Register(w, 1);
            Assert.That(Element(w, "GridRowIdentity0").Name, Is.EqualTo(identity));
            Edit(w, 0, "keep this title");
            Set(w, "GridQuickTitleFilter", "keep"); Invoke(w, "GridQuickFilterApply");
            Wait(() => Text(w, "DraftStatus").Contains("1/10行"));
            Assert.That(Element(w, "GridRowIdentity0").Name, Is.EqualTo(identity));
            Element(w, "GridCell0_0").AsTextBox().Click();
            Element(w, "GridDetails").Focus(); Key(VirtualKeyShort.SPACE);
            Wait(() => WorkspaceUi.HasVisibleElement(w, "SelectedCellDetails"));
            Assert.That(Element(w, "SelectedCellDetails").Name, Does.Contain("#1  sample-user/first"));
            Capture(w, f.Root, "identity-filtered-keyboard-details");
            Register(w, 2);
            Assert.That(Element(w, "ProjectSummary").Name, Is.EqualTo("Project 2"));
            Assert.That(Element(w, "GridRowIdentity0").Name, Is.EqualTo(identity));
            WorkspaceUi.OpenProjectNavigation(w);
            WorkspaceUi.ProjectNavigation(w).FindFirstDescendant(cf => cf.ByName("Project 1"))!.Click();
            Wait(() => Element(w, "ProjectSummary").Name == "Project 1");
            WorkspaceUi.CloseProjectNavigation(w);
            Assert.That(CellText(w, 0), Is.EqualTo("keep this title"));
            Assert.That(Element(w, "GridRowIdentity0").Name, Is.EqualTo(identity));
            Assert.That(f.Calls().Any(c => c.GetProperty("mutation").GetBoolean()), Is.False);
            Capture(w, f.Root, "identity-returned-to-project");
        });
    }
}
