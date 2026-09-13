using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class RegistrationStoreTests
{
    [Test]
    public async Task RegistrationSurvivesNewStoreInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-registration-" + Guid.NewGuid());
        var store = new RegistrationStore(root);
        var key = new ScopedId(new("github.com", 42), "P1");
        var project = new ProjectReadModel(key, new(key.Scope, "O1"), "User", 1,
            "https://github.com/users/owner/projects/1", "Project", [],
            new Dictionary<ScopedId, IssueReadModel>(), [], true, true);
        var registration = new ProjectRegistration("viewer", "owner", [], "owner/repo", DateTimeOffset.UtcNow, project);
        await store.SaveAsync(registration);
        var restored = await new RegistrationStore(root).LoadAsync();
        Assert.That(restored.Problems, Is.Empty);
        Assert.That(restored.Registrations.Single().DefaultRepository, Is.EqualTo("owner/repo"));
        Assert.That(restored.Registrations.Single().Snapshot.Id, Is.EqualTo(key));
    }
}
