using GhProjectsBoards.Core.Assist;
using Windows.Security.Credentials;

namespace GhProjectsBoards.App;

internal interface IAssistSecretStore
{
    bool IsRegistered { get; }
    string? Read();
    void Store(string key);
    void Delete();
}

internal sealed class AssistSecretStore : IAssistSecretStore
{
    private const string Resource = "GhProjectsBoards.AiAssist";
    private const string User = "BYOK";
    private static PasswordCredential? Find()
    {
        try { return new PasswordVault().Retrieve(Resource, User); }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
    }
    public bool IsRegistered => Find() is not null;
    public string? Read() { var credential = Find(); credential?.RetrievePassword(); return credential?.Password; }
    public void Store(string key) => new PasswordVault().Add(new PasswordCredential(Resource, User, key));
    public void Delete() { if (Find() is { } credential) new PasswordVault().Remove(credential); }
}

// Only the runtime/model transport is replaceable. Tools, validation and application use real Core.
internal interface IAssistRuntime
{
    IAssistTurn Create(AssistSettings settings, string root, string host, string login, AssistPlanTools tools, string? key);
    Task<AssistConnectionCheck> Check(AssistSettings settings, string root, string host, string login, string? key, CancellationToken cancellation);
}
internal interface IAssistTurn : IAsyncDisposable
{
    IAsyncEnumerable<AssistEvent> Send(string text, string? selected, CancellationToken cancellation);
}
internal sealed class AssistRuntime : IAssistRuntime
{
    public IAssistTurn Create(AssistSettings settings, string root, string host, string login, AssistPlanTools tools, string? key)
        => new Turn(new(settings, root, host, login, tools, key));
    public Task<AssistConnectionCheck> Check(AssistSettings settings, string root, string host, string login, string? key, CancellationToken cancellation)
        => AssistConversation.CheckConnectionAsync(settings, root, host, login, key, cancellation: cancellation);
    private sealed class Turn(AssistConversation conversation) : IAssistTurn
    {
        public IAsyncEnumerable<AssistEvent> Send(string text, string? selected, CancellationToken cancellation)
            => conversation.SendAsync(text, selected, cancellation);
        public ValueTask DisposeAsync() => conversation.DisposeAsync();
    }
}
