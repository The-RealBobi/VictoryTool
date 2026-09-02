using System.Text.RegularExpressions;
using VictoryTool.Application.Diagnostics;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Characters;

public sealed record TaxonomyTextReference(uint Id, byte Type, uint NameTextId);
public sealed record SkillTextReference(uint SkillId, uint NameTextId, bool IsAuraCommand = false);
public sealed record LocalizedSkill(uint Id, string Name, bool IsAuraCommand = false)
{
    public override string ToString() => Name;
}

public enum EquipmentCategory
{
    Uniform,
    Shoes,
    Gloves,
}

public sealed record EquipmentTextReference(
    int Id,
    EquipmentCategory Category,
    uint NameTextId,
    string ResourceKey);

public sealed record TeamTextReference(
    uint Id,
    uint NameTextId,
    uint TeamKitIe = 0,
    uint TeamKitGo = 0,
    uint TeamKitAreOri = 0,
    uint TeamKitV = 0);

public sealed record LocalizedTeam(
    int Id,
    string Name,
    uint TeamKitIe = 0,
    uint TeamKitGo = 0,
    uint TeamKitAreOri = 0,
    uint TeamKitV = 0)
{
    public override string ToString() => Name;
}

public sealed record LocalizedEquipment(
    int Id,
    EquipmentCategory Category,
    string Name,
    string ResourceKey)
{
    public override string ToString() => Name;
}

internal sealed record TaxonomyTextLoadResult(
    IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> Texts,
    int RejectedFileCount);

public sealed class GameTaxonomyIndex
{
    private static readonly Regex EquipmentTagPattern = new(
        "<(?<kind>[A-Z]+):(?<tag>[A-Z0-9_]+)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EquipmentMetadataPattern = new(
        "\\s*\\[\\$[^\\]]+\\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<uint, TaxonomyTextReference> _series;
    private readonly IReadOnlyDictionary<uint, TaxonomyTextReference> _academicYears;
    private readonly IReadOnlyDictionary<uint, SkillTextReference> _skills;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> _texts;
    private readonly IReadOnlyList<EquipmentTextReference> _equipment;
    private readonly IReadOnlyList<TeamTextReference> _teams;

    private GameTaxonomyIndex(
        IReadOnlyDictionary<uint, TaxonomyTextReference> series,
        IReadOnlyDictionary<uint, TaxonomyTextReference> academicYears,
        IReadOnlyDictionary<uint, SkillTextReference> skills,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> texts,
        IReadOnlyList<EquipmentTextReference> equipment,
        IReadOnlyList<TeamTextReference> teams)
    {
        _series = series;
        _academicYears = academicYears;
        _skills = skills;
        _texts = texts;
        _equipment = equipment;
        _teams = teams;
    }

    public static GameTaxonomyIndex Empty { get; } = Create([], [], [],
        new Dictionary<string, IReadOnlyDictionary<uint, string>>(), [], []);

    public static GameTaxonomyIndex Create(
        IEnumerable<TaxonomyTextReference> series,
        IEnumerable<TaxonomyTextReference> academicYears,
        IEnumerable<SkillTextReference> skills,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, string>> texts,
        IEnumerable<EquipmentTextReference>? equipment = null,
        IEnumerable<TeamTextReference>? teams = null) => new(
            series.ToDictionary(item => item.Id),
            academicYears.ToDictionary(item => item.Id),
            skills.ToDictionary(item => item.SkillId),
            texts,
            equipment?.ToArray() ?? [],
            teams?.ToArray() ?? []);

    public string ResolveSeries(uint id, string locale) =>
        _series.TryGetValue(id, out var item) && ResolveText(item.NameTextId, locale) is { } text
            ? text
            : $"Unknown series (0x{id:X8})";

    public string ResolveAcademicYear(uint id, string locale)
    {
        if (id == 0)
            return locale.Equals("es", StringComparison.OrdinalIgnoreCase) ? "Desconocido" : "Unknown";
        if (!_academicYears.TryGetValue(id, out var item))
            return $"Unknown academic year (0x{id:X8})";
        if (ResolveText(item.NameTextId, locale) is { } text)
            return text;
        return ResolveAcademicType(item.Type, locale)
            ?? $"Unknown academic year (0x{id:X8})";
    }

    public string ResolveSkill(uint id, string locale) =>
        _skills.TryGetValue(id, out var item) && ResolveText(item.NameTextId, locale) is { } text
            ? text
            : $"Unknown skill (0x{id:X8})";

    public IReadOnlyList<LocalizedSkill> GetSkills(string locale) => _skills.Keys
        .OrderBy(id => ResolveSkill(id, locale), StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(id => id)
        .Select(id => new LocalizedSkill(id, ResolveSkill(id, locale), _skills[id].IsAuraCommand))
        .ToArray();

    public IReadOnlyList<LocalizedEquipment> GetEquipment(string locale, EquipmentCategory category) =>
        _equipment
            .Where(item => item.Category == category)
            .Select(item => (Item: item, Name: ResolveEquipmentName(item, locale)))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Item.ResourceKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Item.Id)
            .Select(item => new LocalizedEquipment(
                item.Item.Id,
                item.Item.Category,
                item.Name,
                item.Item.ResourceKey))
            .ToArray();

    public IReadOnlyList<LocalizedTeam> GetTeams(string locale) => _teams
        .Select(item => (Item: item, Name: ResolveTeamName(item, locale)))
        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.Item.Id)
        .Select(item => new LocalizedTeam(
            unchecked((int)item.Item.Id),
            item.Name,
            item.Item.TeamKitIe,
            item.Item.TeamKitGo,
            item.Item.TeamKitAreOri,
            item.Item.TeamKitV))
        .ToArray();

