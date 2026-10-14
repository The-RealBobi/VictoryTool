using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.G4.Animations;

public static class G4mtAnimationClipAppender
{
    public static byte[] Append(G4mtAnimationBank target, G4mtAnimationBank source, IReadOnlySet<uint> clipHashes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clipHashes);
        if (clipHashes.Count == 0) return target.Source.ToArray();

        var selected = source.Clips.Where(clip => clipHashes.Contains(clip.NameHash)).ToArray();
        if (selected.Length != clipHashes.Count)
            throw new InvalidDataException("One or more requested source animation clips were not found.");
        var existing = target.Clips.GroupBy(clip => clip.NameHash)
            .ToDictionary(group => group.Key, group => group.Select(clip => clip.Name).Distinct().ToArray());
        foreach (var clip in selected)
        {
            if (existing.TryGetValue(clip.NameHash, out var oldNames))
            {
                if (oldNames.Any(name => !string.Equals(name, clip.Name, StringComparison.Ordinal)))
                    throw new InvalidDataException($"G4MT clip hash {clip.NameHash:X8} is already used by another name.");
            }
            else if ((clip.Flags & 1) != 0)
                throw new NotSupportedException($"Additive G4MT clip '{clip.Name}' cannot be appended as an absolute pose.");
        }
        selected = selected.Where(clip => !existing.ContainsKey(clip.NameHash)).ToArray();
        if (selected.Length == 0) return target.Source.ToArray();

        var targetIndices = target.TargetHashes.Select((hash, index) => (hash, index))
            .ToDictionary(pair => pair.hash, pair => pair.index);
        var newInfos = new MemoryStream();
        var newChannels = new MemoryStream();
        var newKeys = new MemoryStream();
        var newData = new MemoryStream();
        var addedScales = new List<float>();
        var clipRows = target.Clips.Select(clip => target.Source.AsMemory(0x40 + clip.Index * 16, 16).ToArray()).ToList();
        var clipHashesInOrder = target.Clips.Select(clip => clip.NameHash).ToList();
        var clipNames = target.Clips.Select(clip => clip.Name).ToList();
        var appendedClipIndices = selected.Select((clip, index) =>
                (clip.Index, NewIndex: checked((ushort)(target.Clips.Count + index))))
            .ToDictionary(item => item.Index, item => item.NewIndex);
        var oldDataLength = target.Source.Length - target.DataOffset;
        var oldKeyCount = target.Channels.Select(channel => checked((int)(channel.KeyStart + channel.KeyCount))).DefaultIfEmpty().Max();
        var oldInfoCount = target.TargetInfos.Count;
        var oldChannelCount = target.Channels.Count;

