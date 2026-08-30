using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.G4.Textures;

public enum G4TexturePayloadKind
{
    Unknown,
    Dds,
    NxTexture,
}

public sealed record G4TextureEntry(
    int Index,
    string Name,
    uint StoredHash,
    byte Id,
    ushort Width,
    ushort Height,
    int PayloadOffset,
    int PayloadSize,
    G4TexturePayloadKind PayloadKind,
    ReadOnlyMemory<byte> RawRecord,
    ReadOnlyMemory<byte> Payload);

public sealed record G4TextureReplacement(ReadOnlyMemory<byte> Payload, int Width, int Height);

public sealed record G4SubTextureEntry(
    int Index,
    string Name,
    uint StoredHash,
    byte Id,
    int ParentTextureIndex,
    short X,
    short Y,
    short Width,
    short Height,
    ReadOnlyMemory<byte> RawRecord);

public sealed class G4TxDocument
{
    private const int MinimumHeaderSize = 0x30;
    private const int TextureRecordSize = 0x30;
    private const int SubTextureRecordSize = 0x18;
    private readonly byte[] _source;

    private G4TxDocument(
        byte[] source,
        ushort headerSize,
        uint tableSize,
        ushort textureCount,
        ushort totalCount,
        byte subTextureCount,
        int payloadBaseOffset,
        IReadOnlyList<G4TextureEntry> textures,
        IReadOnlyList<G4SubTextureEntry> subTextures)
    {
        _source = source;
        HeaderSize = headerSize;
        TableSize = tableSize;
        TextureCount = textureCount;
        TotalCount = totalCount;
        SubTextureCount = subTextureCount;
        PayloadBaseOffset = payloadBaseOffset;
        Textures = textures;
        SubTextures = subTextures;
    }

    public ushort HeaderSize { get; }
    public uint TableSize { get; }
    public ushort TextureCount { get; }
    public ushort TotalCount { get; }
    public byte SubTextureCount { get; }
    public int PayloadBaseOffset { get; }
    public IReadOnlyList<G4TextureEntry> Textures { get; }
    public IReadOnlyList<G4SubTextureEntry> SubTextures { get; }

