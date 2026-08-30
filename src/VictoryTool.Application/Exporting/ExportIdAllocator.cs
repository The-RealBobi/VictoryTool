using System.Text;
using System.Security.Cryptography;
using VictoryTool.Application.Profiles;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record ExportIdRequest(
    Guid BatchEntryId,
    string Domain,
    string SymbolicKey,
    bool RequiresExactCrc = false);

public sealed record ExportIdAssignment(
    Guid BatchEntryId,
    string Domain,
    string SymbolicKey,
    string ResolvedKey,
    uint NumericId);

public sealed class ExportIdInventory
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<uint>> _occupied;

    private ExportIdInventory(IReadOnlyDictionary<string, IReadOnlySet<uint>> occupied) =>
        _occupied = occupied;

    public static ExportIdInventory Empty { get; } = Create(
        new Dictionary<string, IEnumerable<uint>>());

    public static ExportIdInventory Create(
        IReadOnlyDictionary<string, IEnumerable<uint>> occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        return new ExportIdInventory(occupied.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<uint>)pair.Value.ToHashSet(),
            StringComparer.Ordinal));
    }

    internal HashSet<uint> CopyDomain(string domain) =>
        _occupied.TryGetValue(domain, out var values) ? values.ToHashSet() : [];

    public bool Contains(string domain, uint value) =>
        _occupied.TryGetValue(domain, out var values) && values.Contains(value);

    public int Count(string domain) =>
        _occupied.TryGetValue(domain, out var values) ? values.Count : 0;
}

public static class CharacterIdInventoryBuilder
{
    public static ExportIdInventory Build(
        IEnumerable<CfgBinEntry> baseEntries,
        IEnumerable<CfgBinEntry> parameterEntries,
        IEnumerable<CfgBinEntry>? shopEntries = null,
        IEnumerable<CfgBinEntry>? modelEntries = null,
        IEnumerable<uint>? deliveryIds = null,
        IEnumerable<uint>? deliveryReceivedFlags = null)
    {
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(parameterEntries);
        var characterIds = new HashSet<uint>();
        var parameterIds = new HashSet<uint>();
        var nameTextIds = new HashSet<uint>();
        var descriptionTextIds = new HashSet<uint>();
        var shopItemIds = new HashSet<uint>();
        var modelIds = new HashSet<uint>();
        var deliveryIdSet = (deliveryIds ?? []).ToHashSet();
        var deliveryReceivedSet = (deliveryReceivedFlags ?? []).ToHashSet();
        foreach (var entry in baseEntries.Where(entry => entry.Name == "CHARA_BASE_INFO"))
        {
            AddInteger(entry, 0, characterIds);
            AddInteger(entry, 3, nameTextIds);
            AddInteger(entry, 4, nameTextIds);
            AddInteger(entry, 5, nameTextIds);
            AddInteger(entry, 19, descriptionTextIds);
        }
        foreach (var entry in parameterEntries.Where(entry => entry.Name == "CHARA_PARAM_INFO"))
        {
            AddInteger(entry, 0, parameterIds);
            AddInteger(entry, 1, characterIds);
        }
        foreach (var entry in (shopEntries ?? []).Where(entry => entry.Name == "SHOP_INFO_ITEM"))
            AddInteger(entry, 0, shopItemIds);
        foreach (var entry in (modelEntries ?? []).Where(entry => entry.Name == "CHARA_MODEL_INFO"))
            AddInteger(entry, 0, modelIds);
        return ExportIdInventory.Create(new Dictionary<string, IEnumerable<uint>>
        {
            ["character"] = characterIds,
            ["parameter"] = parameterIds,
            ["nameText"] = nameTextIds,
            ["descriptionText"] = descriptionTextIds,
            ["shopItem"] = shopItemIds,
            ["model"] = modelIds,
            ["delivery"] = deliveryIdSet,
            ["deliveryReceived"] = deliveryReceivedSet,
        });
    }

    private static void AddInteger(CfgBinEntry entry, int index, ISet<uint> destination)
    {
        if ((uint)index >= (uint)entry.Values.Count) return;
        switch (entry.Values[index].Value)
        {
            case int value:
                destination.Add(unchecked((uint)value));
                break;
            case long value when value is >= int.MinValue and <= uint.MaxValue:
                destination.Add(unchecked((uint)value));
                break;
        }
    }
}

public interface ICharacterIdInventoryService
{
    Task<ExportIdInventory> LoadAsync(GameDumpProfile profile, CancellationToken cancellationToken);
}

public sealed class FileSystemCharacterIdInventoryService : ICharacterIdInventoryService
{
    public async Task<ExportIdInventory> LoadAsync(
        GameDumpProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var directory = Path.Combine(profile.GameDataPath, "character");
        var baseEntries = await ReadEntriesAsync(directory, "chara_base_*.cfg.bin", cancellationToken);
        var parameterEntries = await ReadEntriesAsync(directory, "chara_param_*.cfg.bin", cancellationToken);
        var shopEntries = await ReadOptionalEntriesAsync(
            Path.Combine(profile.GameDataPath, "shop"), "shop_config_*.cfg.bin", cancellationToken);
        var modelEntries = await ReadOptionalEntriesAsync(directory, "chara_model_*.cfg.bin", cancellationToken);
        var (deliveryIds, deliveryReceivedFlags) = await ReadDeliveryIdsAsync(profile, cancellationToken);
        return CharacterIdInventoryBuilder.Build(
            baseEntries, parameterEntries, shopEntries, modelEntries, deliveryIds, deliveryReceivedFlags);
    }

