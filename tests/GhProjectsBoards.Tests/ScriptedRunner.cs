using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Tests;

internal sealed class ScriptedRunner(Func<GhCommand, GhProcessResult> response) : IGhProcessRunner
{
    public List<GhCommand> Commands { get; } = [];
    public Func<GhCommand, CancellationToken, Task>? BeforeResponse { get; set; }
    public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
    {
        lock (Commands) Commands.Add(command);
        if (BeforeResponse is { } before) await before(command, cancellationToken);
        return response(command);
    }

    public static GhProcessResult Http(string body, int status = 200, int exit = 0, string headers = "")
        => new(ProcessCompletion.Exited, true, exit, $"HTTP/2.0 {status} Test\r\n{headers}\r\n{body}");
}
