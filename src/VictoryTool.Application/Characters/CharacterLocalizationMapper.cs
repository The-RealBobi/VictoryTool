using VictoryTool.CfgBin;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Characters;

public static class CharacterLocalizationMapper
{
    public static string? ResolveJapaneseName(CharacterCatalogItem source)
    {
        var names = source.RomanizedNames;
        var candidates = new[] { names?.GetValueOrDefault("ja")?.FullName, names?.GetValueOrDefault("en")?.FullName }
            .Concat(names?.Values.Select(value => value.FullName) ?? []);
        return ResolveJapaneseName(candidates, source.Localizations?.GetValueOrDefault("ja")?.FullName);
    }

    public static string? ResolveJapaneseName(IReadOnlyDictionary<string, string?> fields) =>
        ResolveJapaneseName(new[] { "ja", "en" }.Concat(GameLocaleCatalog.SupportedCharacterLocales)
            .Select(locale => fields.GetValueOrDefault($"Romanization.{locale}.FullName")),
            fields.GetValueOrDefault("Localization.ja.FullName"));

    public static CharacterDraftLocalization FillJapaneseNameFallback(
        CharacterDraftLocalization? localization, CharacterCatalogItem source)
    {
        var current = localization ?? new CharacterDraftLocalization(null, null);
        var name = ResolveJapaneseName(source);
        if (string.IsNullOrWhiteSpace(name)) return current;
        var locales = GameLocaleCatalog.SupportedCharacterLocales.ToDictionary(
            locale => locale,
            locale =>
            {
                var text = current.Locales.GetValueOrDefault(locale)
                    ?? new CharacterDraftLocalizedText(null, null, null, null);
                return string.IsNullOrWhiteSpace(text.JapaneseName) ? text with { JapaneseName = name } : text;
            },
            StringComparer.OrdinalIgnoreCase);
        return current with { LocaleValues = locales };
    }

    private static string? ResolveJapaneseName(IEnumerable<string?> candidates, string? localizedJapaneseName) =>
        candidates.Append(localizedJapaneseName).FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value) && !CharacterDisplayName.ContainsJapanese(value));

    public static IReadOnlyDictionary<int, CharacterLocalizedText> Map(
        string locale,
        IEnumerable<CharacterBaseMetadata> characters,
        IEnumerable<CfgBinEntry> nameEntries,
        IEnumerable<CfgBinEntry> descriptionEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(nameEntries);
        ArgumentNullException.ThrowIfNull(descriptionEntries);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = GlobalLog.BeginOperation("character_localization_map", new Dictionary<string, object?>
        {
            ["locale"] = locale,
        });

        var names = new Dictionary<(int Key, int Form), string>();
        foreach (var entry in nameEntries.Where(entry => entry.Name == "NOUN_INFO" && entry.Values.Count >= 6))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetInteger(entry, 0, out var key)
                || !TryGetInteger(entry, 1, out var form)
                || entry.Values[5].Value is not string text)
            {
                continue;
            }
            names.TryAdd((key, form), text);
        }

        var descriptions = new Dictionary<int, string>();
        foreach (var entry in descriptionEntries.Where(entry => entry.Name == "TEXT_INFO" && entry.Values.Count >= 3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetInteger(entry, 0, out var key) && entry.Values[2].Value is string text)
                descriptions.TryAdd(key, text);
        }

        var result = new Dictionary<int, CharacterLocalizedText>();
        foreach (var character in characters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[character.BaseId] = new CharacterLocalizedText(
                locale,
                Find(names, character.FullNameTextId, 0),
                Find(names, character.FullNameTextId, 11),
                Find(names, character.FullNameTextId, 12),
                Find(names, character.ShortNameTextId, 0),
                Find(names, character.UpperNameTextId, 0),
                character.DescriptionTextId == 0
                    ? null
                    : descriptions.GetValueOrDefault(character.DescriptionTextId));
        }
        GlobalLog.Debug("character_localization_mapped", new Dictionary<string, object?>
        {
            ["nameEntryCount"] = names.Count,
            ["descriptionEntryCount"] = descriptions.Count,
            ["characterCount"] = result.Count,
            ["localizedNameCount"] = result.Values.Count(value => value.FullName is not null),
            ["localizedDescriptionCount"] = result.Values.Count(value => value.Description is not null),
        });
        return result;
    }

    private static string? Find(
        IReadOnlyDictionary<(int Key, int Form), string> names,
        int key,
        int form) => key == 0 ? null : names.GetValueOrDefault((key, form));

    private static bool TryGetInteger(CfgBinEntry entry, int index, out int value)
    {
        switch (entry.Values[index].Value)
        {
            case int int32:
                value = int32;
                return true;
            case long int64 when int64 is >= int.MinValue and <= int.MaxValue:
                value = (int)int64;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
