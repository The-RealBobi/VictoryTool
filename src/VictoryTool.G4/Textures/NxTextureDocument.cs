using System.Buffers.Binary;

namespace VictoryTool.G4.Textures;

public enum NxTextureFormat
{
    Unknown,
    Rgba8888,
    Bc1,
    Bc3,
    Bc7,
}

public sealed record NxMipLevel(
    int Level,
    int Width,
    int Height,
    int AbsoluteOffset,
    int Size,
    ReadOnlyMemory<byte> Data);

public sealed class NxTextureDocument
{
    private const int DataOffset = 0x100;
    private readonly byte[] _source;

    private NxTextureDocument(
        byte[] source,
        int width,
        int height,
        uint formatIdentifier,
        NxTextureFormat format,
        int maximumBitExtensionCount,
        IReadOnlyList<NxMipLevel> mipLevels)
    {
        _source = source;
        Width = width;
        Height = height;
        FormatIdentifier = formatIdentifier;
        Format = format;
        MaximumBitExtensionCount = maximumBitExtensionCount;
        MipLevels = mipLevels;
    }

    public int Width { get; }
    public int Height { get; }
    public uint FormatIdentifier { get; }
    public NxTextureFormat Format { get; }
    public int MaximumBitExtensionCount { get; }
    public IReadOnlyList<NxMipLevel> MipLevels { get; }

