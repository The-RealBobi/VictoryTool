using System.Buffers.Binary;
using System.Numerics;

namespace VictoryTool.G4.Animations;

public sealed record G4mtLocalSrt(Vector3 Scale, Quaternion Rotation, Vector3 Translation);

internal sealed class G4mtSampleRangeException : Exception
{
    public G4mtSampleRangeException(string message) : base(message) { }
}

public static class G4mtAnimationSampleWriter
{
    public static byte[] RewriteClipTrackInPlace(G4mtAnimationBank bank, int clipIndex, int targetInfoIndex,
        IReadOnlyDictionary<int, G4mtLocalSrt> localSrtByFrame)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(localSrtByFrame);
        return RewriteClipTracksInPlace(bank, clipIndex,
            new Dictionary<int, IReadOnlyDictionary<int, G4mtLocalSrt>>
            {
                [targetInfoIndex] = localSrtByFrame,
            });
    }

    public static byte[] RewriteClipTracksInPlace(G4mtAnimationBank bank, int clipIndex,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, G4mtLocalSrt>> localSrtByTargetInfo)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(localSrtByTargetInfo);
        if ((uint)clipIndex >= bank.Clips.Count)
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        if (localSrtByTargetInfo.Count == 0)
            throw new ArgumentException("At least one G4MT target-info track is required.", nameof(localSrtByTargetInfo));

        var clip = bank.Clips[clipIndex];
        if ((clip.Flags & 1) != 0)
            throw new NotSupportedException($"G4MT callback variant {clip.Flags & 1} has no verified SRT mapping.");
        var replacements = new Dictionary<int, IReadOnlyList<IReadOnlyList<float>>>();
        foreach (var (targetInfoIndex, localSrtByFrame) in localSrtByTargetInfo)
        {
            if ((uint)targetInfoIndex >= bank.TargetInfos.Count)
                throw new ArgumentOutOfRangeException(nameof(localSrtByTargetInfo),
                    $"G4MT target-info {targetInfoIndex} does not exist.");
            if (targetInfoIndex < clip.TargetInfoStart || targetInfoIndex >= clip.TargetInfoStart + clip.TargetInfoCount)
                throw new InvalidDataException($"G4MT target-info {targetInfoIndex} is not part of clip '{clip.Name}'.");
            if (bank.Clips.Any(other => other.Index != clipIndex
                && targetInfoIndex >= other.TargetInfoStart
                && targetInfoIndex < other.TargetInfoStart + other.TargetInfoCount))
                throw new InvalidDataException($"G4MT target-info {targetInfoIndex} is shared by multiple clips.");

            var info = bank.TargetInfos[targetInfoIndex];
            var channelTypes = new HashSet<byte>();
            for (var channelIndex = info.ChannelStart; channelIndex < info.ChannelStart + info.ChannelCount; channelIndex++)
            {
                var channel = bank.Channels[channelIndex];
                if (!channelTypes.Add(channel.Type))
                    throw new InvalidDataException($"G4MT target-info {targetInfoIndex} repeats channel type {channel.Type}.");
                if (channel.Type is not (1 or 2 or 3 or 9 or 10 or 11 or 12))
                    throw new NotSupportedException($"G4MT channel type {channel.Type} has no verified SRT mapping.");

                var expectedComponents = channel.Type == 9 ? 4 : 1;
                if (channel.ComponentCount != expectedComponents)
                    throw new InvalidDataException($"G4MT channel {channelIndex} has an unexpected SRT component count.");

                var samples = new IReadOnlyList<float>[channel.Keys.Count];
                for (var keyIndex = 0; keyIndex < channel.Keys.Count; keyIndex++)
                {
                    if (!localSrtByFrame.TryGetValue(channel.Keys[keyIndex], out var pose))
                        throw new InvalidDataException($"No local SRT pose was supplied for G4MT frame {channel.Keys[keyIndex]}.");
                    float[] value = channel.Type switch
                    {
                        1 => [pose.Scale.X],
                        2 => [pose.Scale.Y],
                        3 => [pose.Scale.Z],
                        9 => [pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W],
                        10 => [pose.Translation.X],
                        11 => [pose.Translation.Y],
                        12 => [pose.Translation.Z],
                        _ => throw new InvalidDataException($"G4MT channel type {channel.Type} has no SRT field."),
                    };
                    samples[keyIndex] = value;
                }
                if (!replacements.TryAdd(channelIndex, samples))
                {
                    var sharedChannel = bank.Channels[channelIndex];
                    var existing = EncodeExistingSamples(bank, sharedChannel, replacements[channelIndex]);
                    var candidate = EncodeExistingSamples(bank, sharedChannel, samples);
                    if (!existing.AsSpan().SequenceEqual(candidate))
                        throw new InvalidDataException(
                            $"Shared G4MT channel {channelIndex} requires different replacement sample bytes.");
                }
            }
        }
        return RewriteInPlace(bank, replacements);
    }

    public static byte[] RewriteInPlace(G4mtAnimationBank bank,
        IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyList<float>>> replacements)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(replacements);
        var output = bank.Source.ToArray();
        var written = new Dictionary<int, byte>();
        foreach (var (channelIndex, samples) in replacements)
        {
            if ((uint)channelIndex >= bank.Channels.Count)
                throw new ArgumentOutOfRangeException(nameof(replacements), $"G4MT channel {channelIndex} does not exist.");
            var channel = bank.Channels[channelIndex];
            byte[] encoded;
            try
            {
                encoded = Encode(bank, channel, samples, bank.Scales[channel.ScaleIndex]);
            }
            catch (G4mtSampleRangeException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }

            var dataStart = checked(bank.DataOffset + (int)channel.DataOffset);
            for (var index = 0; index < encoded.Length; index++)
            {
                var offset = dataStart + index;
                if (written.TryGetValue(offset, out var previous) && previous != encoded[index])
                    throw new InvalidDataException("Overlapping G4MT channel samples require different replacement bytes.");
                written[offset] = encoded[index];
                output[offset] = encoded[index];
            }
        }
        return output;
    }

    internal static byte[] EncodeExistingSamples(
        G4mtAnimationBank bank, G4mtChannel channel, IReadOnlyList<IReadOnlyList<float>> samples) =>
        Encode(bank, channel, samples, bank.Scales[channel.ScaleIndex]);

    private static byte[] Encode(
        G4mtAnimationBank bank, G4mtChannel channel, IReadOnlyList<IReadOnlyList<float>> samples,
        float scale)
    {
        if (samples.Count != channel.KeyCount)
            throw new InvalidDataException($"G4MT channel {channel.Index} replacement key count differs from its source.");

        var elementSize = channel.Variant;
        if (channel.Codec is not (1 or 2 or 3) || elementSize is not (1 or 2))
            throw new NotSupportedException($"G4MT codec {channel.Codec}/{channel.Variant} has no verified sample writer.");
        var decoderIndex = channel.Codec * 5 + channel.Variant;
        if (decoderIndex is not (6 or 12 or 17))
            throw new NotSupportedException($"G4MT decoder table entry {decoderIndex} has no verified sample writer.");
        if (decoderIndex != 6 && (!float.IsFinite(scale) || scale == 0f))
            throw new InvalidDataException($"G4MT channel {channel.Index} has an invalid scale value.");
        if (channel.BytesPerKey < channel.ComponentCount * elementSize)
            throw new InvalidDataException($"G4MT channel {channel.Index} sample stride is too short.");

        var sourceStart = checked(bank.DataOffset + (int)channel.DataOffset);
        var output = bank.Source.AsSpan(sourceStart, checked((int)channel.KeyCount * channel.BytesPerKey)).ToArray();
        for (var key = 0; key < samples.Count; key++)
        {
            var sample = samples[key];
            if (sample.Count != channel.ComponentCount)
                throw new InvalidDataException($"G4MT channel {channel.Index} replacement component count differs from its source.");
            var sampleOffset = key * channel.BytesPerKey;
            for (var component = 0; component < sample.Count; component++)
            {
                var value = sample[component];
                if (!float.IsFinite(value))
                    throw new InvalidDataException($"G4MT channel {channel.Index} contains a non-finite replacement sample.");
                var offset = sampleOffset + component * elementSize;
                if (decoderIndex == 6)
                {
                    if (!TryRound(value, sbyte.MinValue, sbyte.MaxValue, out var signedByte))
                        throw new G4mtSampleRangeException($"G4MT channel {channel.Index} exceeds the signed-byte sample range.");
                    output[offset] = unchecked((byte)(sbyte)signedByte);
                }
                else if (decoderIndex == 12)
                {
                    if (!TryRound((double)value / scale * 65536.0, 0, ushort.MaxValue, out var unsignedShort))
                        throw new G4mtSampleRangeException($"G4MT channel {channel.Index} exceeds the unsigned-short sample range.");
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset), (ushort)unsignedShort);
                }
                else
                {
                    if (!TryRound((double)value / scale * 32768.0, short.MinValue, short.MaxValue, out var signedShort))
                        throw new G4mtSampleRangeException($"G4MT channel {channel.Index} exceeds the signed-short sample range.");
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(offset), (short)signedShort);
                }
            }
        }
        return output;
    }

    private static bool TryRound(double value, int minimum, int maximum, out int result)
    {
        result = 0;
        if (!double.IsFinite(value)) return false;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < minimum || rounded > maximum) return false;
        result = (int)rounded;
        return true;
    }
}
