using VictoryTool.CfgBin;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using System.Text;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterCoreWriteRequest(
    int SourceBaseId,
    int SourceParameterId,
    uint CharacterId,
    uint ParameterId,
    string InternalName,
    uint FullNameTextId,
    uint ShortNameTextId,
    uint UpperNameTextId,
    uint DescriptionTextId,
    int Affinity,
    int MainPosition,
    int SubPosition,
    CharacterDraftSkills? Skills = null,
    uint? ModelId = null,
    bool EnforceIdentityHash = false,
    int? PlayStyle = null,
    int? Growth = null,
    int? Rank = null,
    int? AbilityBoardId = null,
    int? SpecialRarity = null,
    int? Gender = null,
    int? AcademicYear = null,
    int? SourceSeries = null,
    int? UniformPortraitVariant = null,
    int? TeamAssociation1 = null,
    int? TeamAssociation2 = null,
    int? TeamAssociation3 = null,
    int? OriginGameAssociationIndex = null,
    bool? SkillsUnlocked = null,
    bool WritesBaseRow = true);

public sealed record CharacterCoreWriteResult(byte[] BaseTable, byte[] ParameterTable);

public sealed record CharacterCoreWriteSource(
    CharacterCoreWriteRequest Request, ReadOnlyMemory<byte> BaseTable, ReadOnlyMemory<byte> ParameterTable);

public interface ICharacterCoreT2bWriter
{
    CharacterCoreWriteResult Append(
        ReadOnlySpan<byte> baseTable,
        ReadOnlySpan<byte> parameterTable,
        CharacterCoreWriteRequest request);

    CharacterCoreWriteResult Append(
        ReadOnlySpan<byte> destinationBaseTable,
        ReadOnlySpan<byte> destinationParameterTable,
        ReadOnlySpan<byte> sourceBaseTable,
        ReadOnlySpan<byte> sourceParameterTable,
        CharacterCoreWriteRequest request) =>
        Append(destinationBaseTable, destinationParameterTable, request);

    CharacterCoreWriteResult AppendBatch(ReadOnlySpan<byte> baseTable, ReadOnlySpan<byte> parameterTable,
        IReadOnlyList<CharacterCoreWriteSource> sources, CancellationToken cancellationToken)
    {
        var result = new CharacterCoreWriteResult(baseTable.ToArray(), parameterTable.ToArray());
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = Append(result.BaseTable, result.ParameterTable, source.BaseTable.Span, source.ParameterTable.Span, source.Request);
        }
        return result;
    }
}

public sealed class CharacterCoreT2bWriter : ICharacterCoreT2bWriter
{
    public CharacterCoreWriteResult Append(
        ReadOnlySpan<byte> baseTable,
        ReadOnlySpan<byte> parameterTable,
        CharacterCoreWriteRequest request)
        => Append(baseTable, parameterTable, baseTable, parameterTable, request);

    public CharacterCoreWriteResult Append(
        ReadOnlySpan<byte> destinationBaseTable,
        ReadOnlySpan<byte> destinationParameterTable,
        ReadOnlySpan<byte> sourceBaseTable,
        ReadOnlySpan<byte> sourceParameterTable,
        CharacterCoreWriteRequest request)
        => AppendBatch(destinationBaseTable, destinationParameterTable,
            [new CharacterCoreWriteSource(request, sourceBaseTable.ToArray(), sourceParameterTable.ToArray())], CancellationToken.None);

