using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using GhProjectsBoards.App.GitHub;
using GhProjectsBoards.Core.PlanEditor;
using GhProjectsBoards.Core.Projects;
using NUnit.Framework;

namespace GhProjectsBoards.Tests;

[TestFixture, Category("Integration")]
internal sealed class FakeGhConcurrencyTests
{
    [TestCase("calls.jsonl"), TestCase("held-gh.pid")]
    public async Task ConcurrentDetailProcessesRecordEveryCallOnceAndPreserveStateDespiteFileContention(string heldFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "ghpb-fake-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "scenario.json"), JsonSerializer.Serialize(new {
                planEditor = true, holdQuery = "ProjectItemDetails" }));
            File.WriteAllText(Path.Combine(root, "release-gh"), "");
            FakePlanEditor.Save(root, new(Enumerable.Range(1, 6).Select(i =>
                new PlanFakeIssue(new PlanRow("I" + i, "Task " + i, "acme/repo"), "", true)).ToImmutableArray(), 7));
            var stateBefore = File.ReadAllBytes(FakePlanEditor.StatePath(root));
            var runner = new GhProcessRunner(new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root });
            Task<GhProcessResult>[] calls;
            using (var held = new FileStream(Path.Combine(root, heldFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // Run the actual fake entry point in separate .NET processes, without an apphost dependency.
                calls = Enumerable.Range(1, 6).Select(i => runner.RunAsync(new GhCommand("dotnet",
                    [typeof(FakeGhProgram).Assembly.Location, "api", "graphql", "--hostname", "github.com", "--method", "POST", "--input", "-"],
                    JsonSerializer.Serialize(new { query = ProjectQueries.ItemDetails, variables = new { ids = new[] { "T-I" + i } } })))).ToArray();
                // Keep the exclusive handle long enough for a child to attempt its diagnostic write.
                await Task.WhenAny(Task.WhenAny(calls), Task.Delay(TimeSpan.FromSeconds(1)));
            }
            var results = await Task.WhenAll(calls);
            for (var i = 0; i < results.Length; i++)
            {
                Assert.That(results[i].Completion, Is.EqualTo(ProcessCompletion.Exited));
                Assert.That(results[i].ExitCode, Is.Zero, results[i].StandardError);
                var body = results[i].StandardOutput;
                using var json = JsonDocument.Parse(body[(body.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]);
                Assert.That(json.RootElement.GetProperty("data").GetProperty("nodes")[0].GetProperty("id").GetString(), Is.EqualTo("T-I" + (i + 1)));
            }
            var records = File.ReadAllLines(Path.Combine(root, "calls.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.That(records, Has.Length.EqualTo(6));
            Assert.That(records.Select(r => r.GetProperty("pid").GetInt32()).Distinct().Count(), Is.EqualTo(6));
            Assert.That(records.All(r => r.GetProperty("endpoint").GetString() == "graphql" && !r.GetProperty("mutation").GetBoolean()), Is.True);
            Assert.That(File.ReadAllBytes(FakePlanEditor.StatePath(root)), Is.EqualTo(stateBefore));
            Assert.That(records.Select(r => r.GetProperty("pid").GetInt32()), Does.Contain(int.Parse(File.ReadAllText(Path.Combine(root, "held-gh.pid")))));
        }
        finally { Directory.Delete(root, true); }
    }
}
