using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.G4.Animations;

public sealed record G4skNode(
    int Index,
    string Name,
    int? ParentIndex,
    float[] LocalSrt,
    float[] GlobalMatrix,
    float[] InverseBindMatrix,
    byte Flags,
    uint AuxiliaryU32);

public sealed record G4skSkeleton(byte[] TemplateBytes, IReadOnlyList<G4skNode> Nodes);

public static class G4skSkeletonReader
{
    private const int HeaderSize = 0x40;
    private const int TransformSize = 0x30;
    private const double TransformTolerance = 2e-5;

    public static G4skSkeleton Read(ReadOnlySpan<byte> input)
    {
        if (input.Length < HeaderSize || !input[..4].SequenceEqual("G4SK"u8))
            throw new InvalidDataException("The input is not a G4SK skeleton.");
        var data = input.ToArray();
        if (U16(data, 4) != HeaderSize || U16(data, 6) != 104 || data[8] != 0)
            throw new InvalidDataException("Expected little-endian G4SK v104.");
        if (U32(data, 0x0C) != data.Length - HeaderSize)
            throw new InvalidDataException("G4SK resource extent does not match its file size.");

        var baseOffset = checked(U16(data, 0x0A) * 4);
        var count = U16(data, 0x20);
        if (count == 0 || baseOffset != HeaderSize)
            throw new InvalidDataException("G4SK node table or global-matrix base is unsupported.");

        var globalOffset = baseOffset;
        var inverseOffset = SectionOffset(data, 0x24, baseOffset);
        var localOffset = SectionOffset(data, 0x26, baseOffset);
        var hashOffset = SectionOffset(data, 0x28, baseOffset);
        var parentOffset = SectionOffset(data, 0x2A, baseOffset);
        var orderOffset = SectionOffset(data, 0x2C, baseOffset);
        var flagsOffset = SectionOffset(data, 0x2E, baseOffset);
        var auxiliaryOffset = SectionOffset(data, 0x30, baseOffset);
        var namesOffset = SectionOffset(data, 0x32, baseOffset);
        Require(data, globalOffset, checked(count * TransformSize));
        Require(data, inverseOffset, checked(count * TransformSize));
        Require(data, localOffset, checked(count * TransformSize));
        Require(data, hashOffset, checked(count * 4));
        Require(data, parentOffset, checked(count * 2));
        Require(data, orderOffset, checked(count * 2));
        Require(data, flagsOffset, count);
        Require(data, auxiliaryOffset, checked(count * 4));
        Require(data, namesOffset, checked(count * 2));

        var hashes = new uint[count];
        var sortedIndices = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            hashes[index] = U32(data, hashOffset + index * 4);
            sortedIndices[index] = U16(data, orderOffset + index * 2);
        }
        var sorted = sortedIndices.Order().ToArray();
        for (var index = 0; index < count; index++)
        {
            if (sorted[index] != index)
                throw new InvalidDataException("G4SK hash-order table is not a permutation.");
            if (index > 0 && hashes[sortedIndices[index - 1]] > hashes[sortedIndices[index]])
                throw new InvalidDataException("G4SK hash-order table does not sort name hashes.");
        }

