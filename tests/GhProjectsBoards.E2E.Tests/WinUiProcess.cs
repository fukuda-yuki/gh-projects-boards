using System.Diagnostics;
using NUnit.Framework;

namespace GhProjectsBoards.E2E.Tests;

internal static class WinUiProcess
{
    public static void AssertRuntime(Process process)
    {
        process.Refresh();
        Assert.That(process.HasExited, Is.False, "The UI process must still be running.");
        Assert.That(process.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(module.ModuleName, "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase)),
            Is.True, "Desktop acceptance must execute the WinUI 3 application, not a substitute executable.");
    }
}
