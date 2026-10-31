using System.Text.RegularExpressions;
using VictoryTool.Application.Characters;
using VictoryTool.G4.Animations;

namespace VictoryTool.Application.Exporting;

internal static class AvatarAnimationResources
{
    private static readonly Regex Identifier = new("(?<![A-Za-z0-9])c[0-9]{4,10}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ClipSuffix = new("_c[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<AnimationBankSource> Select(string dumpRoot, G4AvatarRecipe recipe,
        CharacterMotionFamilyResolver resolver, CharacterMotionFamily family, CharacterAnimationProfile profile, CancellationToken cancellationToken)
    {
        recipe.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var banks = new List<AnimationBankSource>();
        if (recipe.ExportChr)
        {
            banks.AddRange(resolver.ResolveBanks(dumpRoot, family, profile));
            if (banks.Count == 0)
                throw new InvalidDataException($"No registered CHR routes match the selected family '{recipe.ChrFamily}'.");
        }
        if (recipe.ExportEvent)
            banks.AddRange(DiscoverEvents(dumpRoot, recipe.EventSkeleton, cancellationToken));
        return banks;
    }

    internal static IReadOnlyList<AnimationBankSource> DiscoverEvents(string dumpRoot, string eventSkeleton,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var banks = new List<AnimationBankSource>();
        var eventRoot = Path.Combine(dumpRoot, "common", "event");
        var files = (Directory.Exists(eventRoot)
                ? Directory.EnumerateFiles(eventRoot, "*", SearchOption.AllDirectories)
                : [])
            .Where(path => { cancellationToken.ThrowIfCancellationRequested(); return Path.GetExtension(path).ToLowerInvariant() is ".g4pk" or ".g4mt"; })
            .Select(path => (Path: path, Relative: Path.GetRelativePath(dumpRoot, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = files.Where(file => HasIdentifier(file.Relative, eventSkeleton)).ToArray();
        if (selected.Length == 0)
            throw new InvalidDataException($"No EVENT animation banks match source skeleton '{eventSkeleton}' in the selected dump. Check the dump or disable EVENT animations.");
        var groups = selected.Select(file => EventGroup(file.Relative)).Where(group => group is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasIdentifier(file.Relative, eventSkeleton)
                && !(Path.GetFileNameWithoutExtension(file.Path).Contains("point_eff", StringComparison.OrdinalIgnoreCase)
                    && groups.Contains(EventGroup(file.Relative)))) continue;
            banks.Add(new AnimationBankSource(file.Path, string.Empty, file.Relative, false)
            {
                IsEvent = true,
                SourceIdentifier = eventSkeleton,
            });
        }
        return banks;
    }

    public static byte[] Export(byte[] source, AnimationBankSource bank, G4AvatarRecipe recipe,
        CancellationToken cancellationToken) => Export(source, bank, recipe, 1, cancellationToken);

    public static byte[] Export(byte[] source, AnimationBankSource bank, G4AvatarRecipe recipe,
        int clipWorkers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsNativeEventEffect(bank)) return source;
        if (source.AsSpan().StartsWith("G4MT"u8))
            return bank.IsEvent
                ? G4HumanoidAnimationExporter.ExportEvent(G4mtAnimationBank.Read(source), recipe, clipWorkers, cancellationToken)
                : G4HumanoidAnimationExporter.Export(G4mtAnimationBank.Read(source), recipe, clipWorkers, cancellationToken);
        var package = G4pkAnimationPackage.Read(source);
        var outputPackage = package;
        var output = source;
        foreach (var resource in package.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bankData = G4mtAnimationBank.Read(resource.Content.Span);
            var content = bank.IsEvent
                ? G4HumanoidAnimationExporter.ExportEvent(bankData, recipe, clipWorkers, cancellationToken)
                : G4HumanoidAnimationExporter.Export(bankData, recipe, clipWorkers, cancellationToken);
            var name = bank.IsEvent
                ? resource.Name
                : $"{Path.GetFileNameWithoutExtension(bank.DestinationResourcePath)}.g4mt";
            output = outputPackage.ReplaceResource(resource.PackageIndex, name, content);
            outputPackage = G4pkAnimationPackage.Read(output);
            var written = outputPackage.PackageEntries[resource.PackageIndex];
            if (written.Name != name || !written.Content.Span.SequenceEqual(content))
                throw new InvalidDataException($"Generated animation resource '{name}' failed read-back validation.");
        }
        var animationIndices = package.Resources.Select(resource => resource.PackageIndex).ToHashSet();
        foreach (var original in package.PackageEntries.Where(entry => !animationIndices.Contains(entry.PackageIndex)))
        {
            var written = outputPackage.PackageEntries[original.PackageIndex];
            if (written.Name != original.Name || !written.Content.Span.SequenceEqual(original.Content.Span))
                throw new InvalidDataException($"Animation package changed companion resource '{original.Name}'.");
        }
        return output;
    }

    internal static bool IsNativeEventEffect(AnimationBankSource bank) => bank.IsEvent
        && Path.GetFileNameWithoutExtension(bank.Path).Contains("point_eff", StringComparison.OrdinalIgnoreCase);

    private static bool HasIdentifier(string path, string identifier) => path.Split('/')
        .Select(part => ClipSuffix.Replace(Path.GetFileNameWithoutExtension(part), string.Empty))
        .Any(part => Identifier.Matches(part).Any(match => match.Value.Equals(identifier, StringComparison.OrdinalIgnoreCase)));

    private static string? EventGroup(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 5 ? $"{parts[2]}/{parts[3]}" : null;
    }
}
