using System.Text.Json;

namespace VictoryTool.Application.Characters;

public static class CharacterLocalizationJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string Serialize(CharacterDraftLocalization? localization)
    {
        var values = localization?.Locales ?? GameLocaleCatalog.CreateEmptyLocalizations();
        return JsonSerializer.Serialize(values, Options);
    }

    public static CharacterDraftLocalization Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new JsonException("The locales JSON file is empty.");

        var values = JsonSerializer.Deserialize<Dictionary<string, CharacterDraftLocalizedText>>(json, Options)
            ?? throw new JsonException("The locales JSON must contain an object of locale entries.");
        var supported = GameLocaleCatalog.SupportedCharacterLocales
            .ToDictionary(locale => locale, StringComparer.OrdinalIgnoreCase);
        foreach (var locale in values.Keys)
        {
            if (!supported.ContainsKey(locale))
                throw new JsonException($"Unsupported character locale '{locale}'.");
        }

        var normalized = GameLocaleCatalog.SupportedCharacterLocales.ToDictionary(
            locale => locale,
            locale => values.TryGetValue(locale, out var value) && value is not null
                ? value
                : new CharacterDraftLocalizedText(null, null, null, null),
            StringComparer.OrdinalIgnoreCase);
        return new CharacterDraftLocalization(
            normalized.GetValueOrDefault("en")?.LocalizedName,
            normalized.GetValueOrDefault("ja")?.RomanizedName,
            normalized);
    }
}
