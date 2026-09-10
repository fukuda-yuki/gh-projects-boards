using System.IO;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
[Category("Integration")]
internal sealed class GhProcessTests
{
    internal static string FakeExecutable => Path.ChangeExtension(typeof(GhProcessTests).Assembly.Location, ".exe");

    [TestCase(false, ProcessCompletion.TimedOut)]
    [TestCase(true, ProcessCompletion.Cancelled)]
    public async Task StopsItsOwnProcessWhenTheOperationEnds(bool cancel, ProcessCompletion expected)
    {
        using var source = new CancellationTokenSource();
        if (cancel) source.CancelAfter(TimeSpan.FromMilliseconds(250));
        var result = await new GhProcessRunner().RunAsync(new GhCommand(FakeExecutable, ["wait"],
            timeout: cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(250)), source.Token);
        Assert.That(result.Completion, Is.EqualTo(expected));
        Assert.That(result.Started, Is.True);
        Assert.That(result.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
    }

    [Test]
    public async Task CancellationBeforeLaunchDoesNotStartTheProcess()
    {
        var result = await new GhProcessRunner().RunAsync(new GhCommand(FakeExecutable, ["echo"]), new CancellationToken(true));
        Assert.That(result.Completion, Is.EqualTo(ProcessCompletion.Cancelled));
        Assert.That(result.Started, Is.False);
    }

    [Test]
    public async Task MissingExecutableIsDifferentFromAProgramFailure()
    {
        var result = await new GhProcessRunner().RunAsync(new GhCommand(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), []));
        Assert.That(result.Completion, Is.EqualTo(ProcessCompletion.NotFound));
        Assert.That(result.Started, Is.False);
        Assert.That(result.ExitCode, Is.Null);
    }

    [Test]
    public async Task RemovesAmbientCredentialsAndRoutingOnlyFromTheChild()
    {
        var settings = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "synthetic-one", ["GITHUB_TOKEN"] = "synthetic-two",
            ["GH_ENTERPRISE_TOKEN"] = "synthetic-three", ["GITHUB_ENTERPRISE_TOKEN"] = "synthetic-four",
            ["GH_DEBUG"] = "api", ["DEBUG"] = "1", ["GH_HOST"] = "wrong.example", ["GH_REPO"] = "wrong/repo",
            ["GH_CONFIG_DIR"] = "test configuration directory"
        };
        var result = await new GhProcessRunner(settings).RunAsync(new GhCommand(FakeExecutable, ["environment"]));
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.That(json.RootElement.GetProperty("present").GetArrayLength(), Is.Zero);
        Assert.That(json.RootElement.GetProperty("promptDisabled").GetString(), Is.EqualTo("1"));
        Assert.That(json.RootElement.GetProperty("configDirectory").GetString(), Is.EqualTo("test configuration directory"));
        Assert.That(settings["GH_TOKEN"], Is.EqualTo("synthetic-one"), "The supplied environment must remain unchanged.");
    }

    [Test]
    public async Task PreservesUnicodeArgumentsAndStdinWithoutShellInterpretation()
    {
        const string argument = "日本語 \"引用\" & echo NO; $(not-a-command)";
        const string body = "本文\n二行目\r\n\"quoted\" \\ @- ` $(not-a-command)";
        var result = await new GhProcessRunner().RunAsync(new GhCommand(FakeExecutable, ["echo", argument], body));

        Assert.That(result.Completion, Is.EqualTo(ProcessCompletion.Exited));
        Assert.That(result.ExitCode, Is.EqualTo(7));
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.That(json.RootElement.GetProperty("arguments")[0].GetString(), Is.EqualTo(argument));
        Assert.That(json.RootElement.GetProperty("input").GetString(), Is.EqualTo(body));
        Assert.That(result.StandardError, Is.EqualTo("synthetic stderr"));
        Assert.That(result.ToString(), Does.Not.Contain(body).And.Not.Contain("synthetic stderr"));
    }
}
