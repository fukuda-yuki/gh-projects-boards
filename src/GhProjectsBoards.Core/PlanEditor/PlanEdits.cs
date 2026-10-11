namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanEdits
{
    public static PlanTask Estimate(PlanTask task, decimal? value)
    {
        ValidateEffort(value);
        return task with { Estimate = value };
    }
    public static PlanTask Remaining(PlanTask task, decimal? value)
    {
        ValidateEffort(value);
        return task with { Remaining = value };
    }
    public static PlanTask Actual(PlanTask task, decimal? value)
    {
        ValidateEffort(value);
        return task with { Actual = value };
    }
    public static PlanTask Start(PlanTask task, DateOnly? value)
        => ValidateDates(task with { Start = value, StartNoEarlierThan = value });
    public static PlanTask End(PlanTask task, DateOnly? value)
        => ValidateDates(task with { End = value, Fixed = task.Fixed || value.HasValue });

    private static void ValidateEffort(decimal? value)
    {
        if (value < 0) throw new ArgumentException("工数は0以上で入力してください。");
    }
    private static PlanTask ValidateDates(PlanTask task)
    {
        if (task.KeepsDates && task.Start > task.End) throw new ArgumentException("終了日は開始日以降にしてください。");
        return task;
    }
}
