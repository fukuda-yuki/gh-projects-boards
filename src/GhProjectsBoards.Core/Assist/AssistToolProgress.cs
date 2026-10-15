using GitHub.Copilot;

namespace GhProjectsBoards.Core.Assist;

internal sealed class AssistToolProgress
{
    private readonly Dictionary<string, string> active = [];
    private readonly Dictionary<string, string> externalCalls = [];
    private readonly HashSet<string> seen = [];
    private int reads;

    internal AssistEvent? Observe(SessionEvent value)
    {
        lock (active) {
            string? call = null, name = null;
            if (value is ExternalToolRequestedEvent requested) {
                call = requested.Data.ToolCallId; name = requested.Data.ToolName;
                externalCalls[requested.Data.RequestId] = call;
            }
            else if (value is ToolExecutionStartEvent started) {
                call = started.Data.ToolCallId; name = started.Data.ToolName;
            }
            if (call is not null && name is not null) {
                // The relay and execution streams can describe the same invocation in either order.
                if (!seen.Add(call)) return null;
                if (name.StartsWith("custom:", StringComparison.Ordinal)) name = name[7..];
                var text = name switch {
                    "find_tasks" or "get_tasks" or "summarize_plan" => $"計画を調べています（{++reads} 回目）…",
                    "propose_changes" => "変更案を検証しています…",
                    "read_manual" => "マニュアルを調べています…",
                    _ => null
                };
                if (text is null) return null;
                active[call] = text;
            }
            else {
                call = value switch {
                    ToolExecutionCompleteEvent completed => completed.Data.ToolCallId,
                    ExternalToolCompletedEvent completed when externalCalls.Remove(completed.Data.RequestId, out var id) => id,
                    _ => null
                };
                if (call is null || !active.Remove(call)) return null;
            }
            // Completion of one parallel call must not hide another call still in progress.
            return new(AssistEventKind.Progress, active.Values.LastOrDefault() ?? "回答しています…");
        }
    }
}