        var nodes = new G4skNode[count];
        var resolvedGlobals = new double[count][];
        for (var index = 0; index < count; index++)
        {
            var name = ReadName(data, namesOffset, count, index);
            if (Crc32(Encoding.ASCII.GetBytes(name)) != hashes[index])
                throw new InvalidDataException($"G4SK name hash mismatch for node {index}.");

            var rawParent = U16(data, parentOffset + index * 2);
            if (rawParent != count && rawParent >= index)
                throw new InvalidDataException($"G4SK parent does not precede node {index}.");
            int? parentIndex = rawParent == count ? null : rawParent;
            var localSrt = ReadFloats(data, localOffset + index * TransformSize, 12);
            var global = ReadFloats(data, globalOffset + index * TransformSize, 12);
            var inverse = ReadFloats(data, inverseOffset + index * TransformSize, 12);
            var localMatrix = LocalMatrix(localSrt);
            var resolved = parentIndex is null ? localMatrix : Compose(resolvedGlobals[parentIndex.Value], localMatrix);
            resolvedGlobals[index] = resolved;
            if (MaximumDistance(resolved, global) > TransformTolerance)
                throw new InvalidDataException($"G4SK local-to-global transform check failed for node {index}.");

            nodes[index] = new G4skNode(index, name, parentIndex, localSrt, global, inverse,
                data[flagsOffset + index], U32(data, auxiliaryOffset + index * 4));
        }
        return new G4skSkeleton(data, nodes);
    }

    private static string ReadName(byte[] data, int tableOffset, int count, int index)
    {
        var relative = U16(data, tableOffset + index * 2);
        var start = checked(tableOffset + relative);
        if (relative < count * 2 || start >= data.Length)
            throw new InvalidDataException("A G4SK joint-name offset is invalid.");
        var searchEnd = Math.Min(start + 256, data.Length);
        var end = Array.IndexOf(data, (byte)0, start, searchEnd - start);
        if (end <= start)
            throw new InvalidDataException("A G4SK joint name is not terminated.");
        var bytes = data.AsSpan(start, end - start);
        foreach (var value in bytes)
            if (value < 0x20 || value > 0x7E)
                throw new InvalidDataException($"G4SK node {index} has a non-ASCII name.");
        return Encoding.ASCII.GetString(bytes);
    }

    private static double[] LocalMatrix(float[] srt)
    {
        var sx = (double)srt[0]; var sy = (double)srt[1]; var sz = (double)srt[2];
        var x = (double)srt[4]; var y = (double)srt[5]; var z = (double)srt[6]; var w = (double)srt[7];
        var lengthSquared = x * x + y * y + z * z + w * w;
        var factor = lengthSquared == 0 ? 0 : 2 / lengthSquared;
        var r00 = 1 - factor * (y * y + z * z); var r01 = factor * (x * y - z * w); var r02 = factor * (x * z + y * w);
        var r10 = factor * (x * y + z * w); var r11 = 1 - factor * (x * x + z * z); var r12 = factor * (y * z - x * w);
        var r20 = factor * (x * z - y * w); var r21 = factor * (y * z + x * w); var r22 = 1 - factor * (x * x + y * y);
        return [r00 * sx, r01 * sy, r02 * sz, srt[8], r10 * sx, r11 * sy, r12 * sz, srt[9],
            r20 * sx, r21 * sy, r22 * sz, srt[10]];
    }

    private static double[] Compose(double[] parent, double[] local)
    {
        var result = new double[12];
        for (var row = 0; row < 3; row++)
        {
            var rowOffset = row * 4;
            for (var column = 0; column < 3; column++)
                result[rowOffset + column] = parent[rowOffset] * local[column]
                    + parent[rowOffset + 1] * local[4 + column]
                    + parent[rowOffset + 2] * local[8 + column];
            result[rowOffset + 3] = parent[rowOffset] * local[3]
                + parent[rowOffset + 1] * local[7]
                + parent[rowOffset + 2] * local[11] + parent[rowOffset + 3];
        }
        return result;
    }

    private static double MaximumDistance(double[] left, float[] right)
    {
        var maximum = 0d;
        for (var index = 0; index < left.Length; index++)
            maximum = Math.Max(maximum, Math.Abs(left[index] - right[index]));
        return maximum;
    }

    private static float[] ReadFloats(byte[] data, int offset, int count)
    {
        var values = new float[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = BitConverter.Int32BitsToSingle(unchecked((int)U32(data, offset + index * 4)));
            if (!float.IsFinite(values[index]))
                throw new InvalidDataException("G4SK contains a non-finite transform value.");
        }
        return values;
    }

    private static int SectionOffset(byte[] data, int field, int baseOffset) =>
        checked(baseOffset + U16(data, field) * 4);

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

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("A G4SK table exceeds the skeleton bounds.");
    }
}

public static class G4skSkeletonWriter
{
    private const int HeaderSize = 0x40;
    private const int TransformSize = 0x30;

