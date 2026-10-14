using VictoryTool.CfgBin;
using VictoryTool.Application.Characters;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterMotionFamily(
    string Name, uint Crc32, string PackagePattern, string MotionTablePath, string SkeletonModelStem)
{
    public string ObjectConfigPath { get; init; } = string.Empty;
    public string SkeletonResourcePath { get; init; } = string.Empty;
    public string? BodyObjectConfigPath { get; init; }
    public string? BodySkeletonResourcePath { get; init; }
    public string? AnimeResourcePath { get; init; }
    public string? MotionEventResourcePath { get; init; }
}

public sealed class CharacterMotionFamilyResolver
{
    public IReadOnlyList<AnimationBankSource> ResolveBanks(
        string dumpRoot,
        CharacterMotionFamily family,
        CharacterAnimationProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpRoot);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(profile);
        var root = Path.GetFullPath(dumpRoot);
        var tablePath = Path.Combine(root, family.MotionTablePath.Replace('/', Path.DirectorySeparatorChar));
        var document = CfgBinDocument.Read(File.ReadAllBytes(tablePath));
        var motionRows = document.Entries.Where(entry => entry.Name == "CHARA_MOTION_INFO"
            && entry.Values.Count >= 2
            && unchecked((uint)ReadInteger(entry.Values[0])) == family.Crc32).ToArray();
        if (motionRows.Length != 1)
            throw new InvalidDataException($"Expected one CHARA_MOTION_INFO row for family '{family.Name}', found {motionRows.Length}.");

        var presetInfos = ReadCountedRows(document, "CHARA_MOTION_PRESET_INFO",
            "CHARA_MOTION_PRESET_INFO_LIST_BEG", "CHARA_MOTION_PRESET_INFO_LIST_END");
        var presetReferences = ReadCountedRows(document, "CHARA_MOTION_PRESET_INFO_REF_DATA",
            "CHARA_MOTION_PRESET_INFO_LIST_BEG", "CHARA_MOTION_PRESET_INFO_LIST_END");
        var presetData = ReadCountedRows(document, "CHARA_MOTION_PRESET_DATA",
            "CHARA_MOTION_PRESET_DATA_LIST_BEG", "CHARA_MOTION_PRESET_DATA_LIST_END");
        if (presetInfos.Count != presetReferences.Count)
            throw new InvalidDataException("CHARA_MOTION_PRESET_INFO and REF_DATA row counts differ.");

        var infoByKey = new Dictionary<uint, int>();
        for (var index = 0; index < presetInfos.Count; index++)
        {
            if (presetInfos[index].Values.Count == 0)
                throw new InvalidDataException($"CHARA_MOTION_PRESET_INFO row {index} has no key.");
            var key = unchecked((uint)ReadInteger(presetInfos[index].Values[0]));
            if (!infoByKey.TryAdd(key, index))
                throw new InvalidDataException($"CHARA_MOTION_PRESET_INFO key {key:X8} is duplicated.");
        }

        var referencedInfoRows = new HashSet<int>();
        foreach (var value in motionRows[0].Values.Skip(2))
        {
            if (!TryReadInteger(value, out var numeric)) continue;
            if (infoByKey.TryGetValue(unchecked((uint)numeric), out var rowIndex))
                referencedInfoRows.Add(rowIndex);
        }
        var familyKey = unchecked((uint)ReadInteger(motionRows[0].Values[0]));
        if (infoByKey.TryGetValue(familyKey, out var familyPresetIndex))
            referencedInfoRows.Add(familyPresetIndex);
        if (referencedInfoRows.Count == 0)
            throw new InvalidDataException($"CHARA_MOTION_INFO for '{family.Name}' references no motion preset rows.");

        var commonCharacterRoot = Path.Combine(root, "common", "chr");
        var banksByResourcePath = new Dictionary<string, AnimationBankSource>(StringComparer.Ordinal);
        var presetMarker = FindMarker(document, "CHARA_MOTION_PRESET_DATA_LIST_BEG");
        var declaredPresetCount = ReadInteger(presetMarker.Values[0]);
        if (declaredPresetCount != presetData.Count)
            throw new InvalidDataException($"CHARA_MOTION_PRESET_DATA declares {declaredPresetCount} rows but contains {presetData.Count}.");

