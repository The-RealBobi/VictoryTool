using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace VictoryTool.G4.Animations;

public sealed record G4pkAnimationResource(int PackageIndex, string Name, ReadOnlyMemory<byte> Content, int Offset);
public sealed record G4pkPackageEntry(int PackageIndex, string Name, ReadOnlyMemory<byte> Content, int Offset);

public sealed class G4pkAnimationPackage
{
    private readonly byte[] _source;
    private readonly int _headerSize;
    private readonly int _offsetTableOffset;
    private readonly int _sizeTableOffset;
    private readonly int _resourceHashOffset;
    private readonly int _stringBase;
    private readonly int _packageResourceCount;
    private readonly int _hashCount;
    private readonly int _nameCount;

    private G4pkAnimationPackage(byte[] source, int headerSize, int packageResourceCount, int offsetTableOffset,
        int sizeTableOffset, int resourceHashOffset, int stringBase, int hashCount, int nameCount,
        IReadOnlyList<G4pkPackageEntry> packageEntries, IReadOnlyList<G4pkAnimationResource> resources)
    {
        _source = source;
        _headerSize = headerSize;
        _packageResourceCount = packageResourceCount;
        _offsetTableOffset = offsetTableOffset;
        _sizeTableOffset = sizeTableOffset;
        _resourceHashOffset = resourceHashOffset;
        _stringBase = stringBase;
        _hashCount = hashCount;
        _nameCount = nameCount;
        PackageEntries = packageEntries;
        Resources = resources;
    }

    public IReadOnlyList<G4pkPackageEntry> PackageEntries { get; }
    public IReadOnlyList<G4pkAnimationResource> Resources { get; }

