using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class UserManualTests
{
    internal static string RepositoryRoot()
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
            .Where(e => !((string?)e.Attribute("Include"))!.Contains('*'))
            .ToDictionary(e => (string)e.Attribute("Link")!, e => Path.GetFullPath(Path.Combine(root, "src/GhProjectsBoards.App", (string)e.Attribute("Include")!)));
        var images = project.Descendants("Content").Single(e => (string?)e.Attribute("Include") == "../../docs/manual/images/*.png");
        Assert.That((string?)images.Attribute("Link"), Is.EqualTo("manual/images/%(Filename)%(Extension)"));
        Assert.That((string?)images.Attribute("CopyToOutputDirectory"), Is.EqualTo("PreserveNewest"));
        Assert.That((string?)images.Attribute("CopyToPublishDirectory"), Is.EqualTo("PreserveNewest"));
        foreach (var image in Directory.GetFiles(Path.Combine(root, "docs/manual/images"), "*.png"))
            shipped.Add("manual/images/" + Path.GetFileName(image), image);
        shipped.Add("使い方.md", Path.Combine(root, "docs/internal-distribution-ja.md"));
        var links = Regex.Matches(manual, @"\]\(<?(?<target>[^\s)>]+)>?(?:\s+""[^""]*"")?\)|^\s{0,3}\[[^\]]+\]:\s*<?(?<target>[^\s>]+)", RegexOptions.Multiline);
        Assert.That(links, Is.Not.Empty);
        foreach (Match link in links) {
            var target = Uri.UnescapeDataString(link.Groups["target"].Value);
            if (target.StartsWith('#') || Uri.TryCreate(target, UriKind.Absolute, out _)) continue;
            var path = target.Split('#')[0];
            Assert.That(path == "使い方.md" || path.StartsWith("templates/", StringComparison.Ordinal) || path.StartsWith("manual/images/", StringComparison.Ordinal), Is.True, target);
            Assert.That(shipped.TryGetValue(path, out var source), Is.True, "Not in the package: " + target);
            Assert.That(File.Exists(source), Is.True, "Missing package source: " + target);
        }
    }

    [Test]
    public void EveryCitedImageExistsAndEveryImageIsCited()
    {
        var root = RepositoryRoot();
        var manual = File.ReadAllText(Path.Combine(root, "docs/user-manual.md"));
        var cited = Regex.Matches(manual, @"!\[(?<alt>[^\]]+)\]\((?<path>manual/images/[^)]+\.png)\)")
            .Select(m => m.Groups["path"].Value).ToArray();
        var files = Directory.GetFiles(Path.Combine(root, "docs/manual/images"), "*.png").Select(p => "manual/images/" + Path.GetFileName(p)).ToArray();
        Assert.That(cited, Is.Not.Empty);
        Assert.That(files, Is.EquivalentTo(cited), "Run scripts/Capture-ManualImages.ps1; real app captures are required.");
        foreach (var path in cited) {
            Assert.That(Path.GetFileName(path), Does.Match(@"^\d{2}-[a-z-]+\.png$"));
            Assert.That(File.Exists(Path.Combine(root, "docs", path)), Is.True, path);
        }
    }
}
