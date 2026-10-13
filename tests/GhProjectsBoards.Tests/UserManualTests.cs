using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class UserManualTests
{
    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GhProjectsBoards.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Run from a build inside the repository.");
    }

    [Test]
    public void ManualRelativeLinksResolveInsideInstalledPackage()
    {
        var root = RepositoryRoot();
        var manual = File.ReadAllText(Path.Combine(root, "docs/user-manual.md"));
        var project = XDocument.Load(Path.Combine(root, "src/GhProjectsBoards.App/GhProjectsBoards.App.csproj"));
        var shipped = project.Descendants("Content").Where(e => (string?)e.Attribute("CopyToPublishDirectory") == "PreserveNewest")
            .ToDictionary(e => (string)e.Attribute("Link")!, e => Path.GetFullPath(Path.Combine(root, "src/GhProjectsBoards.App", (string)e.Attribute("Include")!)));
        shipped.Add("使い方.md", Path.Combine(root, "docs/internal-distribution-ja.md"));
        var links = Regex.Matches(manual, @"\]\(<?(?<target>[^\s)>]+)>?(?:\s+""[^""]*"")?\)|^\s{0,3}\[[^\]]+\]:\s*<?(?<target>[^\s>]+)", RegexOptions.Multiline);
        Assert.That(links, Is.Not.Empty);
        foreach (Match link in links) {
            var target = Uri.UnescapeDataString(link.Groups["target"].Value);
            if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _)) continue;
            var path = target.Split('#')[0];
            Assert.That(path == "使い方.md" || path.StartsWith("templates/", StringComparison.Ordinal), Is.True, target);
            Assert.That(shipped.TryGetValue(path, out var source), Is.True, "Not in the package: " + target);
            Assert.That(File.Exists(source), Is.True, "Missing package source: " + target);
        }
    }
}
