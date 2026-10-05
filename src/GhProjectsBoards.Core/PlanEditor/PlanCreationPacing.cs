using System.Collections.Immutable;
namespace GhProjectsBoards.Core.PlanEditor;

internal sealed record PlanCreationStart(DateTimeOffset Started, int Issues);
internal static class PlanCreationPacing
{
    internal static DateTimeOffset NextStart(ImmutableArray<PlanCreationStart> starts, int issues, DateTimeOffset now)
    {
        PlanOperations.Require(issues is >= 1 and <= 10, "作成バッチ数が不正です。");
        var next = starts.IsEmpty ? now : Later(now, starts[^1].Started.AddSeconds(starts[^1].Issues));
        while (true)
        {
            var active = starts.Where(s => s.Started > next.AddMinutes(-1)).ToArray();
            if (active.Sum(s => s.Issues) + issues <= 60) return next;
            next = Later(next, active[0].Started.AddMinutes(1));
        }
    }
    internal static ImmutableArray<PlanCreationStart> Reserve(ImmutableArray<PlanCreationStart> starts, int issues, DateTimeOffset now)
    {
        PlanOperations.Require(NextStart(starts, issues, now) <= now, "Issue 作成の待機時間が必要です。");
        return starts.Where(s => s.Started > now.AddMinutes(-1)).Append(new(now, issues)).ToImmutableArray();
    }
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
