using GhProjectsBoards.App;
using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NUnit.Framework;

namespace GhProjectsBoards.UiIntegration.Tests;

public sealed partial class HostedTests
{
    [TestCase(false), TestCase(true)]
    public async Task OneConfirmationShowsConflictAndPermitsExplicitExclusionOrLocalResolution(bool resolve)
    {
        await Ui.Run(() =>
        {
            var rows = Work.Open(Workspace.Selected!);
            Work.Commit("P1", rows[0].Cells[0], "My first title"); Work.Commit("P1", rows[1].Cells[0], "My second title");
            h.Existing.Titles["I1"] = "GitHub first title";
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        ContentDialog original = null!;
        await Ui.Run(() =>
        {
            original = Ui.Dialog("ApplyReviewDialog")!;
            Assert.That(Ui.Find<ListView>("ApplyTargetRows", original).SelectedItems, Is.Empty);
            Assert.That(original.IsPrimaryButtonEnabled, Is.False);
            Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", original));
        });
        await Ui.Until(() => !Workspace.IsBusy && Ui.DialogText("ApplyReviewDialog").Contains("競合:"));
        if (resolve) await Ui.Until(() => Ui.Find<Button>("ApplyResolve-P1-T1-Title-Local", original).IsLoaded);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.SameAs(original));
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("GitHub first title").And.Contain("My first title").And.Contain("My second title"));
            Assert.That(original.IsPrimaryButtonEnabled, Is.False); Assert.That(h.Writes, Is.Empty);
            if (resolve) Ui.Click(Ui.Find<Button>("ApplyResolve-P1-T1-Title-Local", original));
            else
            {
                var list = Ui.Find<ListView>("ApplyTargetRows", original);
                list.SelectedItems.Remove(list.Items[0]);
            }
        });
        await Ui.Until(() => original.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.SameAs(original));
            Assert.That(Workspace.ApplyReview!.Batch.Operations.Select(o => o.IssueId),
                Is.EquivalentTo(resolve ? new[] { "I1", "I2" } : new[] { "I2" }));
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Idle(); Assert.That(h.Writes, Is.Empty);
    }

    [TestCase(false), TestCase(true)]
    public async Task ConnectionRecoveryPreservesWorkAndOnlyReturnsSameIdentityToReview(bool changeAccount)
    {
        var p = Workspace.Selected!;
        ConnectionPanel connection = null!;
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        await model.CheckAsync(false);
        await Ui.Run(() =>
        {
            var cell = Work.Open(p)[0].Cells[0]; Work.Commit("P1", cell, "Committed outgoing");
            Ui.Find<TextBox>("GridCell0_0").Text = "送らない未確定入力";
            connection = new ConnectionPanel(); connection.Initialize(Workspace, model);
            panel.ConnectionRequested += (_, _) => { panel.Visibility = Visibility.Collapsed; connection.Visibility = Visibility.Visible; };
            connection.ReturnRequested += (_, _) => { connection.Visibility = Visibility.Collapsed; panel.Visibility = Visibility.Visible; panel.Update(); panel.ReturnFromConnection(); };
        });
        await Ui.Mount(connection);
        await Ui.Run(() => connection.Visibility = Visibility.Collapsed);
        await Ui.Run(() => Ui.Click("ReviewApplyButton"));
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", Ui.Dialog("ApplyReviewDialog"))));
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            Workspace.SuspendConnection();
            Assert.That(Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled, Is.False);
            Ui.Click(Ui.Find<Button>("ApplyConnectionSettings", Ui.Dialog("ApplyReviewDialog")));
        });
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        if (changeAccount) h.Existing.Boundary.ViewerId = 99;
        await Ui.Run(() =>
        {
            Assert.That(Ui.Tree(connection).OfType<TextBlock>().Any(t => t.Text == "接続設定"), Is.True);
            Assert.That(Ui.Tree(connection).OfType<FrameworkElement>().Select(AutomationProperties.GetAutomationId),
                Has.None.EqualTo("RegistrationUrl").And.None.EqualTo("IssueUrlInput").And.None.EqualTo("ProjectUrlInput"));
            Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection));
        });
        if (changeAccount)
        {
            await Ui.Until(() => model.StatusText.Contains("変更を検出") && Ui.Find<Button>("NewConnectionButton", connection).IsEnabled);
            await Ui.Run(() => Ui.Click(Ui.Find<Button>("NewConnectionButton", connection)));
        }
        await Ui.Until(() => model.Connection?.IsConnected == true && !Workspace.IsBusy && Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        await Ui.Run(() =>
        {
            Assert.That(Workspace.Selected?.Snapshot.Id, Is.EqualTo(changeAccount ? null : p.Snapshot.Id));
            Assert.That(model.ExecutablePath, Is.EqualTo("explicit-gh.exe"));
            Assert.That(h.Writes, Is.Empty);
            Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection));
        });
        if (changeAccount)
        {
            await Ui.Until(() => panel.Visibility == Visibility.Visible);
            await Ui.Idle();
            await Ui.Run(() =>
            {
                Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.Null);
                Assert.That(Workspace.Profile!.ViewerId, Is.EqualTo(99));
                Assert.That(Workspace.ApplyReview, Is.Null);
                Assert.That(Work.Fields, Is.Empty);
                Assert.That(h.Writes, Is.Empty);
            });
            var saved = await new DraftStore(h.Existing.Root).LoadAsync(p.Snapshot.Id.Scope);
            Assert.That(saved!.Fields.Single(f => f.Key.NodeId == "I1").Buffer, Is.EqualTo("送らない未確定入力"));
            Assert.That(saved.Journal, Is.Empty);
            return;
        }
        await Ui.DialogReady("ApplyReviewDialog");
        await Ui.Until(() => Ui.Dialog("ApplyReviewDialog")!.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("Committed outgoing").And.Contain("送らない未確定入力: 送らない未確定入力"));
            Assert.That(h.Writes, Is.Empty);
            Assert.That(Work.Journal, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
        await Ui.Idle();
        await Ui.Run(() => Assert.That(Ui.Find<TextBox>("GridCell0_0").Text, Is.EqualTo("送らない未確定入力")));
    }

    [TestCase(false), TestCase(true)]
    public async Task FailedOrIncompleteLatestCheckRetainsWorkAndRetriesInTheSameConfirmation(bool incomplete)
    {
        bool fail = true;
        var boundary = h.Existing.Boundary.Override!;
        h.Existing.Boundary.Override = (query, variables) => fail && !incomplete
            ? GhProjectsBoards.Tests.ScriptedRunner.Http("{}", 503) : boundary(query, variables);
        await Ui.Run(() =>
        {
            Work.Commit("P1", Work.Open(Workspace.Selected!)[0].Cells[0], "Retained title");
            h.Incomplete = incomplete;
            Ui.Click("ReviewApplyButton");
        });
        await Ui.DialogReady("ApplyReviewDialog"); await Ui.Until(() => !Workspace.IsBusy);
        ContentDialog dialog = null!;
        await Ui.Run(() => dialog = Ui.Dialog("ApplyReviewDialog")!);
        await Ui.Run(() => Ui.Toggle(Ui.Find<CheckBox>("ApplySelectAll", dialog)));
        await Ui.Until(() => !Workspace.IsBusy);
        await Ui.Run(() =>
        {
            Assert.That(dialog.IsPrimaryButtonEnabled, Is.False);
            Assert.That(Ui.DialogText("ApplyReviewDialog"), Does.Contain("最新状態を確認できません").And.Contain("保存済み情報").And.Contain("最新未確認").And.Contain("Retained title"));
            Assert.That(h.Writes, Is.Empty);
            fail = false; h.Incomplete = false;
            Ui.Click(Ui.Find<Button>("ApplyCheckAgain", dialog));
        });
        await Ui.Until(() => dialog.IsPrimaryButtonEnabled);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Dialog("ApplyReviewDialog"), Is.SameAs(dialog));
            Assert.That(Ui.Find<ListView>("ApplyTargetRows", dialog).SelectedItems.Count, Is.EqualTo(1));
            Assert.That(h.Writes, Is.Empty);
            Ui.DialogButton("ApplyReviewDialog", "CloseButton");
        });
    }

    [Test]
    public async Task OfflineProjectAdditionIsSeparateAndConnectionCheckDoesNotDiscoverOrRegister()
    {
        await Ui.Run(() => { Workspace.SuspendConnection(); Ui.Click("AddProjectButton"); });
        await Ui.Ready<TextBox>("RegistrationUrl");
        ConnectionPanel connection = null!;
        var model = new ConnectionViewModel((_, _) => h.Existing.Service) { ExecutablePath = "explicit-gh.exe" };
        await Ui.Run(() =>
        {
            Ui.Find<TextBox>("RegistrationUrl").Text = "https://github.com/users/sample-user/projects/2";
            connection = new ConnectionPanel(); connection.Initialize(Workspace, model);
            panel.ConnectionRequested += (_, _) => { panel.Visibility = Visibility.Collapsed; connection.Visibility = Visibility.Visible; };
            connection.ReturnRequested += (_, _) => { connection.Visibility = Visibility.Collapsed; panel.Visibility = Visibility.Visible; panel.Update(); };
        });
        await Ui.Mount(connection);
        await Ui.Run(() => connection.Visibility = Visibility.Collapsed);
        await Ui.Run(() => Ui.Click("RegistrationConnectionSettings"));
        await Ui.Until(() => connection.Visibility == Visibility.Visible);
        var registration = Workspace.Registrations.Single();
        await Ui.Run(() => Ui.Click(Ui.Find<Button>("CheckConnectionButton", connection)));
        await Ui.Until(() => model.Connection?.IsConnected == true && Ui.Find<Button>("CheckConnectionButton", connection).IsEnabled);
        await Ui.Run(() =>
        {
            Assert.That(Workspace.Registrations.Single(), Is.SameAs(registration));
            Assert.That(h.Writes, Is.Empty);
            Ui.Click(Ui.Find<Button>("ProjectsPageButton", connection));
        });
        await Ui.Until(() => panel.Visibility == Visibility.Visible);
        await Ui.Run(() =>
        {
            Assert.That(Ui.Find<TextBox>("RegistrationUrl").Text, Is.EqualTo("https://github.com/users/sample-user/projects/2"));
            Assert.That(Ui.Find<Button>("RegisterProjectButton").IsEnabled, Is.False);
            Ui.Click("ResolveProjectButton");
        });
        await Ui.Until(() => Ui.Find<Button>("RegisterProjectButton").IsEnabled);
        await Ui.Run(() => Ui.Click("RegisterProjectButton"));
        await Ui.Until(() => Workspace.Selected?.Snapshot.Id.NodeId == "P2" && !Workspace.IsBusy);
        Assert.That(Workspace.Registrations, Has.Count.EqualTo(2)); Assert.That(h.Writes, Is.Empty);
    }
}