    private string ResolveTeamName(TeamTextReference item, string locale)
    {
        var text = item.NameTextId != 0 ? ResolveText(item.NameTextId, locale) : null;
        if (string.IsNullOrWhiteSpace(text))
            return locale.Equals("es", StringComparison.OrdinalIgnoreCase)
                ? $"Equipo (0x{item.Id:X8})"
                : $"Team (0x{item.Id:X8})";
        return CleanEquipmentTags(text);
    }

    private string ResolveEquipmentName(EquipmentTextReference item, string locale)
    {
        var text = item.NameTextId != 0
            ? ResolveText(item.NameTextId, locale)
            : null;
        if (string.IsNullOrWhiteSpace(text))
            text = null;

        var category = item.Category switch
        {
            EquipmentCategory.Uniform => locale.Equals("es", StringComparison.OrdinalIgnoreCase)
                ? "Uniforme"
                : "Uniform",
            EquipmentCategory.Shoes => locale.Equals("es", StringComparison.OrdinalIgnoreCase)
                ? "Zapatillas"
                : "Shoes",
            _ => locale.Equals("es", StringComparison.OrdinalIgnoreCase)
                ? "Guantes"
                : "Gloves",
        };
        var name = text is null
            ? $"{category} ({item.ResourceKey})"
            : CleanEquipmentTags(text);
        return $"{name} {ResolveEquipmentVariantSuffix(item.ResourceKey, locale)}".TrimEnd();
    }

    private static string CleanEquipmentTags(string value)
    {
        var cleaned = EquipmentTagPattern.Replace(value, match =>
        {
            var tag = match.Groups["tag"].Value;
            var words = tag.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(HumanizeEquipmentTag)
                .ToArray();
            return words.Length == 0 ? string.Empty : string.Join(' ', words);
        });
        return EquipmentMetadataPattern.Replace(cleaned, string.Empty).Trim();
    }

    private static string HumanizeEquipmentTag(string value) => value.Length == 0
        ? value
        : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static string ResolveEquipmentVariantSuffix(string resourceKey, string locale)
    {
        var separator = resourceKey.LastIndexOf('_');
        if (separator < 0 || separator == resourceKey.Length - 1
            || !int.TryParse(resourceKey[(separator + 1)..], out var variant))
            return string.Empty;

        var spanish = locale.Equals("es", StringComparison.OrdinalIgnoreCase);
        return variant switch
        {
            10 => spanish ? "(Local)" : "(Home)",
            20 => spanish ? "(Visitante)" : "(Away)",
            _ => spanish ? $"(Variante {variant})" : $"(Variant {variant})",
        };
    }

    private string? ResolveText(uint id, string locale)
    {
        foreach (var candidate in new[] { locale, "en", "ja" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_texts.TryGetValue(candidate, out var texts) && texts.TryGetValue(id, out var text))
                return text;
        }
        return _texts.Values.Select(map => map.GetValueOrDefault(id)).FirstOrDefault(value => value is not null);
    }

    private static string? ResolveAcademicType(byte type, string locale)
    {
        var spanish = locale.Equals("es", StringComparison.OrdinalIgnoreCase);
        return (type, spanish) switch
        {
            (1, true) => "Primer curso",
            (2, true) => "Segundo curso",
            (3, true) => "Tercer curso",
            (1, false) => "First year",
            (2, false) => "Second year",
            (3, false) => "Third year",
            _ => null,
        };
    }
}

