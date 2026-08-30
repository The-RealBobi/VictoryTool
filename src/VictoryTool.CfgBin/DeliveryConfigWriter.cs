using System.Buffers.Binary;

namespace VictoryTool.CfgBin;

public sealed record CharacterDeliveryWriteRequest(
    uint DeliveryId,
    uint ReceivedFlag,
    uint CharacterParameterId);

public sealed record CharacterPromotionCloneRequest(
    int SourceDeliveryIndex,
    int CharacterContentOffset,
    uint DeliveryId,
    uint ReceivedFlag,
    uint CharacterParameterId,
    uint? TitleId = null);

public sealed class DeliveryConfigWriter
{
    private const int RootRecordSize = 0x20;

    public byte[] CloneCharacterPromotion(
        ReadOnlySpan<byte> source,
        CharacterPromotionCloneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = RdbnpDocument.Read(source);
        var contents = document.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var info = document.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        ValidateSchema(contents, info);
        if (request.SourceDeliveryIndex < 0 || request.SourceDeliveryIndex >= info.Rows.Count)
            throw new ArgumentOutOfRangeException(nameof(request), "The source promotion index is outside the delivery list.");
        if (info.Rows.Any(row => row.GetUInt32("idCrc") == request.DeliveryId))
            throw new InvalidDataException("The destination delivery ID already exists.");
        var sourceRange = info.Rows[request.SourceDeliveryIndex].GetTuple("deliveryContents");
        if (sourceRange.Offset < 0 || sourceRange.Count <= 0
            || sourceRange.Offset > contents.Rows.Count - sourceRange.Count
            || request.CharacterContentOffset < 0 || request.CharacterContentOffset >= sourceRange.Count)
            throw new InvalidDataException("The source promotion content range is invalid.");

        var layout = ReadLayout(source, document, contents, info);
        var contentBytes = source.Slice(
            checked(layout.ValueOffset + layout.ContentsRoot.RelativeOffset + sourceRange.Offset * layout.ContentsRoot.RowSize),
            checked(sourceRange.Count * layout.ContentsRoot.RowSize)).ToArray();
        var replacementOffset = checked(request.CharacterContentOffset * layout.ContentsRoot.RowSize);
        if (contentBytes[replacementOffset] != 2)
            throw new InvalidDataException("The selected promotion content is not a character reward.");
        BinaryPrimitives.WriteUInt32LittleEndian(contentBytes.AsSpan(replacementOffset + 8), request.CharacterParameterId);
        var infoBytes = source.Slice(
            checked(layout.ValueOffset + layout.InfoRoot.RelativeOffset + request.SourceDeliveryIndex * layout.InfoRoot.RowSize),
            layout.InfoRoot.RowSize).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(infoBytes, request.DeliveryId);
        if (request.TitleId is { } titleId)
            BinaryPrimitives.WriteUInt32LittleEndian(infoBytes.AsSpan(4), titleId);
        BinaryPrimitives.WriteUInt32LittleEndian(infoBytes.AsSpan(8), request.ReceivedFlag);
        infoBytes[12] = 42;
        BinaryPrimitives.WriteInt16LittleEndian(infoBytes.AsSpan(14), checked((short)layout.ContentsRoot.RowCount));
        BinaryPrimitives.WriteInt16LittleEndian(infoBytes.AsSpan(16), sourceRange.Count);
        return AppendRawRows(source, layout, contentBytes, infoBytes, request.DeliveryId, request.CharacterParameterId, sourceRange.Count);
    }

