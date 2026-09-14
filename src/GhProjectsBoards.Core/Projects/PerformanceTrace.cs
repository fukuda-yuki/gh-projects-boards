using System.Diagnostics;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

// Explicit in-memory diagnostic scope. Never reads or writes the recovery journal.
internal sealed class PerformanceTrace : IDisposable
{
    private static readonly AsyncLocal<PerformanceTrace?> current = new();
    private readonly PerformanceTrace? previous;
    public readonly List<Sample> Samples = [];
    public sealed record Sample(string Kind, long Start, long End, long Count);
    public PerformanceTrace() { previous = current.Value; current.Value = this; }
    public void Dispose() => current.Value = previous;
    public static IDisposable? Span(string kind) => current.Value is { } capture ? new SpanScope(capture, kind) : null;
    public static void Count(string kind, long count = 1)
    {
        if (current.Value is { } capture) { var now = Stopwatch.GetTimestamp(); capture.Samples.Add(new(kind, now, now, count)); }
    }
    private sealed class SpanScope(PerformanceTrace capture, string kind) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp();
        public void Dispose() => capture.Samples.Add(new(kind, start, Stopwatch.GetTimestamp(), 1));
    }
    internal sealed class Runner(IGhProcessRunner inner) : IGhProcessRunner
    {
        public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
        {
            // Classify only fixed protocol markers; do not retain command text or streams.
            var kind = command.Arguments.Contains("--version") ? "process-version" : command.Arguments.Contains("auth") ? "process-auth"
                : command.Arguments.Contains("user") ? "process-identity" : command.StandardInput?.Contains("mutation ", StringComparison.Ordinal) == true ? "process-mutation" : "process-query";
            using var span = Span(kind);
            var result = await inner.RunAsync(command, cancellationToken);
            Count("returned-utf8-bytes", System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput));
            return result;
        }
    }
}
