using GitHub.Copilot;

namespace GhProjectsBoards.Core.Assist;

internal sealed class AssistToolProgress
{
    private readonly Dictionary<string, string> active = [];
    private int reads;

    internal AssistEvent? Observe(SessionEvent value)
    {
        lock (active) {
            if (value is ToolExecutionStartEvent started) {
                var name = started.Data.ToolName;
                if (name.StartsWith("custom:", StringComparison.Ordinal)) name = name[7..];
                var text = name switch {
                    "find_tasks" or "get_tasks" or "summarize_plan" => $"計画を調べています（{++reads} 回目）…",
                    "propose_changes" => "変更案を検証しています…",
                    "read_manual" => "マニュアルを調べています…",
                    _ => null
                };
                if (text is null) return null;
                active[started.Data.ToolCallId] = text;
            }
            else if (value is not ToolExecutionCompleteEvent completed || !active.Remove(completed.Data.ToolCallId)) return null;
            // Completion of one parallel call must not hide another call still in progress.
            return new(AssistEventKind.Progress, active.Values.LastOrDefault() ?? "回答しています…");
        }
    }
}
