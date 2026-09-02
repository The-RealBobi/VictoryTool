namespace VictoryTool.Application.Text;

public abstract record GameTextNode;
public sealed record GameTextLiteral(string Text) : GameTextNode;
public sealed record GameTextLineBreak : GameTextNode;
public sealed record GameTextRuby(string BaseText, string Reading) : GameTextNode;

public enum CharacterReferenceKind
{
    FirstName,
    LastName,
    FullName,
}

public sealed record GameTextCharacterReference(
    CharacterReferenceKind Kind,
    string Tag,
    string Source) : GameTextNode;

public sealed record GameTextDocument(string Source, IReadOnlyList<GameTextNode> Nodes);

public static class GameTextParser
{
    public static GameTextDocument Parse(string? source)
    {
        source ??= string.Empty;
        var nodes = new List<GameTextNode>();
        var literal = new System.Text.StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            nodes.Add(new GameTextLiteral(literal.ToString()));
            literal.Clear();
        }

        for (var index = 0; index < source.Length;)
        {
            if (source[index] == '\\'
                && index + 2 < source.Length
                && source[index + 1] == '\\'
                && source[index + 2] == 'n')
            {
                FlushLiteral();
                nodes.Add(new GameTextLineBreak());
                index += 3;
                continue;
            }
            if (source[index] == '\\' && index + 1 < source.Length && source[index + 1] == 'n')
            {
                FlushLiteral();
                nodes.Add(new GameTextLineBreak());
                index += 2;
                continue;
            }
            if (source[index] is '\r' or '\n')
            {
                FlushLiteral();
                if (source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                nodes.Add(new GameTextLineBreak());
                index++;
                continue;
            }
            if (source[index] == '[' && TryParseRuby(source, index, out var ruby, out var rubyLength))
            {
                FlushLiteral();
                nodes.Add(ruby);
                index += rubyLength;
                continue;
            }
            if (source[index] == '<' && TryParseReference(source, index, out var reference, out var referenceLength))
            {
                FlushLiteral();
                nodes.Add(reference);
                index += referenceLength;
                continue;
            }

            literal.Append(source[index]);
            index++;
        }
        FlushLiteral();
        return new GameTextDocument(source, nodes);
    }

    private static bool TryParseRuby(string source, int start, out GameTextRuby ruby, out int length)
    {
        ruby = null!;
        length = 0;
        var end = source.IndexOf(']', start + 1);
        if (end < 0) return false;
        var separator = source.IndexOf('/', start + 1, end - start - 1);
        if (separator <= start + 1 || separator >= end - 1) return false;
        var baseText = source[(start + 1)..separator];
        var reading = source[(separator + 1)..end];
        if (baseText.IndexOfAny(['[', ']']) >= 0 || reading.IndexOfAny(['[', ']']) >= 0) return false;
        ruby = new GameTextRuby(baseText, reading);
        length = end - start + 1;
        return true;
    }

    private static bool TryParseReference(
        string source,
        int start,
        out GameTextCharacterReference reference,
        out int length)
    {
        reference = null!;
        length = 0;
        var end = source.IndexOf('>', start + 1);
        if (end < 0) return false;
        var content = source[(start + 1)..end];
        var separator = content.IndexOf(':');
        if (separator <= 0 || separator == content.Length - 1) return false;
        var kind = content[..separator] switch
        {
            "FST" => CharacterReferenceKind.FirstName,
            "LST" => CharacterReferenceKind.LastName,
            "FUL" => CharacterReferenceKind.FullName,
            "FLC" => CharacterReferenceKind.FullName,
            _ => (CharacterReferenceKind?)null,
        };
        if (kind is null) return false;
        var tag = content[(separator + 1)..];
        if (tag.IndexOfAny(['<', '>', ':']) >= 0) return false;
        var token = source[start..(end + 1)];
        reference = new GameTextCharacterReference(kind.Value, tag, token);
        length = token.Length;
        return true;
    }
}
