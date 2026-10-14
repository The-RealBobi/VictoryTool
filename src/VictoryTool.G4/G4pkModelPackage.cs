using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.G4.Packages;

public sealed record G4pkModelMember(string Name, ReadOnlyMemory<byte> Content, int Offset);

public sealed class G4pkModelPackage
{
    private readonly byte[] _header;
    private readonly byte[] _preOffsetTable;
    private readonly byte[] _preNameTable;
    private readonly int _headerSize;
    private readonly int _resourceCount;
    private readonly int _hashTableCount;
    private readonly int _nameIndexBytes;
    private readonly int _offsetTableWordOffset;

    private G4pkModelPackage(
        byte[] header,
        byte[] preOffsetTable,
        byte[] preNameTable,
        int headerSize,
        int resourceCount,
        int hashTableCount,
        int nameIndexBytes,
        int offsetTableWordOffset,
        IReadOnlyList<G4pkModelMember> members)
    {
        _header = header;
        _preOffsetTable = preOffsetTable;
        _preNameTable = preNameTable;
        _headerSize = headerSize;
        _resourceCount = resourceCount;
        _hashTableCount = hashTableCount;
        _nameIndexBytes = nameIndexBytes;
        _offsetTableWordOffset = offsetTableWordOffset;
        Members = members;
    }

    public IReadOnlyList<G4pkModelMember> Members { get; }

