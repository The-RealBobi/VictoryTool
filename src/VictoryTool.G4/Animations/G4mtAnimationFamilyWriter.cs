using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.G4.Animations;

public sealed record G4mtTargetSamples(string Name, int? ParentIndex, IReadOnlyList<G4mtJointRest> Samples);
public sealed record G4mtClipSamples(int TemplateClipIndex, string Name, IReadOnlyList<G4mtTargetSamples> Targets);

public static class G4mtAnimationFamilyWriter
{
    private sealed record Channel(byte[] Header, byte[] Keys, byte[] Data);
    private sealed record Track(string Name, int Block, Channel[] Channels);
    private sealed record Hierarchy(string? Parent, int Clip, int Order);

    public static byte[] WritePreservingAdditive(G4mtAnimationBank template,
        IReadOnlyList<G4mtClipSamples> clips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(clips);
        cancellationToken.ThrowIfCancellationRequested();
        if (BinaryPrimitives.ReadUInt16LittleEndian(template.Source.AsSpan(6)) != 0x68)
            throw new InvalidDataException("G4MT template has an unsupported file type.");
        var expected = template.Clips.Where(clip => (clip.Flags & 1) == 0).Select(clip => clip.Index).ToHashSet();
        if (clips.Count == 0 || clips.Count != expected.Count || !expected.SetEquals(clips.Select(clip => clip.TemplateClipIndex)))
            throw new InvalidDataException("G4MT family export must provide every non-additive source clip exactly once.");
        return WritePreservingAdditiveStreaming(template, clips, cancellationToken);
    }

