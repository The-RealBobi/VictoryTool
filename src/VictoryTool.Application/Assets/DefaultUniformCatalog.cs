using VictoryTool.Application.Profiles;
using VictoryTool.CfgBin;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public sealed class DefaultUniformCatalog
{
    public const string FamilyName = "u110101_10";
    private readonly IReadOnlyDictionary<(int Gender, int UniformVariant), UniformAssetDescriptor> _assets;
    private readonly IReadOnlyDictionary<(int UniformModel, int Gender, int UniformVariant), UniformAssetDescriptor> _modelAssets;
    private readonly IReadOnlyDictionary<(uint UniformInfo, bool Goalkeeper), IReadOnlyList<int>> _uniformKitModels;

    public DefaultUniformCatalog(
        IReadOnlyDictionary<(int Gender, int UniformVariant), UniformAssetDescriptor> assets,
        IReadOnlyDictionary<(int UniformModel, int Gender, int UniformVariant), UniformAssetDescriptor>? modelAssets = null,
        IReadOnlyDictionary<(uint UniformInfo, bool Goalkeeper), IReadOnlyList<int>>? uniformKitModels = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _modelAssets = modelAssets ?? new Dictionary<(int UniformModel, int Gender, int UniformVariant), UniformAssetDescriptor>();
        _uniformKitModels = uniformKitModels ?? new Dictionary<(uint, bool), IReadOnlyList<int>>();
    }

    public static DefaultUniformCatalog Load(
        GameDumpProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var platform = profile.HasPcResources ? "dx11" : "nx";
        var assets = new Dictionary<(int Gender, int UniformVariant), UniformAssetDescriptor>();
        foreach (var member in new[]
                 {
                     (Gender: 1, Variant: 0, Suffix: "00"),
                     (Gender: 1, Variant: 3, Suffix: "01"),
                     (Gender: 2, Variant: 1, Suffix: "02"),
                     (Gender: 2, Variant: 2, Suffix: "03"),
                 })
        {
            if (TryReadAsset(profile, platform, FamilyName, member.Suffix, cancellationToken, out var asset))
                assets[(member.Gender, member.Variant)] = asset;
        }
        return new DefaultUniformCatalog(
            assets,
            LoadModelAssets(profile, platform, cancellationToken),
            LoadUniformKitModels(profile, cancellationToken));
    }

    public bool TryResolve(
        int gender,
        int uniformVariant,
        out UniformAssetDescriptor descriptor) =>
        _assets.TryGetValue((gender, uniformVariant), out descriptor!);

    public bool TryResolveModel(
        int uniformModel,
        int gender,
        int uniformVariant,
        out UniformAssetDescriptor descriptor) =>
        _modelAssets.TryGetValue((uniformModel, gender, uniformVariant), out descriptor!);

    public bool TryResolveUniformKit(
        uint uniformInfo,
        int gender,
        int uniformVariant,
        bool goalkeeper,
        int? preferredUniformModel,
        out UniformAssetDescriptor descriptor)
    {
        if (!_uniformKitModels.TryGetValue((uniformInfo, goalkeeper), out var models))
        {
            descriptor = null!;
            return false;
        }

        if (preferredUniformModel is { } preferred && models.Contains(preferred)
            && TryResolveModel(preferred, gender, uniformVariant, out descriptor))
            return true;

        foreach (var model in models)
        {
            if (TryResolveModel(model, gender, uniformVariant, out descriptor))
                return true;
        }

        descriptor = null!;
        return false;
    }

    private static IReadOnlyDictionary<(int UniformModel, int Gender, int UniformVariant), UniformAssetDescriptor> LoadModelAssets(
        GameDumpProfile profile,
        string platform,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(profile.GameDataPath, "character");
        if (!Directory.Exists(directory)) return new Dictionary<(int, int, int), UniformAssetDescriptor>();
        CfgBinDocument? selected = null;
        var selectedCount = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "chara_parts*.cfg.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = CfgBinDocument.Read(ReadAllBytes(path, cancellationToken));
                var count = document.Entries.Count(entry => entry.Name == "CHARA_PARTS_CLOTHES_MODEL");
                if (count <= selectedCount) continue;
                selected = document;
                selectedCount = count;
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
            }
        }

        if (selected is null) return new Dictionary<(int, int, int), UniformAssetDescriptor>();
        var modelAssets = new Dictionary<(int, int, int), UniformAssetDescriptor>();
        foreach (var entry in selected.Entries.Where(entry =>
                     entry.Name == "CHARA_PARTS_CLOTHES_MODEL" && entry.Values.Count >= 2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetInteger(entry.Values[0].Value, out var model)
                || entry.Values[1].Value is not string resourceKey)
                continue;
            var stem = Path.GetFileNameWithoutExtension(resourceKey);
            if (string.IsNullOrWhiteSpace(stem)
                || !stem.StartsWith("u", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var member in VerifiedPortraitMembers)
            {
                if (modelAssets.ContainsKey((model, member.Gender, member.Variant))) continue;
                if (TryReadAsset(profile, platform, stem, member.Suffix, cancellationToken, out var asset))
                    modelAssets[(model, member.Gender, member.Variant)] = asset;
            }
        }
        return modelAssets;
    }

    private static IReadOnlyDictionary<(uint UniformInfo, bool Goalkeeper), IReadOnlyList<int>> LoadUniformKitModels(
        GameDumpProfile profile,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(profile.GameDataPath, "character");
        var path = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "uniform_config_*.cfg.bin")
                .OrderByDescending(candidate => new FileInfo(candidate).Length)
                .FirstOrDefault()
            : null;
        if (path is null) return new Dictionary<(uint, bool), IReadOnlyList<int>>();

        try
        {
            var document = RdbnpDocument.Read(ReadAllBytes(path, cancellationToken));
            var modelList = document.Lists.FirstOrDefault(list => list.Name == "m_UniformModelInfoList");
            var infoList = document.Lists.FirstOrDefault(list => list.Name == "m_UniformInfoList");
            if (modelList is null || infoList is null
                || !modelList.Type.Fields.Any(field => field.Name == "uniformFielderModelIdCrc")
                || !modelList.Type.Fields.Any(field => field.Name == "uniformKeeperModelIdCrc")
                || !infoList.Type.Fields.Any(field => field.Name == "nameId")
                || !infoList.Type.Fields.Any(field => field.Name == "modelInfo"))
                return new Dictionary<(uint, bool), IReadOnlyList<int>>();

            var result = new Dictionary<(uint, bool), IReadOnlyList<int>>();
            foreach (var info in infoList.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var infoId = info.GetUInt32("nameId");
                var range = info.GetTuple("modelInfo");
                var fielderModels = new List<int>();
                var keeperModels = new List<int>();
                foreach (var model in modelList.Rows.Skip(range.Offset).Take(range.Count))
                {
                    AddModel(model.GetUInt32("uniformFielderModelIdCrc"), fielderModels);
                    AddModel(model.GetUInt32("uniformKeeperModelIdCrc"), keeperModels);
                }
                result[(infoId, false)] = fielderModels;
                result[(infoId, true)] = keeperModels;
            }
            return result;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<(uint, bool), IReadOnlyList<int>>();
        }

        static void AddModel(uint model, ICollection<int> destination)
        {
            if (model != 0) destination.Add(unchecked((int)model));
        }
    }

    internal static bool TryReadAsset(
        GameDumpProfile profile,
        string platform,
        string family,
        string suffix,
        CancellationToken cancellationToken,
        out UniformAssetDescriptor asset)
    {
        var relativePath = Path.Combine(
            platform, "menu", "200_icon", "10_icon_chr", "uniform", $"{family}_{suffix}_l.g4tx");
        var path = Path.Combine(profile.RootPath, relativePath);
        asset = null!;
        if (!File.Exists(path)) return false;
        try
        {
            var document = G4TxDocument.Read(ReadAllBytes(path, cancellationToken));
            var shirtName = $"{family}_{suffix}_1";
            var maskName = $"{family}_{suffix}_2";
            if (!document.Textures.Any(texture => texture.Name == shirtName)
                || !document.Textures.Any(texture => texture.Name == maskName))
                return false;
            asset = new UniformAssetDescriptor(relativePath, shirtName, maskName);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetInteger(object? value, out int result)
    {
        switch (value)
        {
            case int integer:
                result = integer;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                result = (int)longValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static readonly (int Gender, int Variant, string Suffix)[] VerifiedPortraitMembers =
    [
        (1, 0, "00"),
        (1, 3, "01"),
        (2, 1, "02"),
        (2, 2, "03"),
    ];

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > int.MaxValue) throw new InvalidDataException("Uniform container is too large.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }
}