    public static G4pkModelPackage Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40 || !data[..4].SequenceEqual("G4PK"u8))
            throw new InvalidDataException("The input is not a G4PK/G4PKM package.");

        var source = data.ToArray();
        var headerSize = ReadUInt16(source, 4);
        var packageDataSize = ReadUInt32(source, 0x0C);
        var offsetTableBaseWords = ReadUInt16(source, 0x0A);
        var resourceCountValue = ReadUInt16(source, 0x20);
        var offsetTableWordOffset = ReadUInt16(source, 0x22);
        var hashTableCount = ReadUInt16(source, 0x24);
        var nameIndexBytes = ReadUInt16(source, 0x26);
        if (headerSize != 0x40 || packageDataSize != source.Length - headerSize
            || offsetTableBaseWords * 4 != headerSize || source[8] != 0)
            throw new InvalidDataException("Only little-endian G4PK model packages with a 0x40-byte header are supported.");
        if (resourceCountValue == 0)
            throw new InvalidDataException("The package member count is invalid.");

        var resourceCount = resourceCountValue;
        if (resourceCount > ushort.MaxValue || hashTableCount != resourceCount || nameIndexBytes != resourceCount * 2)
            throw new InvalidDataException("The package hash/name tables do not match the member count.");

        var offsetsTableOffset = checked(headerSize + offsetTableWordOffset * 4);
        var sizesTableOffset = checked(offsetsTableOffset + resourceCount * 4);
        var hashTableOffset = checked(sizesTableOffset + resourceCount * 4);
        var hashTableEnd = checked(hashTableOffset + hashTableCount * 4);
        var nameIndexOffset = Align4(checked(hashTableEnd + nameIndexBytes));
        var nameStringsOffset = Align4(checked(nameIndexOffset + nameIndexBytes));
        Require(source, offsetsTableOffset, resourceCount * 4);
        Require(source, sizesTableOffset, resourceCount * 4);
        Require(source, hashTableOffset, hashTableCount * 4);
        Require(source, nameIndexOffset, nameIndexBytes);

        var thirdTableWordOffset = ReadUInt16(source, 0x28);
        var nameIndexWordOffset = ReadUInt16(source, 0x2A);
        var expectedThirdTableWordOffset = (hashTableEnd - headerSize) / 4;
        var expectedNameIndexWordOffset = (nameIndexOffset - headerSize) / 4;
        if (thirdTableWordOffset != expectedThirdTableWordOffset || nameIndexWordOffset != expectedNameIndexWordOffset)
            throw new InvalidDataException("The package table offsets do not match the observed G4PK layout.");

        var members = new List<G4pkModelMember>(resourceCount);
        var namesEnd = nameStringsOffset;
        for (var index = 0; index < resourceCount; index++)
        {
            var nameRelativeOffset = ReadInt16(source, nameIndexOffset + index * 2);
            var nameOffset = checked(nameIndexOffset + nameRelativeOffset);
            if (nameOffset < nameStringsOffset || nameOffset >= source.Length)
                throw new InvalidDataException($"Package member {index} has an invalid name offset.");
            var nameEnd = Array.IndexOf(source, (byte)0, nameOffset);
            if (nameEnd < 0)
                throw new InvalidDataException($"Package member {index} has an unterminated name.");
            namesEnd = Math.Max(namesEnd, nameEnd + 1);
            var name = Encoding.UTF8.GetString(source, nameOffset, nameEnd - nameOffset);
            var expectedHash = Crc32(Encoding.UTF8.GetBytes(name));
            if (ReadUInt32(source, hashTableOffset + index * 4) != expectedHash)
                throw new InvalidDataException($"Package member {name} has a mismatched name CRC32.");

            var relativeOffsetUnits = ReadUInt32(source, offsetsTableOffset + index * 4);
            if (relativeOffsetUnits > int.MaxValue / 4)
                throw new InvalidDataException($"Package member {name} has an invalid offset.");
            var offset = checked(headerSize + (int)relativeOffsetUnits * 4);
            var sizeValue = ReadUInt32(source, sizesTableOffset + index * 4);
            if (sizeValue > int.MaxValue)
                throw new InvalidDataException($"Package member {name} has an invalid size.");
            var size = (int)sizeValue;
            Require(source, offset, size);
            if (offset % 32 != 0 || size < 4)
                throw new InvalidDataException($"Package member {name} has an unsupported alignment or size.");
            members.Add(new G4pkModelMember(name, source.AsMemory(offset, size), offset));
        }

        ValidatePayloadGaps(source, namesEnd, members);
        var preOffsetTable = source.AsSpan(headerSize, offsetsTableOffset - headerSize).ToArray();
        var preNameTable = source.AsSpan(hashTableEnd, nameIndexOffset - hashTableEnd).ToArray();
        return new G4pkModelPackage(
            source.AsSpan(0, headerSize).ToArray(),
            preOffsetTable,
            preNameTable,
            headerSize,
            resourceCount,
            hashTableCount,
            nameIndexBytes,
            offsetTableWordOffset,
            members);
    }

    public byte[] ReplaceCharacterModel(string modelStem, ReadOnlySpan<byte> skeleton, ReadOnlySpan<byte> model)
    {
        if (string.IsNullOrWhiteSpace(modelStem) || modelStem is "." or ".."
            || modelStem.Contains('/') || modelStem.Contains('\\') || modelStem.Contains('\0')
            || Path.GetFileName(modelStem) != modelStem)
            throw new ArgumentException("The model stem must be a single filename without an extension.", nameof(modelStem));
        if (skeleton.Length < 4 || !skeleton[..4].SequenceEqual("G4SK"u8))
            throw new InvalidDataException("The replacement skeleton is not G4SK.");
        if (model.Length < 4 || !model[..4].SequenceEqual("G4MD"u8))
            throw new InvalidDataException("The replacement model is not G4MD.");

        var skeletonIndex = FindUniqueMember("G4SK"u8);
        var modelIndex = FindUniqueMember("G4MD"u8);
        var skeletonBytes = skeleton.ToArray();
        var modelBytes = model.ToArray();
        var replacements = Members.Select((member, index) => index switch
        {
            _ when index == skeletonIndex => new G4pkModelMember($"{modelStem}.g4sk", skeletonBytes, 0),
            _ when index == modelIndex => new G4pkModelMember($"{modelStem}.g4md", modelBytes, 0),
            _ => member,
        }).ToArray();
        var rebuilt = Build(replacements);
        var check = Read(rebuilt);
        if (!check.Members.Select(member => (member.Name, member.Content.Length))
            .SequenceEqual(replacements.Select(member => (member.Name, member.Content.Length)))
            || !check.Members[skeletonIndex].Content.Span.SequenceEqual(skeletonBytes)
            || !check.Members[modelIndex].Content.Span.SequenceEqual(modelBytes))
            throw new InvalidDataException("The rebuilt G4PKM package did not pass independent readback.");
        return rebuilt;
    }

    private int FindUniqueMember(ReadOnlySpan<byte> magic)
    {
        var found = -1;
        for (var index = 0; index < Members.Count; index++)
        {
            if (!Members[index].Content.Span[..4].SequenceEqual(magic))
                continue;
            if (found >= 0)
                throw new InvalidDataException($"The package contains more than one {Encoding.ASCII.GetString(magic)} member.");
            found = index;
        }
        if (found < 0)
            throw new InvalidDataException($"The package has no {Encoding.ASCII.GetString(magic)} member.");
        return found;
    }

    private byte[] Build(IReadOnlyList<G4pkModelMember> members)
    {
        if (members.Count != _resourceCount)
            throw new InvalidDataException("Replacing a model package cannot change its member count.");

        var offsetsTableOffset = checked(_headerSize + _offsetTableWordOffset * 4);
        var sizesTableOffset = checked(offsetsTableOffset + _resourceCount * 4);
        var hashTableOffset = checked(sizesTableOffset + _resourceCount * 4);
        var hashTableEnd = checked(hashTableOffset + _hashTableCount * 4);
        var nameIndexOffset = Align4(checked(hashTableEnd + _preNameTable.Length));
        if (nameIndexOffset - hashTableEnd != _preNameTable.Length)
            throw new InvalidDataException("The preserved package table alignment is inconsistent.");
        var nameStringsOffset = Align4(checked(nameIndexOffset + _nameIndexBytes));

        var encodedNames = members.Select(member => Encoding.UTF8.GetBytes(member.Name + "\0")).ToArray();
        var nameOffsets = new short[members.Count];
        var nameBytes = new byte[encodedNames.Sum(name => name.Length)];
        var nameCursor = 0;
        for (var index = 0; index < members.Count; index++)
        {
            var relativeOffset = checked(nameStringsOffset - nameIndexOffset + nameCursor);
            if (relativeOffset > short.MaxValue)
                throw new InvalidDataException("The package member names exceed the signed offset table.");
            nameOffsets[index] = (short)relativeOffset;
            encodedNames[index].CopyTo(nameBytes, nameCursor);
            nameCursor += encodedNames[index].Length;
        }

        var payloadStart = Align32(checked(nameStringsOffset + nameBytes.Length));
        var memberOffsets = new int[members.Count];
        var cursor = payloadStart;
        for (var index = 0; index < members.Count; index++)
        {
            cursor = Align32(cursor);
            memberOffsets[index] = cursor;
            cursor = checked(cursor + members[index].Content.Length);
        }
        var packageLength = Align32(cursor);
        var result = new byte[packageLength];
        _header.CopyTo(result, 0);
        _preOffsetTable.CopyTo(result, _headerSize);
        _preNameTable.CopyTo(result, hashTableEnd);
        nameCursor = 0;
        for (var index = 0; index < members.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(offsetsTableOffset + index * 4, 4),
                (uint)((memberOffsets[index] - _headerSize) / 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(sizesTableOffset + index * 4, 4),
                (uint)members[index].Content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(hashTableOffset + index * 4, 4),
                Crc32(Encoding.UTF8.GetBytes(members[index].Name)));
            BinaryPrimitives.WriteInt16LittleEndian(
                result.AsSpan(nameIndexOffset + index * 2, 2),
                nameOffsets[index]);
            encodedNames[index].CopyTo(result, nameStringsOffset + nameCursor);
            members[index].Content.Span.CopyTo(result.AsSpan(memberOffsets[index]));
            nameCursor += encodedNames[index].Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C, 4), (uint)(packageLength - _headerSize));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x20, 2), (ushort)_resourceCount);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x22, 2), (ushort)_offsetTableWordOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x24, 2), (ushort)_hashTableCount);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x26, 2), (ushort)_nameIndexBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x28, 2), (ushort)((hashTableEnd - _headerSize) / 4));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x2A, 2), (ushort)((nameIndexOffset - _headerSize) / 4));
        return result;
    }

    private static void ValidatePayloadGaps(byte[] source, int namesEnd, IReadOnlyList<G4pkModelMember> members)
    {
        var ranges = members.OrderBy(member => member.Offset).ToArray();
        var cursor = namesEnd;
        foreach (var member in ranges)
        {
            if (member.Offset < cursor)
                throw new InvalidDataException("G4PK package member payloads overlap.");
            RequireZeroPadding(source, cursor, member.Offset - cursor);
            cursor = checked(member.Offset + member.Content.Length);
        }
        RequireZeroPadding(source, cursor, source.Length - cursor);
    }

    private static void RequireZeroPadding(byte[] source, int offset, int length)
    {
        Require(source, offset, length);
        if (source.AsSpan(offset, length).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("The package contains non-zero bytes outside its tables and member payloads.");
    }

    private static uint Crc32(ReadOnlySpan<byte> value)
    {
        var crc = uint.MaxValue;
        foreach (var item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("A G4PK table or member exceeds the package bounds.");
    }

    private static int ReadInt16(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
    private static ushort ReadUInt16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static int Align4(int value) => checked((value + 3) & ~3);
    private static int Align32(int value) => checked((value + 31) & ~31);
}
