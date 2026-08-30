using System.Text;

namespace VictoryTool.Application.Assets;

public static class FixedWidthResourceIdRewriter
{
    public static byte[] Replace(ReadOnlySpan<byte> source, string oldIdentifier, string newIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(newIdentifier);
        var oldBytes = Encoding.ASCII.GetBytes(oldIdentifier);
        var newBytes = Encoding.ASCII.GetBytes(newIdentifier);
        if (oldBytes.Length != oldIdentifier.Length
            || newBytes.Length != newIdentifier.Length
            || oldBytes.Length != newBytes.Length)
        {
            throw new ArgumentException("Resource identifiers must be equal-length ASCII strings.");
        }

        var result = source.ToArray();
        var replacements = 0;
        for (var offset = 0; offset <= result.Length - oldBytes.Length;)
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
            throw new InvalidDataException($"Resource identifier '{oldIdentifier}' was not found.");
        return result;
    }
}
