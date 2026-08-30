using System.Buffers.Binary;

namespace VictoryTool.CfgBin;

public sealed class CharaFaceIconConfigWriter
{
    private const int HeaderSize = 0x3C;
    private const int RootRecordSize = 0x20;

    public byte[] CloneSceneSettings(ReadOnlySpan<byte> source, uint sourceCharacterId, uint destinationCharacterId)
    {
        var document = RdbnpDocument.Read(source);
        var sceneList = document.Lists.Single(list => list.Name == "m_CharaFaceIconSceneDataList");
        var infoList = document.Lists.Single(list => list.Name == "m_CharaFaceIconInfoList");
        ValidateSchema(sceneList, infoList);
        if (infoList.Rows.Any(row => row.GetUInt32("charaId") == destinationCharacterId))
            throw new InvalidDataException("The destination character already has face-icon scene settings.");
        var sourceRow = infoList.Rows.Single(row => row.GetUInt32("charaId") == sourceCharacterId);
        var sceneReference = sourceRow.GetTuple("sceneDataRef");
        if (sceneReference.Offset < 0 || sceneReference.Count <= 0
            || sceneReference.Offset > sceneList.Rows.Count - sceneReference.Count)
            throw new InvalidDataException("The source face-icon scene range is invalid.");

        var dataBase = checked(ReadInt16(source, 0x0A) * 4);
        var rootOffset = RelativeWords(source, 0x2C, dataBase);
        var valueOffset = RelativeWords(source, 0x36, dataBase);
        var stringOffset = checked(dataBase + ReadInt32(source, 0x38));
        var sceneRoot = ReadRoot(source, rootOffset);
        var infoRoot = ReadRoot(source, rootOffset + RootRecordSize);
        var sceneBytes = checked(sceneReference.Count * sceneRoot.RowSize);
        var sourceSceneOffset = checked(valueOffset + sceneRoot.RelativeOffset
            + sceneReference.Offset * sceneRoot.RowSize);
        var infoEnd = checked(valueOffset + infoRoot.RelativeOffset + infoRoot.RowSize * infoRoot.RowCount);
        if (infoEnd != stringOffset)
            throw new InvalidDataException("The face-icon RDBNP has an unsupported value/string layout.");

        var result = new byte[checked(source.Length + sceneBytes + infoRoot.RowSize)];
        source[..checked(valueOffset + infoRoot.RelativeOffset)].CopyTo(result);
        source.Slice(sourceSceneOffset, sceneBytes).CopyTo(
            result.AsSpan(checked(valueOffset + infoRoot.RelativeOffset), sceneBytes));
        var shiftedInfoOffset = checked(valueOffset + infoRoot.RelativeOffset + sceneBytes);
        source.Slice(checked(valueOffset + infoRoot.RelativeOffset), infoRoot.RowSize * infoRoot.RowCount)
            .CopyTo(result.AsSpan(shiftedInfoOffset));
        var appendedInfoOffset = checked(shiftedInfoOffset + infoRoot.RowSize * infoRoot.RowCount);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(appendedInfoOffset), destinationCharacterId);
        BinaryPrimitives.WriteInt16LittleEndian(
            result.AsSpan(appendedInfoOffset + 4), checked((short)sceneRoot.RowCount));
        BinaryPrimitives.WriteInt16LittleEndian(
            result.AsSpan(appendedInfoOffset + 6), sceneReference.Count);
        source[stringOffset..].CopyTo(result.AsSpan(checked(stringOffset + sceneBytes + infoRoot.RowSize)));

        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(0x0C), checked(ReadInt32(source, 0x0C) + sceneBytes + infoRoot.RowSize));
        BinaryPrimitives.WriteInt32LittleEndian(
            result.AsSpan(0x38), checked(ReadInt32(source, 0x38) + sceneBytes + infoRoot.RowSize));
        WriteRoot(result, rootOffset, sceneRoot with { RowCount = sceneRoot.RowCount + sceneReference.Count });
        WriteRoot(result, rootOffset + RootRecordSize, infoRoot with
        {
            RelativeOffset = infoRoot.RelativeOffset + sceneBytes,
            RowCount = infoRoot.RowCount + 1,
        });
        return result;
    }

    private static void ValidateSchema(RdbnpList scenes, RdbnpList info)
    {
        if (scenes.Type.Name != "CHARA_FACE_ICON_SCENE_DATA"
            || info.Type.Name != "CHARA_FACE_ICON_CONFIG"
            || scenes.Type.Fields.Select(field => field.Name).ToArray() is not ["sceneType", "offsetPos", "scale"]
            || info.Type.Fields.Select(field => field.Name).ToArray() is not ["charaId", "sceneDataRef"])
            throw new InvalidDataException("The RDBNP is not the verified character face-icon configuration schema.");
    }

    private static Root ReadRoot(ReadOnlySpan<byte> source, int offset) => new(
        ReadInt32(source, offset + 4), ReadInt32(source, offset + 8), ReadInt32(source, offset + 12));

    private static void WriteRoot(Span<byte> destination, int offset, Root root)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[(offset + 4)..], root.RelativeOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(offset + 8)..], root.RowSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination[(offset + 12)..], root.RowCount);
    }

    private static int RelativeWords(ReadOnlySpan<byte> source, int offset, int dataBase) =>
        checked(dataBase + ReadInt16(source, offset) * 4);
    private static short ReadInt16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(source[offset..]);
    private static int ReadInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
    private sealed record Root(int RelativeOffset, int RowSize, int RowCount);
}
