using VictoryTool.CfgBin;

namespace VictoryTool.Application.Characters;

public static class CharacterLocalizationMapper
{
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