    private static async Task<IReadOnlyList<CfgBinEntry>> ReadEntriesAsync(
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Character data directory was not found: {directory}");
        var entries = new List<CfgBinEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.AsSpan().StartsWith("RDBNP"u8)) continue;
            entries.AddRange(CfgBinDocument.Read(bytes).Entries);
        }
        if (entries.Count == 0)
            throw new InvalidDataException($"No compatible character table matched {pattern}.");
        return entries;
    }

    private static async Task<IReadOnlyList<CfgBinEntry>> ReadOptionalEntriesAsync(
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return [];
        var entries = new List<CfgBinEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, pattern).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.AsSpan().StartsWith("RDBNP"u8)) continue;
            entries.AddRange(CfgBinDocument.Read(bytes).Entries);
        }
        return entries;
    }

    private static async Task<(IReadOnlyList<uint> DeliveryIds, IReadOnlyList<uint> ReceivedFlags)> ReadDeliveryIdsAsync(
        GameDumpProfile profile,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(profile.GameDataPath, "post");
        if (!Directory.Exists(directory)) return ([], []);
        var ids = new HashSet<uint>();
        var flags = new HashSet<uint>();
        foreach (var path in Directory.EnumerateFiles(directory, "delivery_config_*.cfg.bin", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = RdbnpDocument.Read(await File.ReadAllBytesAsync(path, cancellationToken));
            var info = document.Lists.FirstOrDefault(list => list.Name == "m_DeliveryInfoList");
            if (info is null) continue;
            foreach (var row in info.Rows)
            {
                ids.Add(row.GetUInt32("idCrc"));
                flags.Add(row.GetUInt32("receivedFlag"));
            }
        }
        return (ids.ToArray(), flags.ToArray());
    }
}

public interface IExportIdAllocator
{
    IReadOnlyList<ExportIdAssignment> Allocate(
        IEnumerable<ExportIdRequest> requests,
        ExportIdInventory inventory);
}

public sealed class ExportIdAllocator : IExportIdAllocator
{
    public IReadOnlyList<ExportIdAssignment> Allocate(
        IEnumerable<ExportIdRequest> requests,
        ExportIdInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(inventory);
        var occupiedByDomain = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        var assignments = new List<ExportIdAssignment>();

        foreach (var request in requests)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SymbolicKey);
            if (!occupiedByDomain.TryGetValue(request.Domain, out var occupied))
            {
                occupied = inventory.CopyDomain(request.Domain);
                occupiedByDomain.Add(request.Domain, occupied);
            }

            if (request.RequiresExactCrc)
            {
                var exactResolvedKey = request.SymbolicKey;
                var exactCandidate = ComputeCrc32(exactResolvedKey);
                var added = occupied.Add(exactCandidate);
                if (!added
                    && request.Domain == "character"
                    && IsCustomCharacterName(request.SymbolicKey))
                {
                    var probe = 0u;
                    do
                    {
                        var customSuffix = ComputeCrc32($"{request.SymbolicKey}#{probe}") % 1_000_000u;
                        exactResolvedKey = $"c99{customSuffix:D6}";
                        exactCandidate = ComputeCrc32(exactResolvedKey);
                        probe++;
                    }
                    while (!occupied.Add(exactCandidate));
                }
                else if (!added)
                {
                    throw new InvalidDataException(
                        $"The required {request.Domain} ID for '{request.SymbolicKey}' is already occupied.");
                }
                assignments.Add(new ExportIdAssignment(
                    request.BatchEntryId,
                    request.Domain,
                    request.SymbolicKey,
                    exactResolvedKey,
                    exactCandidate));
                continue;
            }

            // Delivery claims are consumable by the game. A new random key on
            // every export prevents a previously redeemed claim from being
            // silently reused, while the occupied inventory still protects
            // against collisions with the active dump and current batch.
            if (request.Domain is "delivery" or "deliveryReceived")
            {
                uint randomCandidate;
                var randomBytes = new byte[sizeof(uint)];
                do
                {
                    RandomNumberGenerator.Fill(randomBytes);
                    randomCandidate = BitConverter.ToUInt32(randomBytes);
                }
                while (randomCandidate == 0 || !occupied.Add(randomCandidate));

                var randomKey = $"{request.SymbolicKey}#{randomCandidate:X8}";
                assignments.Add(new ExportIdAssignment(
                    request.BatchEntryId,
                    request.Domain,
                    request.SymbolicKey,
                    randomKey,
                    randomCandidate));
                continue;
            }

            var suffix = 0;
            string resolvedKey;
            uint candidate;
            do
            {
                resolvedKey = suffix == 0 ? request.SymbolicKey : $"{request.SymbolicKey}#{suffix}";
                candidate = ComputeCrc32(resolvedKey);
                suffix++;
            }
            while (!occupied.Add(candidate));

            assignments.Add(new ExportIdAssignment(
                request.BatchEntryId,
                request.Domain,
                request.SymbolicKey,
                resolvedKey,
                candidate));
        }

        return assignments;
    }

    private static bool IsCustomCharacterName(string value) =>
        value.Length == 9
        && value.StartsWith("c99", StringComparison.Ordinal)
        && value.Skip(3).All(char.IsAsciiDigit);

    internal static uint ComputeCrc32(string value)
    {
        var crc = uint.MaxValue;
        foreach (var item in Encoding.UTF8.GetBytes(value))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }
}
