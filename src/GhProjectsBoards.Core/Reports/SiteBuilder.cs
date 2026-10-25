namespace GhProjectsBoards.Core.Reports;

// #271: build a whole site from one configuration and the raw records it points at. All Projects are validated and
// computed before anything is written, so a configuration error leaves no half-correct page behind.
internal static class SiteBuilder
{
    internal static void Build(string dataRoot, string configPath, string outFolder)
    {
        var config = SiteConfigReader.Parse(File.ReadAllText(configPath));
        var projects = config.Projects.Select(project => SiteAnalysis.Compute(project, dataRoot)).ToArray();
        var outputs = SiteRenderer.Render(config, projects);
        foreach (var output in outputs) Write(outFolder, output);
    }

    private static void Write(string root, SiteRenderer.Output output)
    {
        var path = Path.Combine(root, output.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, output.Bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
