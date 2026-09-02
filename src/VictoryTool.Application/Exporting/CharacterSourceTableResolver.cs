using VictoryTool.CfgBin;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterSourceTables(
    string BaseTableVirtualPath,
    string BaseTableSourcePath,
    string ParameterTableVirtualPath,
    string ParameterTableSourcePath);

public interface ICharacterSourceTableResolver
{
    CharacterSourceTables Resolve(string dumpRoot, int sourceBaseId, int sourceParameterId);
}

public sealed class CharacterSourceTableResolver : ICharacterSourceTableResolver
{
    public CharacterSourceTables Resolve(string dumpRoot, int sourceBaseId, int sourceParameterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpRoot);
        using var operation = GlobalLog.BeginOperation("source_tables_resolve", new Dictionary<string, object?>
        {
            ["sourceBaseId"] = sourceBaseId,
            ["sourceParameterId"] = sourceParameterId,
        });
        var normalizedRoot = Path.GetFullPath(dumpRoot);
        var characterRoot = Path.Combine(normalizedRoot, "common", "gamedata", "character");
        var basePath = FindSingleTable(
            characterRoot,
            "chara_base_*.cfg.bin",
            "CHARA_BASE_INFO",
            entry => ReadInteger(entry, 0) == sourceBaseId);
        var parameterPath = FindSingleTable(
            characterRoot,
            "chara_param_*.cfg.bin",
            "CHARA_PARAM_INFO",
            entry => ReadInteger(entry, 0) == sourceParameterId
                     && ReadInteger(entry, 1) == sourceBaseId);
        var result = new CharacterSourceTables(
            ToVirtualPath(normalizedRoot, basePath),
            basePath,
            ToVirtualPath(normalizedRoot, parameterPath),
            parameterPath);
        GlobalLog.Debug("source_tables_resolved");
        return result;
    }

    private static string FindSingleTable(
        string root,
        string pattern,
        string entryName,
        Func<CfgBinEntry, bool> predicate)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Character table directory not found: {root}");
        var matches = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
        {
            CfgBinDocument document;
            try
            {
                document = CfgBinDocument.Read(File.ReadAllBytes(path));
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                GlobalLog.Warn("source_table_candidate_rejected", exception: exception);
                continue;
            }
            if (document.Entries.Any(entry => entry.Name == entryName && predicate(entry)))
                matches.Add(path);
        }
        if (matches.Count != 1)
        {
            GlobalLog.Warn("source_table_match_count_invalid", new Dictionary<string, object?>
            {
                ["entryName"] = entryName,
                ["matchCount"] = matches.Count,
            });
            throw new InvalidDataException(
                $"Expected exactly one {entryName} source table, found {matches.Count}.");
        }
        return matches[0];
    }

    private static int ReadInteger(CfgBinEntry entry, int index) => entry.Values[index].Value switch
    {
        int value => value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        _ => int.MinValue,
    };

    private static string ToVirtualPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
