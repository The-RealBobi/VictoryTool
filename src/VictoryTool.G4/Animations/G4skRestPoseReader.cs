using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace VictoryTool.G4.Animations;

public static class G4skRestPoseReader
{
    public static IReadOnlyDictionary<uint, G4mtJointRest> Read(ReadOnlySpan<byte> input)
    {
        if (input.Length < 0x40 || !input[..4].SequenceEqual("G4SK"u8))
            throw new InvalidDataException("The input is not a G4SK skeleton.");
        var data = input.ToArray();
        var count = U16(data, 0x20);
        if (U16(data, 4) != 0x40 || U16(data, 0x0A) != 0x10 || count == 0)
            throw new InvalidDataException("The G4SK header is unsupported.");
        Require(data, 0x40, checked(count * 0x30));

        var localRestOffset = SectionOffset(data, 0x26);
        Require(data, localRestOffset, checked(count * 0x30));
        var nameOffset = FindNameTable(data, count);
        var names = Enumerable.Range(0, count).Select(index => ReadName(data, nameOffset, count, index)).ToArray();
        var rests = new Dictionary<uint, G4mtJointRest>(count);
        for (var index = 0; index < count; index++)
        {
            var name = names[index];
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"G4SK joint {index} has an empty name.");
            var hash = Crc32(Encoding.UTF8.GetBytes(name));
            var offset = localRestOffset + index * 0x30;
            var scale = ReadVector(data, offset);
            var rotation = ReadQuaternion(data, offset + 0x10);
            var translation = ReadVector(data, offset + 0x20);
            if (!rests.TryAdd(hash, new G4mtJointRest(translation, Quaternion.Normalize(rotation), scale)))
                throw new InvalidDataException($"G4SK joint name CRC32 collision at '{name}'.");
        }
        return rests;
    }

    private static Vector3 ReadVector(byte[] data, int offset)
    {
        var result = new Vector3(ReadFloat(data, offset), ReadFloat(data, offset + 4), ReadFloat(data, offset + 8));
        return result;
    }

    private static Quaternion ReadQuaternion(byte[] data, int offset)
    {
        var result = new Quaternion(
            ReadFloat(data, offset), ReadFloat(data, offset + 4),
            ReadFloat(data, offset + 8), ReadFloat(data, offset + 12));
        if (result.LengthSquared() < 1e-12f)
            throw new InvalidDataException("The G4SK contains a zero-length joint rotation.");
        return result;
    }

    private static float ReadFloat(byte[] data, int offset)
    {
        var value = BitConverter.Int32BitsToSingle(unchecked((int)U32(data, offset)));
        if (!float.IsFinite(value))
            throw new InvalidDataException("The G4SK contains a non-finite joint transform.");
        return value;
    }

    private static int FindNameTable(byte[] data, int count)
    {
        var offset = SectionOffset(data, 0x32);
        if (offset < 0 || offset > data.Length - count * 2)
            throw new InvalidDataException("The G4SK joint-name table could not be read.");
        for (var index = 0; index < count; index++)
            _ = ReadName(data, offset, count, index);
        return offset;
    }

    private static string ReadName(byte[] data, int table, int count, int index)
    {
        var relative = U16(data, table + index * 2);
        var start = checked(table + relative);
        if (relative < count * 2 || start >= data.Length)
            throw new InvalidDataException("A G4SK joint-name offset is invalid.");
        var end = Array.IndexOf(data, (byte)0, start);
        if (end <= start)
            throw new InvalidDataException("A G4SK joint name is not terminated.");
        return Encoding.UTF8.GetString(data, start, end - start);
    }

    private static int SectionOffset(byte[] data, int field) =>
        checked(0x40 + U16(data, field) * 4);

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