    public CharacterCoreWriteResult AppendBatch(ReadOnlySpan<byte> destinationBaseTable,
        ReadOnlySpan<byte> destinationParameterTable, IReadOnlyList<CharacterCoreWriteSource> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var source in sources) ValidateRequest(source.Request);
        if (sources.Count == 0) return new(destinationBaseTable.ToArray(), destinationParameterTable.ToArray());
        var baseDocument = CfgBinDocument.Read(destinationBaseTable);
        var parameterDocument = CfgBinDocument.Read(destinationParameterTable);
        var sourceDocuments = new Dictionary<ReadOnlyMemory<byte>, CfgBinDocument>();
        CfgBinDocument ReadSource(ReadOnlyMemory<byte> bytes) => sourceDocuments.TryGetValue(bytes, out var document)
            ? document : sourceDocuments[bytes] = CfgBinDocument.Read(bytes.Span);
        var baseInserts = new List<CfgBinEntryInsert>();
        var parameterInserts = new List<CfgBinEntryInsert>();
        var nextBattleReference = baseDocument.Entries.Count(entry => entry.Name == "CHARA_BASE_BATTLE");
        int? nextBaseIndex = null;
        int? nextSeriesIndex = null;
        var writtenBaseRows = 0;
        var writtenSeriesRows = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = source.Request;
            var sourceBaseDocument = source.BaseTable.Span.SequenceEqual(destinationBaseTable)
                ? baseDocument : ReadSource(source.BaseTable);
            var sourceParameterDocument = source.ParameterTable.Span.SequenceEqual(destinationParameterTable)
                ? parameterDocument : ReadSource(source.ParameterTable);
            var sourceBase = FindSingleSource(sourceBaseDocument, "CHARA_BASE_INFO", 34, request.SourceBaseId, 0);
            var sourceParameter = FindSingleSource(
                sourceParameterDocument, "CHARA_PARAM_INFO", 43, request.SourceParameterId, 0);
            if (request.EnforceIdentityHash)
            {
                if (GetInteger(sourceBase.Values[31]) != 0)
                    throw new InvalidDataException("The source character is restricted and cannot seed a playable custom identity.");
                if (!sourceBase.Values.Skip(21).Take(10).Any(value => GetInteger(value) != 0))
                    throw new InvalidDataException("The source character has no series-category association.");
            }
            if (GetInteger(sourceParameter.Values[1]) != request.SourceBaseId)
                throw new InvalidDataException("The source parameter row is not linked to the requested base row.");

            var sourceBaseCompanion = sourceBaseDocument.Entries.ElementAtOrDefault(sourceBase.Index + 1);
            var isMinimalBaseFixture = sourceBaseDocument.EntryCount == 1;
            if (!isMinimalBaseFixture && sourceBaseCompanion?.Name != "CHARA_BASE_INFO_REF_BATTLE")
                throw new InvalidDataException("The source character base row has no adjacent battle-reference row.");
            if (sourceBaseCompanion?.Name == "CHARA_BASE_INFO_REF_BATTLE" && sourceBaseCompanion.Values.Count != 2)
                throw new InvalidDataException("The source character battle reference must contain an offset and count.");

            int? newBattleReference = null;
            if (request.WritesBaseRow && sourceBaseCompanion?.Name == "CHARA_BASE_INFO_REF_BATTLE"
                && GetInteger(sourceBaseCompanion.Values[1]) != 0)
            {
                var sourceBattleRows = sourceBaseDocument.Entries.Where(entry => entry.Name == "CHARA_BASE_BATTLE").ToArray();
                var reference = checked((int)GetInteger(sourceBaseCompanion.Values[0]));
                var count = checked((int)GetInteger(sourceBaseCompanion.Values[1]));
                if (reference < 0 || count < 0 || reference > sourceBattleRows.Length - count)
                    throw new InvalidDataException("The source character battle reference is outside CHARA_BASE_BATTLE.");
                var template = baseDocument.Entries.FirstOrDefault(entry => entry.Name == "CHARA_BASE_BATTLE")
                    ?? throw new InvalidDataException("The destination base table has no battle-row template.");
                newBattleReference = nextBattleReference;
                nextBattleReference = checked(nextBattleReference + count);
                baseInserts.AddRange(T2bCountedListWriter.PrepareClones(baseDocument, template,
                    sourceBattleRows.Skip(reference).Take(count)
                        .Select(row => (IReadOnlyList<object?>)row.Values.Select(value => value.Value).ToArray()).ToArray(),
                    "CHARA_BASE_BATTLE_LIST_BEG", "CHARA_BASE_BATTLE_LIST_END", null, null));
            }

