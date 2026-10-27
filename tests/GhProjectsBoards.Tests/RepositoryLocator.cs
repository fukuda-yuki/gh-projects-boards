namespace GhProjectsBoards.Tests;

// A test that reads the checkout (the manual, its images, the App sources) locates it by walking up from the
// test binary. A build whose output lives outside the checkout names it through GHPB_REPOSITORY_ROOT instead,
// so the same assertions run from an artifacts path.
internal static class RepositoryLocator
{
    internal static string Root()
    {
        if (Environment.GetEnvironmentVariable("GHPB_REPOSITORY_ROOT") is { Length: > 0 } configured)
        {
            if (!File.Exists(Path.Combine(configured, "GhProjectsBoards.sln")))
                throw new InvalidOperationException("GHPB_REPOSITORY_ROOT does not name the checkout: " + configured);
            return configured;
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GhProjectsBoards.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Run from a build inside the repository or set GHPB_REPOSITORY_ROOT.");
    }
}
