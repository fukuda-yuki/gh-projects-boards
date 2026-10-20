using GhProjectsBoards.Core.PlanEditor;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

// #239: the one normalisation contract every non-text cell rule reads (docs/spec.md "Cell text input").
[TestFixture]
internal sealed class PlanInputTests
{
    [TestCase("1.5d", "12")]
    [TestCase("１．５ｄ", "12")]
    [TestCase("8.0h", "8")]
    [TestCase("2.50", "2.5")]
    public void AnEffortReadsBackWithoutTrailingZeros(string input, string expected)
    {
        Assert.That(PlanInput.TryEffort(input, out var hours), Is.True);
        Assert.That(hours.ToString(System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(expected));
    }

    [TestCase("８ｈ", "8h")]
    [TestCase("＃２", "#2")]
    [TestCase("２・１０", "2・10")]
    [TestCase("１０月１４日", "10月14日")]
    [TestCase("ｐｇ", "pg")]
    [TestCase("　８　", " 8 ")]
    [TestCase("ぺｒそｎ", "ぺrそn")]
    [TestCase("ぺrそn", "ぺrそn")]
    [TestCase("alice", "alice")]
    [TestCase("要件定義", "要件定義")]
    [TestCase("", "")]
    public void FullWidthAsciiFoldsToHalfWidthAndNothingElseChanges(string input, string expected)
        => Assert.That(PlanInput.Fold(input), Is.EqualTo(expected));

    [Test]
    public void FoldingDeletesReordersAndCaseFoldsNothing()
    {
        const string text = "７月１４日 #１２ ＡＢＣ ａｂｃ ・ ＿％－";
        Assert.That(PlanInput.Fold(text), Is.EqualTo("7月14日 #12 ABC abc ・ _%-"));
        Assert.That(PlanInput.Fold(text).Length, Is.EqualTo(text.Length));
    }
}
