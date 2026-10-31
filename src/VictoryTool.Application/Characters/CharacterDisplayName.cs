namespace VictoryTool.Application.Characters;

public static class CharacterDisplayName
{
    public static string Resolve(CharacterCatalogItem character, string locale)
    {
        var localized = character.Localizations?.GetValueOrDefault(locale)?.FullName
            ?? character.Localizations?.GetValueOrDefault("en")?.FullName ?? character.DisplayName;
        var names = character.RomanizedNames;
        var romanized = names?.GetValueOrDefault(locale)?.FullName
            ?? names?.GetValueOrDefault("ja")?.FullName
            ?? names?.GetValueOrDefault("en")?.FullName
            ?? names?.Values.Select(value => value.FullName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return Resolve(localized, romanized, locale);
    }

    public static string Resolve(string localized, string? romanized, string locale) =>
        !string.Equals(locale, "ja", StringComparison.OrdinalIgnoreCase)
        && !locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        && ContainsJapanese(localized) && !string.IsNullOrWhiteSpace(romanized) && !ContainsJapanese(romanized)
            ? romanized : localized;

    private static bool ContainsJapanese(string text) => text.EnumerateRunes().Any(rune => rune.Value is
        >= 0x3040 and <= 0x30ff or >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff
        or >= 0x20000 and <= 0x323af);
}
