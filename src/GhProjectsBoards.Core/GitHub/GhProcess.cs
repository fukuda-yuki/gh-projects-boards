using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GhProjectsBoards.App.GitHub;

internal enum ProcessCompletion { Exited, NotFound, StartFailed, TimedOut, Cancelled, IoFailed }

internal sealed class GhCommand(string executable, IReadOnlyList<string> arguments,
    string? standardInput = null, TimeSpan? timeout = null)
{
    public string Executable { get; } = executable;
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public string? StandardInput { get; } = standardInput;
    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(30);
    public override string ToString() => "GitHub CLI command (payload omitted)";
}

internal sealed class GhProcessResult(ProcessCompletion completion, bool started = false,
    int? exitCode = null, string standardOutput = "", string standardError = "", TimeSpan elapsed = default)
{
    public ProcessCompletion Completion { get; } = completion;
    public bool Started { get; } = started;
    public int? ExitCode { get; } = exitCode;
    // Streams are transient parser input, never a diagnostic string or log entry.
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
    public TimeSpan Elapsed { get; } = elapsed;
    public override string ToString() => $"{Completion}; exit code: {ExitCode}";
}

internal interface IGhProcessRunner
{
    Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default);
}

internal sealed class GhProcessRunner(IReadOnlyDictionary<string, string?>? environmentOverrides = null) : IGhProcessRunner
{
    internal static readonly string[] TokenVariables =
        ["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"];

    public async Task<GhProcessResult> RunAsync(GhCommand command, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        if (cancellationToken.IsCancellationRequested)
        {
            return new GhProcessResult(ProcessCompletion.Cancelled);
        }
        if (command.Timeout <= TimeSpan.Zero)
        {
            return new GhProcessResult(ProcessCompletion.TimedOut);
        }
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in command.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                start.Environment[name] = value;
            }
        }
        // Stored gh credentials are the selected policy. Ambient tokens/routing
        // must not silently change the account or destination of a workspace.
        foreach (var name in TokenVariables.Concat(["GH_DEBUG", "DEBUG", "GH_HOST", "GH_REPO", "GH_FORCE_TTY"]))
        {
            start.Environment.Remove(name);
        }
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
        start.Environment["GH_TELEMETRY"] = "false";
        start.Environment["GH_PAGER"] = "";
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            return new GhProcessResult(exception.NativeErrorCode is 2 or 3
                ? ProcessCompletion.NotFound : ProcessCompletion.StartFailed, elapsed: clock.Elapsed);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return new GhProcessResult(ProcessCompletion.StartFailed, elapsed: clock.Elapsed);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(command.Timeout);
        try
        {
            // Draining both pipes and timing stdin as well prevents a full pipe
            // or an unresponsive child from hanging cancellation/shutdown.
            await Task.WhenAll(WriteInputAsync(), process.WaitForExitAsync(), output, error)
                .WaitAsync(deadline.Token);
            return new GhProcessResult(ProcessCompletion.Exited, true, process.ExitCode,
                await output, await error, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await StopAsync();
            return new GhProcessResult(cancellationToken.IsCancellationRequested
                ? ProcessCompletion.Cancelled : ProcessCompletion.TimedOut, true, elapsed: clock.Elapsed);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await StopAsync();
            return new GhProcessResult(ProcessCompletion.IoFailed, true, elapsed: clock.Elapsed);
        }

        async Task WriteInputAsync()
        {
            await process.StandardInput.WriteAsync(command.StandardInput ?? "");
            process.StandardInput.Close();
        }

        async Task StopAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or TimeoutException)
            {
                // Only this owned process is targeted. Cleanup must not convert
                // an interrupted request into success or expose raw stream data.
            }
        }
    }
}
