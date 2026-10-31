namespace VictoryTool.Desktop.Localization;

internal static class WorkflowText
{
    private static readonly IReadOnlyDictionary<string, Dictionary<string, string>> Translations = LoadTranslations();

    private static Dictionary<string, Dictionary<string, string>> LoadTranslations()
    {
        using var stream = typeof(WorkflowText).Assembly.GetManifestResourceStream("VictoryTool.Desktop.Localization.Translations.json")
            ?? throw new InvalidOperationException("UI translations resource is missing.");
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidOperationException("UI translations resource is empty.");
    }

    public static string ForLocale(string locale, string english) =>
        Translations.TryGetValue(english, out var translations) && translations.TryGetValue(locale, out var text)
            ? text : english;

}
