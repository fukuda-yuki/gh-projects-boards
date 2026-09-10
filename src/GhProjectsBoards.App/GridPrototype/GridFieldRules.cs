using System.Globalization;

namespace GhProjectsBoards.App.GridPrototype;

internal static class GridFieldRules
{
    public static string Name(GridField field) => field switch
    {
        GridField.Title => "タイトル", GridField.State => "Issue状態", GridField.Number => "数値",
        GridField.Date => "日付", GridField.Choice => "選択肢", _ => "列"
    };

    public static bool TryChange(GridValues before, GridField field, string text, out GridValues after, out string error)
    {
        after = before;
        error = "";
        switch (field)
        {
            case GridField.Title:
                if (string.IsNullOrWhiteSpace(text)) error = "タイトルは必須です。";
                else if (text.IndexOfAny(['\r', '\n', '\t']) >= 0) error = "タイトルは1行で入力してください。";
                else after = before with { Title = text };
                break;
            case GridField.State:
                if (text is "Open" or "Closed") after = before with { State = text };
                else error = "Open または Closed を選択してください（必須）。";
                break;
            case GridField.Number:
                if (text.Length == 0) after = before with { Number = null };
                else if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var number)) after = before with { Number = number };
                else error = "小数点を . とする数値を入力してください。";
                break;
            case GridField.Date:
                if (text.Length == 0) after = before with { Date = null };
                else if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) after = before with { Date = date };
                else error = "実在する日付を yyyy-MM-dd で入力してください。";
                break;
            case GridField.Choice:
                if (text.Length == 0) after = before with { Choice = null };
                else if (text is "High" or "Medium" or "Low") after = before with { Choice = text };
                else error = "High、Medium、Low または空欄を指定してください。";
                break;
            default: error = "編集できない列です。"; break;
        }
        return error.Length == 0;
    }
}
