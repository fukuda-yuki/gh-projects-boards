using System.Text;
using System.Text.Json;

namespace GhProjectsBoards.Tests;

// This entry point is only in the test executable, never in the shipped app.
internal static class FakeGhProgram
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.FirstOrDefault() == "environment")
        {
            var variables = new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN",
                "GH_DEBUG", "DEBUG", "GH_HOST", "GH_REPO" };
            Console.Write(JsonSerializer.Serialize(new
            {
                present = variables.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))),
                promptDisabled = Environment.GetEnvironmentVariable("GH_PROMPT_DISABLED"),
                configDirectory = Environment.GetEnvironmentVariable("GH_CONFIG_DIR")
            }));
            return 0;
        }
        if (args.FirstOrDefault() == "echo")
        {
            var input = await Console.In.ReadToEndAsync();
            Console.Write(JsonSerializer.Serialize(new { arguments = args.Skip(1), input }));
            Console.Error.Write("synthetic stderr");
            return 7;
        }
        if (args.FirstOrDefault() == "wait")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return 0;
        }
        return 2;
    }
}