public static class GameTaxonomyLoader
{
    public static GameTaxonomyIndex Load(string rootPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        using var operation = GlobalLog.BeginOperation("taxonomy_load");
        var characterRoot = Path.Combine(rootPath, "common", "gamedata", "character");
        var seriesDocument = ReadRdbnp(Path.Combine(characterRoot, "chara_series_config.cfg.bin"), cancellationToken);
        var academicDocument = ReadRdbnp(Path.Combine(characterRoot, "academic_year_config.cfg.bin"), cancellationToken);
        var skillDocument = ReadLargestSkillDocument(
            Path.Combine(rootPath, "common", "gamedata", "skill"), cancellationToken);

        var series = ReadTaxonomy(seriesDocument, "m_charaSeriesInfoList",
            "charaSeriesId", "charaSeriesType", "charaSeriesNameTextId");
        var academicYears = ReadTaxonomy(academicDocument, "m_academicYearInfoList",
            "academicYearId", "academicYearType", "academicYearNameTextId");
        var skills = ReadSkills(skillDocument).ToDictionary(item => item.SkillId);
        foreach (var skill in ReadAuraCommands(
                     Path.Combine(rootPath, "common", "gamedata", "skill"), cancellationToken))
            skills.TryAdd(skill.SkillId, skill);
        var textLoad = ReadTexts(Path.Combine(rootPath, "common", "text"), cancellationToken);
        var texts = textLoad.Texts;
        var equipment = ReadEquipment(rootPath, cancellationToken);
        var teams = ReadTeams(rootPath, cancellationToken);
        var result = GameTaxonomyIndex.Create(series, academicYears, skills.Values, texts, equipment, teams);
        GlobalLog.Info("taxonomy_loaded", new Dictionary<string, object?>
        {
            ["seriesCount"] = series.Count,
            ["academicYearCount"] = academicYears.Count,
            ["skillCount"] = skills.Count,
            ["localeCount"] = texts.Count,
            ["rejectedTextFileCount"] = textLoad.RejectedFileCount,
            ["equipmentCount"] = equipment.Count,
            ["teamCount"] = teams.Count,
        });
        return result;
    }

    private static RdbnpDocument ReadRdbnp(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = RdbnpDocument.Read(File.ReadAllBytes(path));
        GlobalLog.Debug("rdbnp_table_loaded", new Dictionary<string, object?>
        {
            ["listCount"] = document.Lists.Count,
        });
        return document;
    }