    public static G4pkAnimationPackage Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x40 || !data[..4].SequenceEqual("G4PK"u8))
            throw new InvalidDataException("The input is not a G4PK package.");
        var source = data.ToArray();
        var headerSize = ReadUInt16(source, 4);
        var count = checked((int)ReadUInt32(source, 0x20));
        var hashCount = ReadUInt16(source, 0x24);
        var table3Count = ReadUInt16(source, 0x26);
        var nameCount = table3Count / 2;
        if (headerSize < 0x40 || count == 0 || nameCount < count)
            throw new InvalidDataException("The G4PK resource table is invalid.");
        var offsetsTable = headerSize;
        var sizesTable = checked(offsetsTable + count * 4);
        var resourceHashOffset = checked(sizesTable + count * 4);
        var stringBase = Align4(checked(resourceHashOffset + hashCount * 4 + nameCount * 2));
        Require(source, offsetsTable, count * 4);
        Require(source, sizesTable, count * 4);
        Require(source, stringBase, nameCount * 2);

        var packageEntries = new List<G4pkPackageEntry>();
        var resources = new List<G4pkAnimationResource>();
        for (var index = 0; index < count; index++)
        {
            var nameRelative = BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(stringBase + index * 2));
            var nameOffset = checked(stringBase + nameRelative);
            var name = ReadCString(source, nameOffset);
            var relativeOffset = ReadUInt32(source, offsetsTable + index * 4);
            if (relativeOffset > int.MaxValue / 4)
                throw new InvalidDataException($"G4PK resource {index} has an invalid offset.");
            var offset = checked(headerSize + (int)relativeOffset * 4);
            var rawSize = ReadUInt32(source, sizesTable + index * 4);
            if (rawSize > int.MaxValue)
                throw new InvalidDataException($"G4PK resource {index} has an invalid size.");
            var size = (int)rawSize;
            Require(source, offset, size);
            var content = source.AsMemory(offset, size);
            packageEntries.Add(new G4pkPackageEntry(index, name, content, offset));
            if (size >= 4 && source.AsSpan(offset, 4).SequenceEqual("G4MT"u8))
                resources.Add(new G4pkAnimationResource(index, name, content, offset));
        }
        return new G4pkAnimationPackage(source, headerSize, count, offsetsTable, sizesTable, resourceHashOffset,
            stringBase, hashCount, nameCount, packageEntries, resources);
    }

    public byte[] ReplaceResource(int resourceIndex, string newName, ReadOnlySpan<byte> content)
    {
        if ((uint)resourceIndex >= _packageResourceCount)
            throw new ArgumentOutOfRangeException(nameof(resourceIndex));
        if (content.Length < 4 || !content[..4].SequenceEqual("G4MT"u8))
            throw new InvalidDataException("The replacement resource is not G4MT.");
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('\0'))
            throw new ArgumentException("The G4PK resource name is invalid.", nameof(newName));
        if (_hashCount != _packageResourceCount || _nameCount != _packageResourceCount
            || PackageEntries.Count != _packageResourceCount)
            throw new NotSupportedException("The G4PK resource, name, and hash tables do not have a one-to-one layout.");
        var target = PackageEntries[resourceIndex];
        if (target.Content.Length < 4 || !target.Content.Span[..4].SequenceEqual("G4MT"u8))
            throw new InvalidDataException($"G4PK resource {resourceIndex} is not G4MT.");
        if (string.Equals(target.Name, newName, StringComparison.Ordinal)
            && target.Content.Span.SequenceEqual(content))
            return _source.ToArray();

        for (var index = 0; index < PackageEntries.Count; index++)
        {
            var expected = Crc32(Encoding.UTF8.GetBytes(PackageEntries[index].Name));
            if (ReadUInt32(_source, _resourceHashOffset + index * 4) != expected)
                throw new NotSupportedException($"G4PK resource hash {index} does not match its name CRC.");
        }

        var encodedName = Encoding.UTF8.GetBytes(newName + "\0");
        var ordered = PackageEntries.OrderBy(entry => entry.Offset).ToArray();
        var firstOffset = ordered[0].Offset;
        var output = new List<byte>(checked(_source.Length + encodedName.Length + content.Length - target.Content.Length));
        output.AddRange(_source.AsSpan(0, firstOffset).ToArray());
        var targetNameRelative = BinaryPrimitives.ReadInt16LittleEndian(
            _source.AsSpan(_stringBase + resourceIndex * 2, 2));
        var targetNameOffset = checked(_stringBase + targetNameRelative);
        var nameSlotEnd = firstOffset;
        var nameIsShared = false;
        for (var index = 0; index < _nameCount; index++)
        {
            var relative = BinaryPrimitives.ReadInt16LittleEndian(
                _source.AsSpan(_stringBase + index * 2, 2));
            var otherNameOffset = checked(_stringBase + relative);
            if (index != resourceIndex && otherNameOffset == targetNameOffset)
                nameIsShared = true;
            if (otherNameOffset > targetNameOffset && otherNameOffset < nameSlotEnd)
                nameSlotEnd = otherNameOffset;
        }

        int newNameOffset;
        if (!nameIsShared && targetNameOffset >= _stringBase && targetNameOffset + encodedName.Length <= nameSlotEnd)
        {
            encodedName.CopyTo(CollectionsMarshal.AsSpan(output).Slice(targetNameOffset));
            newNameOffset = targetNameOffset;
        }
        else
        {
            newNameOffset = output.Count;
            output.AddRange(encodedName);
            while ((output.Count & 3) != 0) output.Add(0);
        }
        var relativeNameOffset = checked(newNameOffset - _stringBase);
        if (relativeNameOffset is < short.MinValue or > short.MaxValue)
            throw new NotSupportedException("The replacement G4PK name is outside the signed name-offset range.");
        BinaryPrimitives.WriteInt16LittleEndian(CollectionsMarshal.AsSpan(output).Slice(_stringBase + resourceIndex * 2, 2),
            checked((short)relativeNameOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(CollectionsMarshal.AsSpan(output).Slice(
            _resourceHashOffset + resourceIndex * 4, 4), Crc32(Encoding.UTF8.GetBytes(newName)));

        for (var index = 0; index < ordered.Length; index++)
        {
            var entry = ordered[index];
            var offset = output.Count;
            if (offset < _headerSize || ((offset - _headerSize) & 3) != 0)
                throw new InvalidDataException("A rebuilt G4PK resource is not four-byte aligned.");
            BinaryPrimitives.WriteUInt32LittleEndian(CollectionsMarshal.AsSpan(output).Slice(
                _offsetTableOffset + entry.PackageIndex * 4, 4), checked((uint)((offset - _headerSize) / 4)));
            var replacement = entry.PackageIndex == resourceIndex ? content : entry.Content.Span;
            BinaryPrimitives.WriteUInt32LittleEndian(CollectionsMarshal.AsSpan(output).Slice(
                _sizeTableOffset + entry.PackageIndex * 4, 4), checked((uint)replacement.Length));
            output.AddRange(replacement.ToArray());

            if (index + 1 < ordered.Length)
            {
                var gapStart = checked(entry.Offset + entry.Content.Length);
                var gapLength = checked(ordered[index + 1].Offset - gapStart);
                if (gapLength < 0)
                    throw new InvalidDataException("G4PK resource payloads overlap.");
                output.AddRange(_source.AsSpan(gapStart, gapLength).ToArray());
                while ((output.Count & 3) != 0) output.Add(0);
            }
        }
        var last = ordered[^1];
        var tailOffset = checked(last.Offset + last.Content.Length);
        output.AddRange(_source.AsSpan(tailOffset).ToArray());
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C), checked((uint)(result.Length - 0x40)));
        return result;
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

    private static string ReadCString(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length)
            throw new InvalidDataException("A G4PK resource name points outside the package.");
        var end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0)
            throw new InvalidDataException("A G4PK resource name is not terminated.");
        return Encoding.UTF8.GetString(data, offset, end - offset);
    }

    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("A G4PK resource table or payload exceeds the package bounds.");
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static short ReadInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static int Align4(int value) => checked((value + 3) & ~3);
}
