using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.G4.Animations;

public sealed record G4mtClip(int Index, string Name, uint NameHash, ushort StartFrame, ushort EndFrame,
    byte Flags, byte FramesPerSecond, int TargetInfoStart, ushort TargetInfoCount);

public sealed record G4mtTargetInfo(int Index, ushort TargetIndex, int ChannelStart, byte ChannelCount);

public sealed record G4mtChannel(int Index, byte Type, byte Codec, byte Interpolation, byte Variant,
    byte ComponentCount, byte BytesPerKey, byte ScaleIndex, uint KeyStart, uint DataOffset, uint KeyCount,
    IReadOnlyList<ushort> Keys);

public sealed class G4mtAnimationBank
{
    private G4mtAnimationBank(byte[] source, int channelTableOffset, int targetInfoTableOffset, int keyTableOffset,
        int dataOffset, int offsetShift, IReadOnlyList<float> scales, IReadOnlyList<G4mtClip> clips,
        IReadOnlyList<ushort> clipOrder, IReadOnlyList<uint> targetHashes, IReadOnlyList<G4mtTargetInfo> targetInfos,
        IReadOnlyList<G4mtChannel> channels)
    {
        Source = source;
        ChannelTableOffset = channelTableOffset;
        TargetInfoTableOffset = targetInfoTableOffset;
        KeyTableOffset = keyTableOffset;
        DataOffset = dataOffset;
        OffsetShift = offsetShift;
        Scales = scales;
        Clips = clips;
        ClipOrder = clipOrder;
        TargetHashes = targetHashes;
        TargetInfos = targetInfos;
        Channels = channels;
    }

    public byte[] Source { get; }
    public int ChannelTableOffset { get; }
    public int TargetInfoTableOffset { get; }
    public int KeyTableOffset { get; }
    public int DataOffset { get; }
    public int OffsetShift { get; }
    public IReadOnlyList<float> Scales { get; }
    public IReadOnlyList<G4mtClip> Clips { get; }
    public IReadOnlyList<ushort> ClipOrder { get; }
    public IReadOnlyList<uint> TargetHashes { get; }
    public IReadOnlyList<G4mtTargetInfo> TargetInfos { get; }
    public IReadOnlyList<G4mtChannel> Channels { get; }

    public float[] DecodeKey(int channelIndex, int keyIndex)
    {
        var channel = Channels[channelIndex];
        if ((uint)keyIndex >= channel.KeyCount)
            throw new ArgumentOutOfRangeException(nameof(keyIndex));
        var elementSize = channel.Variant;
        var offset = checked(DataOffset + (int)channel.DataOffset + keyIndex * channel.BytesPerKey);
        Require(Source, offset, channel.BytesPerKey);
        var result = new float[channel.ComponentCount];
        var scale = Scales[channel.ScaleIndex];
        for (var component = 0; component < result.Length; component++)
        {
            var valueOffset = offset + component * elementSize;
            result[component] = (channel.Codec, channel.Variant) switch
            {
                (1, 1) => unchecked((sbyte)Source[valueOffset]),
                (1, 2) => BinaryPrimitives.ReadInt16LittleEndian(Source.AsSpan(valueOffset, 2)),
                (1, 4) => F32(Source, valueOffset),
                (2, 1) => Source[valueOffset] * scale / 256f,
                (2, 2) => U16(Source, valueOffset) * scale / 65536f,
                (3, 1) => unchecked((sbyte)Source[valueOffset]) * scale / 128f,
                (3, 2) => BinaryPrimitives.ReadInt16LittleEndian(Source.AsSpan(valueOffset, 2)) * scale / 32768f,
                _ => throw new NotSupportedException($"G4MT codec {channel.Codec}/{channel.Variant} is not supported."),
            };
        }
        return result;
    }

    public float[] SampleChannel(int channelIndex, float frame)
    {
        var channel = Channels[channelIndex];
        if (channel.Keys.Count == 0)
            return [];
        var right = 0;
        while (right < channel.Keys.Count && channel.Keys[right] <= frame)
            right++;
        if (right == 0)
            return DecodeKey(channelIndex, 0);
        var left = right - 1;
        if (right == channel.Keys.Count || channel.Interpolation == 0)
            return DecodeKey(channelIndex, left);
        var start = channel.Keys[left];
        var span = channel.Keys[right] - start;
        var amount = span == 0 ? 0f : (frame - start) / span;
        var first = DecodeKey(channelIndex, left);
        var second = DecodeKey(channelIndex, right);
        if (channel.Type == 9)
            return Slerp(first, second, amount);
        for (var component = 0; component < first.Length; component++)
            first[component] += (second[component] - first[component]) * amount;
        return first;
    }

