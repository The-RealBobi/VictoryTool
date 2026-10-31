using VictoryTool.Application.Text;

namespace VictoryTool.Desktop.ViewModels;

public sealed record RubyTextSegment(string Text, string? Reading)
{
    public bool HasReading => !string.IsNullOrWhiteSpace(Reading);
    public bool ReserveReadingSpace { get; init; }
    public double ReadingHeight => HasReading || ReserveReadingSpace ? 12 : 0;
}

public sealed record RubyTextLine(IReadOnlyList<RubyTextSegment> Segments)
{
    public IEnumerable<RubyTextSegment> VisualSegments => Segments.SelectMany(segment => segment.HasReading
        ? [segment]
        : System.Text.RegularExpressions.Regex.Matches(segment.Text,
            @"\s+|[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]|[^\s\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]+")
            .Select(match => new RubyTextSegment(match.Value, null)))
        .Select(segment => segment with { ReserveReadingSpace = Segments.Any(item => item.HasReading) });
}

public sealed record RubyTextDocumentViewModel(
    IReadOnlyList<RubyTextLine> Lines,
    string AccessibleText)
{
    public string PlainText => string.Join(Environment.NewLine, Lines.Select(line => string.Concat(line.Segments.Select(segment => segment.Text))));

    public static RubyTextDocumentViewModel FromParagraphs(GameTextDocument document)
    {
        var nodes = document.Nodes.Select((node, index) => node is GameTextLineBreak
            && (index == 0 || document.Nodes[index - 1] is not GameTextLineBreak)
            && (index == document.Nodes.Count - 1 || document.Nodes[index + 1] is not GameTextLineBreak)
                ? new GameTextLiteral(" ") : node).ToArray();
        return From(document with { Nodes = nodes });
    }

    public static RubyTextDocumentViewModel From(GameTextDocument document)
    {
        var lines = new List<RubyTextLine>();
        var segments = new List<RubyTextSegment>();
        var accessible = new System.Text.StringBuilder();

        foreach (var node in document.Nodes)
        {
            switch (node)
            {
                case GameTextLiteral literal:
                    segments.Add(new RubyTextSegment(literal.Text, null));
                    accessible.Append(literal.Text);
                    break;
                case GameTextRuby ruby:
                    segments.Add(new RubyTextSegment(ruby.BaseText, ruby.Reading));
                    accessible.Append(ruby.BaseText).Append(" (").Append(ruby.Reading).Append(')');
                    break;
                case GameTextLineBreak:
                    lines.Add(new RubyTextLine(segments.ToArray()));
                    segments.Clear();
                    accessible.AppendLine();
                    break;
                case GameTextCharacterReference reference:
                    segments.Add(new RubyTextSegment(reference.Source, null));
                    accessible.Append(reference.Source);
                    break;
            }
        }

        lines.Add(new RubyTextLine(segments.ToArray()));
        return new RubyTextDocumentViewModel(lines, accessible.ToString());
    }
}
