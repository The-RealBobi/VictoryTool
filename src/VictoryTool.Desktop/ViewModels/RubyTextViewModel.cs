using VictoryTool.Application.Text;

namespace VictoryTool.Desktop.ViewModels;

public sealed record RubyTextSegment(string Text, string? Reading)
{
    public bool HasReading => !string.IsNullOrWhiteSpace(Reading);
}

public sealed record RubyTextLine(IReadOnlyList<RubyTextSegment> Segments);

public sealed record RubyTextDocumentViewModel(
    IReadOnlyList<RubyTextLine> Lines,
    string AccessibleText)
{
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
