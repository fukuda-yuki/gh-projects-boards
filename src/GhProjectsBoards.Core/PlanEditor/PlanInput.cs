using System.Globalization;

namespace GhProjectsBoards.Core.PlanEditor;

// One normalisation for the cell text the PMO types or pastes, usually through the Japanese IME.
// The contract is in docs/spec.md "Cell text input (#239)"; #237 reads the `#` reference rule
// from the same folded text.
internal static class PlanInput
{
    // Full-width ASCII forms and the ideographic space become half-width. Nothing is deleted,
    // trimmed, reordered or case-folded, so `＃` survives as `#` and `・`, `月` and `日` are intact.
    internal static string Fold(string text)
    {
        if (text.Length == 0) return text;
        var folded = new char[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            folded[index] = character switch {
                '\u3000' => ' ',
                >= '\uFF01' and <= '\uFF5E' => (char)(character - 0xFEE0),
                _ => character };
        }
        return new string(folded);
    }
    // The sheet shows effort as `8h`; `d` is one working day of eight hours.
    internal static bool TryEffort(string text, out decimal hours)
    {
        hours = 0;
        var value = Fold(text).Trim();
        var unit = value.Length > 0 ? value[^1] : '\0';
        var days = unit is 'd' or 'D';
        if (unit is 'h' or 'H' or 'd' or 'D') value = value[..^1];
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var number)) return false;
        if (!days) { hours = number; return true; }
        // The day unit scales the number, so an accepted decimal that cannot be scaled stays refused.
        if (number > decimal.MaxValue / 8 || number < decimal.MinValue / 8) return false;
        hours = number * 8;
        return true;
    }
    internal static bool TryNumber(string text, out decimal number)
        => decimal.TryParse(Fold(text), NumberStyles.Number, CultureInfo.CurrentCulture, out number);
}
