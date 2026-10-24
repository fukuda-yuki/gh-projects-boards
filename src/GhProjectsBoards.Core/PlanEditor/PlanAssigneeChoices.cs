using System.Collections.Immutable;

namespace GhProjectsBoards.Core.PlanEditor;

// #225: one person the 担当者 cell offers while it is edited. The login is the value the cell commits, so a
// displayed name never has to be typed; the configured display name is shown beside it when it differs, so two
// people with similar logins stay distinguishable.
internal sealed record PlanAssigneeChoice(string Identity, string Login, string DisplayName = "")
{
    internal bool ShowsDisplayName => DisplayName.Length > 0 && !DisplayName.Equals(Login, StringComparison.OrdinalIgnoreCase);
    internal string Label => ShowsDisplayName ? DisplayName + " · " + Login : Login;
}

// #225: the candidate list for a 担当者 cell. #253 keeps the Project's people to the people assigned in it plus
// the people stored in settings, so this source is the same set the cell already matches a typed login against;
// it reads the open document only and never a repository's assignable users. A search filters on the login and
// on the display name, and full-width forms are folded for finding only: the login a choice commits stays the
// ASCII identifier the cell accepts.
internal static class PlanAssigneeChoices
{
    internal static readonly char[] Separators = [',', '、', ';'];

    // The accepted text of each known person, exactly as the cell resolves a typed login. A stored person's
    // configured name is used when the last read did not return a login for them.
    internal static (string Identity, string Name)[] Known(PlanDocument document)
        => document.State.Settings.People.Select(p => (p.Identity, Name: document.Sync.PeopleNames.GetValueOrDefault(p.Identity, p.Name)))
            .Concat(document.Sync.PeopleNames.Select(p => (Identity: p.Key, Name: p.Value))).Distinct().ToArray();

    internal static ImmutableArray<PlanAssigneeChoice> All(PlanDocument document)
    {
        var stored = document.State.Settings.People.ToDictionary(p => p.Identity, p => p.Name);
        return Known(document).DistinctBy(p => p.Identity)
            .Select(p => new PlanAssigneeChoice(p.Identity, p.Name, stored.GetValueOrDefault(p.Identity) ?? ""))
            .OrderBy(c => c.Login, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    internal static ImmutableArray<PlanAssigneeChoice> Matching(IEnumerable<PlanAssigneeChoice> choices, string text)
    {
        var query = PlanInput.Fold(Query(text));
        if (query.Length == 0) return choices.ToImmutableArray();
        return choices.Where(c => c.Login.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
    }

    // #225: after a refused login the list stays open and offers the nearest people first, so a typo does not
    // force the PMO to clear the text before the intended person is visible. A query that matches keeps the
    // ordinary filtered list.
    internal static ImmutableArray<PlanAssigneeChoice> Closest(IEnumerable<PlanAssigneeChoice> choices, string text)
    {
        var matching = Matching(choices, text);
        if (!matching.IsEmpty) return matching;
        var query = PlanInput.Fold(Query(text));
        return choices.OrderByDescending(c => Math.Max(Shared(c.Login, query), Shared(c.DisplayName, query)))
            .ThenBy(c => c.Login, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    internal static string Query(string text)
    {
        var at = text.LastIndexOfAny(Separators);
        return (at < 0 ? text : text[(at + 1)..]).Trim();
    }

    // The earlier values and their typed separator stay as written; the value being typed is replaced by the
    // chosen login.
    internal static string Compose(string text, string login)
    {
        var at = text.LastIndexOfAny(Separators);
        return at < 0 ? login : text[..(at + 1)].TrimEnd() + " " + login;
    }

    internal static string[] Values(string text) => text.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int Shared(string value, string query)
    {
        var shared = 0;
        while (shared < value.Length && shared < query.Length && char.ToUpperInvariant(value[shared]) == char.ToUpperInvariant(query[shared])) shared++;
        return shared;
    }
}
