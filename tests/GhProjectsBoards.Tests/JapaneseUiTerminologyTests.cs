using System.Text.RegularExpressions;
using System.Xml.Linq;
using NUnit.Framework;
namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class JapaneseUiTerminologyTests
{
    [TestCase("src/GhProjectsBoards.App")]
    [TestCase("tests/GhProjectsBoards.UiIntegration.Tests")]
    public void UiLiteralsDoNotUseSupersededJapaneseTerms(string sourceDirectory)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GhProjectsBoards.sln"))) root = root.Parent;
        Assert.That(root, Is.Not.Null, "Run from a build inside the repository so App sources can be audited.");
        var app = Path.Combine(root!.FullName, sourceDirectory);
        string[] forbidden = ["状況日", "インジケーター", "許容量", "未算定", "尺度", "未検証", "既定リポジトリ", "人時",
            "GitHubを採用", "ローカルを採用", "（未配分）", "稼働%", "発行済みからの遅れ", "残工数", "残が未入力", "GitHub列", "不足する日程列を追加", "計画対象外  Draft"];
        // Skip comments and character literals; retain regular, interpolated, verbatim and raw string text.
        var tokens = new Regex("//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|'(?:\\\\.|[^'\\\\])*'|(?<literal>\"\"\"[\\s\\S]*?\"\"\"|@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\")");
        var failures = new List<string>();
        var files = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".cs" or ".xaml")
            .Where(p => !Path.GetRelativePath(app, p).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToArray();
        Assert.That(files, Is.Not.Empty);
        foreach (var file in files) {
            var source = File.ReadAllText(file);
            var literals = Path.GetExtension(file) == ".xaml"
                ? XDocument.Parse(source).Descendants().SelectMany(e => e.Attributes().Select(a => a.Value).Concat(e.Nodes().OfType<XText>().Select(t => t.Value)))
                : tokens.Matches(source).Where(m => m.Groups["literal"].Success).Select(m => m.Value);
            foreach (var literal in literals) {
                if (Regex.IsMatch(literal, @"[ぁ-んァ-ヶー一-龯々] +プロジェクト|プロジェクト +[ぁ-んァ-ヶー一-龯々]"))
                    failures.Add($"{Path.GetRelativePath(root.FullName, file)}: Unnatural project spacing: {literal}");
                foreach (var term in forbidden.Where(literal.Contains))
                    failures.Add($"{Path.GetRelativePath(root.FullName, file)}: {term}: {literal}");
            }
        }
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
    }
}
