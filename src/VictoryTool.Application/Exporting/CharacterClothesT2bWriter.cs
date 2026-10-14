using VictoryTool.Application.Diagnostics;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterClothesWriteRequest(
    int SourceUniformModelId,
    uint UniformModelId,
    string ResourceKey,
    string ModelPath,
    string TexturePath,
    string SourceResourceKey,
    int ClothesInfoType,
    bool IsShoes = false,
    string? SourceModelPath = null,
    int? SourceInfoIndex = null);

public interface ICharacterClothesT2bWriter
{
    byte[] Append(ReadOnlySpan<byte> clothesTable, CharacterClothesWriteRequest request);
}

public sealed class CharacterClothesT2bWriter : ICharacterClothesT2bWriter
{
    public byte[] Append(ReadOnlySpan<byte> clothesTable, CharacterClothesWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TexturePath);
        using var operation = GlobalLog.BeginOperation("character_clothes_write", new Dictionary<string, object?>
        {
            ["sourceUniformModelId"] = request.SourceUniformModelId,
            ["uniformModelId"] = request.UniformModelId,
            ["resourceKey"] = request.ResourceKey,
        });

        var document = CfgBinDocument.Read(clothesTable);
        var modelTableName = request.IsShoes ? "CHARA_PARTS_SHOES_MODEL" : "CHARA_PARTS_CLOTHES_MODEL";
        var referenceTableName = request.IsShoes ? "CHARA_PARTS_SHOES_MODEL_REF_INFO" : "CHARA_PARTS_CLOTHES_MODEL_REF_INFO";
        var infoTableName = request.IsShoes ? "CHARA_PARTS_SHOES_INFO" : "CHARA_PARTS_CLOTHES_INFO";
        var modelListStart = request.IsShoes ? "CHARA_PARTS_SHOES_MODEL_LIST_BEG" : "CHARA_PARTS_CLOTHES_MODEL_LIST_BEG";
        var modelListEnd = request.IsShoes ? "CHARA_PARTS_SHOES_MODEL_LIST_END" : "CHARA_PARTS_CLOTHES_MODEL_LIST_END";
        var infoListStart = request.IsShoes ? "CHARA_PARTS_SHOES_INFO_LIST_BEG" : "CHARA_PARTS_CLOTHES_INFO_LIST_BEG";
        var infoListEnd = request.IsShoes ? "CHARA_PARTS_SHOES_INFO_LIST_END" : "CHARA_PARTS_CLOTHES_INFO_LIST_END";
        var modelSource = FindSingle(
            document,
            modelTableName,
            entry => entry.Values.Count >= (request.IsShoes ? 1 : 2)
                && GetInteger(entry.Values[0]) == request.SourceUniformModelId);
        var modelReferenceSource = document.Entries.SingleOrDefault(entry =>
            entry.Index == modelSource.Index + 1
            && entry.Name == referenceTableName
            && entry.Values.Count >= 2)
            ?? throw new InvalidDataException(
                $"The source equipment model '{request.SourceResourceKey}' has no {referenceTableName} row.");
        var infoRows = document.Entries.Where(entry => entry.Name == infoTableName).ToArray();
        var infoSource = request.IsShoes && request.SourceInfoIndex is { } sourceInfoIndex
            ? (uint)sourceInfoIndex < (uint)infoRows.Length && infoRows[sourceInfoIndex].Values.Count >= 3
                ? infoRows[sourceInfoIndex]
                : throw new InvalidDataException("The source shoes info index does not resolve to a valid info row.")
            : FindSingle(
                document,
                infoTableName,
                entry => entry.Values.Count >= (request.IsShoes ? 3 : 22)
                    && entry.Values[0].Value is string path
                    && path.Equals(request.SourceModelPath ?? $"_uniform/{request.SourceResourceKey}/{request.SourceResourceKey}.g4md", StringComparison.Ordinal));