    private static float[] Slerp(float[] first, float[] second, float amount)
    {
        if (first.Length != 4)
            throw new InvalidDataException("A G4MT quaternion channel must contain four components.");
        var firstLength = MathF.Sqrt(first.Sum(value => value * value));
        var secondLength = MathF.Sqrt(second.Sum(value => value * value));
        if (firstLength < 1e-12f) { first = [0, 0, 0, 1]; firstLength = 1; }
        if (secondLength < 1e-12f) { second = [0, 0, 0, 1]; secondLength = 1; }
        for (var index = 0; index < 4; index++)
        {
            first[index] /= firstLength;
            second[index] /= secondLength;
        }
        var dot = first.Zip(second, (left, right) => left * right).Sum();
        if (dot < 0)
        {
            for (var index = 0; index < 4; index++)
                second[index] = -second[index];
            dot = -dot;
        }
        if (dot > 0.9995f)
        {
            for (var index = 0; index < 4; index++)
                first[index] += (second[index] - first[index]) * amount;
            var length = MathF.Sqrt(first.Sum(value => value * value));
            return first.Select(value => value / length).ToArray();
        }
        var theta = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        var denominator = MathF.Sin(theta);
        var leftWeight = MathF.Sin((1f - amount) * theta) / denominator;
        var rightWeight = MathF.Sin(amount * theta) / denominator;
        for (var index = 0; index < 4; index++)
            first[index] = first[index] * leftWeight + second[index] * rightWeight;
        return first;
    }

