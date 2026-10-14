using System.Text;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed class CharacterMotionInfoT2bWriter
{
    public byte[] Append(
        ReadOnlySpan<byte> table,
        uint sourceFamilyCrc32,
        string familyName,
        IReadOnlyList<AnimationBankSource> banks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        ArgumentNullException.ThrowIfNull(banks);
        if (familyName.Contains('/') || familyName.Contains('\\') || familyName.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("The generated motion family name is invalid.", nameof(familyName));
        if (banks.Count == 0) throw new InvalidDataException("No source motion banks were resolved.");

        var routeBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var destinationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bank in banks)
        {
            var sourcePath = NormalizeResourcePath(bank.ResourcePath);
            var destinationPath = NormalizeResourcePath(bank.DestinationResourcePath
                ?? throw new InvalidDataException($"Motion bank '{sourcePath}' has no output path."));
            if (!routeBySource.TryAdd(sourcePath, destinationPath)
                && !string.Equals(routeBySource[sourcePath], destinationPath, StringComparison.Ordinal))
                throw new InvalidDataException($"Motion resource '{sourcePath}' maps to multiple output paths.");
            if (!destinationPaths.Add(destinationPath))
                throw new InvalidDataException($"Two animation banks map to the same output path '{destinationPath}'.");
        }

        var document = CfgBinDocument.Read(table);
        var familyCrc = Crc32(familyName);
        if (document.Entries.Any(entry => entry.Name == "CHARA_MOTION_INFO" && entry.Values.Count >= 2
                && unchecked((uint)GetInteger(entry.Values[0])) == familyCrc))
            throw new InvalidDataException($"Motion family CRC {familyCrc} is already registered.");
        var sourceMotionRows = document.Entries.Where(entry => entry.Name == "CHARA_MOTION_INFO"
            && entry.Values.Count >= 2
            && unchecked((uint)GetInteger(entry.Values[0])) == sourceFamilyCrc32).ToArray();
        if (sourceMotionRows.Length != 1)
            throw new InvalidDataException($"Expected one source motion row, found {sourceMotionRows.Length}.");
        var sourceMotion = sourceMotionRows[0];
        if (sourceMotion.Values[1].Type != CfgBinValueType.String)
            throw new InvalidDataException("CHARA_MOTION_INFO package path is not a string.");
        var sourcePattern = GetString(sourceMotion.Values[1]);
        if (!sourcePattern.Contains("<TYPE>", StringComparison.Ordinal)
            || !sourcePattern.EndsWith(".g4pk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Source motion path is not a G4PK family pattern: '{sourcePattern}'.");

        var presetInfos = ReadCountedRows(document, "CHARA_MOTION_PRESET_INFO",
            "CHARA_MOTION_PRESET_INFO_LIST_BEG", "CHARA_MOTION_PRESET_INFO_LIST_END");
        var presetReferences = ReadCountedRows(document, "CHARA_MOTION_PRESET_INFO_REF_DATA",
            "CHARA_MOTION_PRESET_INFO_LIST_BEG", "CHARA_MOTION_PRESET_INFO_LIST_END");
        var presetData = ReadCountedRows(document, "CHARA_MOTION_PRESET_DATA",
            "CHARA_MOTION_PRESET_DATA_LIST_BEG", "CHARA_MOTION_PRESET_DATA_LIST_END");
        if (presetInfos.Count != presetReferences.Count)
            throw new InvalidDataException("Motion preset info and reference rows do not form parallel lists.");
        var infoByKey = new Dictionary<uint, int>();
        for (var index = 0; index < presetInfos.Count; index++)
        {
            var row = presetInfos[index];
            if (row.Values.Count == 0 || !infoByKey.TryAdd(unchecked((uint)GetInteger(row.Values[0])), index))
                throw new InvalidDataException($"CHARA_MOTION_PRESET_INFO row {index} has a missing or duplicate key.");
        }

        var referencedInfoRows = new List<(uint Key, int Index)>();
        var seenInfoKeys = new HashSet<uint>();
        foreach (var value in sourceMotion.Values.Skip(2))
        {
            if (!TryGetInteger(value, out var integer)) continue;
            var key = unchecked((uint)integer);
            if (infoByKey.TryGetValue(key, out var rowIndex) && seenInfoKeys.Add(key))
                referencedInfoRows.Add((key, rowIndex));
        }
        if (infoByKey.TryGetValue(sourceFamilyCrc32, out var familyPresetIndex)
            && seenInfoKeys.Add(sourceFamilyCrc32))
            referencedInfoRows.Add((sourceFamilyCrc32, familyPresetIndex));
        if (referencedInfoRows.Count == 0)
            throw new InvalidDataException("The source CHARA_MOTION_INFO row has no linked preset-info rows.");

        var presetBegin = FindMarker(document, "CHARA_MOTION_PRESET_DATA_LIST_BEG");
        var presetCount = GetInteger(presetBegin.Values[0]);
        if (presetCount != presetData.Count)
            throw new InvalidDataException("CHARA_MOTION_PRESET_DATA list count does not match its rows.");
        var clones = new List<(CfgBinEntry Template, IReadOnlyList<object?> Values)>();
        var pendingRanges = new List<PendingPresetRange>();
        foreach (var (sourceKey, rowIndex) in referencedInfoRows)
        {
            var reference = presetReferences[rowIndex];
            if (reference.Values.Count < 2)
                throw new InvalidDataException($"CHARA_MOTION_PRESET_INFO_REF_DATA row {rowIndex} has no range fields.");
            var start = GetInteger(reference.Values[0]);
            var count = GetInteger(reference.Values[1]);
            if (start < 0 || count <= 0 || start > presetData.Count - count)
                throw new InvalidDataException($"Preset range {start}+{count} is outside CHARA_MOTION_PRESET_DATA.");

            var outputStart = checked((int)(presetCount + clones.Count));
            for (var sourceIndex = checked((int)start); sourceIndex < checked((int)(start + count)); sourceIndex++)
            {
                var sourcePreset = presetData[sourceIndex];
                if (sourcePreset.Values.Count == 0 || sourcePreset.Values[0].Type != CfgBinValueType.String)
                    throw new InvalidDataException($"CHARA_MOTION_PRESET_DATA row {sourceIndex} has no string path.");
                var sourcePath = NormalizeResourcePath(GetString(sourcePreset.Values[0]));
                // shortcut: ungenerated banks retain source-rig paths; convert them before relying on those animation types.
                var destinationPath = routeBySource.GetValueOrDefault(sourcePath, sourcePath);
                var values = sourcePreset.Values.Select(value => value.Value).ToArray();
                values[0] = destinationPath;
                clones.Add((sourcePreset, values));
            }
            pendingRanges.Add(new PendingPresetRange(sourceKey, rowIndex, checked((int)start), outputStart, checked((int)count)));
        }

        if (clones.Count == 0) throw new InvalidDataException("No linked motion preset rows were cloned.");
        var result = T2bCountedListWriter.InsertClones(document, clones,
            "CHARA_MOTION_PRESET_DATA_LIST_BEG", "CHARA_MOTION_PRESET_DATA_LIST_END");

        var existingHashes = presetInfos.Select(row => GetInteger(row.Values[0])).ToHashSet();
        var generatedHashes = new HashSet<long>();
        var hashRemaps = new Dictionary<uint, uint>();
        foreach (var range in pendingRanges)
        {
            var current = CfgBinDocument.Read(result);
            var infoBegin = FindMarker(current, "CHARA_MOTION_PRESET_INFO_LIST_BEG");
            var end = current.Entries.Single(entry => entry.Name == "CHARA_MOTION_PRESET_INFO_LIST_END"
                && entry.Index > infoBegin.Index);
            var infoTemplate = current.Entries.Skip(infoBegin.Index + 1).Take(end.Index - infoBegin.Index - 1)
                .FirstOrDefault(entry => entry.Name == "CHARA_MOTION_PRESET_INFO")
                ?? throw new InvalidDataException("CHARA_MOTION_PRESET_INFO has no template row.");
            var referenceTemplate = current.Entries.Skip(infoBegin.Index + 1).Take(end.Index - infoBegin.Index - 1)
                .FirstOrDefault(entry => entry.Name == "CHARA_MOTION_PRESET_INFO_REF_DATA")
                ?? throw new InvalidDataException("CHARA_MOTION_PRESET_INFO_REF_DATA has no template row.");
            var sourceInfo = presetInfos[range.SourceInfoRow];
            var sourceReference = presetReferences[range.SourceInfoRow];
            var generatedHash = range.SourceKey == sourceFamilyCrc32
                ? unchecked((int)familyCrc)
                : CreatePresetInfoHash(familyName, GetInteger(sourceInfo.Values[0]),
                    existingHashes, generatedHashes);
            if (range.SourceKey == sourceFamilyCrc32
                && (existingHashes.Contains(generatedHash) || !generatedHashes.Add(generatedHash)))
                throw new InvalidDataException($"Generated family CRC {familyCrc:X8} is already a motion preset key.");
            var generatedKey = unchecked((uint)(int)generatedHash);
            if (!hashRemaps.TryAdd(range.SourceKey, generatedKey))
                throw new InvalidDataException($"Preset key {range.SourceKey:X8} was linked more than once.");
            var infoValues = sourceInfo.Values.Select(value => value.Value).ToArray();
            infoValues[0] = FormatInteger(current.ValueWidth, generatedHash);
            var referenceValues = sourceReference.Values.Select(value => value.Value).ToArray();
            referenceValues[0] = FormatInteger(current.ValueWidth, range.OutputStart);
            referenceValues[1] = FormatInteger(current.ValueWidth, range.Count);
            result = T2bCountedListWriter.InsertClones(current, infoTemplate, [infoValues],
                "CHARA_MOTION_PRESET_INFO_LIST_BEG", "CHARA_MOTION_PRESET_INFO_LIST_END",
                companionRows: [(referenceTemplate, (IReadOnlyList<object?>)referenceValues)]);
        }

        var restored = CfgBinDocument.Read(result);
        var sourceMotionRestored = restored.Entries.Single(entry => entry.Name == "CHARA_MOTION_INFO"
            && unchecked((uint)GetInteger(entry.Values[0])) == sourceFamilyCrc32);
        var motionValues = sourceMotionRestored.Values.Select(value => value.Value).ToArray();
        motionValues[0] = FormatInteger(restored.ValueWidth, unchecked((int)familyCrc));
        motionValues[1] = $"{familyName}/{familyName}_p<TYPE>.g4pk";
        for (var index = 2; index < sourceMotionRestored.Values.Count; index++)
        {
            if (!TryGetInteger(sourceMotionRestored.Values[index], out var oldKey)) continue;
            if (hashRemaps.TryGetValue(unchecked((uint)oldKey), out var newKey))
                motionValues[index] = FormatInteger(restored.ValueWidth, unchecked((int)newKey));
        }
        var motionBegin = FindMarker(restored, "CHARA_MOTION_INFO_LIST_BEG");
        var motionTemplate = restored.Entries.Skip(motionBegin.Index + 1)
            .FirstOrDefault(entry => entry.Name == "CHARA_MOTION_INFO")
            ?? throw new InvalidDataException("CHARA_MOTION_INFO has no template row.");
        result = T2bCountedListWriter.InsertClones(restored, motionTemplate, [motionValues],
            "CHARA_MOTION_INFO_LIST_BEG", "CHARA_MOTION_INFO_LIST_END");

        ValidateWrittenFamily(result, sourceFamilyCrc32, familyCrc, familyName, hashRemaps, pendingRanges,
            presetData, routeBySource);
        return result;
    }

    public static uint Crc32(string value) => Crc32(Encoding.UTF8.GetBytes(value));

    private static void ValidateWrittenFamily(
        byte[] result, uint sourceFamilyCrc32, uint familyCrc, string familyName,
        IReadOnlyDictionary<uint, uint> hashRemaps,
        IReadOnlyList<PendingPresetRange> ranges, IReadOnlyList<CfgBinEntry> sourcePresetRows,
        IReadOnlyDictionary<string, string> routeBySource)
    {
        var document = CfgBinDocument.Read(result);
        var motion = document.Entries.Single(entry => entry.Name == "CHARA_MOTION_INFO"
            && unchecked((uint)GetInteger(entry.Values[0])) == familyCrc);
        if (!Equals(motion.Values[1].Value, $"{familyName}/{familyName}_p<TYPE>.g4pk"))
            throw new InvalidDataException("Generated CHARA_MOTION_INFO family path failed read-back validation.");
        foreach (var (sourceKey, generatedKey) in hashRemaps)
        {
            var isFamilyKeyPreset = generatedKey == familyCrc && sourceKey == sourceFamilyCrc32;
            if (isFamilyKeyPreset
                    ? unchecked((uint)GetInteger(motion.Values[0])) != generatedKey
                    : !motion.Values.Skip(2).Any(value => TryGetInteger(value, out var integer)
                        && unchecked((uint)integer) == generatedKey))
                throw new InvalidDataException($"Generated motion row does not reference preset key {generatedKey:X8}.");
            var infos = document.Entries.Where(entry => entry.Name == "CHARA_MOTION_PRESET_INFO").ToArray();
            var refs = document.Entries.Where(entry => entry.Name == "CHARA_MOTION_PRESET_INFO_REF_DATA").ToArray();
            var infoIndex = Array.FindIndex(infos, entry => unchecked((uint)GetInteger(entry.Values[0])) == generatedKey);
            if (infoIndex < 0 || infoIndex >= refs.Length)
                throw new InvalidDataException($"Generated preset key {generatedKey:X8} has no paired reference row.");
            var range = ranges.Single(item => item.SourceKey == sourceKey);
            if (GetInteger(refs[infoIndex].Values[0]) != range.OutputStart
                || GetInteger(refs[infoIndex].Values[1]) != range.Count)
                throw new InvalidDataException($"Generated preset key {generatedKey:X8} points to the wrong preset range.");
        }
        var outputPresetRows = document.Entries.Where(entry => entry.Name == "CHARA_MOTION_PRESET_DATA").ToArray();
        var oldCount = sourcePresetRows.Count;
        foreach (var range in ranges)
        {
            if (range.OutputStart < oldCount || range.OutputStart + range.Count > outputPresetRows.Length)
                throw new InvalidDataException("Generated motion preset range points outside the appended rows.");
            var sourceStart = range.SourceStart;
            for (var index = 0; index < range.Count; index++)
            {
                var source = sourcePresetRows[checked((int)sourceStart) + index];
                var output = outputPresetRows[range.OutputStart + index];
                var originalPath = NormalizeResourcePath(GetString(source.Values[0]));
                if (!Equals(output.Values[0].Value, routeBySource.GetValueOrDefault(originalPath, originalPath)))
                    throw new InvalidDataException("A generated preset range does not preserve its selected animation bank route.");
                for (var field = 1; field < source.Values.Count; field++)
                    if (!Equals(output.Values[field].Value, source.Values[field].Value))
                        throw new InvalidDataException("An opaque motion preset field changed while cloning its range.");
            }
        }
    }

    private static IReadOnlyList<CfgBinEntry> ReadCountedRows(
        CfgBinDocument document, string rowName, string beginName, string endName)
    {
        var begin = FindMarker(document, beginName);
        var end = document.Entries.SingleOrDefault(entry => entry.Name == endName && entry.Index > begin.Index)
            ?? throw new InvalidDataException($"{endName} is missing.");
        var rows = document.Entries.Skip(begin.Index + 1).Take(end.Index - begin.Index - 1)
            .Where(entry => entry.Name == rowName).ToArray();
        if (begin.Values.Count == 0 || GetInteger(begin.Values[0]) != rows.Length)
            throw new InvalidDataException($"{beginName} does not match {rowName} row count.");
        return rows;
    }

    private static CfgBinEntry FindMarker(CfgBinDocument document, string name) =>
        document.Entries.SingleOrDefault(entry => entry.Name == name)
        ?? throw new InvalidDataException($"{name} is missing.");

    private static object FormatInteger(CfgBinValueWidth width, long value) => width switch
    {
        CfgBinValueWidth.Int32 => (object)checked((int)value),
        CfgBinValueWidth.Int64 => value,
        _ => throw new InvalidDataException("Motion table integer width is not known."),
    };

    private static string NormalizeResourcePath(string value)
    {
        var path = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Split('/').Any(segment => segment is "" or "." or "..")
            || !path.EndsWith(".g4pk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Motion bank path '{value}' is not a safe relative G4PK path.");
        return path;
    }

    private static string GetString(CfgBinValue value) => value.Value as string
        ?? throw new InvalidDataException("A motion preset path is not a string.");

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => throw new InvalidDataException("A motion table key or index is not an integer."),
    };

    private static bool TryGetInteger(CfgBinValue value, out long number)
    {
        if (value.Value is int integer) { number = integer; return true; }
        if (value.Value is long longInteger) { number = longInteger; return true; }
        number = 0;
        return false;
    }

    private static long CreatePresetInfoHash(
        string familyName, long sourceHash, IReadOnlySet<long> existingHashes, HashSet<long> generatedHashes)
    {
        for (var attempt = 0; ; attempt++)
        {
            var hash = unchecked((int)Crc32($"{familyName}:{sourceHash}:{attempt}"));
            if (!existingHashes.Contains(hash) && generatedHashes.Add(hash)) return hash;
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var item in bytes)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private sealed record PendingPresetRange(
        uint SourceKey, int SourceInfoRow, int SourceStart, int OutputStart, int Count);
}