    public byte[] AppendCharacterDelivery(
        ReadOnlySpan<byte> source,
        CharacterDeliveryWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = RdbnpDocument.Read(source);
        var contents = document.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var info = document.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        ValidateSchema(contents, info);
        if (info.Rows.Any(row => row.GetUInt32("idCrc") == request.DeliveryId))
            throw new InvalidDataException("The destination delivery ID already exists.");

        var dataBase = checked(ReadInt16(source, 0x0A) * 4);
        var rootOffset = RelativeWords(source, 0x2C, dataBase);
        var rootCount = ReadInt16(source, 0x2E);
        var valueOffset = RelativeWords(source, 0x36, dataBase);
        var stringOffset = checked(dataBase + ReadInt32(source, 0x38));
        var roots = new Root[rootCount];
        for (var index = 0; index < rootCount; index++)
            roots[index] = ReadRoot(source, rootOffset + index * RootRecordSize, index);
        Array.Sort(roots, static (left, right) => left.RelativeOffset.CompareTo(right.RelativeOffset));
        var contentsRoot = roots.Single(root => root.Index == document.Lists.IndexOf(contents));
        var infoRoot = roots.Single(root => root.Index == document.Lists.IndexOf(info));
        var additions = new Dictionary<int, byte[]>
        {
            [contentsRoot.Index] = CreateContentRow(
                source, valueOffset, contentsRoot, request.CharacterParameterId),
            [infoRoot.Index] = CreateInfoRow(
                source, valueOffset, infoRoot, request, checked((short)contentsRoot.RowCount)),
        };

        using var values = new MemoryStream();
        var sourceCursor = valueOffset;
        var shifts = new Dictionary<int, int>();
        foreach (var root in roots)
        {
            var rootStart = checked(valueOffset + root.RelativeOffset);
            if (rootStart < sourceCursor)
                throw new InvalidDataException("The delivery RDBNP root value ranges overlap.");
            values.Write(source[sourceCursor..rootStart]);
            shifts[root.Index] = checked((int)values.Length - root.RelativeOffset);
            var rootLength = checked(root.RowSize * root.RowCount);
            values.Write(source.Slice(rootStart, rootLength));
            if (additions.TryGetValue(root.Index, out var addition)) values.Write(addition);
            sourceCursor = checked(rootStart + rootLength);
        }
        if (sourceCursor > stringOffset)
            throw new InvalidDataException("The delivery RDBNP values overlap its string section.");
        values.Write(source[sourceCursor..stringOffset]);
        var valueBytes = values.ToArray();
        var delta = checked(valueBytes.Length - (stringOffset - valueOffset));
        var result = new byte[checked(source.Length + delta)];
        source[..valueOffset].CopyTo(result);
        valueBytes.CopyTo(result.AsSpan(valueOffset));
        source[stringOffset..].CopyTo(result.AsSpan(checked(stringOffset + delta)));

        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x0C), checked(ReadInt32(source, 0x0C) + delta));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x38), checked(ReadInt32(source, 0x38) + delta));
        foreach (var root in roots)
        {
            WriteRoot(result, rootOffset + root.Index * RootRecordSize, root with
            {
                RelativeOffset = checked(root.RelativeOffset + shifts[root.Index]),
                RowCount = root.RowCount + (additions.ContainsKey(root.Index) ? 1 : 0),
            });
        }
        ValidateResult(result, request, contents.Rows.Count, info.Rows.Count);
        return result;
    }

    private static byte[] CreateContentRow(
        ReadOnlySpan<byte> source,
        int valueOffset,
        Root root,
        uint characterParameterId)
    {
        var row = source.Slice(checked(valueOffset + root.RelativeOffset + 21 * root.RowSize), root.RowSize).ToArray();
        row[0] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), characterParameterId);
        row[12] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(20), 0);
        row[24] = 0;
        return row;
    }

    private static byte[] CreateInfoRow(
        ReadOnlySpan<byte> source,
        int valueOffset,
        Root root,
        CharacterDeliveryWriteRequest request,
        short contentIndex)
    {
        var row = source.Slice(checked(valueOffset + root.RelativeOffset + 25 * root.RowSize), root.RowSize).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(row, request.DeliveryId);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), request.ReceivedFlag);
        row[12] = 42;
        row[13] = 2;
        BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(14), contentIndex);
        BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(20), uint.MaxValue);
        return row;
    }

    private static void ValidateSchema(RdbnpList contents, RdbnpList info)
    {
        if (contents.Type.Name != "DELIVERY_CONTENTS_DATA"
            || info.Type.Name != "DELIVERY_INFO"
            || contents.Rows.Count <= 21
            || info.Rows.Count <= 25
            || contents.Type.Fields.Select(field => field.Name).ToArray() is not
                ["contents_type", "itemIdCrc", "charaParamIdCrc", "rarity", "num", "aocIdCrc", "replaceItemIdCrc", "isPassphrase"]
            || info.Type.Fields.Select(field => field.Name).ToArray() is not
                ["idCrc", "title", "receivedFlag", "newFlag", "sendTargetType", "deliveryContents", "openCond"])
            throw new InvalidDataException("The RDBNP is not the verified delivery configuration schema.");
    }

    private static void ValidateResult(byte[] result, CharacterDeliveryWriteRequest request, int oldContents, int oldInfo)
    {
        var document = RdbnpDocument.Read(result);
        var contents = document.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var info = document.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        if (contents.Rows.Count != oldContents + 1 || info.Rows.Count != oldInfo + 1
            || contents.Rows[^1].GetUInt32("charaParamIdCrc") != request.CharacterParameterId
            || info.Rows[^1].GetUInt32("idCrc") != request.DeliveryId
            || info.Rows[^1].GetTuple("deliveryContents") != new RdbnpTuple(checked((short)oldContents), 1))
            throw new InvalidDataException("The appended character delivery failed read-back validation.");
    }

    private static Layout ReadLayout(
        ReadOnlySpan<byte> source,
        RdbnpDocument document,
        RdbnpList contents,
        RdbnpList info)
    {
        var dataBase = checked(ReadInt16(source, 0x0A) * 4);
        var rootOffset = RelativeWords(source, 0x2C, dataBase);
        var rootCount = ReadInt16(source, 0x2E);
        var valueOffset = RelativeWords(source, 0x36, dataBase);
        var stringOffset = checked(dataBase + ReadInt32(source, 0x38));
        var roots = new Root[rootCount];
        for (var index = 0; index < rootCount; index++)
            roots[index] = ReadRoot(source, rootOffset + index * RootRecordSize, index);
        Array.Sort(roots, static (left, right) => left.RelativeOffset.CompareTo(right.RelativeOffset));
        return new Layout(dataBase, rootOffset, valueOffset, stringOffset, roots,
            roots.Single(root => root.Index == document.Lists.IndexOf(contents)),
            roots.Single(root => root.Index == document.Lists.IndexOf(info)));
    }

    private static byte[] AppendRawRows(
        ReadOnlySpan<byte> source,
        Layout layout,
        byte[] contentBytes,
        byte[] infoBytes,
        uint deliveryId,
        uint characterParameterId,
        short contentCount)
    {
        var additions = new Dictionary<int, byte[]>
        {
            [layout.ContentsRoot.Index] = contentBytes,
            [layout.InfoRoot.Index] = infoBytes,
        };
        using var values = new MemoryStream();
        var sourceCursor = layout.ValueOffset;
        var shifts = new Dictionary<int, int>();
        foreach (var root in layout.Roots)
        {
            var rootStart = checked(layout.ValueOffset + root.RelativeOffset);
            if (rootStart < sourceCursor) throw new InvalidDataException("The delivery RDBNP root value ranges overlap.");
            values.Write(source[sourceCursor..rootStart]);
            shifts[root.Index] = checked((int)values.Length - root.RelativeOffset);
            var rootLength = checked(root.RowSize * root.RowCount);
            values.Write(source.Slice(rootStart, rootLength));
            if (additions.TryGetValue(root.Index, out var addition)) values.Write(addition);
            sourceCursor = checked(rootStart + rootLength);
        }
        values.Write(source[sourceCursor..layout.StringOffset]);
        var valueBytes = values.ToArray();
        var delta = checked(valueBytes.Length - (layout.StringOffset - layout.ValueOffset));
        var result = new byte[checked(source.Length + delta)];
        source[..layout.ValueOffset].CopyTo(result);
        valueBytes.CopyTo(result.AsSpan(layout.ValueOffset));
        source[layout.StringOffset..].CopyTo(result.AsSpan(checked(layout.StringOffset + delta)));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x0C), checked(ReadInt32(source, 0x0C) + delta));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x38), checked(ReadInt32(source, 0x38) + delta));
        foreach (var root in layout.Roots)
        {
            var increment = root.Index == layout.ContentsRoot.Index ? contentCount : root.Index == layout.InfoRoot.Index ? 1 : 0;
            WriteRoot(result, layout.RootOffset + root.Index * RootRecordSize, root with
            {
                RelativeOffset = checked(root.RelativeOffset + shifts[root.Index]),
                RowCount = root.RowCount + increment,
            });
        }
        var restored = RdbnpDocument.Read(result);
        var restoredContents = restored.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var restoredInfo = restored.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        var writtenInfo = restoredInfo.Rows.Single(row => row.GetUInt32("idCrc") == deliveryId);
        var range = writtenInfo.GetTuple("deliveryContents");
        if (range.Count != contentCount
            || restoredContents.Rows.Skip(range.Offset).Take(range.Count)
                .All(row => row.GetUInt32("charaParamIdCrc") != characterParameterId))
            throw new InvalidDataException("The cloned character promotion failed read-back validation.");
        return result;
    }

    private static Root ReadRoot(ReadOnlySpan<byte> source, int offset, int index) => new(
        index, ReadInt32(source, offset + 4), ReadInt32(source, offset + 8), ReadInt32(source, offset + 12));

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
    private sealed record Root(int Index, int RelativeOffset, int RowSize, int RowCount);
    private sealed record Layout(int DataBase, int RootOffset, int ValueOffset, int StringOffset,
        Root[] Roots, Root ContentsRoot, Root InfoRoot);
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> source, T item)
    {
        for (var index = 0; index < source.Count; index++)
            if (EqualityComparer<T>.Default.Equals(source[index], item)) return index;
        return -1;
    }
}
