using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Text;

public static class GameTextResolver
{
    public static GameTextDocument Resolve(
        GameTextDocument document,
        CharacterTextReferenceIndex references,
        string locale)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        using var operation = GlobalLog.BeginOperation("game_text_resolve", new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["nodeCount"] = document.Nodes.Count,
        });

        var nodes = new List<GameTextNode>(document.Nodes.Count);
        var resolvedCount = 0;
        foreach (var node in document.Nodes)
        {
            var resolved = node is GameTextCharacterReference reference
                ? references.Resolve(reference.Kind, reference.Tag, locale) ?? reference.Source
                : null;
            if (resolved is null)
            {
                Append(nodes, node);
                continue;
            }
            resolvedCount++;
            Append(nodes, new GameTextLiteral(resolved));
        }

        GlobalLog.Debug("game_text_resolved", new Dictionary<string, object?>
        {
            ["resolvedReferenceCount"] = resolvedCount,
            ["outputNodeCount"] = nodes.Count,
        });
        return document with { Nodes = nodes };
    }

    private static void Append(List<GameTextNode> nodes, GameTextNode node)
    {
        if (node is GameTextLiteral literal && nodes.LastOrDefault() is GameTextLiteral previous)
            nodes[^1] = previous with { Text = previous.Text + literal.Text };
        else
            nodes.Add(node);
    }
}