    public static byte[] SerializeV104(G4skSkeleton template, IReadOnlyList<G4skNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(nodes);
        var source = template.TemplateBytes;
        if (source.Length < HeaderSize || !source.AsSpan(0, 4).SequenceEqual("G4SK"u8)
            || U16(source, 4) != HeaderSize || U16(source, 6) != 104 || source[8] != 0)
            throw new InvalidDataException("G4SK template is not little-endian v104.");
        if (nodes.Count == 0 || nodes.Count >= ushort.MaxValue)
            throw new InvalidDataException("G4SK node count must be between 1 and 65534.");
        if (source[0x23] != 0 || source[0x3A] != 0 || source[0x3B] != 0 || U16(source, 0x3C) != 0)
            throw new NotSupportedException("G4SK template uses unsupported optional per-node tables.");

        var baseOffset = checked(U16(source, 0x0A) * 4);
        if (baseOffset != HeaderSize)
            throw new NotSupportedException("G4SK template has an unsupported global-matrix base offset.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new uint[nodes.Count];
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (node.Index != index)
                throw new InvalidDataException("G4SK nodes must use contiguous physical indices.");
            if (string.IsNullOrEmpty(node.Name) || !node.Name.All(char.IsAscii) || node.Name.Contains('\0'))
                throw new InvalidDataException($"G4SK node {index} has an invalid name.");
            if (!names.Add(node.Name))
                throw new InvalidDataException($"G4SK node name is duplicated: {node.Name}");
            if (node.ParentIndex is { } parent && (parent < 0 || parent >= index))
                throw new InvalidDataException($"G4SK node {index} has a parent that does not precede it.");
            ValidateMatrix(node.LocalSrt, index, "local SRT");
            ValidateMatrix(node.GlobalMatrix, index, "global transform");
            ValidateMatrix(node.InverseBindMatrix, index, "inverse-bind");
            hashes[index] = Crc32(Encoding.ASCII.GetBytes(node.Name));
        }

        using var output = new MemoryStream();
        output.Write(source, 0, HeaderSize);
        WriteMatrixTable(output, nodes, node => node.GlobalMatrix);
        var sectionOffsets = new Dictionary<int, ushort>();
        AppendSection(output, sectionOffsets, 0x24, MatrixTable(nodes, node => node.InverseBindMatrix));
        AppendSection(output, sectionOffsets, 0x26, MatrixTable(nodes, node => node.LocalSrt));
        var hashBytes = new byte[nodes.Count * sizeof(uint)];
        for (var index = 0; index < hashes.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(hashBytes.AsSpan(index * sizeof(uint)), hashes[index]);
        AppendSection(output, sectionOffsets, 0x28, hashBytes);

        var parentBytes = new byte[nodes.Count * sizeof(ushort)];
        for (var index = 0; index < nodes.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(parentBytes.AsSpan(index * sizeof(ushort)),
                checked((ushort)(nodes[index].ParentIndex ?? nodes.Count)));
        AppendSection(output, sectionOffsets, 0x2A, parentBytes);

        var orderBytes = new byte[nodes.Count * sizeof(ushort)];
        foreach (var (nodeIndex, sortedIndex) in hashes.Select((hash, index) => (hash, index))
            .OrderBy(item => item.hash).ThenBy(item => item.index).Select((item, index) => (item.index, index)))
            BinaryPrimitives.WriteUInt16LittleEndian(orderBytes.AsSpan(sortedIndex * sizeof(ushort)), checked((ushort)nodeIndex));
        AppendSection(output, sectionOffsets, 0x2C, orderBytes);

        AppendSection(output, sectionOffsets, 0x2E, nodes.Select(node => node.Flags).ToArray());
        var auxiliaryBytes = new byte[nodes.Count * sizeof(uint)];
        for (var index = 0; index < nodes.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(auxiliaryBytes.AsSpan(index * sizeof(uint)), nodes[index].AuxiliaryU32);
        AppendSection(output, sectionOffsets, 0x30, auxiliaryBytes);

        var nameOffsets = new byte[nodes.Count * sizeof(ushort)];
        using var nameData = new MemoryStream();
        for (var index = 0; index < nodes.Count; index++)
        {
            var relativeOffset = checked(nodes.Count * sizeof(ushort) + (int)nameData.Length);
            if (relativeOffset > ushort.MaxValue)
                throw new InvalidDataException("G4SK name offsets exceed U16 range.");
            BinaryPrimitives.WriteUInt16LittleEndian(nameOffsets.AsSpan(index * sizeof(ushort)), (ushort)relativeOffset);
            var bytes = Encoding.ASCII.GetBytes(nodes[index].Name);
            nameData.Write(bytes);
            nameData.WriteByte(0);
        }
        var nameSection = new byte[nameOffsets.Length + nameData.Length];
        nameOffsets.CopyTo(nameSection, 0);
        nameData.ToArray().CopyTo(nameSection, nameOffsets.Length);
        AppendSection(output, sectionOffsets, 0x32, nameSection);
        PadToAlignment(output, 16);

        var result = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x20), checked((ushort)nodes.Count));
        foreach (var (field, offset) in sectionOffsets)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(field), offset);
        foreach (var field in new[] { 0x34, 0x36, 0x38 })
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(field), sectionOffsets[0x28]);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C), checked((uint)(result.Length - HeaderSize)));
        return result;
    }

    private static void AppendSection(MemoryStream output, IDictionary<int, ushort> offsets, int field, byte[] bytes)
    {
        PadToAlignment(output, 16);
        var offset = checked((int)output.Position - HeaderSize);
        var units = offset / 4;
        if (units > ushort.MaxValue)
            throw new InvalidDataException($"G4SK section at +0x{field:X2} exceeds its U16 offset.");
        offsets.Add(field, (ushort)units);
        output.Write(bytes);
    }

    private static void WriteMatrixTable(MemoryStream output, IReadOnlyList<G4skNode> nodes, Func<G4skNode, float[]> select)
    {
        foreach (var node in nodes)
            foreach (var value in select(node))
                WriteSingle(output, value);
    }

    private static byte[] MatrixTable(IReadOnlyList<G4skNode> nodes, Func<G4skNode, float[]> select)
    {
        using var output = new MemoryStream(nodes.Count * TransformSize);
        WriteMatrixTable(output, nodes, select);
        return output.ToArray();
    }

    private static void ValidateMatrix(float[] values, int nodeIndex, string label)
    {
        if (values.Length != 12 || values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException($"G4SK node {nodeIndex} has an invalid {label} matrix.");
    }

    private static void WriteSingle(Stream output, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void PadToAlignment(Stream output, int alignment)
    {
        while (output.Position % alignment != 0)
            output.WriteByte(0);
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

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
}