    public static byte[] WritePreservingAdditiveStreaming(G4mtAnimationBank template,
        IEnumerable<G4mtClipSamples> clips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(clips);
        cancellationToken.ThrowIfCancellationRequested();
        if (BinaryPrimitives.ReadUInt16LittleEndian(template.Source.AsSpan(6)) != 0x68)
            throw new InvalidDataException("G4MT template has an unsupported file type.");
        var expected = template.Clips.Where(clip => (clip.Flags & 1) == 0).Select(clip => clip.Index).ToHashSet();
        var generated = new Dictionary<int, Track[]>();
        var hierarchy = new Dictionary<string, Hierarchy>(StringComparer.Ordinal);
        var signatures = new Dictionary<byte[], int>(new BytesComparer());
        foreach (var clip in clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expected.Contains(clip.TemplateClipIndex) || generated.ContainsKey(clip.TemplateClipIndex))
                throw new InvalidDataException("G4MT family export must provide every non-additive source clip exactly once.");
            var original = template.Clips[clip.TemplateClipIndex];
            if (clip.Name != original.Name || string.IsNullOrEmpty(clip.Name) || clip.Name.Contains('\0'))
                throw new InvalidDataException("G4MT family export must preserve source clip names.");
            var targets = ParentFirst(clip.Targets);
            if (targets.Count == 0 || targets.Count > ushort.MaxValue)
                throw new InvalidDataException("G4MT clip requires between 1 and 65535 target tracks.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var cumulative = new double[targets.Count][][];
            var tracks = new Track[targets.Count];
            for (var index = 0; index < targets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = targets[index];
                if (string.IsNullOrEmpty(target.Name) || target.Name.Any(character => character > 127 || character == 0) || !names.Add(target.Name))
                    throw new InvalidDataException("G4MT target names must be unique, non-empty ASCII strings.");
                var parent = target.ParentIndex is int parentIndex ? targets[parentIndex].Name : null;
                var entry = new Hierarchy(parent, clip.TemplateClipIndex, index);
                if (hierarchy.TryGetValue(target.Name, out var previous))
                {
                    if (previous.Parent != parent)
                        throw new InvalidDataException($"G4MT target '{target.Name}' has conflicting parents across clips.");
                    if (previous.Clip < entry.Clip || previous.Clip == entry.Clip && previous.Order < entry.Order)
                        entry = previous;
                }
                hierarchy[target.Name] = entry;
                var count = original.EndFrame - original.StartFrame + 1;
                if (count <= 0 || target.Samples.Count != count)
                    throw new InvalidDataException($"G4MT target '{target.Name}' has {target.Samples.Count} samples; expected {count}.");
                cumulative[index] = new double[count][];
                for (var frame = 0; frame < count; frame++)
                {
                    var scale = target.Samples[frame].Scale;
                    var parentScale = target.ParentIndex is int p ? cumulative[p][frame] : [1.0, 1.0, 1.0];
                    cumulative[index][frame] = [(double)scale.X * parentScale[0], (double)scale.Y * parentScale[1], (double)scale.Z * parentScale[2]];
                }
                var channels = Encode(target, original, cumulative[index], cancellationToken);
                using var signature = new MemoryStream();
                using (var writer = new BinaryWriter(signature, Encoding.UTF8, true))
                    foreach (var channel in channels)
                    {
                        writer.Write(channel.Header);
                        writer.Write(channel.Keys.Length);
                        writer.Write(channel.Keys);
                        writer.Write(channel.Data);
                    }
                var bytes = signature.ToArray();
                if (!signatures.TryGetValue(bytes, out var block))
                {
                    block = signatures.Count;
                    signatures.Add(bytes, block);
                }
                tracks[index] = new Track(target.Name, block, channels);
            }
            generated.Add(clip.TemplateClipIndex, tracks);
        }
        if (generated.Count == 0 || !expected.SetEquals(generated.Keys))
            throw new InvalidDataException("G4MT family export must provide every non-additive source clip exactly once.");
        var targetNames = new List<string>();
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states.TryGetValue(name, out var state))
            {
                if (state == 1) throw new InvalidDataException("G4MT family target hierarchy contains a cycle.");
                return;
            }
            states[name] = 1;
            if (hierarchy[name].Parent is string parent) Visit(parent);
            states[name] = 2;
            targetNames.Add(name);
        }
        foreach (var entry in hierarchy.OrderBy(pair => pair.Value.Clip).ThenBy(pair => pair.Value.Order)) Visit(entry.Key);
        var hashes = targetNames.Select(Crc32).ToList();
        if (hashes.Distinct().Count() != hashes.Count)
            throw new InvalidDataException("G4SK target names contain a CRC32 collision.");
        var targetIndices = hashes.Select((hash, index) => (hash, index)).ToDictionary(pair => pair.hash, pair => pair.index);
        foreach (var hash in template.TargetHashes)
            if (!targetIndices.ContainsKey(hash))
            {
                targetIndices.Add(hash, hashes.Count);
                hashes.Add(hash);
            }
        if (hashes.Count > ushort.MaxValue) throw new InvalidDataException("G4MT family exceeds the target table limit.");
        var scales = template.Scales.ToList();
        var scaleIndices = scales.Select((value, index) => (value, index)).GroupBy(pair => pair.value)
            .ToDictionary(group => group.Key, group => group.Last().index);
        var generatedScale = scaleIndices.TryGetValue(1f, out var unitIndex) ? unitIndex : -1;
        if (generatedScale < 0) { generatedScale = scales.Count; scales.Add(1f); }
        if (scales.Count > 256) throw new InvalidDataException("G4MT family exceeds the scale table limit.");
        using var rows = new MemoryStream();
        using var infos = new MemoryStream();
        using var channelsOut = new MemoryStream();
        using var keys = new MemoryStream();
        using var data = new MemoryStream();
        using var infoWriter = new BinaryWriter(infos, Encoding.UTF8, true);
        using var channelWriter = new BinaryWriter(channelsOut, Encoding.UTF8, true);
        var blocks = new Dictionary<(bool Native, int Start, int Count), int>();
        foreach (var clip in template.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var infoStart = checked((int)(infos.Length / 8));
            Check24(infoStart, "target-info");
            var native = (clip.Flags & 1) != 0;
            var count = native ? clip.TargetInfoCount : generated[clip.Index].Length;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                G4mtTargetInfo? info = native ? template.TargetInfos[clip.TargetInfoStart + index] : null;
                var track = native ? null : generated[clip.Index][index];
                var blockKey = native ? (true, info!.ChannelStart, (int)info.ChannelCount) : (false, track!.Block, track.Channels.Length);
                if (!blocks.TryGetValue(blockKey, out var channelStart))
                {
                    channelStart = checked((int)(channelsOut.Length / 20));
                    Check24(channelStart, "channel");
                    blocks.Add(blockKey, channelStart);
                    for (var channelIndex = 0; channelIndex < blockKey.Item3; channelIndex++)
                    {
                        byte[] record, keyBytes, dataBytes;
                        if (native)
                        {
                            var channel = template.Channels[info!.ChannelStart + channelIndex];
                            record = template.Source.AsSpan(template.ChannelTableOffset + channel.Index * 20, 20).ToArray();
                            keyBytes = template.Source.AsSpan(template.KeyTableOffset + checked((int)channel.KeyStart * 2), checked((int)channel.KeyCount * 2)).ToArray();
                            dataBytes = template.Source.AsSpan(template.DataOffset + checked((int)channel.DataOffset), checked((int)channel.KeyCount * channel.BytesPerKey)).ToArray();
                            record[6] = (byte)scaleIndices[template.Scales[channel.ScaleIndex]];
                        }
                        else
                        {
                            var channel = track!.Channels[channelIndex];
                            record = new byte[20];
                            channel.Header.CopyTo(record, 0);
                            record[6] = (byte)generatedScale;
                            keyBytes = channel.Keys;
                            dataBytes = channel.Data;
                        }
                        Put32(record, 8, checked((uint)(keys.Length / 2)));
                        Put32(record, 12, checked((uint)data.Length));
                        Put32(record, 16, (uint)(keyBytes.Length / 2));
                        channelWriter.Write(record);
                        keys.Write(keyBytes);
                        data.Write(dataBytes);
                    }
                }
                var hash = native ? template.TargetHashes[info!.TargetIndex] : Crc32(track!.Name);
                infoWriter.Write((ushort)targetIndices[hash]);
                infoWriter.Write((ushort)(channelStart & 0xffff));
                infoWriter.Write((byte)blockKey.Item3);
                infoWriter.Write((byte)(channelStart >> 16));
                infoWriter.Write(native ? BinaryPrimitives.ReadUInt16LittleEndian(template.Source.AsSpan(template.TargetInfoTableOffset + info!.Index * 8 + 6)) : (ushort)0);
            }
            var row = template.Source.AsSpan(0x40 + clip.Index * 16, 16).ToArray();
            Put16(row, 4, infoStart & 0xffff);
            Put16(row, 6, count);
            row[10] = (byte)(infoStart >> 16);
            rows.Write(row);
        }
        using var namesOut = new MemoryStream();
        using var namesWriter = new BinaryWriter(namesOut, Encoding.UTF8, true);
        var nativeClipOrder = template.ClipOrder.Select((clipIndex, rank) => (Index: (int)clipIndex, Rank: rank))
            .ToDictionary(item => item.Index, item => item.Rank);
        var expectedClipOrder = template.Clips.OrderBy(clip => clip.NameHash)
            .ThenBy(clip => nativeClipOrder[clip.Index]).Select(clip => (ushort)clip.Index).ToArray();
        foreach (var clipIndex in expectedClipOrder) namesWriter.Write(clipIndex);
        Pad(namesOut, 4);
        var nameBase = checked((int)namesOut.Length);
        namesWriter.Write(new byte[template.Clips.Count * 2]);
        foreach (var clip in template.Clips)
        {
            var relative = checked((int)namesOut.Length - nameBase);
            if (relative > ushort.MaxValue) throw new InvalidDataException("G4MT clip-name table exceeds its 16-bit offset limit.");
            var end = namesOut.Position;
            namesOut.Position = nameBase + clip.Index * 2;
            namesWriter.Write((ushort)relative);
            namesOut.Position = end;
            namesWriter.Write(Encoding.UTF8.GetBytes(clip.Name));
            namesWriter.Write((byte)0);
        }
        byte[] Pack<T>(IEnumerable<T> values, Action<BinaryWriter, T> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            foreach (var value in values) write(writer, value);
            return stream.ToArray();
        }
        var sections = new[] { Pack(scales, (writer, value) => writer.Write(value)),
            Pack(template.Clips.Select(clip => clip.NameHash), (writer, value) => writer.Write(value)),
            Pack(hashes, (writer, value) => writer.Write(value)), namesOut.ToArray(), infos.ToArray(), channelsOut.ToArray(), keys.ToArray(), data.ToArray() };
        var shift = Math.Max(2, template.OffsetShift);
        int[] offsets = [];
        for (; shift <= 4; shift++)
        {
            var offset = 0x40 + (int)rows.Length;
            offsets = new int[8];
            var valid = true;
            for (var index = 0; index < sections.Length; index++)
            {
                var exponent = index < 4 ? 0 : index < 6 ? shift : shift * 2;
                var alignment = 4 << exponent;
                offset = checked(offset + (alignment - (offset - 0x40) % alignment) % alignment);
                offsets[index] = offset;
                if (((offset - 0x40) >> (2 + exponent)) > ushort.MaxValue) valid = false;
                offset = checked(offset + sections[index].Length);
            }
            if (valid) break;
        }
        if (shift > 4) throw new InvalidDataException("G4MT family exceeds the representable section-offset range.");
        using var output = new MemoryStream();
        output.Write(template.Source.AsSpan(0, 0x40));
        output.Write(rows.ToArray());
        for (var index = 0; index < sections.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(new byte[offsets[index] - output.Length]);
            output.Write(sections[index]);
        }
        var result = output.ToArray();
        Put16(result, 0x20, template.Clips.Count);
        Put16(result, 0x22, hashes.Count);
        result[0x36] = (byte)shift;
        int[] fields = [0x28, 0x2a, 0x2c, 0x2e, 0x24, 0x26, 0x30, 0x32];
        for (var index = 0; index < fields.Length; index++)
        {
            var exponent = index < 4 ? 0 : index < 6 ? shift : shift * 2;
            Put16(result, fields[index], (offsets[index] - 0x40) >> (2 + exponent));
        }
        Put32(result, 0x0c, checked((uint)result.Length - 0x40));
        var readback = G4mtAnimationBank.Read(result);
        if (!readback.Clips.Select(clip => clip.Name).SequenceEqual(template.Clips.Select(clip => clip.Name)))
            throw new InvalidDataException("Generated G4MT family failed clip-name read-back.");
        if (!readback.ClipOrder.SequenceEqual(expectedClipOrder))
            throw new InvalidDataException("Generated G4MT family has an invalid clip-hash lookup order.");
        return result;
    }

    private static IReadOnlyList<G4mtTargetSamples> ParentFirst(IReadOnlyList<G4mtTargetSamples> targets)
    {
        var order = new List<int>();
        var state = new byte[targets.Count];
        void Visit(int index)
        {
            if (state[index] == 2) return;
            if (state[index] == 1) throw new InvalidDataException("G4MT target hierarchy contains a cycle.");
            state[index] = 1;
            if (targets[index].ParentIndex is int parent)
            {
                if ((uint)parent >= targets.Count || parent == index) throw new InvalidDataException("G4MT target has an invalid parent index.");
                Visit(parent);
            }
            state[index] = 2;
            order.Add(index);
        }
        for (var index = 0; index < targets.Count; index++) Visit(index);
        var indices = order.Select((old, index) => (old, index)).ToDictionary(pair => pair.old, pair => pair.index);
        return order.Select(index => targets[index] with { ParentIndex = targets[index].ParentIndex is int parent ? indices[parent] : null }).ToArray();
    }

    private static Channel[] Encode(G4mtTargetSamples target, G4mtClip clip, double[][] cumulativeScale, CancellationToken cancellationToken)
    {
        byte[] types = [1, 2, 3, 9, 10, 11, 12];
        var channels = new Channel[types.Length];
        var lowerName = target.Name.ToLowerInvariant();
        var tolerance = new[] { "eye", "eld", "ebw", "lgt" }.Any(lowerName.Contains) ? 0.0 : 1e-5;
        for (var channelIndex = 0; channelIndex < types.Length; channelIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = types[channelIndex];
            var samples = new double[target.Samples.Count][];
            for (var frame = 0; frame < samples.Length; frame++)
            {
                var sample = target.Samples[frame];
                samples[frame] = type switch
                {
                    1 or 2 or 3 => [cumulativeScale[frame][type - 1]],
                    9 => [sample.Rotation.X, sample.Rotation.Y, sample.Rotation.Z, sample.Rotation.W],
                    10 => [sample.Translation.X], 11 => [sample.Translation.Y], 12 => [sample.Translation.Z],
                    _ => throw new InvalidDataException("Unsupported SRT channel."),
                };
                if (samples[frame].Any(value => !double.IsFinite(value))) throw new InvalidDataException($"G4MT target '{target.Name}' has a non-finite channel value.");
                if (type == 9)
                {
                    var length = Math.Sqrt(samples[frame].Sum(value => value * value));
                    if (length < 1e-8) throw new InvalidDataException($"G4MT target '{target.Name}' has a zero-length rotation.");
                    for (var component = 0; component < 4; component++) samples[frame][component] /= length;
                    if (frame > 0 && samples[frame].Zip(samples[frame - 1], (left, right) => left * right).Sum() < 0)
                        for (var component = 0; component < 4; component++) samples[frame][component] = -samples[frame][component];
                }
            }
            var keep = Simplify(samples, tolerance, cancellationToken);
            using var keyStream = new MemoryStream();
            using var keyWriter = new BinaryWriter(keyStream);
            using var dataStream = new MemoryStream();
            using var dataWriter = new BinaryWriter(dataStream);
            foreach (var frame in keep)
            {
                keyWriter.Write(checked((ushort)(clip.StartFrame + frame)));
                foreach (var value in samples[frame])
                {
                    var packed = (float)value;
                    if (!float.IsFinite(packed)) throw new InvalidDataException("G4MT channel value exceeds the float range.");
                    dataWriter.Write(packed);
                }
            }
            byte components = type == 9 ? (byte)4 : (byte)1;
            channels[channelIndex] = new Channel([type, 1, 1, 4, components, (byte)(components * 4), 0, 0], keyStream.ToArray(), dataStream.ToArray());
        }
        return channels;
    }

    private static int[] Simplify(double[][] samples, double tolerance, CancellationToken cancellationToken)
    {
        if (samples.Skip(1).All(sample => sample.SequenceEqual(samples[0]))) return [0];
        if (samples.Length <= 2) return Enumerable.Range(0, samples.Length).ToArray();
        var keep = new SortedSet<int> { 0, samples.Length - 1 };
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, samples.Length - 1));
        while (stack.TryPop(out var range))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worst = -1;
            var worstError = tolerance * tolerance;
            for (var index = range.First + 1; index < range.Last; index++)
            {
                var amount = (double)(index - range.First) / (range.Last - range.First);
                var error = 0.0;
                for (var component = 0; component < samples[index].Length; component++)
                {
                    var delta = samples[index][component] - (samples[range.First][component] + (samples[range.Last][component] - samples[range.First][component]) * amount);
                    error += delta * delta;
                }
                if (error > worstError) { worstError = error; worst = index; }
            }
            if (worst < 0) continue;
            keep.Add(worst);
            stack.Push((range.First, worst));
            stack.Push((worst, range.Last));
        }
        return keep.ToArray();
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? left, byte[]? right) => left is not null && right is not null && left.AsSpan().SequenceEqual(right);
        public int GetHashCode(byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }
    private static uint Crc32(string name)
    {
        var crc = uint.MaxValue;
        foreach (var value in Encoding.UTF8.GetBytes(name))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
    private static void Check24(int index, string table)
    {
        if (index > 0xffffff) throw new InvalidDataException($"G4MT family {table} table exceeds its 24-bit index limit.");
    }
    private static void Pad(MemoryStream stream, int alignment) => stream.Write(new byte[(alignment - stream.Length % alignment) % alignment]);
    private static void Put16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
