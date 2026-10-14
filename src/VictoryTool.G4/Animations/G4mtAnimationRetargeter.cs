using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Numerics;
using System.Text;

namespace VictoryTool.G4.Animations;

public sealed record G4mtJointRest(Vector3 Translation, Quaternion Rotation, Vector3 Scale);
public sealed record G4mtRigJoint(uint? SourceParentHash, uint? TargetParentHash,
    G4mtJointRest SourceRest, G4mtJointRest TargetRest, bool RetargetTranslation);

public static class G4mtAnimationRetargeter
{
    private const float MaximumTrsProjectionError = 0.01f;

    public static byte[] RemapTargets(G4mtAnimationBank bank, IReadOnlyDictionary<uint, uint> sourceToTarget)
        => RemapSingleTargets(bank, sourceToTarget);

    public static byte[] RemapTargets(G4mtAnimationBank bank,
        IReadOnlyDictionary<uint, IReadOnlyList<uint>> sourceToTargets)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(sourceToTargets);
        if (sourceToTargets.Values.Any(targets => targets.Count == 0 || targets.Distinct().Count() != targets.Count))
            throw new InvalidDataException("Animation target fan-out must contain unique target joint hashes.");
        var fanOut = sourceToTargets.Values.Any(targets => targets.Count > 1);
        if (!fanOut)
            return RemapSingleTargets(bank, sourceToTargets.ToDictionary(pair => pair.Key, pair => pair.Value[0]));

