namespace VictoryTool.Application.Projects;

public sealed class ModProject
{
    private readonly List<string> _packagePaths = [];
    private readonly HashSet<string> _knownPackagePaths = new(PathComparer);

    public ModProject(string gameDumpPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDumpPath);
        GameDumpPath = Path.GetFullPath(gameDumpPath);
    }

    public string GameDumpPath { get; }

    public IReadOnlyList<string> PackagePaths => _packagePaths;

    public bool AddPackage(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        if (!string.Equals(Path.GetExtension(packagePath), ".vrchara", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Character packages must use the .vrchara extension.",
                nameof(packagePath));
        }

        var normalizedPath = Path.GetFullPath(packagePath);
        if (!_knownPackagePaths.Add(normalizedPath))
        {
            return false;
        }

        _packagePaths.Add(normalizedPath);
        return true;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