        foreach (var clip in selected)
        {
            var newInfoStart = checked(oldInfoCount + (int)(newInfos.Length / 8));
            var row = source.Source.AsSpan(0x40 + clip.Index * 16, 16).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(4), (ushort)newInfoStart);
            BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(6), clip.TargetInfoCount);
            row[12] = checked((byte)(newInfoStart >> 16));
            clipRows.Add(row);
            clipHashesInOrder.Add(clip.NameHash);
            clipNames.Add(clip.Name);

            for (var infoIndex = clip.TargetInfoStart; infoIndex < clip.TargetInfoStart + clip.TargetInfoCount; infoIndex++)
            {
                var info = source.TargetInfos[infoIndex];
                var sourceHash = source.TargetHashes[info.TargetIndex];
                if (!targetIndices.TryGetValue(sourceHash, out var targetIndex))
                    throw new InvalidDataException($"Target G4MT bank has no joint {sourceHash:X8} used by clip '{clip.Name}'.");
                var newChannelStart = checked(oldChannelCount + (int)(newChannels.Length / 20));
                var infoRecord = source.Source.AsSpan(source.TargetInfoTableOffset + info.Index * 8, 8).ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(infoRecord, checked((ushort)targetIndex));
                BinaryPrimitives.WriteUInt16LittleEndian(infoRecord.AsSpan(2), (ushort)newChannelStart);
                infoRecord[5] = checked((byte)(newChannelStart >> 16));
                newInfos.Write(infoRecord);

                for (var channelIndex = info.ChannelStart; channelIndex < info.ChannelStart + info.ChannelCount; channelIndex++)
                {
                    var channel = source.Channels[channelIndex];
                    var record = source.Source.AsSpan(source.ChannelTableOffset + channel.Index * 20, 20).ToArray();
                    var keyStart = checked(oldKeyCount + (int)(newKeys.Length / 2));
                    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), checked((uint)keyStart));
                    for (var keyIndex = 0; keyIndex < channel.Keys.Count; keyIndex++)
                        WriteU16(newKeys, channel.Keys[keyIndex]);

                    var dataStart = checked(oldDataLength + (int)newData.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), checked((uint)dataStart));
                    var sourceData = checked(source.DataOffset + (int)channel.DataOffset);
                    var byteCount = checked((int)channel.KeyCount * channel.BytesPerKey);
                    Require(source.Source, sourceData, byteCount);
                    newData.Write(source.Source, sourceData, byteCount);

                    var scale = source.Scales[channel.ScaleIndex];
                    var scaleIndex = FindScale(target.Scales, addedScales, scale);
                    if (scaleIndex < 0)
                    {
                        scaleIndex = target.Scales.Count + addedScales.Count;
                        if (scaleIndex > byte.MaxValue)
                            throw new InvalidDataException("The appended G4MT scales exceed the channel scale-index limit.");
                        addedScales.Add(scale);
                    }
                    record[6] = checked((byte)scaleIndex);
                    newChannels.Write(record);
                }
            }
        }

        var clipOrder = target.ClipOrder.Select((index, order) => (Index: (int)index, Order: order))
            .Concat(source.ClipOrder.Select((index, order) =>
                    appendedClipIndices.TryGetValue(index, out var newIndex)
                        ? (Index: (int)newIndex, Order: target.ClipOrder.Count + order)
                        : ((int Index, int Order)?)null)
                .Where(item => item.HasValue)
                .Select(item => item!.Value))
            .OrderBy(item => clipHashesInOrder[item.Index])
            .ThenBy(item => item.Order)
            .Select(item => checked((ushort)item.Index))
            .ToArray();
        return Assemble(target, clipRows, clipHashesInOrder, clipNames, clipOrder,
            newInfos.ToArray(), newChannels.ToArray(), newKeys.ToArray(), newData.ToArray(), addedScales);
    }

    private static byte[] Assemble(G4mtAnimationBank target, IReadOnlyList<byte[]> clipRows,
        IReadOnlyList<uint> hashes, IReadOnlyList<string> names, IReadOnlyList<ushort> clipOrder,
        byte[] newInfos, byte[] newChannels,
        byte[] newKeys, byte[] newData, IReadOnlyList<float> addedScales)
    {
        var source = target.Source;
        var headerWords = U16(source, 0x0A);
        var clipCount = target.Clips.Count;
        var targetCount = target.TargetHashes.Count;
        var shift = target.OffsetShift;
        var scaleOffset = Offset(source, 0x28, 0);
        var clipHashOffset = Offset(source, 0x2A, 0);
        var targetHashOffset = Offset(source, 0x2C, 0);
        var nameOffset = Offset(source, 0x2E, 0);
        var infoOffset = Offset(source, 0x24, shift);
        var channelOffset = Offset(source, 0x26, shift);
        var keyOffset = Offset(source, 0x30, shift * 2);
        var dataOffset = Offset(source, 0x32, shift * 2);
        var channelEnd = checked(channelOffset + target.Channels.Count * 20);
        var keyCount = target.Channels.Select(channel => checked((int)(channel.KeyStart + channel.KeyCount))).DefaultIfEmpty().Max();
        var keyEnd = checked(keyOffset + keyCount * 2);
        var infoEnd = checked(infoOffset + target.TargetInfos.Count * 8);
        var newClipCount = clipRows.Count;
        if (newClipCount > ushort.MaxValue || clipRows.Any(row => row.Length != 16))
            throw new InvalidDataException("The appended G4MT clip table is too large or malformed.");

        var clipTable = Join(clipRows.ToArray());
        if (clipTable.Length != newClipCount * 16)
            throw new InvalidDataException("The G4MT clip table has an unsupported layout.");
        var scales = Slice(source, scaleOffset, clipHashOffset - scaleOffset);
        using (var appendedScales = new MemoryStream())
        {
            appendedScales.Write(scales);
            foreach (var scale in addedScales) WriteF32(appendedScales, scale);
            scales = appendedScales.ToArray();
        }
        var clipHashes = new byte[newClipCount * 4];
        for (var index = 0; index < hashes.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(clipHashes.AsSpan(index * 4), hashes[index]);
        var targetHashes = Slice(source, targetHashOffset, nameOffset - targetHashOffset);
        var namesBlock = MakeNamesSection(source, nameOffset, names, clipOrder, clipCount);

        var infos = Join(Slice(source, infoOffset, infoEnd - infoOffset), newInfos,
            Slice(source, infoEnd, channelOffset - infoEnd));
        var channels = Join(Slice(source, channelOffset, channelEnd - channelOffset), newChannels,
            Slice(source, channelEnd, keyOffset - channelEnd));
        var keys = Join(Slice(source, keyOffset, keyEnd - keyOffset), newKeys,
            Slice(source, keyEnd, dataOffset - keyEnd));
        var data = Join(Slice(source, dataOffset, source.Length - dataOffset), newData);

        var blocks = new (string Name, byte[] Data, int OffsetShift)[]
        {
            ("scales", scales, 0), ("clipHashes", clipHashes, 0), ("targetHashes", targetHashes, 0),
            ("names", namesBlock, 0), ("infos", infos, shift), ("channels", channels, shift),
            ("keys", keys, shift * 2), ("data", data, shift * 2),
        };
        var output = new MemoryStream();
        output.Write(source.AsSpan(0, 0x40));
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        output.Write(clipTable);
        foreach (var block in blocks)
        {
            Align(output, 4 << block.OffsetShift);
            offsets.Add(block.Name, checked((int)output.Position));
            output.Write(block.Data);
        }
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x20), checked((ushort)newClipCount));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C), checked((uint)(result.Length - 0x40)));
        SetOffset(result, 0x28, offsets["scales"], headerWords, 0);
        SetOffset(result, 0x2A, offsets["clipHashes"], headerWords, 0);
        SetOffset(result, 0x2C, offsets["targetHashes"], headerWords, 0);
        SetOffset(result, 0x2E, offsets["names"], headerWords, 0);
        SetOffset(result, 0x24, offsets["infos"], headerWords, shift);
        SetOffset(result, 0x26, offsets["channels"], headerWords, shift);
        SetOffset(result, 0x30, offsets["keys"], headerWords, shift * 2);
        SetOffset(result, 0x32, offsets["data"], headerWords, shift * 2);
        _ = G4mtAnimationBank.Read(result);
        return result;
    }

    private static byte[] MakeNamesSection(byte[] source, int offset,
        IReadOnlyList<string> names, IReadOnlyList<ushort> clipOrder, int oldCount)
    {
        var oldNamesOffsetTable = FindNamesOffsetTable(source, offset, oldCount, names);
        var gap = Slice(source, offset + oldCount * 2, oldNamesOffsetTable - (offset + oldCount * 2));
        using var result = new MemoryStream();
        foreach (var index in clipOrder) WriteU16(result, index);
        result.Write(gap);
        var stringBase = checked(names.Count * 2);
        var pool = new MemoryStream();
        for (var index = 0; index < names.Count; index++)
        {
            var nameOffset = checked(stringBase + (int)pool.Length);
            if (nameOffset > ushort.MaxValue)
                throw new InvalidDataException("The appended G4MT clip names exceed their 16-bit offsets.");
            WriteU16(result, (ushort)nameOffset);
            var bytes = Encoding.UTF8.GetBytes(names[index] + "\0");
            pool.Write(bytes);
        }
        result.Write(pool.ToArray());
        Align(result, 4);
        return result.ToArray();
    }

    private static int FindNamesOffsetTable(byte[] data, int sectionOffset, int oldCount, IReadOnlyList<string> names)
    {
        var start = AlignValue(sectionOffset + oldCount * 2, 4);
        var end = Math.Min(data.Length, sectionOffset + Math.Max(0x1000, oldCount * 8));
        for (var table = start; table + oldCount * 2 <= end; table += 2)
        {
            var matches = true;
            for (var index = 0; index < oldCount; index++)
            {
                var relative = U16(data, table + index * 2);
                var nameStart = table + relative;
                if (relative < oldCount * 2 || nameStart >= data.Length || ReadCString(data, nameStart) != names[index])
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return table;
        }
        throw new InvalidDataException("The source G4MT clip-name offsets could not be located.");
    }

    private static int FindScale(IReadOnlyList<float> target, IReadOnlyList<float> appended, float value)
    {
        for (var index = 0; index < target.Count; index++)
            if (BitConverter.SingleToInt32Bits(target[index]) == BitConverter.SingleToInt32Bits(value)) return index;
        for (var index = 0; index < appended.Count; index++)
            if (BitConverter.SingleToInt32Bits(appended[index]) == BitConverter.SingleToInt32Bits(value)) return target.Count + index;
        return -1;
    }

    private static int Offset(byte[] data, int field, int shift) =>
        checked((U16(data, 0x0A) + (U16(data, field) << shift)) * 4);

    private static void SetOffset(byte[] data, int field, int offset, int headerWords, int shift)
    {
        var units = offset / 4 - headerWords;
        var divisor = 1 << shift;
        if (offset % 4 != 0 || units < 0 || units % divisor != 0 || units / divisor > ushort.MaxValue)
            throw new InvalidDataException("An appended G4MT section offset cannot be represented.");
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field), (ushort)(units / divisor));
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        Require(data, offset, length);
        return data.AsSpan(offset, length).ToArray();
    }

    private static byte[] Join(params byte[][] blocks)
    {
        using var result = new MemoryStream();
        foreach (var block in blocks) result.Write(block);
        return result.ToArray();
    }

    private static void Align(Stream stream, int alignment)
    {
        var padding = (-stream.Position) & (alignment - 1);
        for (var index = 0; index < padding; index++) stream.WriteByte(0);
    }

    private static int AlignValue(int value, int alignment) => checked((value + alignment - 1) & ~(alignment - 1));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static void WriteU16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }
    private static void WriteF32(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        stream.Write(bytes);
    }
    private static string ReadCString(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length) return string.Empty;
        var end = Array.IndexOf(data, (byte)0, offset);
        return end <= offset ? string.Empty : Encoding.UTF8.GetString(data, offset, end - offset);
    }
    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("An appended G4MT section exceeds the animation bank bounds.");
    }
}