            var baseValues = sourceBase.Values.Select(value => value.Value).ToArray();
            SetUnsigned(baseDocument, baseValues, 0, request.CharacterId);
            baseValues[1] = request.InternalName;
            SetSigned(baseDocument, baseValues, 2, checked((nextBaseIndex ??= AllocateNextPositiveValue(baseDocument, "CHARA_BASE_INFO", 2)) + writtenBaseRows));
            SetUnsigned(baseDocument, baseValues, 3, request.FullNameTextId);
            SetUnsigned(baseDocument, baseValues, 4, request.ShortNameTextId);
            SetUnsigned(baseDocument, baseValues, 5, request.UpperNameTextId);
            if (request.ModelId is { } modelId) SetUnsigned(baseDocument, baseValues, 6, modelId);
            SetUnsigned(baseDocument, baseValues, 19, request.DescriptionTextId);
            SetOptionalSigned(baseDocument, baseValues, 11, request.Gender);
            SetOptionalSigned(baseDocument, baseValues, 13, request.AcademicYear);
            SetOptionalSigned(baseDocument, baseValues, 15, request.SourceSeries);
            SetOptionalSigned(baseDocument, baseValues, 16, request.TeamAssociation1);
            SetOptionalSigned(baseDocument, baseValues, 17, request.TeamAssociation2);
            SetOptionalSigned(baseDocument, baseValues, 18, request.TeamAssociation3);
            ApplyOriginGameAssociation(baseDocument, baseValues, request.OriginGameAssociationIndex);
            if (GetInteger(baseValues[21]) != 0)
                SetSigned(baseDocument, baseValues, 21, checked((nextSeriesIndex ??= AllocateNextPositiveValue(baseDocument, "CHARA_BASE_INFO", 21)) + writtenSeriesRows));

            var parameterValues = sourceParameter.Values.Select(value => value.Value).ToArray();
            SetUnsigned(parameterDocument, parameterValues, 0, request.ParameterId);
            SetUnsigned(parameterDocument, parameterValues, 1, request.CharacterId);
            SetSigned(parameterDocument, parameterValues, 2, request.Affinity);
            SetSigned(parameterDocument, parameterValues, 3, request.MainPosition);
            SetSigned(parameterDocument, parameterValues, 4, request.SubPosition);
            SetOptionalSigned(parameterDocument, parameterValues, 8, request.PlayStyle);
            SetOptionalSigned(parameterDocument, parameterValues, 7, request.Growth);
            SetOptionalSigned(parameterDocument, parameterValues, 9, request.Rank);
            SetOptionalSigned(parameterDocument, parameterValues, 10, request.AbilityBoardId);
            SetOptionalSigned(parameterDocument, parameterValues, 41, request.SpecialRarity);
            SetOptionalSigned(parameterDocument, parameterValues, 39, request.SkillsUnlocked is true ? 1 : request.SkillsUnlocked is false ? 0 : null);
            ApplySkills(parameterDocument, parameterValues, request.Skills);

            var destinationBaseTemplate = baseDocument.Entries.FirstOrDefault(entry => entry.Name == "CHARA_BASE_INFO")
                ?? throw new InvalidDataException("The destination base table has no character-row template.");
            var destinationParameterTemplate = parameterDocument.Entries.FirstOrDefault(entry => entry.Name == "CHARA_PARAM_INFO")
                ?? throw new InvalidDataException("The destination parameter table has no character-row template.");
            var baseCompanion = baseDocument.Entries.ElementAtOrDefault(destinationBaseTemplate.Index + 1);
            IReadOnlyList<object?>? baseCompanionValues = null;
            if (baseCompanion?.Name == "CHARA_BASE_INFO_REF_BATTLE")
            {
                if (sourceBaseCompanion is null)
                    throw new InvalidDataException("The source character has no battle reference to clone.");
                var values = sourceBaseCompanion.Values.Select(value => value.Value).ToArray();
                if (newBattleReference is { } battleReference)
                    SetSigned(baseDocument, values, 0, battleReference);
                baseCompanionValues = values;
            }
            var parameterInsertion = parameterDocument.EntryCount == 1
                ? (int?)null
                : parameterDocument.Entries
                    .Skip(parameterDocument.Entries.First(entry => entry.Name == "CHARA_PARAM_INFO").Index)
                    .First(entry => entry.Name != "CHARA_PARAM_INFO").Index;

