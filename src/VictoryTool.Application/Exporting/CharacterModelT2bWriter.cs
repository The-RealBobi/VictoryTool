using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterModelWriteRequest(
    int SourceModelId,
    uint ModelId,
    string FaceModelPath,
    uint? SkinColorRgba = null,
    int? UniformModel = null,
    int? ShoesModel = null,
    int? GloveModel = null,
    int? EquipmentColor = null,
    int? UniformCollarOpen = null,
    int? EquipmentFlag2 = null,
    int? ChestSize = null,
    int? ForceKit = null,
    int? BodyModelId = null);

public interface ICharacterModelT2bWriter
{
    byte[] Append(ReadOnlySpan<byte> modelTable, CharacterModelWriteRequest request);
}

public sealed class CharacterModelT2bWriter : ICharacterModelT2bWriter
{
    public byte[] Append(ReadOnlySpan<byte> modelTable, CharacterModelWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FaceModelPath);
        if (request.ChestSize is < 0 or > 2)
            throw new InvalidDataException("Chest size must be between 0 and 2.");
        if (request.UniformCollarOpen is not null and not (0 or 1))
            throw new InvalidDataException("The uniform collar-open flag must be 0 or 1.");
        var document = CfgBinDocument.Read(modelTable);
        var sources = document.Entries.Where(entry =>
            entry.Name == "CHARA_MODEL_INFO"
            && entry.Values.Count >= 34
            && GetInteger(entry.Values[0]) == request.SourceModelId).ToArray();
        if (sources.Length != 1)
            throw new InvalidDataException(
                $"Expected exactly one CHARA_MODEL_INFO source row for ID {request.SourceModelId}, found {sources.Length}.");
        var source = sources[0];
        if (source.Values[10].Type != CfgBinValueType.String)
            throw new InvalidDataException("CHARA_MODEL_INFO face-model path is not a string.");
        long expectedId = document.ValueWidth == CfgBinValueWidth.Int32
            ? unchecked((int)request.ModelId)
            : request.ModelId;
        if (document.Entries.Any(entry =>
                entry.Name == "CHARA_MODEL_INFO"
                && entry.Values.Count > 0
                && GetInteger(entry.Values[0]) == expectedId))
            throw new InvalidDataException("The new character model ID already exists.");

        var values = source.Values.Select(value => value.Value).ToArray();
        values[0] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)unchecked((int)request.ModelId)
            : (long)request.ModelId;
        values[10] = request.FaceModelPath;
        if (request.SkinColorRgba is { } skinColor)
            values[16] = document.ValueWidth == CfgBinValueWidth.Int32
                ? (object)unchecked((int)skinColor)
                : (long)skinColor;
        SetOptional(document, values, 4, request.BodyModelId);
        SetOptional(document, values, 5, request.UniformModel);
        SetOptional(document, values, 6, request.ShoesModel);
        SetOptional(document, values, 7, request.GloveModel);
        SetOptional(document, values, 8, request.EquipmentColor);
        SetOptional(document, values, 12, request.UniformCollarOpen);
        SetOptional(document, values, 13, request.EquipmentFlag2);
        SetOptional(document, values, 31, request.ChestSize);
        SetOptional(document, values, 33, request.ForceKit);
        var result = T2bCountedListWriter.InsertClones(
            document, source, [values],
            "CHARA_MODEL_INFO_LIST_BEG", "CHARA_MODEL_INFO_LIST_END");
        var restored = CfgBinDocument.Read(result);
        var written = restored.Entries.Single(entry =>
            entry.Name == "CHARA_MODEL_INFO"
            && entry.Values.Count >= 34
            && GetInteger(entry.Values[0]) == expectedId);
        if (!string.Equals(written.Values[10].Value as string, request.FaceModelPath, StringComparison.Ordinal))
            throw new InvalidDataException("The written character model path failed read-back validation.");
        if (request.SkinColorRgba is { } requestedSkinColor
            && unchecked((uint)GetInteger(written.Values[16])) != requestedSkinColor)
            throw new InvalidDataException("The written character skin colour failed read-back validation.");
        if (request.BodyModelId is { } requestedBodyModelId
            && GetInteger(written.Values[4]) != requestedBodyModelId)
            throw new InvalidDataException("The written character body model reference failed read-back validation.");
        for (var index = 1; index < source.Values.Count; index++)
        {
            if (index == 10
                || (index == 5 && request.UniformModel is not null)
                || (index == 6 && request.ShoesModel is not null)
                || (index == 7 && request.GloveModel is not null)
                || (index == 8 && request.EquipmentColor is not null)
                || (index == 12 && request.UniformCollarOpen is not null)
                || (index == 13 && request.EquipmentFlag2 is not null)
                || (index == 16 && request.SkinColorRgba is not null)
                || (index == 31 && request.ChestSize is not null)
                || (index == 33 && request.ForceKit is not null)
                || (index == 4 && request.BodyModelId is not null)) continue;
            if (!Equals(source.Values[index].Value, written.Values[index].Value))
                throw new InvalidDataException($"Opaque character model field {index} changed unexpectedly.");
        }
        return result;
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int intValue => intValue,
        long longValue => longValue,
        _ => long.MinValue,
    };

    private static void SetOptional(CfgBinDocument document, object?[] values, int index, int? value)
    {
        if (value is not { } present) return;
        values[index] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)present
            : (long)present;
    }
}
