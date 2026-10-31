using System.Text;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Text;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Characters;

public sealed record CharacterNameTagReference(uint TagId, uint CharacterId, uint OverloadNameId);

public sealed class CharacterTextReferenceIndex
{
    private readonly IReadOnlyDictionary<uint, CharacterNameTagReference> _references;
    private readonly IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> _names;
    private readonly IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> _overloadNames;
    private readonly IReadOnlyDictionary<uint, uint> _mapReferences;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> _mapNames;

    private CharacterTextReferenceIndex(
        IReadOnlyDictionary<uint, CharacterNameTagReference> references,
        IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> names,
        IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> overloadNames,
        IReadOnlyDictionary<uint, uint> mapReferences,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> mapNames)
    {
        _overloadNames = overloadNames;
        _mapReferences = mapReferences;
        _mapNames = mapNames;
        _references = references;
        _names = names;
    }

    public static CharacterTextReferenceIndex Empty { get; } = Create([], new Dictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>>());

    public static CharacterTextReferenceIndex Create(
        IEnumerable<CharacterNameTagReference> references,
        IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> names) => new(
            references.ToDictionary(reference => reference.TagId),
            names, new Dictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>>(), new Dictionary<uint, uint>(), new Dictionary<string, IReadOnlyDictionary<uint, string>>());

    public string? Resolve(CharacterReferenceKind kind, string tag, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var hash = ComputeCrc32(Encoding.ASCII.GetBytes(tag));
        if (kind == CharacterReferenceKind.MapName)
            return _mapReferences.TryGetValue(hash, out var textId)
                ? (_mapNames.GetValueOrDefault(locale) ?? _mapNames.GetValueOrDefault("en"))?.GetValueOrDefault(textId)
                : null;
        if (!_references.TryGetValue(hash, out var reference)) return null;
        var localizedNames = _overloadNames.GetValueOrDefault(reference.OverloadNameId)
            ?? _names.GetValueOrDefault(reference.CharacterId);
        if (localizedNames is null) return null;
        var names = localizedNames.GetValueOrDefault(locale)
            ?? localizedNames.GetValueOrDefault("en")
            ?? localizedNames.GetValueOrDefault("ja")
            ?? localizedNames.Values.FirstOrDefault();
        if (names is null) return null;
        return kind switch
        {
            CharacterReferenceKind.FirstName => names.GivenName ?? names.FullName,
            CharacterReferenceKind.LastName => names.FamilyName ?? names.FullName,
            CharacterReferenceKind.FullName => names.FullName,
            _ => null,
        };
    }

    public static CharacterTextReferenceIndex Load(
        string rootPath,
        IEnumerable<CharacterBaseMetadata> characters,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, CharacterLocalizedText>> localizations,
        CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("character_text_reference_load");
        var path = Directory.EnumerateFiles(
                Path.Combine(rootPath, "common", "gamedata", "character"),
                "chara_name_tag_*.cfg.bin")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("No character name-tag configuration was found.");
        cancellationToken.ThrowIfCancellationRequested();
        var document = RdbnpDocument.Read(File.ReadAllBytes(path));
        var list = document.Lists.FirstOrDefault(item => item.Name == "m_charaNameTagConfigList")
            ?? throw new InvalidDataException("RDBNP list 'm_charaNameTagConfigList' was not found.");
        var references = list.Rows.Select(row => new CharacterNameTagReference(
            row.GetUInt32("tagId"),
            row.GetUInt32("charaId"),
            row.GetUInt32("overloadNameId"))).ToArray();
        var names = new Dictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>>();
        foreach (var character in characters)
        {
            if (!localizations.TryGetValue(character.BaseId, out var localized)) continue;
            names[unchecked((uint)character.BaseId)] = localized.ToDictionary(
                pair => pair.Key,
                pair => new CharacterNameSet(
                    pair.Value.FullName,
                    pair.Value.FamilyName,
                    pair.Value.GivenName),
                StringComparer.OrdinalIgnoreCase);
        }
        var overloadNames = new Dictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>>();
        var overloadIds = references.Select(reference => reference.OverloadNameId).Where(id => id != 0).ToHashSet();
        foreach (var locale in GameLocaleCatalog.SupportedCharacterLocales)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var textPath = Path.Combine(rootPath, "common", "text", locale, "chara_text.cfg.bin");
            if (!File.Exists(textPath)) continue;
            var forms = CfgBinDocument.Read(File.ReadAllBytes(textPath)).Entries
                .Where(entry => entry.Name == "NOUN_INFO" && entry.Values.Count >= 6 && entry.Values[5].Value is string)
                .GroupBy(entry => unchecked((uint)Convert.ToInt64(entry.Values[0].Value)));
            foreach (var group in forms.Where(group => overloadIds.Contains(group.Key)))
            {
                var values = group.GroupBy(entry => Convert.ToInt32(entry.Values[1].Value))
                    .ToDictionary(form => form.Key, form => (string)form.First().Values[5].Value!);
                var localized = overloadNames.GetValueOrDefault(group.Key)?.ToDictionary()
                    ?? new Dictionary<string, CharacterNameSet>(StringComparer.OrdinalIgnoreCase);
                localized[locale] = new CharacterNameSet(values.GetValueOrDefault(0), values.GetValueOrDefault(11), values.GetValueOrDefault(12));
                overloadNames[group.Key] = localized;
            }
        }
        var mapReferences = new Dictionary<uint, uint>();
        var mapNames = new Dictionary<string, IReadOnlyDictionary<uint, string>>(StringComparer.OrdinalIgnoreCase);
        var mapDirectory = Path.Combine(rootPath, "common", "gamedata", "map");
        var mapTagPath = Directory.Exists(mapDirectory)
            ? Directory.EnumerateFiles(mapDirectory, "map_name_tag_*.cfg.bin").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (mapTagPath is not null)
        {
            var mapDocument = RdbnpDocument.Read(File.ReadAllBytes(mapTagPath));
            var mapList = mapDocument.Lists.FirstOrDefault(item => item.Name == "m_mapNameTagConfigList")
                ?? throw new InvalidDataException("RDBNP list 'm_mapNameTagConfigList' was not found.");
            foreach (var row in mapList.Rows)
                mapReferences.TryAdd(row.GetUInt32("tagId"), row.GetUInt32("mapTextId"));
            foreach (var locale in GameLocaleCatalog.SupportedCharacterLocales)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var textPath = Path.Combine(rootPath, "common", "text", locale, "map_text.cfg.bin");
                if (!File.Exists(textPath)) continue;
                mapNames[locale] = CfgBinDocument.Read(File.ReadAllBytes(textPath)).Entries
                    .Where(entry => entry.Name == "NOUN_INFO" && entry.Values.Count >= 6
                        && Convert.ToInt32(entry.Values[1].Value) == 0 && entry.Values[5].Value is string)
                    .GroupBy(entry => unchecked((uint)Convert.ToInt64(entry.Values[0].Value)))
                    .ToDictionary(group => group.Key, group => (string)group.First().Values[5].Value!);
            }
        }
        var result = new CharacterTextReferenceIndex(references.ToDictionary(reference => reference.TagId), names, overloadNames, mapReferences, mapNames);
        GlobalLog.Debug("character_text_reference_loaded", new Dictionary<string, object?>
        {
            ["referenceCount"] = references.Length,
            ["localizedCharacterCount"] = names.Count,
        });
        return result;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }
}
