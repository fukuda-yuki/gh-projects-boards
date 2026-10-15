using System.Text.Json;
using GhProjectsBoards.Core.Assist;
using GitHub.Copilot;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture]
internal sealed class AssistSdkBoundaryTests
{
    [TestCase(false), TestCase(true)]
    public void SdkExternalToolRequestsReportProgressOnceAcrossBothEventFamilies(bool externalFirst)
    {
        var progress = new AssistToolProgress();
        AssistEvent? Request(string request, string call, string name) => progress.Observe(new ExternalToolRequestedEvent {
            Data = new() { RequestId = request, ToolCallId = call, ToolName = name, SessionId = "session" }
        });
        AssistEvent? Start(string call, string name) => progress.Observe(new ToolExecutionStartEvent {
            Data = new() { ToolCallId = call, ToolName = name }
        });
        AssistEvent? Complete(string request) => progress.Observe(new ExternalToolCompletedEvent { Data = new() { RequestId = request } });
        Assert.That(externalFirst ? Request("r1", "c1", "find_tasks")?.Text : Start("c1", "find_tasks")?.Text,
            Is.EqualTo("計画を調べています（1 回目）…"));
        Assert.That(externalFirst ? Start("c1", "find_tasks") : Request("r1", "c1", "find_tasks"), Is.Null);
        Assert.That(Request("r2", "c2", "get_tasks")?.Text, Is.EqualTo("計画を調べています（2 回目）…"));
        Assert.That(Request("r3", "c3", "summarize_plan")?.Text, Is.EqualTo("計画を調べています（3 回目）…"));
        Assert.That(Request("rp", "cp", "propose_changes")?.Text, Is.EqualTo("変更案を検証しています…"));
        Assert.That(Complete("r1")?.Text, Is.EqualTo("変更案を検証しています…"));
        Assert.That(progress.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "cp", Success = true } })?.Text,
            Is.EqualTo("計画を調べています（3 回目）…"));
        Assert.That(Complete("rp"), Is.Null);
        Complete("r2");
        Assert.That(Complete("r3")?.Text, Is.EqualTo("回答しています…"));
        Assert.That(Request("rm", "cm", "read_manual")?.Text, Is.EqualTo("マニュアルを調べています…"));
        Assert.That(Complete("rm")?.Text, Is.EqualTo("回答しています…"));
        Assert.That(Start("c1", "find_tasks"), Is.Null, "A late duplicate must not reactivate a completed call.");
        Assert.That(Request("unknown", "unknown", "untrusted-secret-tool"), Is.Null);
        Assert.That(Complete("unknown"), Is.Null);
    }

    [Test]
    public void SdkToolEventsReportSafeProgressAndKeepConcurrentWorkVisible()
    {
        var progress = new AssistToolProgress();
        AssistEvent? Start(string id, string name) => progress.Observe(new ToolExecutionStartEvent {
            Data = new() { ToolCallId = id, ToolName = name }
        });
        AssistEvent? Complete(string id) => progress.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = id, Success = true } });
        Assert.That(Start("one", "find_tasks")?.Text, Is.EqualTo("計画を調べています（1 回目）…"));
        Assert.That(Start("two", "custom:get_tasks")?.Text, Is.EqualTo("計画を調べています（2 回目）…"));
        Assert.That(Start("three", "summarize_plan")?.Text, Is.EqualTo("計画を調べています（3 回目）…"));
        Assert.That(Start("proposal", "propose_changes")?.Text, Is.EqualTo("変更案を検証しています…"));
        Assert.That(Complete("one")?.Text, Is.EqualTo("変更案を検証しています…"));
        Assert.That(Complete("proposal")?.Text, Is.EqualTo("計画を調べています（3 回目）…"));
        Complete("two");
        Assert.That(Complete("three")?.Text, Is.EqualTo("回答しています…"));
        Assert.That(Start("manual", "read_manual")?.Text, Is.EqualTo("マニュアルを調べています…"));
        Assert.That(Start("unknown", "untrusted-secret-tool"), Is.Null);
        Assert.That(Complete("unknown"), Is.Null);
        Assert.That(new AssistToolProgress().Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "new", ToolName = "get_tasks" } })?.Text,
            Is.EqualTo("計画を調べています（1 回目）…"));
    }

    [TestCase(false), TestCase(true)]
    public async Task HungRuntimeShutdownUsesOneDeadlineAndForcesStopBeforeReturning(bool hangsOnDispose)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "assist-stop-" + Guid.NewGuid().ToString("N"));
        using var state = new AssistRuntimeDirectory(root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(state.Path, "session.json"), "temporary conversation");
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forced = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try {
            var stopped = await AssistConversation.StopOwnedRuntime(() => hangsOnDispose ? Task.CompletedTask : hung.Task,
                () => { forced = true; hung.TrySetResult(); return Task.CompletedTask; },
                () => new(hung.Task), state);
            Assert.That(forced, Is.True, "Force stop must be issued even when graceful disposal hangs.");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3.3)));
            Assert.That(stopped, Is.True);
            Assert.That(Directory.Exists(state.Path), Is.False);
        } finally { hung.TrySetResult(); state.Dispose(); Directory.Delete(root, true); }
    }

    [Test]
    public async Task UnresponsiveForceStopReportsFailureWithinTheSameDeadlineAndRetainsStateForRecovery()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "assist-stop-" + Guid.NewGuid().ToString("N"));
        using var state = new AssistRuntimeDirectory(root, Guid.NewGuid().ToString("N"));
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forced = false; var watch = System.Diagnostics.Stopwatch.StartNew();
        try {
            var stopped = await AssistConversation.StopOwnedRuntime(() => hung.Task,
                () => { forced = true; return hung.Task; }, () => ValueTask.CompletedTask, state);
            Assert.That(forced, Is.True);
            Assert.That(stopped, Is.False);
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3.3)));
            Assert.That(Directory.Exists(state.Path), Is.True);
        } finally { hung.TrySetResult(); state.Dispose(); Directory.Delete(root, true); }
    }

    [TestCase(AssistProvider.Openai, AssistWireApi.Completions, "openai", "completions")]
    [TestCase(AssistProvider.Openai, AssistWireApi.Responses, "openai", "responses")]
    [TestCase(AssistProvider.Anthropic, AssistWireApi.Completions, "anthropic", null)]
    [TestCase(AssistProvider.Azure, AssistWireApi.Completions, "azure", null)]
    public void ByokCarriesOnlyExplicitProviderValuesAndConversationHeader(AssistProvider provider, AssistWireApi wire, string type, string? api)
    {
        var settings = new AssistSettings { Mode = AssistMode.Byok, Provider = provider, WireApi = wire,
            Endpoint = "https://provider.example/v1", Model = "model", ConversationIdHeader = "x-opencode-session" };
        var config = AssistConversation.SessionOptions(settings, "sentinel-key", "conversation", null);
        Assert.Multiple(() => {
            Assert.That(config.Provider!.Type, Is.EqualTo(type)); Assert.That(config.Provider.WireApi, Is.EqualTo(api));
            Assert.That(config.Provider.BaseUrl, Is.EqualTo(settings.Endpoint)); Assert.That(config.Provider.ModelId, Is.EqualTo(settings.Model));
            Assert.That(config.Provider.ApiKey, Is.EqualTo("sentinel-key"));
            Assert.That(config.Provider.Headers, Is.EquivalentTo(new Dictionary<string, string> { ["x-opencode-session"] = "conversation" }));
            Assert.That(JsonSerializer.Serialize(settings), Does.Not.Contain("sentinel-key"));
        });
    }
    [TestCase(AssistMode.Copilot, true), TestCase(AssistMode.Byok, false)]
    public void RuntimeUsesOwnedEmptyStdioWithExplicitPathAndCuratedEnvironment(AssistMode mode, bool loggedIn)
    {
        var config = AssistConversation.ClientOptions(new() { Mode = mode }, "isolated", Path.Combine(TestContext.CurrentContext.WorkDirectory, "copilot-runtime.exe"),
            new Dictionary<string, string> { ["PATH"] = "tools", ["GH_TOKEN"] = "sentinel" });
        Assert.Multiple(() => {
            Assert.That(config.Mode, Is.EqualTo(CopilotClientMode.Empty));
            Assert.That(config.Connection, Is.InstanceOf<StdioRuntimeConnection>());
            Assert.That(config.BaseDirectory, Is.EqualTo("isolated")); Assert.That(config.WorkingDirectory, Is.EqualTo("isolated"));
            Assert.That(config.UseLoggedInUser, Is.EqualTo(loggedIn)); Assert.That(config.GitHubToken, Is.Null);
            Assert.That(config.Environment!.Keys, Is.EquivalentTo(new[] { "PATH" })); Assert.That(config.LogLevel, Is.EqualTo(CopilotLogLevel.None));
        });
    }
    [TestCase("company.ghe.com", "company.ghe.com"), TestCase("github.com", null)]
    public void CopilotRuntimeAuthenticatesAgainstTheConnectedHostNotAnInheritedOne(string connectedHost, string? expected)
    {
        var config = AssistConversation.ClientOptions(new() { Mode = AssistMode.Copilot }, "isolated", Path.Combine(TestContext.CurrentContext.WorkDirectory, "copilot-runtime.exe"),
            new Dictionary<string, string> { ["PATH"] = "tools", ["GH_HOST"] = "inherited.example" }, connectedHost);
        Assert.That(config.Environment!.GetValueOrDefault("GH_HOST"), Is.EqualTo(expected));
    }
    [Test]
    public async Task ConnectionCheckConfigurationHasNoToolsOrPlanInstructionsAndDeniesPermissions()
    {
        var config = AssistConversation.SessionOptions(new() { Mode = AssistMode.Copilot, Model = "model" }, null, "id", null);
        Assert.That(config.AvailableTools, Is.Empty); Assert.That(config.Tools, Is.Empty); Assert.That(config.Provider, Is.Null);
        Assert.That(config.SystemMessage!.Mode, Is.EqualTo(SystemMessageMode.Replace));
        Assert.That(config.SystemMessage.Content, Does.Not.Contain("find_tasks"));
        var denied = await config.OnPermissionRequest!(null!, null!);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(denied));
        Assert.That(json.RootElement.GetProperty("kind").GetString(), Is.EqualTo("reject"));
        Assert.That(config.EnableConfigDiscovery, Is.False); Assert.That(config.EnableSkills, Is.False);
    }
    [TestCase("Authorization"), TestCase("X-Api-Key"), TestCase("bad\r\nheader"), TestCase("Host")]
    public void ConversationHeaderCannotOverrideAuthenticationOrHttpFraming(string header)
    {
        Assert.Throws<ArgumentException>(() => new AssistSettings { ConversationIdHeader = header }.Validate());
    }
    [TestCase("https://user:password@provider.example/v1"), TestCase("https://provider.example/v1?key=sentinel"), TestCase("file:///C:/config")]
    public void EndpointCannotPersistCredentialsOrNonHttpLocations(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new AssistSettings { Mode = AssistMode.Byok, Model = "model", Endpoint = endpoint }.Validate());
    }
    [TestCase(401, "invalid api key", AssistFailureKind.InvalidKey)]
    [TestCase(403, "policy", AssistFailureKind.Policy)]
    [TestCase(404, "model not found", AssistFailureKind.UnknownModel)]
    [TestCase(503, "network connection failed", AssistFailureKind.Unreachable)]
    [TestCase(500, "internal error", AssistFailureKind.Runtime)]
    public void ProviderErrorsAreClassifiedWithoutReturningSecretsOrPrompt(int status, string error, AssistFailureKind expected)
    {
        var result = AssistConversation.Failure(error + " sentinel-key user-plan-title", status);
        Assert.That(result.Kind, Is.EqualTo(expected));
        Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain("sentinel-key").And.Not.Contain("user-plan-title"));
    }
    [Test]
    public async Task DisabledAssistNeverStartsRuntimeOrCreatesStorage()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "unused-" + Guid.NewGuid().ToString("N"));
        await using var conversation = new AssistConversation(new(), path, "github.com", "pmo", null, runtimePath: "missing.exe");
        var events = new List<AssistEvent>();
        await foreach (var e in conversation.SendAsync("hello")) events.Add(e);
        Assert.That(events.Single().Failure!.Kind, Is.EqualTo(AssistFailureKind.Disabled));
        Assert.That(Directory.Exists(path), Is.False);
    }
    [Test]
    public async Task CancellationBeforeSendIsAnEventAndDoesNotStartRuntime()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "unused-" + Guid.NewGuid().ToString("N"));
        await using var conversation = new AssistConversation(new(), path, "github.com", "pmo", null, runtimePath: "missing.exe");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var events = new List<AssistEvent>();
        await foreach (var e in conversation.SendAsync("hello", cancellation: cancellation.Token)) events.Add(e);
        Assert.That(events.Single().Kind, Is.EqualTo(AssistEventKind.Cancelled));
        Assert.That(Directory.Exists(path), Is.False);
    }
    [TestCase("{}"), TestCase("{\"version\":2,\"mode\":\"none\"}"), TestCase("{\"version\":1,\"mode\":0}"),
     TestCase("{\"version\":1,\"mode\":\"none\",\"apiKey\":\"sentinel\"}"), TestCase("{\"version\":1,\"mode\":\"none\",\"unknown\":true}")]
    public void SettingsSchemaRejectsMissingVersionUnknownPropertiesAndNumericModes(string json)
    {
        Assert.That(() => AssistSettingsStore.Parse(System.Text.Encoding.UTF8.GetBytes(json)), Throws.Exception);
    }
}
