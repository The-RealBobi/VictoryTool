namespace VictoryTool.Application.Characters;

public enum CharacterSort
{
    DisplayName,
    Id,
    Affinity,
    Position,
}

public sealed record CharacterFilterSet(
    IReadOnlyCollection<CharacterAffinity>? Affinities = null,
    IReadOnlyCollection<CharacterOrigin>? Origins = null,
    IReadOnlyCollection<string>? Series = null,
    IReadOnlyCollection<int>? AcademicYears = null,
    IReadOnlyCollection<int>? Genders = null,
    IReadOnlyCollection<int>? BodyTypes = null,
    IReadOnlyCollection<string>? Positions = null,
    IReadOnlyCollection<int>? PlayStyles = null,
    IReadOnlyCollection<int>? Ranks = null,
    IReadOnlyCollection<int>? SpecialRarities = null)
{
    public static CharacterFilterSet Empty { get; } = new();
}

public sealed record CharacterCatalogQuery(
    string SearchText,
    CharacterFilterSet Filters,
    CharacterSort Sort)
{
    public IReadOnlyList<CharacterCatalogItem> Apply(IEnumerable<CharacterCatalogItem> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var query = SearchText?.Trim() ?? string.Empty;
        var filtered = source.Where(character => Matches(character, query) && MatchesFilters(character));

        return Sort switch
        {
            CharacterSort.Id => filtered
                .OrderBy(character => character.Id, StringComparer.Ordinal)
                .ToArray(),
            CharacterSort.Affinity => filtered
                .OrderBy(character => character.Affinity)
                .ThenBy(character => character.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.Id, StringComparer.Ordinal)
                .ToArray(),
            CharacterSort.Position => filtered
                .OrderBy(character => character.Position, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.Id, StringComparer.Ordinal)
                .ToArray(),
            _ => filtered
                .OrderBy(character => character.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(character => character.Id, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private bool MatchesFilters(CharacterCatalogItem character) =>
        (Filters.Affinities is not { Count: > 0 } || Filters.Affinities.Contains(character.Affinity))
        && (Filters.Origins is not { Count: > 0 } || Filters.Origins.Contains(character.Origin))
        && (Filters.Series is not { Count: > 0 } || Filters.Series.Contains(character.Series, StringComparer.OrdinalIgnoreCase))
        && (Filters.AcademicYears is not { Count: > 0 }
            || character.BaseMetadata is { } academic && Filters.AcademicYears.Contains(academic.AcademicYear))
        && (Filters.Genders is not { Count: > 0 }
            || character.BaseMetadata is { } gender && Filters.Genders.Contains(gender.Gender))
        && (Filters.BodyTypes is not { Count: > 0 }
            || character.BaseMetadata is { } body && Filters.BodyTypes.Contains(body.BodyType))
        && (Filters.Positions is not { Count: > 0 }
            || Filters.Positions.Contains(character.Position, StringComparer.OrdinalIgnoreCase))
        && (Filters.PlayStyles is not { Count: > 0 }
            || character.Variants?.Any(variant => Filters.PlayStyles.Contains(variant.PlayStyle)) == true)
        && (Filters.Ranks is not { Count: > 0 }
            || character.Variants?.Any(variant => Filters.Ranks.Contains(variant.Rank)) == true)
        && (Filters.SpecialRarities is not { Count: > 0 }
            || character.Variants?.Any(variant => Filters.SpecialRarities.Contains(variant.SpecialRarity)) == true);

    private static bool Matches(CharacterCatalogItem character, string query)
    {
        if (query.Length == 0) return true;
        return CandidateValues(character).Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> CandidateValues(CharacterCatalogItem character)
    {
        yield return character.Id;
        yield return character.DisplayName;
        if (!string.IsNullOrWhiteSpace(character.BaseMetadata?.InternalName))
            yield return character.BaseMetadata.InternalName;

        if (character.Localizations is not null)
        {
            foreach (var localization in character.Localizations.Values)
            {
                foreach (var value in new[]
                {
                    localization.FullName,
                    localization.FamilyName,
                    localization.GivenName,
                    localization.ShortName,
                    localization.UpperName,
                })
                {
                    if (!string.IsNullOrWhiteSpace(value)) yield return value;
                }
            }
        }

        if (character.RomanizedNames is null) yield break;
        foreach (var names in character.RomanizedNames.Values)
        {
            foreach (var value in new[] { names.FullName, names.FamilyName, names.GivenName })
            {
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }
    }
}