    public static G4TxDocument Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    public static G4TxDocument Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < MinimumHeaderSize || !source[..4].SequenceEqual("G4TX"u8))
            throw new InvalidDataException("The input is not a valid G4TX container.");

        var bytes = source.ToArray();
        var headerSize = ReadUInt16(bytes, 0x04, "header size");
        var tableSize = ReadUInt32(bytes, 0x0C, "table size");
        var textureCount = ReadUInt16(bytes, 0x20, "texture count");
        var totalCount = ReadUInt16(bytes, 0x22, "total count");
        var subTextureCount = bytes[0x25];
        if (headerSize < MinimumHeaderSize || totalCount < textureCount)
            throw new InvalidDataException("The G4TX table counts or header size are invalid.");

        int payloadBase;
        int recordsEnd;
        try
        {
            payloadBase = Align16(checked(headerSize + checked((int)tableSize)));
            recordsEnd = checked(headerSize
                + checked(textureCount * TextureRecordSize)
                + checked(subTextureCount * SubTextureRecordSize));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The G4TX table offsets overflow the supported range.", exception);
        }

        if (recordsEnd > payloadBase || payloadBase > bytes.Length)
            throw new InvalidDataException("The G4TX table extends outside the container.");

        var hashTableOffset = Align16(recordsEnd);
        var idTableOffset = CheckedAdd(hashTableOffset, checked(totalCount * sizeof(uint)), "hash table");
        var stringOffsetTable = Align4(CheckedAdd(idTableOffset, totalCount, "ID table"));
        var stringDataStart = CheckedAdd(stringOffsetTable, checked(totalCount * sizeof(ushort)), "string offset table");
        if (stringDataStart > payloadBase)
            throw new InvalidDataException("The G4TX metadata tables overlap the payload area.");

        var names = new string[totalCount];
        var hashes = new uint[totalCount];
        var ids = new byte[totalCount];
        for (var index = 0; index < totalCount; index++)
        {
            hashes[index] = ReadUInt32(bytes, hashTableOffset + index * sizeof(uint), "texture hash");
            ids[index] = ReadByte(bytes, idTableOffset + index, "texture ID");
            var relativeNameOffset = ReadUInt16(
                bytes,
                stringOffsetTable + index * sizeof(ushort),
                "texture name offset");
            var nameOffset = CheckedAdd(stringOffsetTable, relativeNameOffset, "texture name");
            if (nameOffset < stringDataStart || nameOffset >= payloadBase)
                throw new InvalidDataException("A G4TX texture name points outside the string table.");
            names[index] = ReadNullTerminatedAscii(bytes, nameOffset, payloadBase);
        }

        var subTextures = new G4SubTextureEntry[subTextureCount];
        for (var index = 0; index < subTextureCount; index++)
        {
            var recordOffset = checked(headerSize + textureCount * TextureRecordSize + index * SubTextureRecordSize);
            var parentIndex = ReadUInt16(bytes, recordOffset, "subtexture parent index");
            if (parentIndex >= textureCount)
                throw new InvalidDataException("A G4TX subtexture references an invalid parent texture.");
            var metadataIndex = textureCount + index;
            subTextures[index] = new G4SubTextureEntry(
                index,
                names[metadataIndex],
                hashes[metadataIndex],
                ids[metadataIndex],
                parentIndex,
                ReadInt16(bytes, recordOffset + 0x04, "subtexture X"),
                ReadInt16(bytes, recordOffset + 0x06, "subtexture Y"),
                ReadInt16(bytes, recordOffset + 0x08, "subtexture width"),
                ReadInt16(bytes, recordOffset + 0x0A, "subtexture height"),
                bytes.AsMemory(recordOffset, SubTextureRecordSize));
        }

        var textures = new G4TextureEntry[textureCount];
        for (var index = 0; index < textureCount; index++)
        {
            var recordOffset = checked(headerSize + index * TextureRecordSize);
            var relativePayloadOffset = ReadUInt32(bytes, recordOffset + 0x04, "texture payload offset");
            var payloadSizeValue = ReadUInt32(bytes, recordOffset + 0x08, "texture payload size");
            if (relativePayloadOffset > int.MaxValue || payloadSizeValue > int.MaxValue)
                throw new InvalidDataException("A G4TX texture payload exceeds the supported size.");

            var absolutePayloadOffset = CheckedAdd(payloadBase, (int)relativePayloadOffset, "texture payload");
            var payloadSize = (int)payloadSizeValue;
            var payloadEnd = CheckedAdd(absolutePayloadOffset, payloadSize, "texture payload");
            if (absolutePayloadOffset < payloadBase || payloadEnd > bytes.Length)
                throw new InvalidDataException("A G4TX texture payload points outside the container.");

            var payload = bytes.AsMemory(absolutePayloadOffset, payloadSize);
            textures[index] = new G4TextureEntry(
                index,
                names[index],
                hashes[index],
                ids[index],
                ReadUInt16(bytes, recordOffset + 0x18, "texture width"),
                ReadUInt16(bytes, recordOffset + 0x1A, "texture height"),
                absolutePayloadOffset,
                payloadSize,
                DetectPayloadKind(payload.Span),
                bytes.AsMemory(recordOffset, TextureRecordSize),
                payload);
        }

        return new G4TxDocument(
            bytes,
            headerSize,
            tableSize,
            textureCount,
            totalCount,
            subTextureCount,
            payloadBase,
            textures,
            subTextures);
    }

    public byte[] WriteUnmodified() => (byte[])_source.Clone();

    public byte[] ReplaceTextures(IReadOnlyDictionary<string, G4TextureReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return WriteUnmodified();

        var entriesByName = new Dictionary<string, G4TextureEntry>(StringComparer.Ordinal);
        foreach (var texture in Textures)
        {
            if (!entriesByName.TryAdd(texture.Name, texture))
                throw new InvalidDataException("The G4TX container has duplicate texture names and cannot be mutated safely.");
        }
        foreach (var name in replacements.Keys)
        {
            if (!entriesByName.ContainsKey(name))
                throw new ArgumentException($"The G4TX container does not contain texture '{name}'.", nameof(replacements));
        }

        var result = (byte[])_source.Clone();
        foreach (var (name, replacement) in replacements)
        {
            var texture = entriesByName[name];
            if (replacement.Payload.Length != texture.PayloadSize)
                throw new NotSupportedException(
                    "Size-changing G4TX replacement is blocked until opaque payload-region spans are fully verified.");
            if (replacement.Width != texture.Width || replacement.Height != texture.Height)
            {
                throw new NotSupportedException(
                    "Dimension-changing G4TX replacement requires a separately verified native template.");
            }
            var replacementKind = DetectPayloadKind(replacement.Payload.Span);
            if (texture.PayloadKind == G4TexturePayloadKind.Unknown
                || replacementKind == G4TexturePayloadKind.Unknown
                || replacementKind != texture.PayloadKind)
            {
                throw new InvalidDataException(
                    "A G4TX replacement must preserve a verified DDS or NXTCH payload kind.");
            }
            replacement.Payload.Span.CopyTo(result.AsSpan(texture.PayloadOffset, texture.PayloadSize));
        }
        return result;
    }

    public byte[] RenameIdentifier(string oldIdentifier, string newIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(newIdentifier);
        var oldBytes = Encoding.ASCII.GetBytes(oldIdentifier);
        var newBytes = Encoding.ASCII.GetBytes(newIdentifier);
        if (oldBytes.Length != oldIdentifier.Length
            || newBytes.Length != newIdentifier.Length
            || oldBytes.Length != newBytes.Length)
            throw new ArgumentException("G4TX identifiers must be equal-length ASCII strings.");

        var result = (byte[])_source.Clone();
        var replacements = 0;
        for (var offset = 0; offset <= PayloadBaseOffset - oldBytes.Length;)
        {
            if (!result.AsSpan(offset, oldBytes.Length).SequenceEqual(oldBytes))
            {
                offset++;
                continue;
            }
            newBytes.CopyTo(result.AsSpan(offset, newBytes.Length));
            replacements++;
            offset += oldBytes.Length;
        }
        if (replacements == 0)
            throw new InvalidDataException($"G4TX identifier '{oldIdentifier}' was not found in metadata.");

        var recordsEnd = checked(HeaderSize
            + checked(TextureCount * TextureRecordSize)
            + checked(SubTextureCount * SubTextureRecordSize));
        var hashTableOffset = Align16(recordsEnd);
        var renamed = Read(result);
        var names = renamed.Textures.Select(texture => texture.Name)
            .Concat(renamed.SubTextures.Select(texture => texture.Name))
            .ToArray();
        for (var index = 0; index < names.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                result.AsSpan(hashTableOffset + index * sizeof(uint), sizeof(uint)),
                ComputeCrc32(names[index]));
        return result;
    }

    private static uint ComputeCrc32(string value)
    {
        var crc = uint.MaxValue;
        foreach (var item in Encoding.ASCII.GetBytes(value))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    private static G4TexturePayloadKind DetectPayloadKind(ReadOnlySpan<byte> payload)
    {
        if (payload.StartsWith("DDS "u8)) return G4TexturePayloadKind.Dds;
        if (payload.StartsWith("NXTCH000"u8)) return G4TexturePayloadKind.NxTexture;
        return G4TexturePayloadKind.Unknown;
    }

    private static string ReadNullTerminatedAscii(byte[] source, int offset, int limit)
    {
        var end = offset;
        while (end < limit && source[end] != 0) end++;
        if (end == limit)
            throw new InvalidDataException("A G4TX texture name is not null terminated.");
        return Encoding.ASCII.GetString(source, offset, end - offset);
    }

    private static byte ReadByte(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, sizeof(byte), field);
        return source[offset];
    }

    private static ushort ReadUInt16(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, sizeof(ushort), field);
        return BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, sizeof(ushort)));
    }

    private static short ReadInt16(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, sizeof(short), field);
        return BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(offset, sizeof(short)));
    }

    private static uint ReadUInt32(byte[] source, int offset, string field)
    {
        EnsureRange(source, offset, sizeof(uint), field);
        return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));
    }

    private static void EnsureRange(byte[] source, int offset, int size, string field)
    {
        if (offset < 0 || size < 0 || offset > source.Length - size)
            throw new InvalidDataException($"The G4TX {field} points outside the container.");
    }

    private static int CheckedAdd(int left, int right, string field)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"The G4TX {field} offset overflowed.", exception);
        }
    }

    private static int Align4(int value) => CheckedAdd(value, 3, "alignment") & ~3;
    private static int Align16(int value) => CheckedAdd(value, 15, "alignment") & ~15;

    private static void AlignStream16(MemoryStream stream)
    {
        while ((stream.Position & 15) != 0) stream.WriteByte(0);
    }
}
