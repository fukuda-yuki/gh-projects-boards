using System.Xml.Linq;
using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class PlanManualHtmlTests
{
    private static XDocument Render(string text) => XDocument.Parse(PlanManualHtml.Render(text, TestContext.CurrentContext.TestDirectory));

    [Test]
    public void HeadingsHaveJapaneseFragmentIdsAndUniqueSuffixes()
    {
        var html = Render("# 計画の使い方\n\n## CSV と GitHub（設定）!\n\n## CSV と GitHub（設定）!\n\n[設定へ](#csv-と-github設定)");
        Assert.That(html.Descendants("h1").Single().Value, Is.EqualTo("計画の使い方"));
        Assert.That(html.Descendants("h2").Select(e => (string?)e.Attribute("id")), Is.EqualTo(new[] { "csv-と-github設定", "csv-と-github設定-1" }));
        Assert.That((string?)html.Descendants("a").Single().Attribute("href"), Is.EqualTo("#csv-と-github設定"));
    }

    [Test]
    public void ParagraphsEscapeTextAndRenderBoldCodeAndLineBreaks()
    {
        var html = Render("**発行 & 確認** は `<input>`\n次の行  \n最終行\n\n<script>alert('x')</script>");
        Assert.That(html.Descendants("strong").Single().Value, Is.EqualTo("発行 & 確認"));
        Assert.That(html.Descendants("code").Single().Value, Is.EqualTo("<input>"));
        Assert.That(html.Descendants("br").Count(), Is.EqualTo(2));
        Assert.That(html.Descendants("script"), Is.Empty);
        Assert.That(html.Descendants("input"), Is.Empty);
        Assert.That(html.Descendants("p").Last().Value, Is.EqualTo("<script>alert('x')</script>"));
    }

    [Test]
    public void OrderedAndUnorderedListsKeepNestedItemsInsideTheirParent()
    {
        var html = Render("1. 接続\n2. 確認\n   - 工数\n   - 日付\n3. 発行\n\n- 完了\n  1. 結果\n  2. 次週");
        var ordered = html.Descendants("body").Single().Elements("ol").Single();
        Assert.That(ordered.Elements("li").Count(), Is.EqualTo(3));
        Assert.That(ordered.Elements("li").ElementAt(1).Element("ul")!.Elements("li").Select(e => e.Value), Is.EqualTo(new[] { "工数", "日付" }));
        var unordered = html.Descendants("body").Single().Elements("ul").Single();
        Assert.That(unordered.Element("li")!.Element("ol")!.Elements("li").Select(e => e.Value), Is.EqualTo(new[] { "結果", "次週" }));
    }

    [Test]
    public void PipeTablesHaveHeadersAndFormattedEscapedCells()
    {
        var html = Render("| 状態 | 意味 |\n| :--- | ---: |\n| **未発行** | `<変更>` & 確認 |\n| 完了 | [案内](#案内) |");
        Assert.That(html.Descendants("th").Select(e => e.Value), Is.EqualTo(new[] { "状態", "意味" }));
        Assert.That(html.Descendants("tbody").Single().Elements("tr").Count(), Is.EqualTo(2));
        Assert.That(html.Descendants("td").First().Element("strong")!.Value, Is.EqualTo("未発行"));
        Assert.That(html.Descendants("code").Single().Value, Is.EqualTo("<変更>"));
    }

    [Test]
    public void ImagesAndRelativeDocumentsResolveAgainstShippedManualNotTemporaryHtml()
    {
        var directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "配布 folder");
        var html = XDocument.Parse(PlanManualHtml.Render("![設定 & \"確認\"](manual/images/12-period.png)\n\n[起動](使い方.md) [外部](https://example.com/?a=1&b=2)", directory));
        var img = html.Descendants("img").Single();
        Assert.That((string?)img.Attribute("src"), Is.EqualTo(new Uri(Path.Combine(directory, "manual/images/12-period.png")).AbsoluteUri));
        Assert.That((string?)img.Attribute("alt"), Is.EqualTo("設定 & \"確認\""));
        Assert.That(html.Descendants("a").Select(e => (string?)e.Attribute("href")), Is.EqualTo(new[] {
            new Uri(Path.Combine(directory, "使い方.md")).AbsoluteUri, "https://example.com/?a=1&b=2"
        }));
    }

    [TestCase("javascript:alert%281%29")]
    [TestCase("data:text/html,unsafe")]
    public void ActiveUrlSchemesAreNeverEmitted(string target)
    {
        var html = Render($"[リンク]({target}) ![画像]({target})");
        Assert.That(html.Descendants().Attributes().Any(a => a.Value == target), Is.False);
        Assert.That(html.Descendants("body").Single().Value, Does.Contain("リンク").And.Contain("画像"));
    }

    [Test]
    public void InlineCodeDoesNotInterpretMarkdownAndIncompleteMarkupRemainsText()
    {
        var html = Render("`**literal** [link](#x)` **unfinished <b>\n\n![a](manual/images/a.png) **[Help](#help)**");
        Assert.That(html.Descendants("code").Single().Value, Is.EqualTo("**literal** [link](#x)"));
        Assert.That(html.Descendants("b"), Is.Empty);
        Assert.That(html.Descendants("strong").Single().Element("a")!.Value, Is.EqualTo("Help"));
        Assert.That(html.Descendants("p").First().Value, Does.Contain("**unfinished <b>"));
    }

    [Test]
    public void ShippedManualRendersEveryHeadingImageAndInternalLink()
    {
        var root = UserManualTests.RepositoryRoot();
        var markdown = File.ReadAllText(Path.Combine(root, "docs/user-manual.md"));
        var html = XDocument.Parse(PlanManualHtml.Render(markdown, Path.Combine(root, "docs")));
        var ids = html.Descendants().Attributes("id").Select(a => a.Value).ToHashSet();
        foreach (var link in html.Descendants("a").Attributes("href").Where(a => a.Value.StartsWith('#')))
            Assert.That(ids, Does.Contain(link.Value[1..]), link.Value);
        Assert.That(html.Descendants("h1").Single().Value, Is.EqualTo("プロジェクトの計画と設定"));
        Assert.That(html.Descendants("img").Count(), Is.EqualTo(Directory.GetFiles(Path.Combine(root, "docs/manual/images"), "*.png").Length));
        Assert.That(html.Descendants("table").Count(), Is.GreaterThanOrEqualTo(3));
    }
}
