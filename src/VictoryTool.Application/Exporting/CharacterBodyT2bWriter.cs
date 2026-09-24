using VictoryTool.Application.Diagnostics;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterBodyWriteRequest(
    int SourceBodyModelId,
    int BodyModelId,
    string BodyModelPath,
    string? SkeletonModelPath = null);

public interface ICharacterBodyT2bWriter
{
    byte[] Append(ReadOnlySpan<byte> modelTable, CharacterBodyWriteRequest request);
}

public sealed class CharacterBodyT2bWriter : ICharacterBodyT2bWriter
{
    public byte[] Append(ReadOnlySpan<byte> modelTable, CharacterBodyWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BodyModelPath);
        using var operation = GlobalLog.BeginOperation("character_body_write", new Dictionary<string, object?>
        {
            ["sourceBodyModelId"] = request.SourceBodyModelId,
            ["bodyModelId"] = request.BodyModelId,
        });

        var document = CfgBinDocument.Read(modelTable);
        var source = FindSingle(document, request.SourceBodyModelId);
        if (source.Values[2].Type != CfgBinValueType.String)
            throw new InvalidDataException("CHARA_BODY_INFO model path is not a string.");
        if (request.SkeletonModelPath is not null && source.Values[1].Type != CfgBinValueType.String)
            throw new InvalidDataException("CHARA_BODY_INFO skeleton model path is not a string.");
        if (document.Entries.Any(entry => entry.Name == "CHARA_BODY_INFO"
                && entry.Values.Count >= 1
                && GetInteger(entry.Values[0]) == request.BodyModelId))
            throw new InvalidDataException($"The new body model ID {request.BodyModelId} already exists.");

        var values = source.Values.Select(value => value.Value).ToArray();
        values[0] = document.ValueWidth == CfgBinValueWidth.Int32
            ? (object)request.BodyModelId
            : (long)request.BodyModelId;
        values[2] = request.BodyModelPath;
        if (request.SkeletonModelPath is not null) values[1] = request.SkeletonModelPath;
        var result = T2bCountedListWriter.InsertClones(
            document,
            source,
            [values],
            "CHARA_BODY_INFO_LIST_BEG",
            "CHARA_BODY_INFO_LIST_END");

        var restored = CfgBinDocument.Read(result);
        var written = FindSingle(restored, request.BodyModelId);
        if (!string.Equals(written.Values[2].Value as string, request.BodyModelPath, StringComparison.Ordinal))
            throw new InvalidDataException("The written body model path failed read-back validation.");
        if (!string.Equals(written.Values[1].Value as string, request.SkeletonModelPath ?? source.Values[1].Value as string, StringComparison.Ordinal))
            throw new InvalidDataException("The written skeleton model path failed read-back validation.");
        for (var index = 1; index < source.Values.Count; index++)
        {
            if (index == 2 || (index == 1 && request.SkeletonModelPath is not null)) continue;
            if (!Equals(source.Values[index].Value, written.Values[index].Value))
                throw new InvalidDataException($"Opaque body model field {index} changed unexpectedly.");
        }
        GlobalLog.Debug("character_body_written", new Dictionary<string, object?>
        {
            ["tableBytes"] = result.Length,
        });
        return result;
    }

    private static CfgBinEntry FindSingle(CfgBinDocument document, int bodyModelId)
    {
        var matches = document.Entries.Where(entry => entry.Name == "CHARA_BODY_INFO"
            && entry.Values.Count >= 7
            && GetInteger(entry.Values[0]) == bodyModelId).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Expected exactly one CHARA_BODY_INFO row for ID {bodyModelId}, found {matches.Length}.");
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int number => number,
        long number => number,
        _ => long.MinValue,
    };
}