    private static RdbnpDocument ReadLargestSkillDocument(string directory, CancellationToken cancellationToken)
    {
        RdbnpDocument? selected = null;
        var largestCount = -1;
        foreach (var path in Directory.EnumerateFiles(directory, "skill_config*.cfg.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ReadRdbnp(path, cancellationToken);
            var count = document.Lists.FirstOrDefault(list => list.Name == "m_skillInfoList")?.Rows.Count ?? 0;
            if (count <= largestCount) continue;
            selected = document;
            largestCount = count;
        }
        return selected ?? throw new InvalidDataException("No readable RDBNP skill configuration was found.");
    }

    private static IReadOnlyList<TaxonomyTextReference> ReadTaxonomy(
        RdbnpDocument document,
        string listName,
        string idField,
        string typeField,
        string textField)
    {
        var list = document.Lists.FirstOrDefault(item => item.Name == listName)
            ?? throw new InvalidDataException($"RDBNP list '{listName}' was not found.");
        return list.Rows.Select(row => new TaxonomyTextReference(
            row.GetUInt32(idField), row.GetByte(typeField), row.GetUInt32(textField))).ToArray();
    }

    private static IReadOnlyList<SkillTextReference> ReadSkills(RdbnpDocument document)
    {
        var list = document.Lists.FirstOrDefault(item => item.Name == "m_skillInfoList")
            ?? throw new InvalidDataException("RDBNP list 'm_skillInfoList' was not found.");
        return list.Rows.Select(row => new SkillTextReference(
            row.GetUInt32("skillID"), row.GetUInt32("skillNameId"))).ToArray();
    }

    private static IReadOnlyList<SkillTextReference> ReadAuraCommands(
        string directory,
        CancellationToken cancellationToken)
    {
        CfgBinDocument? selected = null;
        var largestCount = -1;
        foreach (var path in Directory.EnumerateFiles(directory, "aura_skill_config*.cfg.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CfgBinDocument document;
            try
            {
                document = CfgBinDocument.Read(File.ReadAllBytes(path));
            }
            catch (InvalidDataException)
            {
                continue;
            }

            var count = document.Entries.Count(entry => entry.Name == "AURA_CMD_INFO");
            if (count <= largestCount) continue;
            selected = document;
            largestCount = count;
        }

        if (selected is null) return [];
        var result = new List<SkillTextReference>();
        foreach (var entry in selected.Entries.Where(entry =>
                     entry.Name == "AURA_CMD_INFO" && entry.Values.Count > 2))
        {
            if (!TryGetUInt32(entry.Values[0].Value, out var id)
                || !TryGetUInt32(entry.Values[2].Value, out var nameTextId))
                continue;
            result.Add(new SkillTextReference(id, nameTextId, IsAuraCommand: true));
        }
        return result;
    }

    internal static TaxonomyTextLoadResult ReadTexts(
        string textRoot,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<uint, string>>(StringComparer.OrdinalIgnoreCase);
        var rejectedFileCount = 0;
        foreach (var localeDirectory in Directory.EnumerateDirectories(textRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var texts = new Dictionary<uint, string>();
            foreach (var fileName in new[] { "chara_add_info_text.cfg.bin", "skill_text.cfg.bin", "item_text.cfg.bin", "team_text.cfg.bin" })
            {
                var path = Path.Combine(localeDirectory, fileName);
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var entry in CfgBinDocument.Read(File.ReadAllBytes(path)).Entries)
                    {
                        if (entry.Name != "NOUN_INFO" || entry.Values.Count < 6
                            || !TryGetUInt32(entry.Values[0].Value, out var id)
                            || !TryGetInt32(entry.Values[1].Value, out var form) || form != 0
                            || entry.Values[5].Value is not string text)
                            continue;
                        texts.TryAdd(id, text);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    rejectedFileCount++;
                    GlobalLog.Warn("taxonomy_text_file_rejected", new Dictionary<string, object?>
                    {
                        ["locale"] = Path.GetFileName(localeDirectory),
                        ["fileName"] = fileName,
                    }, exception);
                }
            }
            result[Path.GetFileName(localeDirectory)] = texts;
        }
        GlobalLog.Info("taxonomy_texts_loaded", new Dictionary<string, object?>
        {
            ["localeCount"] = result.Count,
            ["textCount"] = result.Values.Sum(texts => texts.Count),
            ["rejectedFileCount"] = rejectedFileCount,
        });
        return new TaxonomyTextLoadResult(result, rejectedFileCount);
    }

    private static IReadOnlyList<TeamTextReference> ReadTeams(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var characterDirectory = Path.Combine(rootPath, "common", "gamedata", "character");
        var path = Directory.Exists(characterDirectory)
            ? Directory.EnumerateFiles(characterDirectory, "belong_team_config*.cfg.bin")
                .OrderByDescending(candidate => new FileInfo(candidate).Length)
                .FirstOrDefault()
            : null;
        if (path is null) return [];

        try
        {
            var document = RdbnpDocument.Read(File.ReadAllBytes(path));
            var list = document.Lists.FirstOrDefault(item => item.Name == "m_belongTeamInfoList");
            if (list is null) return [];
            var result = new List<TeamTextReference>(list.Rows.Count);
            foreach (var row in list.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = row.GetUInt32("belongTeamId");
                var nameTextId = row.GetUInt32("teamNameTextId");
                result.Add(new TeamTextReference(
                    id,
                    nameTextId,
                    row.GetUInt32("teamKit_IE"),
                    row.GetUInt32("teamKit_GO"),
                    row.GetUInt32("teamKit_AREORI"),
                    row.GetUInt32("teamKit_V")));
            }
            return result
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .ToArray();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<EquipmentTextReference> ReadEquipment(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var characterDirectory = Path.Combine(rootPath, "common", "gamedata", "character");
        var partsPath = Directory.Exists(characterDirectory)
            ? Directory.EnumerateFiles(characterDirectory, "chara_parts*.cfg.bin")
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault()
            : null;
        if (partsPath is null) return [];

        CfgBinDocument parts;
        try
        {
            parts = CfgBinDocument.Read(File.ReadAllBytes(partsPath));
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var references = new Dictionary<(EquipmentCategory Category, int Id), string>();
        AddModelStems(parts, "CHARA_PARTS_CLOTHES_MODEL", "CHARA_PARTS_CLOTHES_INFO", EquipmentCategory.Uniform, references, cancellationToken);
        AddModelStems(parts, "CHARA_PARTS_SHOES_MODEL", "CHARA_PARTS_SHOES_INFO", EquipmentCategory.Shoes, references, cancellationToken);
        AddModelStems(parts, "CHARA_PARTS_GLOVE_MODEL", "CHARA_PARTS_GLOVE_INFO", EquipmentCategory.Gloves, references, cancellationToken);
        if (references.Count == 0) return [];

        var itemDirectory = Path.Combine(rootPath, "common", "gamedata", "item");
        var itemPath = Directory.Exists(itemDirectory)
            ? Directory.EnumerateFiles(itemDirectory, "item_config*.cfg.bin")
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault()
            : null;
        var itemNames = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        if (itemPath is not null)
        {
            try
            {
                var itemDocument = CfgBinDocument.Read(File.ReadAllBytes(itemPath));
                foreach (var entry in itemDocument.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var category = entry.Name switch
                    {
                        "ITEM_FASHION_INFO" => EquipmentCategory.Uniform,
                        "ITEM_SHOES_INFO" => EquipmentCategory.Shoes,
                        _ => (EquipmentCategory?)null,
                    };
                    if (category is null || entry.Values.Count <= 11
                        || entry.Values[11].Value is not string key
                        || !TryGetUInt32(entry.Values[2].Value, out var nameTextId))
                        continue;
                    itemNames[key] = nameTextId;
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                itemNames.Clear();
            }
        }

        var result = new List<EquipmentTextReference>(references.Count);
        foreach (var ((category, id), resourceKey) in references)
        {
            var itemKeys = category switch
            {
                EquipmentCategory.Uniform => BuildUniformItemKeys(resourceKey),
                EquipmentCategory.Shoes => BuildShoesItemKeys(resourceKey),
                _ => [],
            };
            var nameTextId = itemKeys
                .Select(itemNames.GetValueOrDefault)
                .FirstOrDefault(value => value != 0);
            result.Add(new EquipmentTextReference(
                id,
                category,
                nameTextId,
                resourceKey));
        }
        return result;
    }

    private static void AddModelStems(
        CfgBinDocument document,
        string modelName,
        string infoName,
        EquipmentCategory category,
        IDictionary<(EquipmentCategory Category, int Id), string> references,
        CancellationToken cancellationToken)
    {
        var modelIds = document.Entries
            .Where(entry => entry.Name == modelName && entry.Values.Count > 0)
            .Select(entry => TryGetInt32(entry.Values[0].Value, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
        foreach (var entry in document.Entries.Where(entry => entry.Name == infoName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var value in entry.Values)
            {
                if (value.Value is not string path || !path.EndsWith(".g4tx", StringComparison.OrdinalIgnoreCase)) continue;
                var stem = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(stem) || !TryGetCrc32(stem, out var id)
                    || !modelIds.Contains(id)) continue;
                references.TryAdd((category, id), stem);
            }
        }
    }

    private static IReadOnlyList<string> BuildUniformItemKeys(string resourceKey)
    {
        if (resourceKey.Length < 2 || resourceKey[0] != 'u') return [];
        var separator = resourceKey.IndexOf('_');
        if (separator <= 1) return [$"uni_{resourceKey}"];
        return [$"uni_{resourceKey}", $"uni_{resourceKey[..separator]}"];
    }

    private static IReadOnlyList<string> BuildShoesItemKeys(string resourceKey)
    {
        if (resourceKey.Length < 2 || resourceKey[0] != 's') return [];
        var separator = resourceKey.IndexOf('_');
        if (separator <= 1) return [$"eq_sh{resourceKey[1..]}"];
        return [$"eq_sh{resourceKey[1..]}", $"eq_sh{resourceKey[1..separator]}"];
    }

    private static bool TryGetCrc32(string value, out int id)
    {
        var crc = uint.MaxValue;
        foreach (var item in System.Text.Encoding.UTF8.GetBytes(value))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        id = unchecked((int)~crc);
        return true;
    }

    private static bool TryGetUInt32(object? value, out uint result)
    {
        switch (value)
        {
            case int int32:
                result = unchecked((uint)int32);
                return true;
            case long int64:
                result = unchecked((uint)int64);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetInt32(object? value, out int result)
    {
        switch (value)
        {
            case int int32:
                result = int32;
                return true;
            case long int64 when int64 is >= int.MinValue and <= int.MaxValue:
                result = (int)int64;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
