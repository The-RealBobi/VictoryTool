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

    private CharacterTextReferenceIndex(
        IReadOnlyDictionary<uint, CharacterNameTagReference> references,
        IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> names)
    {
        _references = references;
        _names = names;
    }

    public static CharacterTextReferenceIndex Empty { get; } = Create([], new Dictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>>());

    public static CharacterTextReferenceIndex Create(
        IEnumerable<CharacterNameTagReference> references,
        IReadOnlyDictionary<uint, IReadOnlyDictionary<string, CharacterNameSet>> names) => new(
            references.ToDictionary(reference => reference.TagId),
            names);

    public string? Resolve(CharacterReferenceKind kind, string tag, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var hash = ComputeCrc32(Encoding.ASCII.GetBytes(tag));
        if (!_references.TryGetValue(hash, out var reference)
            || !_names.TryGetValue(reference.CharacterId, out var localizedNames))
            return null;
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
        var result = Create(references, names);
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