    public static NxTextureDocument Read(ReadOnlySpan<byte> source)
    {
        global::System.Diagnostics.Trace.WriteLine($"nxtch_read_started bytes={source.Length}");
        if (source.Length < DataOffset || !source[..8].SequenceEqual("NXTCH000"u8))
            throw new InvalidDataException("The input is not a valid NXTCH texture.");

        var bytes = source.ToArray();
        var dataSize = ReadUInt32(bytes, 0x08, "data size");
        var widthValue = ReadUInt32(bytes, 0x14, "width");
        var heightValue = ReadUInt32(bytes, 0x18, "height");
        var formatIdentifier = ReadUInt32(bytes, 0x24, "format");
        var mipCountValue = ReadUInt32(bytes, 0x28, "mip count");
        if (widthValue is 0 or > int.MaxValue || heightValue is 0 or > int.MaxValue)
            throw new InvalidDataException("The NXTCH dimensions are invalid.");
        if (mipCountValue is 0 or > 52)
            throw new InvalidDataException("The NXTCH mip count is invalid.");
        if (dataSize > int.MaxValue || DataOffset + (long)dataSize > bytes.Length)
            throw new InvalidDataException("The NXTCH texture data points outside the payload.");

        var width = (int)widthValue;
        var height = (int)heightValue;
        var mipCount = (int)mipCountValue;
        var offsets = new int[mipCount];
        for (var level = 0; level < mipCount; level++)
        {
            var offsetValue = ReadUInt32(bytes, 0x30 + level * sizeof(uint), "mip offset");
            if (offsetValue > dataSize || offsetValue > int.MaxValue)
                throw new InvalidDataException("An NXTCH mip offset points outside the texture data.");
            offsets[level] = (int)offsetValue;
            if (level != 0 && offsets[level] < offsets[level - 1])
                throw new InvalidDataException("NXTCH mip offsets must be monotonic.");
        }

        var mipLevels = new NxMipLevel[mipCount];
        for (var level = 0; level < mipCount; level++)
        {
            var end = level + 1 < mipCount ? offsets[level + 1] : (int)dataSize;
            var size = end - offsets[level];
            var absoluteOffset = DataOffset + offsets[level];
            mipLevels[level] = new NxMipLevel(
                level,
                Math.Max(1, width >> level),
                Math.Max(1, height >> level),
                absoluteOffset,
                size,
                bytes.AsMemory(absoluteOffset, size));
        }

        var document = new NxTextureDocument(
            bytes,
            width,
            height,
            formatIdentifier,
            formatIdentifier switch
            {
                0x25 => NxTextureFormat.Rgba8888,
                0x42 => NxTextureFormat.Bc1,
                0x44 => NxTextureFormat.Bc3,
                0x4D => NxTextureFormat.Bc7,
                _ => NxTextureFormat.Unknown,
            },
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x74, sizeof(int))),
            mipLevels);
        global::System.Diagnostics.Trace.WriteLine(
            $"nxtch_read_completed width={document.Width} height={document.Height} format={document.Format}");
        return document;
    }

    public byte[] WriteUnmodified() => (byte[])_source.Clone();

    public byte[] GetLinearMipData(int level)
    {
        if ((uint)level >= (uint)MipLevels.Count)
            throw new ArgumentOutOfRangeException(nameof(level));
        var mip = MipLevels[level];
        var (bitDepth, unitSize, blockCompressed) = Format switch
        {
            NxTextureFormat.Rgba8888 => (32, 4, false),
            NxTextureFormat.Bc1 => (4, 8, true),
            NxTextureFormat.Bc3 => (8, 16, true),
            NxTextureFormat.Bc7 => (8, 16, true),
            _ => throw new NotSupportedException($"Unsupported NXTCH format identifier 0x{FormatIdentifier:X}.")
        };
        var fields = CreateBitFields(bitDepth, blockCompressed, mip.Height, MaximumBitExtensionCount);
        var (paddedWidth, paddedHeight) = GetPaddedDimensions(mip.Width, mip.Height, fields);
        return blockCompressed
            ? UnswizzleBlocks(mip, unitSize, paddedWidth, paddedHeight, fields)
            : UnswizzlePixels(mip, unitSize, paddedWidth, paddedHeight, fields);
    }

    private static byte[] UnswizzleBlocks(
        NxMipLevel mip,
        int unitSize,
        int paddedWidth,
        int paddedHeight,
        IReadOnlyList<(int X, int Y)> fields)
    {
        var blockWidth = (mip.Width + 3) / 4;
        var blockHeight = (mip.Height + 3) / 4;
        var paddedBlocks = checked(((paddedWidth + 3) / 4) * ((paddedHeight + 3) / 4));
        var output = new byte[checked(blockWidth * blockHeight * unitSize)];
        var sourceBlockCount = Math.Min(mip.Data.Length / unitSize, paddedBlocks);
        for (var sourceBlock = 0; sourceBlock < sourceBlockCount; sourceBlock++)
        {
            var (x, y) = GetSwizzledPoint(checked(sourceBlock * 16), paddedWidth, fields);
            var targetX = x / 4;
            var targetY = y / 4;
            if (targetX >= blockWidth || targetY >= blockHeight) continue;
            var targetOffset = checked((targetY * blockWidth + targetX) * unitSize);
            mip.Data.Span.Slice(sourceBlock * unitSize, unitSize).CopyTo(output.AsSpan(targetOffset, unitSize));
        }
        return output;
    }

    private static byte[] UnswizzlePixels(
        NxMipLevel mip,
        int unitSize,
        int paddedWidth,
        int paddedHeight,
        IReadOnlyList<(int X, int Y)> fields)
    {
        var output = new byte[checked(mip.Width * mip.Height * unitSize)];
        var sourceCount = Math.Min(mip.Data.Length / unitSize, checked(paddedWidth * paddedHeight));
        for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            var (x, y) = GetSwizzledPoint(sourceIndex, paddedWidth, fields);
            if (x >= mip.Width || y >= mip.Height) continue;
            var targetOffset = checked((y * mip.Width + x) * unitSize);
            mip.Data.Span.Slice(sourceIndex * unitSize, unitSize).CopyTo(output.AsSpan(targetOffset, unitSize));
        }
        return output;
    }

    private static IReadOnlyList<(int X, int Y)> CreateBitFields(
        int bitDepth,
        bool blockCompressed,
        int height,
        int extensionCount)
    {
        var fields = (blockCompressed, bitDepth) switch
        {
            (true, 4) => new List<(int, int)> { (1, 0), (2, 0), (0, 1), (0, 2), (4, 0), (0, 4), (8, 0), (0, 8), (0, 16), (16, 0) },
            (true, 8) => new List<(int, int)> { (1, 0), (2, 0), (0, 1), (0, 2), (0, 4), (4, 0), (0, 8), (0, 16), (8, 0) },
            (false, 32) => new List<(int, int)> { (1, 0), (2, 0), (0, 1), (4, 0), (0, 2), (0, 4), (8, 0) },
            _ => throw new NotSupportedException($"Unsupported NXTCH bit depth: {bitDepth}.")
        };
        var startY = blockCompressed ? 32 : 8;
        var maximum = blockCompressed ? 512 : 128;
        var remaining = extensionCount;
        while (startY < Math.Min(height, maximum) && (remaining < 0 || remaining-- > 0))
        {
            fields.Add((0, startY));
            startY *= 2;
        }
        return fields;
    }

    private static (int Width, int Height) GetPaddedDimensions(
        int width,
        int height,
        IReadOnlyList<(int X, int Y)> fields)
    {
        var macroWidth = 0;
        var macroHeight = 0;
        foreach (var (x, y) in fields)
        {
            macroWidth |= x;
            macroHeight |= y;
        }
        macroWidth++;
        macroHeight++;
        return (
            checked((width + macroWidth - 1) / macroWidth * macroWidth),
            checked((height + macroHeight - 1) / macroHeight * macroHeight));
    }

    private static (int X, int Y) GetSwizzledPoint(
        int index,
        int width,
        IReadOnlyList<(int X, int Y)> fields)
    {
        var macroWidth = 0;
        var macroHeight = 0;
        foreach (var (x, y) in fields)
        {
            macroWidth |= x;
            macroHeight |= y;
        }
        macroWidth++;
        macroHeight++;
        var pointsPerMacro = checked(macroWidth * macroHeight);
        var widthInTiles = (width + macroWidth - 1) / macroWidth;
        var macroIndex = index / pointsPerMacro;
        var xResult = macroIndex % widthInTiles * macroWidth;
        var yResult = macroIndex / widthInTiles * macroHeight;
        for (var bit = 0; bit < fields.Count; bit++)
        {
            if (((index >> bit) & 1) == 0) continue;
            xResult ^= fields[bit].X;
            yResult ^= fields[bit].Y;
        }
        return (xResult, yResult);
    }

    private static uint ReadUInt32(byte[] source, int offset, string field)
    {
        if (offset < 0 || offset > source.Length - sizeof(uint))
            throw new InvalidDataException($"The NXTCH {field} points outside the header.");
        return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));
    }
}