    public static G4mtAnimationBank Read(ReadOnlySpan<byte> input)
    {
        if (input.Length < 0x40 || !input[..4].SequenceEqual("G4MT"u8))
            throw new InvalidDataException("The input is not a G4MT animation bank.");
        var source = input.ToArray();
        var headerSize = U16(source, 4);
        var headerWords = U16(source, 0x0A);
        var clipCount = U16(source, 0x20);
        var targetCount = U16(source, 0x22);
        var shift = source[0x36];
        if (headerSize != 0x40 || headerWords * 4 != headerSize || shift > 4)
            throw new InvalidDataException("The G4MT header uses an unsupported layout.");

        var sectionUnits = Enumerable.Range(0, 6).Select(index => U16(source, 0x28 + index * 2)).ToArray();
        var scaleOffset = checked((headerWords + sectionUnits[0]) * 4);
        var clipHashOffset = checked((headerWords + sectionUnits[1]) * 4);
        var targetHashOffset = checked((headerWords + sectionUnits[2]) * 4);
        var nameOffset = checked((headerWords + sectionUnits[3]) * 4);
        var targetInfoOffset = ScaledOffset(headerWords, U16(source, 0x24), shift);
        var channelOffset = ScaledOffset(headerWords, U16(source, 0x26), shift);
        var keyOffset = ScaledOffset(headerWords, sectionUnits[4], shift * 2);
        var dataOffset = ScaledOffset(headerWords, sectionUnits[5], shift * 2);
        Require(source, headerSize, clipCount * 0x10);
        if (scaleOffset < headerSize + clipCount * 0x10 || clipHashOffset < scaleOffset || targetHashOffset < clipHashOffset)
            throw new InvalidDataException("The G4MT section order is invalid.");
        Require(source, targetHashOffset, targetCount * 4);
        var scalesLength = clipHashOffset - scaleOffset;
        if (scalesLength % 4 != 0)
            throw new InvalidDataException("The G4MT scale table is misaligned.");
        Require(source, scaleOffset, scalesLength);
        var scales = Enumerable.Range(0, scalesLength / 4).Select(i => F32(source, scaleOffset + i * 4)).ToArray();
        if (scales.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("The G4MT scale table contains a non-finite value.");

        var (clipNames, clipOrder) = ReadClipNames(source, nameOffset, clipCount);
        var rows = new (ushort Start, ushort End, ushort InfoStart, ushort InfoCount, byte Flags, byte Fps, byte InfoHigh)[clipCount];
        var maxInfo = 0;
        for (var index = 0; index < clipCount; index++)
        {
            var offset = headerSize + index * 0x10;
            var start = U16(source, offset);
            var end = U16(source, offset + 2);
            var infoStart = U16(source, offset + 4);
            var infoCount = U16(source, offset + 6);
            var high = source[offset + 10];
            rows[index] = (start, end, infoStart, infoCount, source[offset + 8], source[offset + 9], high);
            maxInfo = Math.Max(maxInfo, infoStart + high * 65536 + infoCount);
        }
        Require(source, targetInfoOffset, maxInfo * 8);
        var infos = new G4mtTargetInfo[maxInfo];
        var channelCount = 0;
        for (var index = 0; index < maxInfo; index++)
        {
            var offset = targetInfoOffset + index * 8;
            var targetIndex = U16(source, offset);
            var channelStart = U16(source, offset + 2) + source[offset + 5] * 65536;
            var count = source[offset + 4];
            if (targetIndex >= targetCount)
                throw new InvalidDataException($"G4MT target-info {index} refers to an invalid target.");
            infos[index] = new G4mtTargetInfo(index, targetIndex, channelStart, count);
            channelCount = Math.Max(channelCount, channelStart + count);
        }
        Require(source, channelOffset, channelCount * 20);
        var channels = new G4mtChannel[channelCount];
        var keyCount = 0;
        for (var index = 0; index < channelCount; index++)
        {
            var offset = channelOffset + index * 20;
            var keyStart = U32(source, offset + 8);
            var dataRelative = U32(source, offset + 12);
            var count = U32(source, offset + 16);
            var scaleIndex = source[offset + 6];
            if (source[offset + 4] == 0 || source[offset + 5] == 0 || scaleIndex >= scales.Length)
                throw new InvalidDataException($"G4MT channel {index} has invalid encoding metadata.");
            keyCount = Math.Max(keyCount, checked((int)(keyStart + count)));
            channels[index] = new G4mtChannel(index, source[offset], source[offset + 1], source[offset + 2],
                source[offset + 3], source[offset + 4], source[offset + 5], scaleIndex, keyStart, dataRelative,
                count, []);
        }
        Require(source, keyOffset, keyCount * 2);
        for (var index = 0; index < channelCount; index++)
        {
            var channel = channels[index];
            var keys = Enumerable.Range(0, checked((int)channel.KeyCount))
                .Select(key => U16(source, keyOffset + checked((int)(channel.KeyStart + (uint)key)) * 2)).ToArray();
            if (keys.Zip(keys.Skip(1)).Any(pair => pair.First > pair.Second))
                throw new InvalidDataException($"G4MT channel {index} key frames are not ordered.");
            var bytesRequired = checked((long)channel.KeyCount * channel.BytesPerKey);
            if (dataOffset + channel.DataOffset + bytesRequired > source.Length)
                throw new InvalidDataException($"G4MT channel {index} data exceeds the bank.");
            channels[index] = channel with { Keys = keys };
        }
        var clips = rows.Select((row, index) => new G4mtClip(index, clipNames[index], U32(source, clipHashOffset + index * 4),
            row.Start, row.End, row.Flags, row.Fps, row.InfoStart + row.InfoHigh * 65536, row.InfoCount)).ToArray();
        var targetHashes = Enumerable.Range(0, targetCount).Select(index => U32(source, targetHashOffset + index * 4)).ToArray();
        return new G4mtAnimationBank(source, channelOffset, targetInfoOffset, keyOffset, dataOffset, shift,
            scales, clips, clipOrder, targetHashes, infos, channels);
    }

    private static (string[] Names, ushort[] Order) ReadClipNames(byte[] data, int offset, int count)
    {
        Require(data, offset, count * 2);
        if (count == 0)
            return ([], []);
        var order = Enumerable.Range(0, count).Select(index => U16(data, offset + index * 2)).ToArray();
        if (!order.Order().SequenceEqual(Enumerable.Range(0, count).Select(index => (ushort)index)))
            throw new InvalidDataException("The G4MT clip order table is invalid.");
        for (var table = (offset + count * 2 + 3) & ~3; table < Math.Min(data.Length, offset + Math.Max(0x1000, count * 8)); table += 2)
        {
            if (table + count * 2 > data.Length)
                break;
            var names = new string[count];
            var valid = true;
            for (var index = 0; index < count; index++)
            {
                var relative = U16(data, table + index * 2);
                if (relative < count * 2 || table + relative >= data.Length)
                {
                    valid = false;
                    break;
                }
                var start = table + relative;
                var end = Array.IndexOf(data, (byte)0, start);
                if (end <= start)
                {
                    valid = false;
                    break;
                }
                names[index] = Encoding.UTF8.GetString(data, start, end - start);
            }
            if (valid)
                return (names, order);
        }
        throw new InvalidDataException("The G4MT clip-name table could not be read.");
    }

    private static int ScaledOffset(int headerWords, ushort units, int shift) => checked((headerWords + (units << shift)) * 4);
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static float F32(byte[] data, int offset) => BitConverter.Int32BitsToSingle(unchecked((int)U32(data, offset)));
    private static void Require(byte[] data, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size)
            throw new InvalidDataException("A G4MT section exceeds the animation bank bounds.");
    }
}