            if (request.WritesBaseRow)
            {
                baseInserts.AddRange(T2bCountedListWriter.PrepareClones(
                    baseDocument, destinationBaseTemplate, [baseValues],
                    "CHARA_BASE_INFO_LIST_BEG", "CHARA_BASE_INFO_LIST_END", null,
                    baseCompanion?.Name == "CHARA_BASE_INFO_REF_BATTLE" ? [(baseCompanion, baseCompanionValues!)] : null));
                writtenBaseRows++;
                if (GetInteger(baseValues[21]) != 0) writtenSeriesRows++;
            }
            parameterInserts.AddRange(T2bCountedListWriter.PrepareClones(
                parameterDocument, destinationParameterTemplate, [parameterValues],
                "CHARA_PARAM_INFO_LIST_BEG", "CHARA_PARAM_INFO_LIST_END", parameterInsertion, null));
        }
        parameterInserts.AddRange(PrepareParameterSortIndices(parameterDocument, sources));
        cancellationToken.ThrowIfCancellationRequested();
        var result = new CharacterCoreWriteResult(
            baseDocument.WriteWithInsertedEntries(baseInserts), parameterDocument.WriteWithInsertedEntries(parameterInserts));
        var restoredBase = CfgBinDocument.Read(result.BaseTable);
        var restoredParameters = CfgBinDocument.Read(result.ParameterTable);
        T2bCountedListWriter.ValidateBatchCount(baseDocument, restoredBase,
            "CHARA_BASE_INFO_LIST_BEG", "CHARA_BASE_INFO_LIST_END", "CHARA_BASE_INFO", writtenBaseRows);
        T2bCountedListWriter.ValidateBatchCount(baseDocument, restoredBase,
            "CHARA_BASE_BATTLE_LIST_BEG", "CHARA_BASE_BATTLE_LIST_END", "CHARA_BASE_BATTLE",
            nextBattleReference - baseDocument.Entries.Count(entry => entry.Name == "CHARA_BASE_BATTLE"));
        T2bCountedListWriter.ValidateBatchCount(parameterDocument, restoredParameters,
            "CHARA_PARAM_INFO_LIST_BEG", "CHARA_PARAM_INFO_LIST_END", "CHARA_PARAM_INFO", sources.Count);
        foreach (var source in sources.Where(source => source.Request.WritesBaseRow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReadBack(restoredBase, restoredParameters, source.Request);
        }
        return result;
    }

    private static void ValidateRequest(CharacterCoreWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InternalName))
            throw new ArgumentException("An internal character name is required.", nameof(request));
        if (request.EnforceIdentityHash && !IsCanonicalInternalName(request.InternalName))
            throw new InvalidDataException("The internal character name must use the canonical c######## form.");
        if (request.EnforceIdentityHash && ComputeCrc32(request.InternalName) != request.CharacterId)
            throw new InvalidDataException("The character ID must equal CRC32 of the internal character name.");
        if (request.EnforceIdentityHash
            && ComputeCrc32($"pc_para_{request.InternalName}") != request.ParameterId)
        {
            throw new InvalidDataException(
                "The character parameter ID must equal CRC32 of the internal parameter key.");
        }
    }

    private static int AllocateNextPositiveValue(CfgBinDocument document, string entryName, int fieldIndex)
    {
        var maximum = document.Entries
            .Where(entry => entry.Name == entryName && entry.Values.Count > fieldIndex)
            .Select(entry => GetInteger(entry.Values[fieldIndex]))
            .Where(value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        if (maximum >= int.MaxValue)
            throw new InvalidDataException($"No positive {entryName}[{fieldIndex}] value remains available.");
        return checked((int)maximum + 1);
    }

    private static IEnumerable<CfgBinEntryInsert> PrepareParameterSortIndices(
        CfgBinDocument document, IReadOnlyList<CharacterCoreWriteSource> sources)
    {
        var rows = document.Entries.Where(entry => entry.Name == "CHARA_PARAM_INFO").ToArray();
        var sortRows = document.Entries.Where(entry => entry.Name == "__SORT_INDEX").ToArray();
        if (sortRows.Length == 0) yield break;
        if (sortRows.Length != rows.Length)
            throw new InvalidDataException("The character parameter sort index is not aligned with its primary rows.");
        foreach (var item in sources.Select((source, index) => (Id: source.Request.ParameterId, Index: rows.Length + index))
                     .OrderBy(item => item.Id))
        {
            var offset = rows.Count(row => unchecked((uint)GetInteger(row.Values[0])) < item.Id);
            var before = offset == sortRows.Length ? sortRows[^1].Index + 1 : sortRows[offset].Index;
            object value = document.ValueWidth == CfgBinValueWidth.Int32 ? (object)item.Index : (long)item.Index;
            yield return new CfgBinEntryInsert(before, sortRows[0].Index, [value]);
        }
    }

    private static void ValidateReadBack(CfgBinDocument baseDocument, CfgBinDocument parameterDocument,
        CharacterCoreWriteRequest request)
    {
        var baseRow = FindSingleWrittenSource(
            baseDocument, "CHARA_BASE_INFO", 34, request.CharacterId);
        var parameterRow = FindSingleWrittenSource(
            parameterDocument, "CHARA_PARAM_INFO", 43, request.ParameterId);
        long expectedCharacterId = parameterDocument.ValueWidth == CfgBinValueWidth.Int32
            ? unchecked((int)request.CharacterId)
            : request.CharacterId;
        if (GetInteger(parameterRow.Values[1]) != expectedCharacterId
            || !string.Equals(baseRow.Values[1].Value as string, request.InternalName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The written character core rows failed read-back validation.");
        }
        if (baseDocument.EntryCount > 2
            && baseDocument.Entries.ElementAtOrDefault(baseRow.Index + 1)?.Name != "CHARA_BASE_INFO_REF_BATTLE")
            throw new InvalidDataException("The written character base row lost its battle-reference companion.");
        if (baseDocument.EntryCount > 2)
        {
            var battleRows = baseDocument.Entries.Where(entry => entry.Name == "CHARA_BASE_BATTLE").ToArray();
            var battleListBegin = baseDocument.Entries.SingleOrDefault(entry =>
                entry.Name == "CHARA_BASE_BATTLE_LIST_BEG");
            if (battleListBegin is null
                || battleListBegin.Values.Count == 0
                || GetInteger(battleListBegin.Values[0]) != battleRows.Length)
            {
                throw new InvalidDataException("The written character battle-list count failed read-back validation.");
            }

            var battleCompanion = baseDocument.Entries[baseRow.Index + 1];
            var battleReference = checked((int)GetInteger(battleCompanion.Values[0]));
            var battleCount = checked((int)GetInteger(battleCompanion.Values[1]));
            if (battleReference < 0 || battleCount < 0 || battleReference > battleRows.Length - battleCount)
                throw new InvalidDataException("The written character battle reference is outside CHARA_BASE_BATTLE.");
        }
        if (parameterDocument.Entries.Any(entry => entry.Name != "CHARA_PARAM_INFO"))
        {
            var firstParameterAuxiliary = parameterDocument.Entries.First(entry =>
                entry.Index > parameterDocument.Entries.First(row => row.Name == "CHARA_PARAM_INFO").Index
                && entry.Name != "CHARA_PARAM_INFO");
            if (parameterRow.Index >= firstParameterAuxiliary.Index)
                throw new InvalidDataException("The written character parameter row lies outside the runtime parameter range.");
            var parameterRows = parameterDocument.Entries.Where(entry => entry.Name == "CHARA_PARAM_INFO").ToArray();
            var sortIndexes = parameterDocument.Entries.Where(entry => entry.Name == "__SORT_INDEX")
                .Select(entry => checked((int)GetInteger(entry.Values[0]))).ToArray();
            if (sortIndexes.Length != parameterRows.Length
                || !sortIndexes.Order().SequenceEqual(Enumerable.Range(0, parameterRows.Length))
                || !sortIndexes.Select(index => unchecked((uint)GetInteger(parameterRows[index].Values[0])))
                    .SequenceEqual(sortIndexes.Select(index => unchecked((uint)GetInteger(parameterRows[index].Values[0]))).Order()))
            {
                throw new InvalidDataException("The written character parameter sort index failed read-back validation.");
            }
        }
        if (request.ModelId is { } modelId)
        {
            long expectedModelId = baseDocument.ValueWidth == CfgBinValueWidth.Int32
                ? unchecked((int)modelId)
                : modelId;
            if (GetInteger(baseRow.Values[6]) != expectedModelId)
                throw new InvalidDataException("The written character model reference failed read-back validation.");
        }

        ValidateParameterValue(parameterRow, 2, request.Affinity, "affinity");
        ValidateParameterValue(parameterRow, 3, request.MainPosition, "main position");
        ValidateParameterValue(parameterRow, 4, request.SubPosition, "sub position");
        ValidateOptionalBaseValue(baseRow, 11, request.Gender, "gender");
        ValidateOptionalBaseValue(baseRow, 13, request.AcademicYear, "academic year");
        ValidateOptionalBaseValue(baseRow, 15, request.SourceSeries, "source series");
        ValidateOptionalBaseValue(baseRow, 16, request.TeamAssociation1, "team association 1");
        ValidateOptionalBaseValue(baseRow, 17, request.TeamAssociation2, "team association 2");
        ValidateOptionalBaseValue(baseRow, 18, request.TeamAssociation3, "team association 3");
        ValidateOriginGameAssociation(baseRow, request.OriginGameAssociationIndex);

        if (request.Skills is not null)
        {
            foreach (var slot in request.Skills.Slots)
            {
                var skillIndex = 11 + (slot.Slot - 1) * 2;
                if (slot.SkillId is { } skillId && GetInteger(parameterRow.Values[skillIndex]) != skillId)
                    throw new InvalidDataException($"Skill slot {slot.Slot} failed read-back validation.");
                if (slot.UnlockLevel is { } unlockLevel
                    && GetInteger(parameterRow.Values[skillIndex + 1]) != unlockLevel)
                {
                    throw new InvalidDataException($"Skill slot {slot.Slot} unlock level failed read-back validation.");
                }
            }
        }
        ValidateOptionalParameterValue(parameterRow, 8, request.PlayStyle, "play style");
        ValidateOptionalParameterValue(parameterRow, 7, request.Growth, "growth");
        ValidateOptionalParameterValue(parameterRow, 9, request.Rank, "rank");
        ValidateOptionalParameterValue(parameterRow, 10, request.AbilityBoardId, "ability board");
        ValidateOptionalParameterValue(parameterRow, 41, request.SpecialRarity, "special rarity");
        ValidateOptionalParameterValue(parameterRow, 39, request.SkillsUnlocked is true ? 1 : request.SkillsUnlocked is false ? 0 : null, "skill-unlocked flag");
    }

    private static void SetOptionalSigned(
        CfgBinDocument document, object?[] values, int index, int? value)
    {
        if (value is { } present) SetSigned(document, values, index, present);
    }

    private static void ApplyOriginGameAssociation(
        CfgBinDocument document, object?[] values, int? associationIndex)
    {
        if (associationIndex is null) return;
        if (associationIndex is < CharacterOriginGameCatalog.FirstAssociationIndex
            or > CharacterOriginGameCatalog.LastAssociationIndex)
        {
            throw new InvalidDataException(
                $"Origin-game association index must be between {CharacterOriginGameCatalog.FirstAssociationIndex} and {CharacterOriginGameCatalog.LastAssociationIndex}.");
        }

        for (var index = CharacterOriginGameCatalog.FirstAssociationIndex;
             index <= CharacterOriginGameCatalog.LastAssociationIndex;
             index++)
            SetSigned(document, values, index, index == associationIndex ? 1 : 0);
    }

    private static void ValidateOriginGameAssociation(
        CfgBinEntry row, int? associationIndex)
    {
        if (associationIndex is null) return;
        if (associationIndex is < CharacterOriginGameCatalog.FirstAssociationIndex
            or > CharacterOriginGameCatalog.LastAssociationIndex)
            throw new InvalidDataException("The written origin-game association index is invalid.");

        for (var index = CharacterOriginGameCatalog.FirstAssociationIndex;
             index <= CharacterOriginGameCatalog.LastAssociationIndex;
             index++)
        {
            var expected = index == associationIndex ? 1 : 0;
            if (GetInteger(row.Values[index]) != expected)
                throw new InvalidDataException("The written origin-game association failed read-back validation.");
        }
    }

    private static void ValidateOptionalParameterValue(
        CfgBinEntry row, int index, int? expected, string field)
    {
        if (expected is { } present && GetInteger(row.Values[index]) != present)
            throw new InvalidDataException($"Character {field} failed read-back validation.");
    }

    private static void ValidateParameterValue(CfgBinEntry row, int index, int expected, string field)
    {
        if (GetInteger(row.Values[index]) != expected)
            throw new InvalidDataException($"Character {field} failed read-back validation.");
    }

    private static void ValidateOptionalBaseValue(
        CfgBinEntry row, int index, int? expected, string field)
    {
        if (expected is { } present && GetInteger(row.Values[index]) != present)
            throw new InvalidDataException($"Character {field} failed read-back validation.");
    }

    private static CfgBinEntry FindSingleWrittenSource(
        CfgBinDocument document,
        string name,
        int minimumValues,
        uint id)
    {
        long expectedId = document.ValueWidth == CfgBinValueWidth.Int32
            ? unchecked((int)id)
            : id;
        var rows = document.Entries.Where(entry =>
            entry.Name == name
            && entry.Values.Count >= minimumValues
            && GetInteger(entry.Values[0]) == expectedId).ToArray();
        return rows.Length == 1
            ? rows[0]
            : throw new InvalidDataException(
                $"Written {name} row for ID {id} failed cardinality validation; found {rows.Length}.");
    }

    private static void ApplySkills(
        CfgBinDocument document,
        object?[] values,
        CharacterDraftSkills? skills)
    {
        if (skills is null) return;
        if (skills.Slots.Count != 9
            || skills.Slots.Select(slot => slot.Slot).Distinct().Count() != 9
            || skills.Slots.Any(slot => slot.Slot is < 1 or > 9))
        {
            throw new InvalidDataException("Character skill data must contain each slot from 1 through 9 exactly once.");
        }

        foreach (var slot in skills.Slots)
        {
            var skillIndex = 11 + (slot.Slot - 1) * 2;
            if (slot.SkillId is { } skillId)
                SetSigned(document, values, skillIndex, skillId);
            if (slot.UnlockLevel is { } unlockLevel)
                SetSigned(document, values, skillIndex + 1, unlockLevel);
        }
    }

    private static CfgBinEntry FindSingleSource(
        CfgBinDocument document,
        string name,
        int minimumValues,
        int id,
        int idIndex)
    {
        var rows = document.Entries.Where(entry =>
            entry.Name == name
            && entry.Values.Count >= minimumValues
            && GetInteger(entry.Values[idIndex]) == id).ToArray();
        return rows.Length == 1
            ? rows[0]
            : throw new InvalidDataException(
                $"Expected exactly one {name} source row for ID {id}, found {rows.Length}.");
    }

    private static void SetUnsigned(CfgBinDocument document, object?[] values, int index, uint value)
    {
        if (document.ValueWidth == CfgBinValueWidth.Int32)
            values[index] = unchecked((int)value);
        else
            values[index] = (long)value;
    }

    private static void SetSigned(CfgBinDocument document, object?[] values, int index, int value)
    {
        if (document.ValueWidth == CfgBinValueWidth.Int32)
            values[index] = value;
        else
            values[index] = (long)value;
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int intValue => intValue,
        long longValue => longValue,
        _ => long.MinValue,
    };

    private static long GetInteger(object? value) => value switch
    {
        int intValue => intValue,
        long longValue => longValue,
        _ => long.MinValue,
    };

    private static uint ComputeCrc32(string value)
    {
        var crc = uint.MaxValue;
        foreach (var item in Encoding.UTF8.GetBytes(value))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static bool IsCanonicalInternalName(string value)
    {
        if (value.Length != 9 || value[0] != 'c') return false;
        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index])) return false;
        }
        return true;
    }
}
