using System.Text.Json;
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

public sealed partial class RegistrationTests
{
    [Test, Category("Onboarding")]
    public void OrdinaryFirstRunGuideRegistersProjectAndRestartUsesOfflineCache()
    {
        using var f = new Fixture();
        Assert.That(Directory.Exists(f.Data), Is.False, "The ordinary first launch must start with an empty isolated data root.");
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1280, 900);
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GuideConnectionButton"));
            Assert.That(Element(w, "GuideRegisterButton").IsEnabled, Is.False);
            Assert.That(f.Calls(), Is.Empty, "Offering onboarding must not access GitHub.");
            Capture(w, f.Root, "onboarding-first-start");

            Invoke(w, "GuideConnectionButton");
            Set(w, "ExecutablePath", ""); Set(w, "HostInput", "example.test");
            Invoke(w, "CheckConnectionButton");
            Wait(() => Element(w, "CheckConnectionButton").IsEnabled && Text(w, "ConnectionStatus").Contains("gh.exe"));
            Assert.That(f.Calls(), Is.Empty, "Missing CLI recovery must not dispatch a request.");
            Connect(w);
            Invoke(w, "ProjectsPageButton");
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GuideRegisterButton") && Element(w, "GuideRegisterButton").IsEnabled);
            Assert.That(Text(w, "GuideConnectionStatus"), Does.Contain("fixture-user").And.Contain("example.test"));

            Invoke(w, "GuideRegisterButton");
            Set(w, "RegistrationUrl", "https://example.test/users/sample-user/projects/1");
            Invoke(w, "ResolveProjectButton");
            Wait(() => Element(w, "RegisterProjectButton").IsEnabled);
            Assert.That(Text(w, "ProjectConfirmation"), Does.Contain("Project 1").And.Contain("fixture-user"));
            Invoke(w, "RegisterProjectButton");
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GuideOpenBoardButton") && Element(w, "GuideOpenBoardButton").IsEnabled);
            Assert.That(Text(w, "GuideProjectStatus"), Does.Contain("登録済み").And.Contain("Project 1"));
            Capture(w, f.Root, "onboarding-registered");
            Invoke(w, "GuideOpenBoardButton");
            Wait(() => WorkspaceUi.HasVisibleElement(w, "GridCell0_0"));
            Assert.That(Text(w, "ProjectSummary"), Is.EqualTo("Project 1"));
            Assert.That(WorkspaceUi.HasVisibleElement(w, "GuideConnectionButton"), Is.False);
            Capture(w, f.Root, "onboarding-first-board");
        });

        var callsBeforeRestart = f.Calls().Length;
        f.Run(w => {
            w.Patterns.Transform.Pattern.Resize(1280, 900);
            Wait(() => WorkspaceUi.HasVisibleElement(w, "WorkspaceIdentity"));
            WorkspaceUi.OpenProjectNavigation(w);
            Wait(() => Element(w, "SavedProfiles").AsComboBox().Items.Length == 1);
            Element(w, "SavedProfiles").Patterns.ExpandCollapse.Pattern.Collapse();
            Assert.That(WorkspaceUi.HasVisibleElement(w, "GuideConnectionButton"), Is.False,
                "A durable saved registration must suppress the automatic first-run guide.");
            OpenSaved(w, "Project 1", profile: true);
            Assert.That(Text(w, "WorkspaceIdentity"), Does.Contain("未認証").And.Contain("fixture-user"));
            Assert.That(Element(w, "RefreshProjectButton").IsEnabled, Is.False);
            Assert.That(f.Calls(), Has.Length.EqualTo(callsBeforeRestart), "Normal restart and cached selection must remain offline.");
            Capture(w, f.Root, "onboarding-cached-restart");
        });

        Assert.That(f.Calls().Any(call => call.GetProperty("mutation").GetBoolean()), Is.False);
        File.WriteAllText(Path.Combine(f.Root, "onboarding-journey.json"), JsonSerializer.Serialize(new {
            entry = "ordinary executable with empty isolated data root",
            endpoint = "first registered Boards view, normal close, normal restart and offline cached Boards view",
            externalBoundary = "isolated fake gh; no real GitHub access",
            remoteMutations = 0, humanAcceptance = "not run"
        }));
    }
}