        if (document.Entries.Any(entry => entry.Name == modelTableName
                && entry.Values.Count >= (request.IsShoes ? 1 : 2)
                && GetInteger(entry.Values[0]) == unchecked((int)request.UniformModelId)))
            throw new InvalidDataException("The new equipment model ID already exists.");
        if (!request.IsShoes && document.Entries.Any(entry => entry.Name == modelTableName
                && entry.Values.Count >= 2
                && string.Equals(entry.Values[1].Value as string, request.ResourceKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The new uniform resource key already exists.");

        var modelValues = modelSource.Values.Select(value => value.Value).ToArray();
        modelValues[0] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)unchecked((int)request.UniformModelId)
            : (long)request.UniformModelId;
        if (modelValues.Length > 1)
            modelValues[1] = request.ResourceKey;
        var appendedInfoReference = document.Entries.Count(entry => entry.Name == infoTableName);
        var modelReferenceValues = modelReferenceSource.Values.Select(value => value.Value).ToArray();
        if (modelReferenceSource.Values[0].Type != CfgBinValueType.Integer)
            throw new InvalidDataException("The clothes-model info reference is not an integer.");
        modelReferenceValues[0] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)checked((int)appendedInfoReference)
            : (long)appendedInfoReference;
        for (var index = 1; index < modelReferenceValues.Length; index++)
        {
            if (modelReferenceSource.Values[index].Type != CfgBinValueType.Integer) continue;
            var value = GetInteger(modelReferenceSource.Values[index]);
            modelReferenceValues[index] = document.ValueWidth == CfgBinValueWidth.Int32
                ? (object)checked((int)value)
                : value;
        }
        if (request.IsShoes && modelReferenceValues.Length > 1)
            modelReferenceValues[1] = document.ValueWidth == CfgBinValueWidth.Int32 ? (object)1 : 1L;
        var result = T2bCountedListWriter.InsertClones(
            document,
            modelSource,
            [modelValues],
            modelListStart,
            modelListEnd,
            companionRows: [(modelReferenceSource, modelReferenceValues)]);

        var withModel = CfgBinDocument.Read(result);
        var sourceModelPath = request.SourceModelPath ?? $"_uniform/{request.SourceResourceKey}/{request.SourceResourceKey}.g4md";
        var infoTemplates = withModel.Entries.Where(entry => entry.Name == infoTableName).ToArray();
        var infoTemplate = request.IsShoes && request.SourceInfoIndex is { } templateInfoIndex
            ? (uint)templateInfoIndex < (uint)infoTemplates.Length && infoTemplates[templateInfoIndex].Values.Count >= 3
                ? infoTemplates[templateInfoIndex]
                : throw new InvalidDataException("The source shoes info index moved while cloning its model row.")
            : infoTemplates.Single(entry => entry.Values.Count >= (request.IsShoes ? 3 : 22)
                && entry.Values[0].Value is string path
                && path.Equals(sourceModelPath, StringComparison.Ordinal));
        var infoValues = infoTemplate.Values.Select(value => value.Value).ToArray();
        infoValues[0] = request.ModelPath;
        infoValues[1] = request.TexturePath;
        infoValues[2] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)request.ClothesInfoType
            : (long)request.ClothesInfoType;
        result = T2bCountedListWriter.InsertClones(
            withModel,
            infoTemplate,
            [infoValues],
            infoListStart,
            infoListEnd);

        ValidateResult(result, request);
        GlobalLog.Debug("character_clothes_written", new Dictionary<string, object?>
        {
            ["tableBytes"] = result.Length,
        });
        return result;
    }

    private static void ValidateResult(byte[] result, CharacterClothesWriteRequest request)
    {
        var restored = CfgBinDocument.Read(result);
        var modelTableName = request.IsShoes ? "CHARA_PARTS_SHOES_MODEL" : "CHARA_PARTS_CLOTHES_MODEL";
        var referenceTableName = request.IsShoes ? "CHARA_PARTS_SHOES_MODEL_REF_INFO" : "CHARA_PARTS_CLOTHES_MODEL_REF_INFO";
        var infoTableName = request.IsShoes ? "CHARA_PARTS_SHOES_INFO" : "CHARA_PARTS_CLOTHES_INFO";
        var model = FindSingle(restored, modelTableName, entry =>
            entry.Values.Count >= (request.IsShoes ? 1 : 2)
            && GetInteger(entry.Values[0]) == unchecked((int)request.UniformModelId));
        if (!request.IsShoes && !string.Equals(model.Values[1].Value as string, request.ResourceKey, StringComparison.Ordinal))
            throw new InvalidDataException("The written clothes-model resource key failed read-back validation.");
        var modelReference = restored.Entries.SingleOrDefault(entry =>
            entry.Index == model.Index + 1
            && entry.Name == referenceTableName
            && entry.Values.Count >= 2)
            ?? throw new InvalidDataException("The written clothes model has no info-reference row.");
        var expectedInfoReference = restored.Entries.Count(entry => entry.Name == infoTableName) - 1;
        if (GetInteger(modelReference.Values[0]) != expectedInfoReference)
            throw new InvalidDataException("The written clothes model info reference does not point to its info row.");
        var info = FindSingle(restored, infoTableName, entry =>
            entry.Values.Count >= 2
            && string.Equals(entry.Values[0].Value as string, request.ModelPath, StringComparison.Ordinal));
        if (!string.Equals(info.Values[1].Value as string, request.TexturePath, StringComparison.Ordinal))
            throw new InvalidDataException("The written clothes-info texture path failed read-back validation.");
        if (GetInteger(info.Values[2]) != request.ClothesInfoType)
            throw new InvalidDataException("The written clothes-info type failed read-back validation.");
    }

    private static CfgBinEntry FindSingle(
        CfgBinDocument document,
        string name,
        Func<CfgBinEntry, bool> predicate)
    {
        var matches = document.Entries.Where(entry => entry.Name == name && predicate(entry)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"Expected exactly one {name} source row, found {matches.Length}.");
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => throw new InvalidDataException("A clothes table identifier is not an integer."),
    };
}
