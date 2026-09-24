using VictoryTool.Application.Diagnostics;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterClothesWriteRequest(
    int SourceUniformModelId,
    uint UniformModelId,
    string ResourceKey,
    string ModelPath,
    string TexturePath,
    string SourceResourceKey = "u05029710");

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
        var modelSource = FindSingle(
            document,
            "CHARA_PARTS_CLOTHES_MODEL",
            entry => entry.Values.Count >= 2 && GetInteger(entry.Values[0]) == request.SourceUniformModelId);
        var modelReferenceSource = document.Entries.SingleOrDefault(entry =>
            entry.Index == modelSource.Index + 1
            && entry.Name == "CHARA_PARTS_CLOTHES_MODEL_REF_INFO"
            && entry.Values.Count >= 2)
            ?? throw new InvalidDataException(
                $"The source clothes model '{request.SourceResourceKey}' has no CHARA_PARTS_CLOTHES_MODEL_REF_INFO row.");
        var infoSource = FindSingle(
            document,
            "CHARA_PARTS_CLOTHES_INFO",
            entry => entry.Values.Count >= 22
                && entry.Values[0].Value is string path
                && path.Equals($"_uniform/{request.SourceResourceKey}/{request.SourceResourceKey}.g4md", StringComparison.Ordinal));

        if (document.Entries.Any(entry => entry.Name == "CHARA_PARTS_CLOTHES_MODEL"
                && entry.Values.Count >= 2
                && GetInteger(entry.Values[0]) == unchecked((int)request.UniformModelId)))
            throw new InvalidDataException("The new uniform model ID already exists.");
        if (document.Entries.Any(entry => entry.Name == "CHARA_PARTS_CLOTHES_MODEL"
                && entry.Values.Count >= 2
                && string.Equals(entry.Values[1].Value as string, request.ResourceKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The new uniform resource key already exists.");

        var modelValues = modelSource.Values.Select(value => value.Value).ToArray();
        modelValues[0] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)unchecked((int)request.UniformModelId)
            : (long)request.UniformModelId;
        modelValues[1] = request.ResourceKey;
        var appendedInfoReference = document.Entries.Count(entry => entry.Name == "CHARA_PARTS_CLOTHES_INFO");
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
        var result = T2bCountedListWriter.InsertClones(
            document,
            modelSource,
            [modelValues],
            "CHARA_PARTS_CLOTHES_MODEL_LIST_BEG",
            "CHARA_PARTS_CLOTHES_MODEL_LIST_END",
            companionRows: [(modelReferenceSource, modelReferenceValues)]);

        var withModel = CfgBinDocument.Read(result);
        var infoTemplate = withModel.Entries.Single(entry => entry.Name == "CHARA_PARTS_CLOTHES_INFO"
            && entry.Values.Count >= 22
            && entry.Values[0].Value is string path
            && path.Equals($"_uniform/{request.SourceResourceKey}/{request.SourceResourceKey}.g4md", StringComparison.Ordinal));
        var infoValues = infoTemplate.Values.Select(value => value.Value).ToArray();
        infoValues[0] = request.ModelPath;
        infoValues[1] = request.TexturePath;
        result = T2bCountedListWriter.InsertClones(
            withModel,
            infoTemplate,
            [infoValues],
            "CHARA_PARTS_CLOTHES_INFO_LIST_BEG",
            "CHARA_PARTS_CLOTHES_INFO_LIST_END");

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
        var model = FindSingle(restored, "CHARA_PARTS_CLOTHES_MODEL", entry =>
            entry.Values.Count >= 2
            && GetInteger(entry.Values[0]) == unchecked((int)request.UniformModelId));
        if (!string.Equals(model.Values[1].Value as string, request.ResourceKey, StringComparison.Ordinal))
            throw new InvalidDataException("The written clothes-model resource key failed read-back validation.");
        var modelReference = restored.Entries.SingleOrDefault(entry =>
            entry.Index == model.Index + 1
            && entry.Name == "CHARA_PARTS_CLOTHES_MODEL_REF_INFO"
            && entry.Values.Count >= 2)
            ?? throw new InvalidDataException("The written clothes model has no info-reference row.");
        var expectedInfoReference = restored.Entries.Count(entry => entry.Name == "CHARA_PARTS_CLOTHES_INFO") - 1;
        if (GetInteger(modelReference.Values[0]) != expectedInfoReference)
            throw new InvalidDataException("The written clothes model info reference does not point to its info row.");
        var info = FindSingle(restored, "CHARA_PARTS_CLOTHES_INFO", entry =>
            entry.Values.Count >= 2
            && string.Equals(entry.Values[0].Value as string, request.ModelPath, StringComparison.Ordinal));
        if (!string.Equals(info.Values[1].Value as string, request.TexturePath, StringComparison.Ordinal))
            throw new InvalidDataException("The written clothes-info texture path failed read-back validation.");
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