        foreach (var infoIndex in referencedInfoRows.Order())
        {
            var reference = presetReferences[infoIndex];
            if (reference.Values.Count < 2)
                throw new InvalidDataException($"CHARA_MOTION_PRESET_INFO_REF_DATA row {infoIndex} has no range.");
            var start = ReadInteger(reference.Values[0]);
            var count = ReadInteger(reference.Values[1]);
            if (start < 0 || count <= 0 || start > presetData.Count - count)
                throw new InvalidDataException($"Motion preset range {start}+{count} is outside CHARA_MOTION_PRESET_DATA.");

            for (var index = checked((int)start); index < checked((int)(start + count)); index++)
            {
                var value = presetData[index].Values.FirstOrDefault();
                var resourcePath = value?.Value as string
                    ?? throw new InvalidDataException($"CHARA_MOTION_PRESET_DATA row {index} has no resource path.");
                resourcePath = NormalizeResourcePath(resourcePath);
                var fullPath = ResolveMotionBankPath(commonCharacterRoot, family.PackagePattern, resourcePath);
                if (fullPath is null)
                    throw new FileNotFoundException($"Referenced motion bank '{resourcePath}' was not found under common/chr.", fullPath);
                var type = ReadBankType(resourcePath);
                var fileStem = Path.GetFileNameWithoutExtension(resourcePath);
                var directoryName = Path.GetFileName(Path.GetDirectoryName(resourcePath));
                var primaryFamily = string.Equals(directoryName, family.Name, StringComparison.Ordinal)
                    && string.Equals(fileStem, $"{family.Name}_p{type}", StringComparison.Ordinal);
                var bank = new AnimationBankSource(fullPath, type, resourcePath, primaryFamily);
                if (banksByResourcePath.TryGetValue(resourcePath, out var existing))
                {
                    if (existing.Type != bank.Type || existing.IsPrimaryFamily != bank.IsPrimaryFamily)
                        throw new InvalidDataException($"Motion resource '{resourcePath}' resolves to conflicting routes.");
                    continue;
                }
                banksByResourcePath.Add(resourcePath, bank);
            }
        }
        return banksByResourcePath.Values
            .OrderBy(bank => bank.ResourcePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ResolveMotionBankPath(string commonCharacterRoot, string packagePattern, string resourcePath)
    {
        var relativePath = resourcePath.Replace('/', Path.DirectorySeparatorChar);
        var exactPath = Path.GetFullPath(Path.Combine(commonCharacterRoot, relativePath));
        var rootPrefix = commonCharacterRoot + Path.DirectorySeparatorChar;
        if (exactPath.StartsWith(rootPrefix, StringComparison.Ordinal) && File.Exists(exactPath))
            return exactPath;

        var patternDirectory = packagePattern.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        var pathParts = resourcePath.Split('/');
        if (pathParts.Length == 0 || patternDirectory != $"_{pathParts[0]}") return null;

        pathParts[0] = patternDirectory;
        var aliasedPath = Path.GetFullPath(Path.Combine(commonCharacterRoot,
            string.Join(Path.DirectorySeparatorChar, pathParts)));
        return aliasedPath.StartsWith(rootPrefix, StringComparison.Ordinal) && File.Exists(aliasedPath)
            ? aliasedPath
            : null;
    }

    public CharacterMotionFamily Resolve(string dumpRoot, string sourceModelStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModelStem);
        var root = Path.GetFullPath(dumpRoot);
        var characterRoot = Path.Combine(root, "common", "gamedata", "character");
        var baseRow = FindRow(characterRoot, "chara_base_*.cfg.bin", "CHARA_BASE_INFO",
            row => ReadString(row, 1) == sourceModelStem, 34);
        var modelId = ReadInt(baseRow, 6);
        var modelRows = ReadRows(characterRoot, "chara_model_*.cfg.bin");
        var modelRow = FindSingle(modelRows, "CHARA_MODEL_INFO", row => ReadInt(row, 0) == modelId, 32);
        var bodyId = ReadInt(modelRow, 4);
        var bodyRow = FindSingle(modelRows, "CHARA_BODY_INFO", row => ReadInt(row, 0) == bodyId, 7);
        var bodyObjectConfigPath = TryReadString(bodyRow.Values[1], out var bodyObjectPath)
            ? NormalizeCharacterResourcePath(bodyObjectPath, ".objbin")
            : null;
        string? bodySkeletonPath = null;
        if (bodyObjectConfigPath is not null)
        {
            var bodyObjectConfigFile = Path.GetFullPath(Path.Combine(root,
                bodyObjectConfigPath.Replace('/', Path.DirectorySeparatorChar)));
            var commonCharacterRoot = Path.GetFullPath(Path.Combine(root, "common", "chr"));
            if (!bodyObjectConfigFile.StartsWith(commonCharacterRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Body object config '{bodyObjectConfigPath}' is outside common/chr.");
            if (File.Exists(bodyObjectConfigFile))
            {
                var bodyObjectConfig = CfgBinDocument.Read(File.ReadAllBytes(bodyObjectConfigFile));
                bodySkeletonPath = ReadSetupResource(bodyObjectConfig, "Skeleton");
            }
        }
        var modelObjectValue = modelRow.Values[1];
        var objectConfigPath = TryReadString(modelObjectValue, out var modelObjectPath)
            ? modelObjectPath
            : IsZeroInteger(modelObjectValue)
                ? ReadString(bodyRow, 1)
                : throw new InvalidDataException($"CHARA_MODEL_INFO[{modelRow.Index}].value[1] is neither an object path nor a zero fallback.");
        objectConfigPath = NormalizeCharacterResourcePath(objectConfigPath, ".objbin");
        var objectConfigFile = ResolveCharacterResource(root, objectConfigPath);
        var objectConfig = CfgBinDocument.Read(File.ReadAllBytes(objectConfigFile));
        var skeletonPath = ReadSetupResource(objectConfig, "Skeleton");
        var skeletonModelStem = Path.GetFileNameWithoutExtension(skeletonPath);
        if (string.IsNullOrWhiteSpace(skeletonModelStem))
            throw new InvalidDataException($"OBJBIN for '{sourceModelStem}' has an invalid Skeleton resource path.");
        var animePath = ReadOptionalSetupResource(objectConfig, "Anime");
        var motionEventPath = ReadOptionalSetupResource(objectConfig, "MotEvent");
        var familyCrc = unchecked((uint)ReadInt(bodyRow, 3));
        var motionFiles = Directory.EnumerateFiles(characterRoot, "chara_motion_*.cfg.bin", SearchOption.TopDirectoryOnly).ToArray();
        var motionMatches = motionFiles.SelectMany(path => CfgBinDocument.Read(File.ReadAllBytes(path)).Entries
                .Where(row => row.Name == "CHARA_MOTION_INFO" && row.Values.Count >= 2
                    && unchecked((uint)ReadInt(row, 0)) == familyCrc)
                .Select(row => (Path: path, Row: row)))
            .ToArray();
        if (motionMatches.Length != 1)
            throw new InvalidDataException($"Expected exactly one CHARA_MOTION_INFO row, found {motionMatches.Length}.");
        var packagePattern = ReadString(motionMatches[0].Row, 1);
        const string token = "<TYPE>";
        var tokenIndex = packagePattern.IndexOf(token, StringComparison.Ordinal);
        if (tokenIndex <= 0 || !packagePattern.EndsWith(".g4pk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Motion family path is not a supported G4PK pattern: '{packagePattern}'.");
        var relativePrefix = packagePattern[..tokenIndex];
        var familyName = relativePrefix.TrimEnd('/').Split('/').Last();
        if (familyName.EndsWith("_p", StringComparison.Ordinal)) familyName = familyName[..^2];
        if (string.IsNullOrWhiteSpace(familyName) || relativePrefix.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Motion family path contains an invalid family name.");
        return new CharacterMotionFamily(familyName, familyCrc, packagePattern,
            Path.GetRelativePath(root, motionMatches[0].Path).Replace('\\', '/'), skeletonModelStem)
        {
            ObjectConfigPath = objectConfigPath,
            SkeletonResourcePath = skeletonPath,
            BodyObjectConfigPath = bodyObjectConfigPath,
            BodySkeletonResourcePath = bodySkeletonPath,
            AnimeResourcePath = animePath,
            MotionEventResourcePath = motionEventPath,
        };
    }

    private static IReadOnlyList<CfgBinEntry> ReadCountedRows(
        CfgBinDocument document, string rowName, string beginName, string endName)
    {
        var begin = FindMarker(document, beginName);
        var end = document.Entries.SingleOrDefault(entry => entry.Name == endName && entry.Index > begin.Index)
            ?? throw new InvalidDataException($"{endName} is missing.");
        var rows = document.Entries.Skip(begin.Index + 1).Take(end.Index - begin.Index - 1)
            .Where(entry => entry.Name == rowName).ToArray();
        if (begin.Values.Count == 0 || ReadInteger(begin.Values[0]) != rows.Length)
            throw new InvalidDataException($"{beginName} does not match the {rowName} row count.");
        return rows;
    }

    private static CfgBinEntry FindMarker(CfgBinDocument document, string name) =>
        document.Entries.SingleOrDefault(entry => entry.Name == name)
        ?? throw new InvalidDataException($"{name} is missing.");

    private static string NormalizeResourcePath(string value)
    {
        var path = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Split('/').Any(segment => segment is "" or "." or "..")
            || !path.EndsWith(".g4pk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Motion bank path '{value}' is not a safe common/chr-relative G4PK path.");
        return path;
    }

    private static string NormalizeCharacterResourcePath(string value, string extension)
    {
        var path = value.Replace('\\', '/');
        if (path.StartsWith("common/chr/", StringComparison.Ordinal))
            path = path["common/chr/".Length..];
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Split('/').Any(segment => segment is "" or "." or "..")
            || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Character resource path '{value}' is not a safe {extension} path.");
        return $"common/chr/{path}";
    }

    private static string ResolveCharacterResource(string root, string resourcePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root,
            resourcePath.Replace('/', Path.DirectorySeparatorChar)));
        var commonCharacterRoot = Path.GetFullPath(Path.Combine(root, "common", "chr"));
        if (!fullPath.StartsWith(commonCharacterRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(fullPath))
            throw new FileNotFoundException($"Character object config '{resourcePath}' was not found under common/chr.", fullPath);
        return fullPath;
    }

    private static string ReadSetupResource(CfgBinDocument objectConfig, string parameter) =>
        ReadSetupResourceValue(objectConfig, parameter)
        ?? throw new InvalidDataException($"OBJBIN has no Skeleton resource.");

    private static string? ReadOptionalSetupResource(CfgBinDocument objectConfig, string parameter) =>
        ReadSetupResourceValue(objectConfig, parameter);

    private static string? ReadSetupResourceValue(CfgBinDocument objectConfig, string parameter)
    {
        var matches = objectConfig.Entries.Where(entry => entry.Name == "SETUP_PARAM"
            && entry.Values.Count >= 2 && Equals(entry.Values[0].Value, parameter)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].Values[1].Value is not string resourcePath)
            throw new InvalidDataException($"OBJBIN has no unique string SETUP_PARAM '{parameter}'.");
        var extension = parameter switch
        {
            "Skeleton" => ".g4sk",
            "Anime" => ".g4pk",
            "MotEvent" => ".mevbin",
            _ => throw new InvalidDataException($"Unsupported object setup resource '{parameter}'."),
        };
        return NormalizeCharacterResourcePath(resourcePath, extension);
    }

    private static bool TryReadString(CfgBinValue value, out string result)
    {
        if (value.Type == CfgBinValueType.String && value.Value is string text && !string.IsNullOrWhiteSpace(text))
        {
            result = text;
            return true;
        }
        result = string.Empty;
        return false;
    }

    private static bool IsZeroInteger(CfgBinValue value) => value.Type == CfgBinValueType.Integer
        && value.Value switch
        {
            int number => number == 0,
            long number => number == 0,
            _ => false,
        };

    private static string ReadBankType(string resourcePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(resourcePath);
        var marker = fileName.LastIndexOf("_p", StringComparison.Ordinal);
        if (marker <= 0) throw new InvalidDataException($"Motion bank path '{resourcePath}' has no _p<type> suffix.");
        var type = fileName[(marker + 2)..];
        if (type.Length == 0 || type.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidDataException($"Motion bank path '{resourcePath}' has an invalid bank type.");
        return type;
    }

    private static CfgBinEntry FindRow(string root, string pattern, string entryName, Func<CfgBinEntry, bool> predicate, int fieldCount) =>
        FindSingle(ReadRows(root, pattern), entryName, predicate, fieldCount);

    private static IReadOnlyList<CfgBinEntry> ReadRows(string root, string pattern)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Character configuration directory not found: {root}");
        return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
            .SelectMany(path => CfgBinDocument.Read(File.ReadAllBytes(path)).Entries)
            .ToArray();
    }

    private static CfgBinEntry FindSingle(IReadOnlyList<CfgBinEntry> rows, string name, Func<CfgBinEntry, bool> predicate, int fieldCount)
    {
        var matches = rows.Where(row => row.Name == name && row.Values.Count >= fieldCount && predicate(row)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Expected exactly one {name} row, found {matches.Length}.");
    }

    private static int ReadInt(CfgBinEntry row, int index) => row.Values[index].Value switch
    {
        int value => value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        _ => throw new InvalidDataException($"{row.Name}[{index}] is not a 32-bit integer."),
    };

    private static long ReadInteger(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => throw new InvalidDataException("A motion configuration key or index is not an integer."),
    };

    private static bool TryReadInteger(CfgBinValue value, out long number)
    {
        if (value.Value is int integer) { number = integer; return true; }
        if (value.Value is long longInteger) { number = longInteger; return true; }
        number = 0;
        return false;
    }

    private static string ReadString(CfgBinEntry row, int index) => row.Values[index].Value as string
        ?? throw new InvalidDataException($"{row.Name}[{index}] is not a string.");
}