        var output = bank.Source.ToArray();
        var headerWords = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(0x0A));
        var targetCount = bank.TargetHashes.Count;
        var targetHashOffset = checked((headerWords + BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(0x2C))) * 4);
        var mappedHashes = new uint[targetCount];
        var extraHashes = new List<uint>();
        var extraIndicesBySource = new Dictionary<uint, ushort[]>();
        for (var index = 0; index < targetCount; index++)
        {
            var oldHash = bank.TargetHashes[index];
            if (!sourceToTargets.TryGetValue(oldHash, out var targets))
            {
                mappedHashes[index] = oldHash;
                continue;
            }
            mappedHashes[index] = targets[0];
            var additionalIndices = new ushort[targets.Count - 1];
            for (var target = 1; target < targets.Count; target++)
            {
                additionalIndices[target - 1] = checked((ushort)(targetCount + extraHashes.Count));
                extraHashes.Add(targets[target]);
            }
            extraIndicesBySource.Add(oldHash, additionalIndices);
        }
        var allHashes = mappedHashes.Concat(extraHashes).ToArray();
        if (allHashes.Distinct().Count() != allHashes.Length)
            throw new InvalidDataException("Animation retargeting maps two tracks to the same target joint hash.");
        var rawHashBytes = new byte[extraHashes.Count * 4];
        for (var index = 0; index < extraHashes.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(rawHashBytes.AsSpan(index * 4), extraHashes[index]);
        var addedHashBytes = Align(bank.TargetInfoTableOffset + rawHashBytes.Length, 64)
            - bank.TargetInfoTableOffset;
        var extraHashBytes = new byte[addedHashBytes];
        rawHashBytes.CopyTo(extraHashBytes, 0);
        var insertionOffset = targetHashOffset + targetCount * 4;
        output = Insert(output, insertionOffset, extraHashBytes);
        for (var index = 0; index < targetCount; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(targetHashOffset + index * 4), mappedHashes[index]);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x22), checked((ushort)allHashes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x2E), checked((ushort)(
            BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(0x2E)) + addedHashBytes / 4)));

        var infoOffset = bank.TargetInfoTableOffset + addedHashBytes;
        var oldInfoCount = bank.TargetInfos.Count;
        var infoBytes = new List<byte>();
        foreach (var clip in bank.Clips)
        {
            var clipInfoStart = infoBytes.Count / 8;
            var clipInfoCount = 0;
            for (var infoIndex = clip.TargetInfoStart; infoIndex < clip.TargetInfoStart + clip.TargetInfoCount; infoIndex++)
            {
                var info = bank.TargetInfos[infoIndex];
                var sourceHash = bank.TargetHashes[info.TargetIndex];
                var targets = sourceToTargets.TryGetValue(sourceHash, out var mapped) ? mapped : [sourceHash];
                var targetIndices = new List<ushort> { info.TargetIndex };
                targetIndices.AddRange(extraIndicesBySource.GetValueOrDefault(sourceHash, []));
                if (targets.Count == 1)
                    targetIndices[0] = info.TargetIndex;
                foreach (var targetIndex in targetIndices)
                {
                    var row = bank.Source.AsSpan(bank.TargetInfoTableOffset + infoIndex * 8, 8).ToArray();
                    BinaryPrimitives.WriteUInt16LittleEndian(row, targetIndex);
                    infoBytes.AddRange(row);
                    clipInfoCount++;
                }
            }
            if (clipInfoCount > ushort.MaxValue || clipInfoStart > 0xFFFFFF)
                throw new InvalidDataException("The expanded G4MT clip target table exceeds its index limit.");
            var clipOffset = 0x40 + clip.Index * 0x10;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(clipOffset + 4), (ushort)clipInfoStart);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(clipOffset + 6), (ushort)clipInfoCount);
            output[clipOffset + 10] = (byte)(clipInfoStart >> 16);
        }
        if (infoBytes.Count / 8 < oldInfoCount)
            throw new InvalidDataException("The expanded G4MT target table unexpectedly lost entries.");
        var expandedInfoBytes = infoBytes.ToArray();
        var oldInfoBytes = oldInfoCount * 8;
        var infoDeltaWithoutPadding = expandedInfoBytes.Length - oldInfoBytes;
        const int maximumSectionAlignment = 4 << (4 * 2);
        var infoPadding = (maximumSectionAlignment
            - ((addedHashBytes + infoDeltaWithoutPadding) % maximumSectionAlignment)) % maximumSectionAlignment;
        Array.Resize(ref expandedInfoBytes, expandedInfoBytes.Length + infoPadding);
        output = Replace(output, infoOffset, oldInfoBytes, expandedInfoBytes);
        var infoDelta = expandedInfoBytes.Length - oldInfoBytes;
        var channelOffset = bank.ChannelTableOffset + addedHashBytes + infoDelta;
        var keyOffset = bank.KeyTableOffset + addedHashBytes + infoDelta;
        var dataOffset = bank.DataOffset + addedHashBytes + infoDelta;
        var offsetShift = -1;
        for (var shift = bank.OffsetShift; shift <= 4; shift++)
        {
            if (!CanEncodeSectionOffset(output, infoOffset, shift)
                || !CanEncodeSectionOffset(output, channelOffset, shift)
                || !CanEncodeSectionOffset(output, keyOffset, shift * 2)
                || !CanEncodeSectionOffset(output, dataOffset, shift * 2))
                continue;
            offsetShift = shift;
            break;
        }
        if (offsetShift < 0)
            throw new InvalidDataException("The expanded G4MT section offsets exceed the format limits.");
        output[0x36] = (byte)offsetShift;
        SetSectionOffset(output, 0x24, infoOffset, offsetShift);
        SetSectionOffset(output, 0x26, channelOffset, offsetShift);
        SetSectionOffset(output, 0x30, keyOffset, offsetShift * 2);
        SetSectionOffset(output, 0x32, dataOffset, offsetShift * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x0C), checked((uint)(output.Length - 0x40)));
        return output;
    }

    private static byte[] RemapSingleTargets(G4mtAnimationBank bank, IReadOnlyDictionary<uint, uint> sourceToTarget)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(sourceToTarget);
        var output = bank.Source.ToArray();
        var headerWords = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(0x0A));
        var targetCount = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(0x22));
        var targetHashOffset = checked((headerWords + BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(0x2C))) * 4);
        var hashes = new uint[targetCount];
        for (var index = 0; index < targetCount; index++)
        {
            var oldHash = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(targetHashOffset + index * 4));
            hashes[index] = sourceToTarget.GetValueOrDefault(oldHash, oldHash);
        }
        if (hashes.Distinct().Count() != hashes.Length)
            throw new InvalidDataException("Animation retargeting maps two tracks to the same target joint hash.");
        for (var index = 0; index < targetCount; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(targetHashOffset + index * 4), hashes[index]);
        return output;
    }

    public static byte[] RetargetAbsolute(
        G4mtAnimationBank bank,
        IReadOnlyDictionary<uint, G4mtJointRest> sourceRest,
        IReadOnlyDictionary<uint, G4mtJointRest> targetRest)
    {
        var rig = sourceRest.Where(pair => targetRest.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => new G4mtRigJoint(null, null, pair.Value, targetRest[pair.Key], true));
        return RetargetAbsolute(bank, rig);
    }

    public static byte[] RetargetAbsolute(
        G4mtAnimationBank bank,
        IReadOnlyDictionary<uint, G4mtRigJoint> rig)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(rig);
        var source = bank.Source;
        using var channelBytes = new MemoryStream();
        using var dataBytes = new MemoryStream();
        var absoluteUses = new bool[bank.TargetInfos.Count];
        var additiveUses = new bool[bank.TargetInfos.Count];
        foreach (var clip in bank.Clips)
        {
            var isAdditive = (clip.Flags & 1) != 0;
            for (var index = clip.TargetInfoStart; index < clip.TargetInfoStart + clip.TargetInfoCount; index++)
                (isAdditive ? additiveUses : absoluteUses)[index] = true;
        }

        var channelOwners = new List<int>?[bank.Channels.Count];
        foreach (var info in bank.TargetInfos)
        for (var index = info.ChannelStart; index < info.ChannelStart + info.ChannelCount; index++)
            (channelOwners[index] ??= []).Add(info.Index);

        var changedInfos = 0;
        var inPlaceChannels = new Dictionary<int, byte[]>();
        var clonedInfoStarts = new Dictionary<int, int>();
        foreach (var info in bank.TargetInfos)
        {
            if (!absoluteUses[info.Index] && !additiveUses[info.Index]) continue;
            if (absoluteUses[info.Index] && additiveUses[info.Index])
                throw new InvalidDataException($"G4MT target-info {info.Index} is shared by absolute and additive clips.");
            var isAdditive = additiveUses[info.Index];
            var hash = bank.TargetHashes[info.TargetIndex];
            if (!rig.TryGetValue(hash, out var joint)) continue;
            ValidateRest(joint.SourceRest);
            ValidateRest(joint.TargetRest);
            if (info.ChannelCount == 0 || !NeedsRetarget(bank, info, rig, hash)) continue;

            var clips = ClipsForInfo(bank, info.Index)
                .Where(clip => ((clip.Flags & 1) != 0) == isAdditive).ToArray();
            using var rewritten = new MemoryStream();
            RewriteSharedInfoChannels(bank, clips, info, rig, isAdditive, rewritten, dataBytes);
            var records = rewritten.ToArray();
            var requiresClone = Enumerable.Range(info.ChannelStart, info.ChannelCount).Any(channelIndex =>
                channelOwners[channelIndex]!.Any(owner => owner != info.Index
                    && (!absoluteUses[owner] && !additiveUses[owner]
                        || (absoluteUses[owner] && additiveUses[owner])
                        || additiveUses[owner] != isAdditive
                        || !rig.TryGetValue(bank.TargetHashes[bank.TargetInfos[owner].TargetIndex], out var otherRig)
                        || joint.SourceParentHash != otherRig.SourceParentHash
                        || joint.TargetParentHash != otherRig.TargetParentHash
                        || !RestAlignmentEquals(joint.SourceRest, otherRig.SourceRest)
                        || !RestAlignmentEquals(joint.TargetRest, otherRig.TargetRest))));
            if (requiresClone)
            {
                clonedInfoStarts.Add(info.Index, checked(bank.Channels.Count + (int)(channelBytes.Length / 20)));
                channelBytes.Write(records);
            }
            else
            {
                for (var index = 0; index < info.ChannelCount; index++)
                    inPlaceChannels[info.ChannelStart + index] = records.AsSpan(index * 20, 20).ToArray();
            }
            changedInfos++;
        }

        if (changedInfos == 0)
            return source.ToArray();

        var channelLength = checked((int)channelBytes.Length);
        var newShift = -1;
        var infoInsertion = 0;
        var channelInsertion = 0;
        var keyPadding = 0;
        var dataPadding = 0;
        var targetInfoPadding = 0;
        string? lastCandidate = null;
        var headerOffset = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(0x0A)) * 4;
        for (var shift = bank.OffsetShift; shift <= 4; shift++)
        {
            var targetAlignment = 4 << shift;
            var keyAlignment = 4 << (shift * 2);
            var candidateInfoOffset = AlignSectionOffset(bank.TargetInfoTableOffset, targetAlignment, headerOffset);
            var candidateTargetInfoPadding = candidateInfoOffset - bank.TargetInfoTableOffset;
            var candidateChannelBase = bank.ChannelTableOffset + candidateTargetInfoPadding;
            var candidateKeyBase = bank.KeyTableOffset + candidateTargetInfoPadding;
            var candidateDataBase = bank.DataOffset + candidateTargetInfoPadding;
            var candidateInfoSize = AlignSectionOffset(candidateChannelBase, targetAlignment, headerOffset)
                - candidateChannelBase;
            var candidateTableSpan = Align(candidateInfoSize + channelLength, keyAlignment);
            var candidateChannelSize = candidateTableSpan - candidateInfoSize;
            var candidateKeyPadding = AlignSectionOffset(candidateKeyBase + candidateTableSpan,
                keyAlignment, headerOffset)
                - (candidateKeyBase + candidateTableSpan);
            var candidateDataPadding = AlignSectionOffset(candidateDataBase + candidateTableSpan + candidateKeyPadding,
                keyAlignment, headerOffset)
                - (candidateDataBase + candidateTableSpan + candidateKeyPadding);
            var candidateChannelOffset = candidateChannelBase + candidateInfoSize;
            var candidateKeyOffset = candidateKeyBase + candidateTableSpan + candidateKeyPadding;
            var candidateDataOffset = candidateDataBase + candidateTableSpan + candidateKeyPadding + candidateDataPadding;
            lastCandidate = $"shift={shift}, info=0x{candidateInfoOffset:X}, channel=0x{candidateChannelOffset:X}, key=0x{candidateKeyOffset:X}, data=0x{candidateDataOffset:X}";
            if (!CanEncodeSectionOffset(source, candidateInfoOffset, shift)
                || candidateChannelOffset % targetAlignment != 0
                || !CanEncodeSectionOffset(source, candidateChannelOffset, shift)
                || !CanEncodeSectionOffset(source, candidateKeyOffset, shift * 2)
                || !CanEncodeSectionOffset(source, candidateDataOffset, shift * 2))
                continue;
            newShift = shift;
            targetInfoPadding = candidateTargetInfoPadding;
            infoInsertion = candidateInfoSize;
            channelInsertion = candidateChannelSize;
            keyPadding = candidateKeyPadding;
            dataPadding = candidateDataPadding;
            break;
        }
        if (newShift < 0)
            throw new InvalidDataException($"Retargeted G4MT sections exceed the bank's representable offset range (originalShift={bank.OffsetShift}, size={source.Length}, clonedChannelBytes={channelBytes.Length}, lastCandidate={lastCandidate}).");
        while (channelBytes.Length < channelInsertion) channelBytes.WriteByte(0);
        var newTargetInfoOffset = bank.TargetInfoTableOffset + targetInfoPadding;
        var first = Insert(source, bank.TargetInfoTableOffset, new byte[targetInfoPadding]);
        var shiftedChannelOffset = bank.ChannelTableOffset + targetInfoPadding;
        var secondWithInfoPadding = Insert(first, shiftedChannelOffset, new byte[infoInsertion]);
        var newChannelOffset = shiftedChannelOffset + infoInsertion;
        var oldChannelBytes = checked(bank.Channels.Count * 20);
        var second = Insert(secondWithInfoPadding, newChannelOffset + oldChannelBytes, channelBytes.ToArray());
        var totalTables = checked(targetInfoPadding + infoInsertion + channelInsertion);
        var keyOffset = checked(bank.KeyTableOffset + totalTables + keyPadding);
        var afterKeyPadding = Insert(second, bank.KeyTableOffset + totalTables, new byte[keyPadding]);
        var dataPaddingPosition = checked(bank.DataOffset + totalTables + keyPadding);
        var afterDataPadding = Insert(afterKeyPadding, dataPaddingPosition, new byte[dataPadding]);
        var dataOffset = checked(dataPaddingPosition + dataPadding);
        var output = new byte[checked(afterDataPadding.Length + (int)dataBytes.Length)];
        afterDataPadding.CopyTo(output, 0);
        dataBytes.ToArray().CopyTo(output, afterDataPadding.Length);

        foreach (var (channelIndex, record) in inPlaceChannels)
            record.CopyTo(output, newChannelOffset + channelIndex * 20);
        foreach (var (infoIndex, start) in clonedInfoStarts)
        {
            if (start > 0xFFFFFF)
                throw new InvalidDataException("The retargeted G4MT channel table exceeds its 24-bit index limit.");
            var infoOffset = newTargetInfoOffset + infoIndex * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(infoOffset + 2), (ushort)start);
            output[infoOffset + 5] = (byte)(start >> 16);
        }
        output[0x36] = checked((byte)newShift);
        SetSectionOffset(output, 0x24, newTargetInfoOffset, newShift);
        SetSectionOffset(output, 0x26, newChannelOffset, newShift);
        SetSectionOffset(output, 0x30, keyOffset, newShift * 2);
        SetSectionOffset(output, 0x32, dataOffset, newShift * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x0C), checked((uint)(output.Length - 0x40)));
        var retargeted = G4mtAnimationBank.Read(output);
        return CompactChannels(retargeted);
    }

    private static byte[] CompactChannels(G4mtAnimationBank bank)
    {
        var channelCurves = new string[bank.Channels.Count];
        var curveChannels = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var channelIndex = 0; channelIndex < bank.Channels.Count; channelIndex++)
        {
            var channel = bank.Channels[channelIndex];
            var fingerprint = new StringBuilder()
                .Append(channel.Type).Append(':').Append(channel.Interpolation).Append(':')
                .Append(channel.ComponentCount).Append(':').Append(channel.Keys.Count).Append(':');
            for (var keyIndex = 0; keyIndex < channel.Keys.Count; keyIndex++)
            {
                fingerprint.Append(channel.Keys[keyIndex]).Append('=');
                foreach (var component in bank.DecodeKey(channelIndex, keyIndex))
                    fingerprint.Append(BitConverter.SingleToInt32Bits(component).ToString("X8")).Append(',');
                fingerprint.Append(';');
            }
            var curve = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())));
            channelCurves[channelIndex] = curve;
            curveChannels.TryAdd(curve, channelIndex);
        }

        var bundlesByKey = new Dictionary<string, ChannelBundle>(StringComparer.Ordinal);
        var infoBundles = new ChannelBundle?[bank.TargetInfos.Count];
        foreach (var info in bank.TargetInfos)
        {
            if (info.ChannelCount == 0) continue;
            var curves = Enumerable.Range(info.ChannelStart, info.ChannelCount)
                .Select(index => channelCurves[index]).ToArray();
            if (curves.Distinct(StringComparer.Ordinal).Count() != curves.Length)
                return bank.Source.ToArray();
            var ordered = curves.Order(StringComparer.Ordinal).ToArray();
            var key = string.Join(',', ordered);
            if (!bundlesByKey.TryGetValue(key, out var bundle))
                bundlesByKey.Add(key, bundle = new ChannelBundle(ordered));
            infoBundles[info.Index] = bundle;
        }

        var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var bundles = bundlesByKey.Values.ToArray();
        for (var index = 0; index < bundles.Length; index++)
        foreach (var curve in bundles[index].Curves)
        {
            if (!owners.TryGetValue(curve, out var matches)) owners.Add(curve, matches = []);
            matches.Add(index);
        }

        var unplaced = new HashSet<int>(Enumerable.Range(0, bundles.Length));
        var packedCurves = new List<string>();
        while (unplaced.Count > 0)
        {
            var first = unplaced.Min();
            unplaced.Remove(first);
            var chain = new List<int> { first };
            var incoming = new HashSet<string>(StringComparer.Ordinal);
            var overlaps = new List<HashSet<string>> { new(StringComparer.Ordinal) };
            var currentIndex = first;
            while (true)
            {
                var available = bundles[currentIndex].Curves.Where(curve => !incoming.Contains(curve));
                var matches = new Dictionary<int, HashSet<string>>();
                foreach (var curve in available)
                foreach (var candidate in owners[curve])
                {
                    if (!unplaced.Contains(candidate)) continue;
                    if (!matches.TryGetValue(candidate, out var shared)) matches.Add(candidate, shared = new(StringComparer.Ordinal));
                    shared.Add(curve);
                }
                if (matches.Count == 0) break;
                var next = matches.OrderByDescending(pair => pair.Value.Count)
                    .ThenBy(pair => string.Join(',', bundles[pair.Key].Curves), StringComparer.Ordinal).First();
                overlaps[^1] = next.Value;
                unplaced.Remove(next.Key);
                chain.Add(next.Key);
                incoming = next.Value;
                overlaps.Add(new(StringComparer.Ordinal));
                currentIndex = next.Key;
            }

            for (var chainIndex = 0; chainIndex < chain.Count; chainIndex++)
            {
                var bundleIndex = chain[chainIndex];
                var incomingSet = chainIndex == 0 ? null : overlaps[chainIndex - 1];
                var outgoingSet = overlaps[chainIndex];
                var start = packedCurves.Count - (incomingSet?.Count ?? 0);
                bundles[bundleIndex].Start = start;
                foreach (var curve in bundles[bundleIndex].Curves)
                    if ((incomingSet is null || !incomingSet.Contains(curve)) && !outgoingSet.Contains(curve))
                        packedCurves.Add(curve);
                foreach (var curve in bundles[bundleIndex].Curves)
                    if (outgoingSet.Contains(curve)) packedCurves.Add(curve);
            }
        }

        if (packedCurves.Count >= bank.Channels.Count)
            return bank.Source.ToArray();

        var output = bank.Source.ToArray();
        var tableLength = checked(bank.Channels.Count * 20);
        var table = output.AsSpan(bank.ChannelTableOffset, tableLength);
        table.Clear();
        for (var index = 0; index < packedCurves.Count; index++)
        {
            var sourceIndex = curveChannels[packedCurves[index]];
            bank.Source.AsSpan(bank.ChannelTableOffset + sourceIndex * 20, 20)
                .CopyTo(table.Slice(index * 20, 20));
        }
        foreach (var info in bank.TargetInfos)
        {
            var start = info.ChannelCount == 0 ? 0 : infoBundles[info.Index]!.Start;
            if (start > 0xFFFFFF)
                throw new InvalidDataException("The compacted G4MT channel table exceeds its 24-bit index limit.");
            var offset = bank.TargetInfoTableOffset + info.Index * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset + 2), (ushort)start);
            output[offset + 5] = (byte)(start >> 16);
        }
        return output;
    }

    private sealed class ChannelBundle(string[] curves)
    {
        public string[] Curves { get; } = curves;
        public int Start { get; set; }
    }

    private static void RewriteInfoChannels(G4mtAnimationBank bank, G4mtClip clip, G4mtTargetInfo info,
        IReadOnlyDictionary<uint, G4mtRigJoint> rig, bool isAdditive,
        MemoryStream channelOutput, MemoryStream dataOutput)
    {
        var bytes = new byte[info.ChannelCount * 20];
        var hash = bank.TargetHashes[info.TargetIndex];
        var clipInfos = Enumerable.Range(clip.TargetInfoStart, clip.TargetInfoCount)
            .Select(index => bank.TargetInfos[index])
            .ToDictionary(targetInfo => bank.TargetHashes[targetInfo.TargetIndex]);
        for (var local = 0; local < info.ChannelCount; local++)
        {
            var channel = bank.Channels[info.ChannelStart + local];
            var sourceOffset = bank.ChannelTableOffset + channel.Index * 20;
            bank.Source.AsSpan(sourceOffset, 20).CopyTo(bytes.AsSpan(local * 20));
            if (channel.Type is not (1 or 2 or 3 or 9 or 10 or 11 or 12))
                continue;
            var components = channel.Type == 9 ? 4 : 1;
            if (channel.ComponentCount != components || channel.BytesPerKey != components * channel.Variant)
                throw new InvalidDataException($"G4MT transform channel {channel.Index} has an unsupported component layout.");
            if (channel.KeyCount == 0)
                continue;
            var outputStart = checked((int)dataOutput.Length);
            var outputKeys = new List<float[]>((int)channel.KeyCount);
            var previousRotation = Quaternion.Identity;
            for (var key = 0; key < channel.KeyCount; key++)
            {
                var pose = SampleRetargetedLocalPose(bank, clipInfos, hash, rig,
                    channel.Keys[(int)key], isAdditive);
                var sourceMatrix = Matrix4x4.CreateScale(pose.Scale)
                    * Matrix4x4.CreateFromQuaternion(pose.Rotation)
                    * Matrix4x4.CreateTranslation(pose.Translation);
                var matrix = sourceMatrix;
                if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
                    throw new InvalidDataException($"G4MT target {bank.TargetHashes[info.TargetIndex]:X8} cannot be represented as TRS.");
                var reconstructed = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation)
                    * Matrix4x4.CreateTranslation(translation);
                if (MatrixDistance(matrix, reconstructed) > 1e-4f)
                    throw new InvalidDataException($"G4MT target {bank.TargetHashes[info.TargetIndex]:X8} retargeting introduces shear.");
                if (isAdditive)
                {
                    var targetRest = rig[hash].TargetRest;
                    scale /= targetRest.Scale;
                    rotation = Quaternion.Normalize(Quaternion.Multiply(rotation,
                        Quaternion.Inverse(targetRest.Rotation)));
                    translation -= targetRest.Translation;
                }
                if (channel.Type == 9)
                {
                    if (Quaternion.Dot(previousRotation, rotation) < 0)
                        rotation = new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
                    previousRotation = rotation;
                    outputKeys.Add([rotation.X, rotation.Y, rotation.Z, rotation.W]);
                }
                else if (channel.Type is >= 1 and <= 3)
                {
                    var value = channel.Type switch
                    {
                        1 => scale.X, 2 => scale.Y, _ => scale.Z,
                    };
                    outputKeys.Add([value]);
                }
                else
                {
                    var value = channel.Type switch
                    {
                        10 => translation.X, 11 => translation.Y, _ => translation.Z,
                    };
                    outputKeys.Add([value]);
                }
            }
            byte[] encoded;
            byte codec;
            byte variant;
            try
            {
                var samples = outputKeys.Select(values => (IReadOnlyList<float>)values).ToArray();
                encoded = G4mtAnimationSampleWriter.EncodeExistingSamples(bank, channel, samples);
                codec = channel.Codec;
                variant = channel.Variant;
            }
            catch (Exception exception) when (exception is NotSupportedException or G4mtSampleRangeException)
            {
                encoded = EncodeCurve(channel, outputKeys, bank.Scales[channel.ScaleIndex], out codec, out variant);
            }
            dataOutput.Write(encoded);
            var record = bytes.AsSpan(local * 20, 20);
            record[1] = codec;
            record[3] = variant;
            record[5] = checked((byte)(components * variant));
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], checked((uint)(bank.Source.Length - bank.DataOffset + outputStart)));
        }
        channelOutput.Write(bytes);
    }

    private static void RewriteSharedInfoChannels(G4mtAnimationBank bank, IReadOnlyList<G4mtClip> clips,
        G4mtTargetInfo info, IReadOnlyDictionary<uint, G4mtRigJoint> rig,
        bool isAdditive, MemoryStream channelOutput, MemoryStream dataOutput)
    {
        if (clips.Count == 0)
            throw new InvalidDataException($"G4MT target-info {info.Index} is not referenced by any clip.");
        if (clips.Count == 1)
        {
            RewriteInfoChannels(bank, clips[0], info, rig, isAdditive, channelOutput, dataOutput);
            return;
        }

        byte[]? referenceChannels = null;
        byte[]? referenceData = null;
        foreach (var clip in clips)
        {
            using var candidateChannels = new MemoryStream();
            using var candidateData = new MemoryStream();
            RewriteInfoChannels(bank, clip, info, rig, isAdditive, candidateChannels, candidateData);
            var channels = candidateChannels.ToArray();
            var data = candidateData.ToArray();
            if (referenceChannels is not null
                && (!referenceChannels.AsSpan().SequenceEqual(channels)
                    || !referenceData!.AsSpan().SequenceEqual(data)))
                throw new InvalidDataException(
                    $"G4MT target-info {info.Index} is shared by clips with different retargeted poses: "
                    + string.Join(", ", clips.Select(item => item.Name)) + ".");
            referenceChannels = channels;
            referenceData = data;
        }

        RewriteInfoChannels(bank, clips[0], info, rig, isAdditive, channelOutput, dataOutput);
    }

    private static G4mtJointRest SampleRetargetedLocalPose(G4mtAnimationBank bank,
        IReadOnlyDictionary<uint, G4mtTargetInfo> infos, uint requestedHash,
        IReadOnlyDictionary<uint, G4mtRigJoint> rig, float frame, bool isAdditive)
    {
        var current = rig[requestedHash];
        if (current.SourceParentHash is not null && current.TargetParentHash is null)
            return SampleRetargetedGlobalPose(bank, infos, requestedHash, rig, frame, isAdditive);

        var sourceBases = new Dictionary<uint, Matrix4x4>();
        var targetBases = new Dictionary<uint, Matrix4x4>();
        var sourcePath = new HashSet<uint>();
        var targetPath = new HashSet<uint>();
        Matrix4x4 ResolveRestBasis(uint hash, bool source)
        {
            var cache = source ? sourceBases : targetBases;
            var path = source ? sourcePath : targetPath;
            if (cache.TryGetValue(hash, out var cached)) return cached;
            if (!rig.TryGetValue(hash, out var joint))
                throw new InvalidDataException($"The animation profile has no G4MT joint {hash:X8}.");
            if (!path.Add(hash))
                throw new InvalidDataException($"The animation profile hierarchy contains a cycle at {hash:X8}.");
            var rest = source ? joint.SourceRest : joint.TargetRest;
            var basis = Matrix4x4.CreateFromQuaternion(rest.Rotation);
            var parent = source ? joint.SourceParentHash : joint.TargetParentHash;
            if (parent is uint parentHash) basis *= ResolveRestBasis(parentHash, source);
            path.Remove(hash);
            cache.Add(hash, basis);
            return basis;
        }

        var sourceParent = current.SourceParentHash is uint sourceParentHash
            ? ResolveRestBasis(sourceParentHash, true) : Matrix4x4.Identity;
        var targetParent = current.TargetParentHash is uint targetParentHash
            ? ResolveRestBasis(targetParentHash, false) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(sourceParent, out var inverseSourceParent)
            || !Matrix4x4.Invert(targetParent, out var inverseTargetParent)
            || !Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(current.SourceRest.Rotation), out var inverseSourceRest))
            throw new InvalidDataException($"G4MT target {requestedHash:X8} has a singular rest rotation.");

        var sourcePose = SampleSourceLocal(bank, infos, requestedHash, current, frame, isAdditive);
        var desiredRotation = isAdditive
            ? Matrix4x4.CreateFromQuaternion(sourcePose.Rotation)
                * inverseSourceRest * Matrix4x4.CreateFromQuaternion(current.TargetRest.Rotation)
            : current.SourceParentHash == current.TargetParentHash
                ? Matrix4x4.CreateFromQuaternion(current.TargetRest.Rotation) * inverseSourceRest
                    * Matrix4x4.CreateFromQuaternion(sourcePose.Rotation)
                : Matrix4x4.CreateFromQuaternion(current.TargetRest.Rotation)
                    * targetParent * inverseSourceParent * inverseSourceRest
                    * Matrix4x4.CreateFromQuaternion(sourcePose.Rotation) * sourceParent * inverseTargetParent;
        var target = Decompose(desiredRotation, requestedHash);
        var delta = sourcePose.Translation - current.SourceRest.Translation;
        var translationBasis = sourceParent * inverseTargetParent;
        var targetTranslation = current.TargetRest.Translation
            + Vector3.TransformNormal(delta, translationBasis);
        var targetScale = current.TargetRest.Scale * (sourcePose.Scale / current.SourceRest.Scale);
        return target with { Translation = targetTranslation, Scale = targetScale };
    }

    private static G4mtJointRest SampleRetargetedGlobalPose(G4mtAnimationBank bank,
        IReadOnlyDictionary<uint, G4mtTargetInfo> infos, uint requestedHash,
        IReadOnlyDictionary<uint, G4mtRigJoint> rig, float frame, bool isAdditive)
    {
        var targetGlobals = new Dictionary<uint, Matrix4x4>();
        var sourcePoseGlobals = new Dictionary<uint, Matrix4x4>();
        var sourceRestGlobals = new Dictionary<uint, Matrix4x4>();
        var targetRestGlobals = new Dictionary<uint, Matrix4x4>();
        var sourcePosePath = new HashSet<uint>();
        var sourceRestPath = new HashSet<uint>();
        var targetRestPath = new HashSet<uint>();
        var targetPosePath = new HashSet<uint>();
        Matrix4x4 ResolveSourcePose(uint hash) => ResolveGlobal(hash, sourcePoseGlobals, sourcePosePath, true,
            (jointHash, joint) => RestMatrix(SampleSourceLocal(bank, infos, jointHash, joint, frame, isAdditive)));
        Matrix4x4 ResolveSourceRest(uint hash) => ResolveGlobal(hash, sourceRestGlobals, sourceRestPath, true,
            (_, joint) => RestMatrix(joint.SourceRest));
        Matrix4x4 ResolveTargetRest(uint hash) => ResolveGlobal(hash, targetRestGlobals, targetRestPath, false,
            (_, joint) => RestMatrix(joint.TargetRest));
        Matrix4x4 ResolveTargetPose(uint hash)
        {
            if (targetGlobals.TryGetValue(hash, out var cached)) return cached;
            if (!rig.TryGetValue(hash, out var joint))
                throw new InvalidDataException($"The animation profile has no G4MT joint {hash:X8}.");
            if (!targetPosePath.Add(hash))
                throw new InvalidDataException($"The animation profile hierarchy contains a cycle at {hash:X8}.");
            var sourceRest = ResolveSourceRest(hash);
            if (!Matrix4x4.Invert(sourceRest, out var inverseSourceRest))
                throw new InvalidDataException($"G4MT target {hash:X8} has a singular source rest transform.");
            var sourcePose = ResolveSourcePose(hash);
            var targetRest = ResolveTargetRest(hash);
            var targetPose = sourcePose * inverseSourceRest * targetRest;
            targetPose.Translation = targetRest.Translation
                + (sourcePose.Translation - sourceRest.Translation);
            if (joint.TargetParentHash is uint parentHash) _ = ResolveTargetPose(parentHash);
            targetPosePath.Remove(hash);
            targetGlobals.Add(hash, targetPose);
            return targetPose;
        }
        Matrix4x4 ResolveGlobal(uint hash, Dictionary<uint, Matrix4x4> cache, HashSet<uint> path,
            bool sourceHierarchy, Func<uint, G4mtRigJoint, Matrix4x4> getLocal)
        {
            if (cache.TryGetValue(hash, out var cached)) return cached;
            if (!rig.TryGetValue(hash, out var joint))
                throw new InvalidDataException($"The animation profile has no G4MT joint {hash:X8}.");
            if (!path.Add(hash))
                throw new InvalidDataException($"The animation profile hierarchy contains a cycle at {hash:X8}.");
            var matrix = getLocal(hash, joint);
            var parentHash = sourceHierarchy ? joint.SourceParentHash : joint.TargetParentHash;
            if (parentHash is uint resolvedParent)
                matrix *= ResolveGlobal(resolvedParent, cache, path, sourceHierarchy, getLocal);
            path.Remove(hash);
            cache.Add(hash, matrix);
            return matrix;
        }

        var targetGlobal = ResolveTargetPose(requestedHash);
        var parentGlobal = rig[requestedHash].TargetParentHash is uint targetParentHash
            ? ResolveTargetPose(targetParentHash) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentGlobal, out var inverseParent))
            throw new InvalidDataException($"G4MT target parent for {requestedHash:X8} has a singular pose transform.");
        return Decompose(targetGlobal * inverseParent, requestedHash);
    }

    private static G4mtJointRest SampleSourceLocal(G4mtAnimationBank bank,
        IReadOnlyDictionary<uint, G4mtTargetInfo> infos, uint hash, G4mtRigJoint joint, float frame, bool isAdditive)
    {
        if (!infos.TryGetValue(hash, out var info)) return joint.SourceRest;
        var translation = joint.SourceRest.Translation;
        var rotation = joint.SourceRest.Rotation;
        var scale = joint.SourceRest.Scale;
        for (var index = info.ChannelStart; index < info.ChannelStart + info.ChannelCount; index++)
        {
            var channel = bank.Channels[index];
            if (channel.KeyCount == 0) continue;
            var value = bank.SampleChannel(index, frame);
            switch (channel.Type)
            {
                case 9:
                    var sampledRotation = Quaternion.Normalize(new Quaternion(value[0], value[1], value[2], value[3]));
                    rotation = isAdditive
                        ? Quaternion.Normalize(Quaternion.Multiply(sampledRotation, rotation))
                        : sampledRotation;
                    break;
                case 1: scale.X = isAdditive ? scale.X * value[0] : value[0]; break;
                case 2: scale.Y = isAdditive ? scale.Y * value[0] : value[0]; break;
                case 3: scale.Z = isAdditive ? scale.Z * value[0] : value[0]; break;
                case 10 when joint.RetargetTranslation: translation.X = isAdditive ? translation.X + value[0] : value[0]; break;
                case 11 when joint.RetargetTranslation: translation.Y = isAdditive ? translation.Y + value[0] : value[0]; break;
                case 12 when joint.RetargetTranslation: translation.Z = isAdditive ? translation.Z + value[0] : value[0]; break;
            }
        }
        return new G4mtJointRest(translation, rotation, scale);
    }

    private static Matrix4x4 RestMatrix(G4mtJointRest rest) => Matrix4x4.CreateScale(rest.Scale)
        * Matrix4x4.CreateFromQuaternion(rest.Rotation) * Matrix4x4.CreateTranslation(rest.Translation);

    private static G4mtJointRest Decompose(Matrix4x4 matrix, uint hash)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
            throw new InvalidDataException($"G4MT target {hash:X8} cannot be represented as TRS.");
        var reconstructed = RestMatrix(new G4mtJointRest(translation, rotation, scale));
        var shear = MatrixDistance(matrix, reconstructed);
        if (shear > MaximumTrsProjectionError)
            throw new InvalidDataException($"G4MT target {hash:X8} retargeting introduces shear ({shear:G6}).");
        return new G4mtJointRest(translation, Quaternion.Normalize(rotation), scale);
    }

    private static bool NeedsRetarget(G4mtAnimationBank bank, G4mtTargetInfo info,
        IReadOnlyDictionary<uint, G4mtRigJoint> rig, uint hash)
    {
        var positionOrOrientationDiffers = false;
        var scaleDiffers = false;
        var hierarchyDiffers = false;
        var visited = new HashSet<uint>();
        foreach (var useSourceParents in new[] { true, false })
        {
            var current = hash;
            visited.Clear();
            while (true)
            {
                if (!visited.Add(current)) throw new InvalidDataException($"The animation profile hierarchy contains a cycle at {current:X8}.");
                if (!rig.TryGetValue(current, out var joint)) return false;
                positionOrOrientationDiffers |= Vector3.DistanceSquared(joint.SourceRest.Translation, joint.TargetRest.Translation) >= 1e-12f
                    || MathF.Abs(Quaternion.Dot(Quaternion.Normalize(joint.SourceRest.Rotation),
                        Quaternion.Normalize(joint.TargetRest.Rotation))) <= 0.999999f;
                scaleDiffers |= Vector3.DistanceSquared(joint.SourceRest.Scale, joint.TargetRest.Scale) >= 1e-12f;
                hierarchyDiffers |= joint.SourceParentHash != joint.TargetParentHash;
                var parentHash = useSourceParents ? joint.SourceParentHash : joint.TargetParentHash;
                if (parentHash is null) break;
                current = parentHash.Value;
            }
        }
        var hasScaleChannels = Enumerable.Range(info.ChannelStart, info.ChannelCount)
            .Any(index => bank.Channels[index].Type is >= 1 and <= 3);
        return hierarchyDiffers || positionOrOrientationDiffers || (scaleDiffers && hasScaleChannels);
    }

    private static G4mtClip[] ClipsForInfo(G4mtAnimationBank bank, int infoIndex) => bank.Clips.Where(clip =>
        infoIndex >= clip.TargetInfoStart && infoIndex < clip.TargetInfoStart + clip.TargetInfoCount).ToArray();

    private static void SetSectionOffset(byte[] data, int field, int offset, int shift)
    {
        var delta = offset / 4 - BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0A));
        var divisor = 1 << shift;
        if (offset % 4 != 0 || delta < 0 || delta % divisor != 0 || delta / divisor > ushort.MaxValue)
            throw new InvalidDataException($"G4MT section at 0x{offset:X} cannot be represented in its offset field.");
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(field), (ushort)(delta / divisor));
    }

    private static bool CanEncodeSectionOffset(byte[] data, int offset, int shift)
    {
        var delta = offset / 4 - BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0A));
        var divisor = 1 << shift;
        return offset % 4 == 0 && delta >= 0 && delta % divisor == 0 && delta / divisor <= ushort.MaxValue;
    }

    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static int AlignSectionOffset(int offset, int alignment, int headerOffset) =>
        checked(headerOffset + Align(offset - headerOffset, alignment));

    private static byte[] Insert(byte[] source, int offset, byte[] bytes)
    {
        var result = new byte[checked(source.Length + bytes.Length)];
        source.AsSpan(0, offset).CopyTo(result);
        bytes.CopyTo(result, offset);
        source.AsSpan(offset).CopyTo(result.AsSpan(offset + bytes.Length));
        return result;
    }

    private static byte[] Replace(byte[] source, int offset, int removeLength, byte[] bytes)
    {
        var result = new byte[checked(source.Length - removeLength + bytes.Length)];
        source.AsSpan(0, offset).CopyTo(result);
        bytes.CopyTo(result, offset);
        source.AsSpan(offset + removeLength).CopyTo(result.AsSpan(offset + bytes.Length));
        return result;
    }

    private static void PadToAlignment(MemoryStream stream, int alignment)
    {
        while (stream.Length % alignment != 0) stream.WriteByte(0);
    }

    private static byte[] EncodeCurve(G4mtChannel channel, IReadOnlyList<float[]> keys, float scale,
        out byte codec, out byte variant)
    {
        codec = channel.Codec;
        variant = channel.Variant;
        var encoded = new byte[checked(keys.Count * channel.BytesPerKey)];
        var valid = float.IsFinite(scale) && scale != 0;
        for (var key = 0; key < keys.Count && valid; key++)
        for (var component = 0; component < keys[key].Length; component++)
        {
            var value = keys[key][component];
            var offset = (key * keys[key].Length + component) * variant;
            switch (codec, variant)
            {
                case (1, 4):
                    BinaryPrimitives.WriteSingleLittleEndian(encoded.AsSpan(offset), value);
                    break;
                case (1, 1) when MathF.Abs(value - MathF.Round(value)) < 1e-6f && value is >= sbyte.MinValue and <= sbyte.MaxValue:
                    encoded[offset] = unchecked((byte)(sbyte)MathF.Round(value));
                    break;
                case (1, 2) when MathF.Abs(value - MathF.Round(value)) < 1e-6f && value is >= short.MinValue and <= short.MaxValue:
                    BinaryPrimitives.WriteInt16LittleEndian(encoded.AsSpan(offset), (short)MathF.Round(value));
                    break;
                case (2, 1):
                    valid = TryEncodeUnsigned(value * 256f / scale, byte.MaxValue, out var u8);
                    if (valid) encoded[offset] = (byte)u8;
                    break;
                case (2, 2):
                    valid = TryEncodeUnsigned(value * 65536f / scale, ushort.MaxValue, out var u16);
                    if (valid) BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(offset), (ushort)u16);
                    break;
                case (3, 1):
                    valid = TryEncodeSigned(value * 128f / scale, sbyte.MinValue, sbyte.MaxValue, out var s8);
                    if (valid) encoded[offset] = unchecked((byte)(sbyte)s8);
                    break;
                case (3, 2):
                    valid = TryEncodeSigned(value * 32768f / scale, short.MinValue, short.MaxValue, out var s16);
                    if (valid) BinaryPrimitives.WriteInt16LittleEndian(encoded.AsSpan(offset), (short)s16);
                    break;
                default:
                    valid = false;
                    break;
            }
        }
        if (valid) return encoded;

        codec = 1;
        variant = 4;
        encoded = new byte[checked(keys.Count * keys[0].Length * sizeof(float))];
        for (var key = 0; key < keys.Count; key++)
        for (var component = 0; component < keys[key].Length; component++)
            BinaryPrimitives.WriteSingleLittleEndian(encoded.AsSpan((key * keys[key].Length + component) * sizeof(float)), keys[key][component]);
        return encoded;
    }

    private static bool TryEncodeSigned(float value, int minimum, int maximum, out int encoded)
    {
        var rounded = MathF.Round(value);
        encoded = (int)rounded;
        return float.IsFinite(value) && rounded >= minimum && rounded <= maximum;
    }

    private static bool TryEncodeUnsigned(float value, int maximum, out int encoded)
    {
        var rounded = MathF.Round(value);
        encoded = (int)rounded;
        return float.IsFinite(value) && rounded >= 0 && rounded <= maximum;
    }

    private static void ValidateRest(G4mtJointRest rest)
    {
        if (!Finite(rest.Translation) || !Finite(rest.Scale) || rest.Scale.X == 0 || rest.Scale.Y == 0 || rest.Scale.Z == 0
            || !float.IsFinite(rest.Rotation.X) || !float.IsFinite(rest.Rotation.Y) || !float.IsFinite(rest.Rotation.Z)
            || !float.IsFinite(rest.Rotation.W) || rest.Rotation.LengthSquared() < 1e-10f)
            throw new InvalidDataException("An animation rest transform is invalid or singular.");
    }

    private static bool RestAlignmentEquals(G4mtJointRest left, G4mtJointRest right) =>
        Vector3.DistanceSquared(left.Translation, right.Translation) < 1e-12f
        && Vector3.DistanceSquared(left.Scale, right.Scale) < 1e-12f
        && MathF.Abs(Quaternion.Dot(Quaternion.Normalize(left.Rotation), Quaternion.Normalize(right.Rotation))) > 0.999999f;

    private static bool NeedsRetarget(G4mtAnimationBank bank, G4mtTargetInfo info,
        G4mtJointRest source, G4mtJointRest target)
    {
        var positionOrOrientationDiffers = Vector3.DistanceSquared(source.Translation, target.Translation) >= 1e-12f
            || MathF.Abs(Quaternion.Dot(Quaternion.Normalize(source.Rotation), Quaternion.Normalize(target.Rotation))) <= 0.999999f;
        var scaleDiffers = Vector3.DistanceSquared(source.Scale, target.Scale) >= 1e-12f;
        var hasScaleChannels = Enumerable.Range(info.ChannelStart, info.ChannelCount)
            .Any(index => bank.Channels[index].Type is >= 1 and <= 3);
        return positionOrOrientationDiffers || (scaleDiffers && hasScaleChannels);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float MatrixDistance(Matrix4x4 left, Matrix4x4 right) =>
        new[] { left.M11-right.M11,left.M12-right.M12,left.M13-right.M13,left.M14-right.M14,
            left.M21-right.M21,left.M22-right.M22,left.M23-right.M23,left.M24-right.M24,
            left.M31-right.M31,left.M32-right.M32,left.M33-right.M33,left.M34-right.M34,
            left.M41-right.M41,left.M42-right.M42,left.M43-right.M43,left.M44-right.M44 }.Max(MathF.Abs);

}
