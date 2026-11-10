using System.Buffers.Binary;
using System.Text;

namespace VictoryTool.Application.Exporting;

internal static class ExportProvenance
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes(
        $"VT_v{typeof(ExportExecutor).Assembly.GetName().Version!.ToString(3)}");

    internal static void MarkT2b(Span<byte> data)
    {
        if (data.Length < 48 || !data[^16..^12].SequenceEqual("\x01t2b"u8))
            return;
        var footer = data.Length - 16;
        var stringsEnd = (long)Read(data, 4) + Read(data, 8);
        var nameSection = (stringsEnd + 15) & ~15L;
        if (nameSection > footer - 16)
            return;
        var section = (int)nameSection;
        var namesEnd = nameSection + Read(data, section + 8) + Read(data, section + 12);
        if (nameSection + Read(data, section) != footer || namesEnd < nameSection + 16 ||
            namesEnd > footer - Marker.Length)
            return;
        var padding = data[(int)namesEnd..footer];
        if (padding.IndexOfAnyExcept((byte)0xFF) < 0)
            Marker.CopyTo(padding);
    }

    internal static byte[] MarkG4pk(byte[] data)
    {
        if (data.Length < 0x40 || !data.AsSpan(0, 4).SequenceEqual("G4PK"u8))
            return data;
        var header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        if (header < 0x40 || header + (long)Read(data, 12) != data.Length)
            return data;
        var length = checked((data.Length + Marker.Length + 63) & ~63);
        var output = new byte[length];
        data.CopyTo(output, 0);
        Marker.CopyTo(output, data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)(length - header));
        return output;
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
