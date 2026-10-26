namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanDataRoot
{
    internal const string Instruction = "GHPB_DATA_ROOT は絶対パスを指定してください。";
    internal static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhProjectsBoards", "Registrations");

    internal static string Resolve() => Environment.GetEnvironmentVariable("GHPB_DATA_ROOT") is { } configured
        ? configured : DefaultFolder;

    internal static string Prepare()
    {
        var path = Resolve();
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new PlanDataRootException(path);
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is not PlanDataRootException)
        {
            throw new PlanDataRootException(path, ex);
        }
    }
}

internal sealed class PlanDataRootException(string path, Exception? inner = null) : Exception(Text(path), inner)
{
    internal string Path { get; } = path;
    private static string Text(string path) => "保存先を利用できません。" + Environment.NewLine + path + Environment.NewLine + PlanDataRoot.Instruction;
}

internal enum PlanStartupKind { DataRoot, General }

internal readonly record struct PlanStartupNotice(PlanStartupKind Kind, string Text)
{
    internal static PlanStartupNotice For(Exception exception) => exception is PlanDataRootException
        ? new(PlanStartupKind.DataRoot, exception.Message)
        : new(PlanStartupKind.General, "起動できませんでした。" + Environment.NewLine
            + exception.GetType().FullName + Environment.NewLine + exception.Message);
}

internal static class PlanStartup
{
    internal static Exception? ViewProbe;

    internal static PlanStartupNotice? Try(Action start)
    {
        try
        {
            PlanDataRoot.Prepare();
            if (ViewProbe is { } probe) throw probe;
            start();
            return null;
        }
        catch (Exception ex) { return PlanStartupNotice.For(ex); }
        finally { ViewProbe = null; }
    }
}
