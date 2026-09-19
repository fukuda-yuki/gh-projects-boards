using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GhProjectsBoards.App.GitHub;

namespace GhProjectsBoards.Core.Projects;

internal sealed record ProjectRegistration(string ViewerLogin, string OwnerLogin,
    IReadOnlyList<RepositoryReadModel> Repositories, string? DefaultRepository,
    DateTimeOffset RetrievedAt, ProjectReadModel Snapshot);
internal sealed record StorageProblem(string File, string Kind);
internal sealed record RegistrationLoad(IReadOnlyList<ProjectRegistration> Registrations, IReadOnlyList<StorageProblem> Problems, DraftRecord[]? Checkpoints = null);

// Each file is an atomic metadata/snapshot pair. ConnectionContext is deliberately absent.
internal sealed class RegistrationStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { info =>
        {
            if (info.Kind == JsonTypeInfoKind.Object)
                foreach (var property in info.Properties) property.IsRequired = property.Name is not ("Capability" or "Scalar");
        } } },
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public string Root { get; }
    public RegistrationStore(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute data root is required.");
        Root = Path.GetFullPath(root);
    }
    public static RegistrationStore ForUser()
    {
        var configured = Environment.GetEnvironmentVariable("GHPB_DATA_ROOT");
        return new(configured is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhProjectsBoards", "Registrations") : configured);
    }
    public string FileFor(ScopedId key) => Path.Combine(Root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Scope.Host}\n{key.Scope.ViewerId}\n{key.NodeId}"))) + ".json");

    public async Task<RegistrationLoad> LoadAsync(CancellationToken token = default)
    {
        var values = new List<ProjectRegistration>();
        var problems = new List<StorageProblem>();
        DraftRecord[] acceptedCheckpoints = [];
        if (!Directory.Exists(Root)) return new(values, File.Exists(Root) ? [new("registration store", "DataRootIsFile")] : problems);
        try
        {
            using var gate = Lock();
            foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
            {
                try
                {
                    var record = await ReadAsync(file, token);
                    if (FileFor(record.Snapshot.Id) != file) throw new InvalidDataException("IdentityFilenameMismatch");
                    values.Add(record);
                }
                catch (Exception ex) when (StorageFailure(ex)) { problems.Add(new(Path.GetFileName(file), Classification(ex))); }
            }
            foreach (var file in Directory.EnumerateFiles(Root).Where(f => f.EndsWith(".tmp", StringComparison.Ordinal) || f.EndsWith(".removed", StringComparison.Ordinal)))
                problems.Add(new(Path.GetFileName(file), "InterruptedOperation"));
            foreach (var file in Directory.EnumerateFiles(Root, "*.bak").Where(f => !File.Exists(f[..^4])))
                problems.Add(new(Path.GetFileName(file), "OrphanedLastGoodBackup"));
            var draftStore = new DraftStore(Root);
            var checkpoints = await draftStore.CheckpointsAsync();
            acceptedCheckpoints = checkpoints.Records;
            foreach (var checkpoint in checkpoints.Records)
            {
                values.RemoveAll(r => r.Snapshot.Id.Scope == checkpoint.Scope);
                values.AddRange(checkpoint.Registrations!.Select(FromRecord));
            }
            values.RemoveAll(r => checkpoints.Problems.Any(p => p.File == Path.GetFileName(draftStore.FileFor(r.Snapshot.Id.Scope))));
            problems.AddRange(checkpoints.Problems);
        }
        catch (Exception ex) when (StorageFailure(ex)) { problems.Add(new("registration store", Classification(ex))); }
        return new(values, problems, acceptedCheckpoints);
    }

    public async Task SaveAsync(ProjectRegistration registration, CancellationToken token = default, Func<bool>? canCommit = null)
    {
        Validate(registration);
        Directory.CreateDirectory(Root);
        using var gate = Lock();
        if ((await new DraftStore(Root).LoadAsync(registration.Snapshot.Id.Scope))?.Registrations is not null)
            throw new InvalidDataException("Profile migrated; reopen before changing registrations.");
        var file = FileFor(registration.Snapshot.Id);
        // Never overwrite an unreadable or newer-format record with an apparent new registration.
        if (File.Exists(file)) _ = await ReadAsync(file, token);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, ToRecord(registration), Json, token);
            await stream.FlushAsync(token);
            stream.Flush(true);
        }
        _ = await ReadAsync(temp, token);
        token.ThrowIfCancellationRequested();
        // No await separates the caller's final draft/generation guard from atomic replacement.
        if (canCommit is not null && !canCommit()) throw new InvalidDataException("RegistrationCommitObsolete");
        if (File.Exists(file)) File.Replace(temp, file, file + ".bak");
        else File.Move(temp, file);
    }

    public async Task RemoveAsync(ScopedId key, CancellationToken token = default)
    {
        Directory.CreateDirectory(Root);
        using var gate = Lock();
        if ((await new DraftStore(Root).LoadAsync(key.Scope))?.Registrations is not null)
            throw new InvalidDataException("Profile migrated; reopen before changing registrations.");
        var file = FileFor(key);
        var removed = file + ".removed";
        if (File.Exists(file))
        {
            var existing = await ReadAsync(file, token);
            if (existing.Snapshot.Id != key) throw new InvalidDataException("ScopeMismatch");
            token.ThrowIfCancellationRequested();
            // The rename is the removal commit. Leftovers are diagnosed, never auto-restored.
            File.Move(file, removed);
        }
        File.Delete(file + ".bak");
        foreach (var temp in Directory.EnumerateFiles(Root, Path.GetFileName(file) + ".*.tmp")) File.Delete(temp);
        File.Delete(removed);
    }

    private FileStream Lock() => new(Path.Combine(Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    // Called only by the final checkpoint predicate, while its common root writer lock is held.
    internal bool MatchesLegacy(ConnectionScope scope, IEnumerable<ProjectRegistration> expected)
    {
        var actual = new List<RegistrationRecord>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            var record = JsonSerializer.Deserialize<RegistrationRecord>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Invalid legacy record.");
            var registration = FromRecord(record);
            if (registration.Snapshot.Id.Scope == scope) actual.Add(record);
        }
        string Canonical(IEnumerable<RegistrationRecord> records) => JsonSerializer.Serialize(records.OrderBy(r => r.Snapshot.Id.NodeId).ToArray(), Json);
        return Canonical(actual) == Canonical(expected.Where(r => r.Snapshot.Id.Scope == scope).Select(ToRecord));
    }
    private static bool StorageFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static string Classification(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "AccessDenied", JsonException => "InvalidJsonOrSchema",
        InvalidDataException => "InvalidRecordOrSchema", IOException => "IoOrCompetingWriter", _ => "InvalidStorage"
    };
    private static async Task<ProjectRegistration> ReadAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var record = await JsonSerializer.DeserializeAsync<RegistrationRecord>(stream, Json, token)
            ?? throw new InvalidDataException("EmptyRecord");
        if (record.Version != 1 || record.Snapshot is null || record.Snapshot.Issues is null) throw new InvalidDataException("UnsupportedSchema");
        return FromRecord(record);
    }
    internal static ProjectRegistration FromRecord(RegistrationRecord record)
    {
        if (record is null || record.Version != 1 || record.Snapshot is null || record.Snapshot.Issues is null) throw new InvalidDataException("UnsupportedSchema");
        var s = record.Snapshot;
        if (s.Issues.Any(i => i is null || i.Id is null) || s.Issues.Select(i => i.Id).Distinct().Count() != s.Issues.Length) throw new InvalidDataException("DuplicateOrNullIssue");
        var project = new ProjectReadModel(s.Id, s.OwnerId, s.OwnerType, s.Number, s.Url, s.Title, s.Fields,
            s.Issues.ToDictionary(i => i.Id), s.Items, s.FieldsComplete, s.ItemsComplete, s.Capability);
        var result = new ProjectRegistration(record.ViewerLogin, record.OwnerLogin, record.Repositories,
            record.DefaultRepository, record.RetrievedAt, project);
        Validate(result);
        return result;
    }
    internal static RegistrationRecord ToRecord(ProjectRegistration r)
    {
        var s = r.Snapshot;
        return new(1, r.ViewerLogin, r.OwnerLogin, r.Repositories, r.DefaultRepository, r.RetrievedAt,
            new(s.Id, s.OwnerId, s.OwnerType, s.Number, s.Url, s.Title, s.Fields, s.Issues.Values.ToArray(), s.Items, s.FieldsComplete, s.ItemsComplete, s.Capability));
    }
    private static void Validate(ProjectRegistration r)
    {
        var p = r.Snapshot;
        if (p?.Id?.Scope is not { } scope || !GitHubAddress.TryHost(scope.Host, out var host) || host != scope.Host || scope.ViewerId <= 0
            || string.IsNullOrWhiteSpace(p.Id.NodeId) || string.IsNullOrWhiteSpace(r.ViewerLogin) || string.IsNullOrWhiteSpace(r.OwnerLogin)
            || p.Fields is null || p.Items is null || p.Issues is null || r.Repositories is null || r.RetrievedAt == default
            || !p.FieldsComplete || !p.ItemsComplete || p.Items.Any(i => i is null || !i.ValuesComplete) || p.Number <= 0
            || p.Title is null || p.OwnerType is not ("User" or "Organization")) throw new InvalidDataException("InvalidRegistration");
        bool Valid(ScopedId? id) => id is not null && id.Scope == scope && !string.IsNullOrWhiteSpace(id.NodeId);
        if (p.Fields.Any(f => f is null) || p.Issues.Any(i => i.Value is null || i.Value.Repository is null || i.Value.Title is null || i.Value.State is null)
            || r.Repositories.Any(r => r is null)) throw new InvalidDataException("NullRecord");
        if (!Valid(p.OwnerId) || !Uri.TryCreate(p.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != host
            || p.Fields.Select(f => f.Id).Distinct().Count() != p.Fields.Count || p.Items.Select(i => i.Id).Distinct().Count() != p.Items.Count
            || p.Fields.Any(f => !Valid(f.Id) || f.ProjectId != p.Id || f.Options is null || f.Options.Any(o => o is null)
                || f.Options.Select(o => o.Id).Distinct().Count() != f.Options.Count || !Enum.IsDefined(f.Availability) || !Enum.IsDefined(f.ValueOwner))
            || p.Items.Any(i => !Valid(i.Id) || (i.ContentId is not null && !Valid(i.ContentId)) || !Enum.IsDefined(i.Kind) || i.Values is null
                || i.Values.Any(v => v is null || (v.FieldId is not null && !Valid(v.FieldId)) || !Enum.IsDefined(v.Availability)))
            || p.Issues.Any(pair => pair.Key != pair.Value.Id || !Valid(pair.Key) || !Valid(pair.Value.Repository.Id) || !Valid(pair.Value.Repository.OwnerId))
            || r.Repositories.Any(repo => !Valid(repo.Id) || !Valid(repo.OwnerId))) throw new InvalidDataException("InconsistentSnapshot");
    }
    internal sealed record RegistrationRecord(int Version, string ViewerLogin, string OwnerLogin,
        IReadOnlyList<RepositoryReadModel> Repositories, string? DefaultRepository, DateTimeOffset RetrievedAt, SnapshotRecord Snapshot);
    internal sealed record SnapshotRecord(ScopedId Id, ScopedId OwnerId, string OwnerType, int Number, string Url, string Title,
        IReadOnlyList<ProjectFieldDefinition> Fields, IssueReadModel[] Issues, IReadOnlyList<ProjectItemReadModel> Items, bool FieldsComplete, bool ItemsComplete, CapabilityObservation? Capability = null);
}
