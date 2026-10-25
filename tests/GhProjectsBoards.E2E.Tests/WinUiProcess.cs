using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

internal static class WinUiProcess
{
    // #275: the gh.exe path is this PC's connection setting, so a test selects its fake gh the way the
    // settings page stores it, before the app starts.
    public static void UseGh(string dataRoot, string gh)
    {
        var folder = Path.Combine(dataRoot, "PlanningEditor", "v1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "connection.json"), JsonSerializer.Serialize(new { version = 1, ghPath = gh }));
    }
    public static void AssertRuntime(Process process)
    {
        process.Refresh();
        Assert.That(process.HasExited, Is.False, "The UI process must still be running.");
        var module = process.Modules.Cast<ProcessModule>().SingleOrDefault(module =>
            string.Equals(module.ModuleName, "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
        Assert.That(module, Is.Not.Null, "Desktop acceptance must execute the WinUI 3 application, not a substitute executable.");
        var artifacts = Environment.GetEnvironmentVariable("GHPB_E2E_ARTIFACTS")
            ?? Environment.GetEnvironmentVariable("GHPB_LIVE_ARTIFACTS")
            ?? Environment.GetEnvironmentVariable("GHPB_READY_ARTIFACTS");
        if (artifacts is not null)
        {
            using var stream = File.OpenRead(module!.FileName);
            File.WriteAllText(Path.Combine(artifacts, $"loaded-winui-{process.Id}.json"), JsonSerializer.Serialize(new
            {
                test = TestContext.CurrentContext.Test.FullName, pid = process.Id, path = module.FileName,
                sha256 = Convert.ToHexString(SHA256.HashData(stream)), version = module.FileVersionInfo.FileVersion
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
