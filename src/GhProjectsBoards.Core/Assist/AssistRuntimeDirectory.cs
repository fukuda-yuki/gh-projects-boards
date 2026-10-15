using GhProjectsBoards.Core.PlanEditor;

namespace GhProjectsBoards.Core.Assist;

// Each runtime has a separate lease so startup cleanup cannot remove another window's live state.
internal sealed class AssistRuntimeDirectory : IDisposable
{
    private readonly FileStream lease;
    private readonly string root;
    public string Path { get; }
    public AssistRuntimeDirectory(string dataRoot, string conversationId)
    {
        root = Root(dataRoot);
        CleanupLeftovers(dataRoot);
        if (!Guid.TryParseExact(conversationId, "N", out _)) throw new ArgumentException("Invalid conversation id.");
        Path = System.IO.Path.Combine(root, "conversation-" + conversationId);
        Directory.CreateDirectory(Path);
        lease = new FileStream(System.IO.Path.Combine(Path, ".owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    private static string Root(string dataRoot) => System.IO.Path.GetFullPath(System.IO.Path.Combine(new PlanStore(dataRoot).Root, "assist"));
    internal static void CleanupLeftovers(string dataRoot)
    {
        var root = Root(dataRoot);
        Directory.CreateDirectory(root);
        RequireOrdinaryDirectory(root);
        foreach (var directory in Directory.GetDirectories(root, "conversation-*")) {
            if (!Guid.TryParseExact(System.IO.Path.GetFileName(directory)[13..], "N", out _)) continue;
            RequireOrdinaryDirectory(directory);
            try {
                using (new FileStream(System.IO.Path.Combine(directory, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
            }
            catch (IOException) { continue; }
            DeleteOwned(root, directory);
        }
    }
    private static void RequireOrdinaryDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Assist state directory cannot be a reparse point.");
    }
    private static void DeleteOwned(string root, string directory)
    {
        var full = System.IO.Path.GetFullPath(directory);
        if (System.IO.Path.GetDirectoryName(full) != root) throw new IOException("Assist cleanup target is outside its data directory.");
        // Never traverse links while deleting runtime-owned state.
        void Delete(string path) {
            RequireOrdinaryDirectory(path);
            foreach (var entry in Directory.GetFileSystemEntries(path)) {
                var flags = File.GetAttributes(entry);
                if ((flags & FileAttributes.ReparsePoint) != 0) throw new IOException("Assist state contains a reparse point.");
                if ((flags & FileAttributes.Directory) != 0) Delete(entry); else File.Delete(entry);
            }
            Directory.Delete(path);
        }
        Delete(full);
    }
    internal void ReleaseLease() => lease.Dispose();
    public void Dispose() { lease.Dispose(); if (Directory.Exists(Path)) DeleteOwned(root, Path); }
}
