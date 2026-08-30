using VictoryTool.CfgBin;

namespace VictoryTool.Application.Characters;

public static class CharacterRomanizedNameMapper
{
    public static IReadOnlyDictionary<int, CharacterNameSet> Map(
        string locale,
        IEnumerable<CharacterBaseMetadata> characters,
        IEnumerable<CfgBinEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(entries);

        var names = new Dictionary<(int Key, int Form), string>();
        foreach (var entry in entries.Where(entry => entry.Name == "NOUN_INFO" && entry.Values.Count >= 6))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetInteger(entry, 0, out var key)
                || !TryGetInteger(entry, 1, out var form)
                || entry.Values[5].Value is not string value)
            {
                continue;
            }

            names.TryAdd((key, form), value);
        }

        var result = new Dictionary<int, CharacterNameSet>();
        foreach (var character in characters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (character.FullNameTextId == 0) continue;
            var nameSet = new CharacterNameSet(
                names.GetValueOrDefault((character.FullNameTextId, 0)),
                names.GetValueOrDefault((character.FullNameTextId, 11)),
                names.GetValueOrDefault((character.FullNameTextId, 12)));
            if (nameSet.FullName is not null || nameSet.FamilyName is not null || nameSet.GivenName is not null)
                result.Add(character.BaseId, nameSet);
        }

        return result;
    }

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
