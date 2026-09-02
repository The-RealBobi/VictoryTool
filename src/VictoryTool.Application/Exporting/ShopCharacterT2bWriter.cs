using VictoryTool.CfgBin;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Exporting;

public sealed record ShopCharacterWriteRequest(
    int SourceItemId,
    int SourceParameterId,
    int ItemId,
    int ParameterId,
    int Rarity,
    int SpecialVariant,
    bool IsFree = false,
    int? SourceShopParameterId = null);

public interface IShopCharacterT2bWriter
{
    byte[] Append(ReadOnlySpan<byte> shopTable, ShopCharacterWriteRequest request);
}

public sealed class ShopCharacterT2bWriter : IShopCharacterT2bWriter
{
    public byte[] Append(ReadOnlySpan<byte> shopTable, ShopCharacterWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = GlobalLog.BeginOperation("shop_character_write", new Dictionary<string, object?>
        {
            ["sourceItemId"] = request.SourceItemId,
            ["isFree"] = request.IsFree,
        });
        var document = CfgBinDocument.Read(shopTable);
        var source = FindSingleItem(
            document,
            request.SourceItemId,
            request.SourceShopParameterId ?? request.SourceParameterId);
        RequireItemLayout(source);
        var sourceShopParameterId = request.SourceShopParameterId ?? request.SourceParameterId;
        if (!request.IsFree && GetInteger(source.Values[24]) != sourceShopParameterId)
            throw new InvalidDataException("The source shop item does not reference the requested character parameter.");
        if (document.Entries.Any(entry =>
                entry.Name == "SHOP_INFO_ITEM"
                && entry.Values.Count == 27
                && GetInteger(entry.Values[0]) == request.ItemId))
            throw new InvalidDataException("The new shop item ID already exists.");

        var listBegin = document.Entries.Take(source.Index)
            .LastOrDefault(entry => entry.Name is "SHOP_INFO_ITEM_LIST_BEG" or "SHOP_INFO_ITEM_LIST_END");
        if (listBegin?.Name != "SHOP_INFO_ITEM_LIST_BEG" || listBegin.Values.Count != 1)
            throw new InvalidDataException("The source shop item has no unambiguous owning item list.");
        var listEnd = document.Entries.Skip(source.Index + 1)
            .FirstOrDefault(entry => entry.Name == "SHOP_INFO_ITEM_LIST_END")
            ?? throw new InvalidDataException("The source shop item list has no closing marker.");
        var existingItems = document.Entries
            .Skip(listBegin.Index + 1)
            .Take(listEnd.Index - listBegin.Index - 1)
            .Count(entry => entry.Name == "SHOP_INFO_ITEM");
        var markerCount = checked((int)GetInteger(listBegin.Values[0]));
        if (markerCount != existingItems)
            throw new InvalidDataException("The source shop item-list marker does not match its row count.");

        var nextItemOrEnd = document.Entries
            .Skip(source.Index + 1)
            .First(entry => entry.Name is "SHOP_INFO_ITEM" or "SHOP_INFO_ITEM_LIST_END");
        var sourceBlock = document.Entries
            .Skip(source.Index)
            .Take(nextItemOrEnd.Index - source.Index)
            .ToArray();
        var inserts = new List<CfgBinEntryInsert>(sourceBlock.Length);
        var retainedFreeConsume = false;
        foreach (var template in sourceBlock)
        {
            if (request.IsFree && template.Name == "SHOP_INFO_ITEM_CONSUME" && retainedFreeConsume)
                continue;
            var values = template.Values.Select(value => value.Value).ToArray();
            if (template.Index == source.Index)
            {
                values[0] = request.ItemId;
                values[24] = request.ParameterId;
                values[25] = request.Rarity;
                values[26] = request.SpecialVariant;
            }
            else if (values.Length > 0
                     && template.Values[0].Type == CfgBinValueType.Integer
                     && GetInteger(template.Values[0]) == request.SourceParameterId)
            {
                values[0] = request.ParameterId;
            }
            if (request.IsFree && template.Name == "SHOP_INFO_ITEM_CONSUME_LIST_BEG")
            {
                values[0] = 1;
            }
            if (request.IsFree && template.Name == "SHOP_INFO_ITEM_CONSUME")
            {
                values[1] = 0;
                retainedFreeConsume = true;
            }
            if (request.IsFree && template.Name == "SHOP_INFO_ITEM_CONDITION_LIST_BEG")
            {
                values[0] = 0;
            }
            if (request.IsFree && template.Name == "SHOP_INFO_ITEM_CONDITION")
            {
                continue;
            }
            inserts.Add(new CfgBinEntryInsert(listEnd.Index, template.Index, values));
        }

        var countEdited = CfgBinDocument.Read(document.WriteWithValueEdits(
            [new CfgBinValueEdit(listBegin.Index, 0, checked(markerCount + 1))]));
        var result = countEdited.WriteWithInsertedEntries(inserts);
        ValidateResult(result, request, document.EntryCount, listBegin.Index, markerCount);
        GlobalLog.Debug("shop_character_written", new Dictionary<string, object?>
        {
            ["tableBytes"] = result.Length,
        });
        return result;
    }

    private static CfgBinEntry FindSingleItem(CfgBinDocument document, int itemId, int? parameterId = null)
    {
        var matches = document.Entries.Where(entry =>
            entry.Name == "SHOP_INFO_ITEM"
            && entry.Values.Count >= 1
            && GetInteger(entry.Values[0]) == itemId
            && (parameterId is null || entry.Values.Count > 24
                && GetInteger(entry.Values[24]) == parameterId)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Expected exactly one SHOP_INFO_ITEM source row for ID {itemId}, found {matches.Length}.");
    }

    private static void RequireItemLayout(CfgBinEntry item)
    {
        if (item.Values.Count != 27 || item.Values.Any(value => value.Type == CfgBinValueType.Unknown))
            throw new InvalidDataException("The SHOP_INFO_ITEM row does not match the verified 27-value layout.");
        foreach (var index in new[] { 0, 24, 25, 26 })
            if (item.Values[index].Type != CfgBinValueType.Integer)
                throw new InvalidDataException("A required SHOP_INFO_ITEM field is not an integer.");
    }

    private static void ValidateResult(
        byte[] result,
        ShopCharacterWriteRequest request,
        int oldEntryCount,
        int listBeginIndex,
        int oldMarkerCount)
    {
        var restored = CfgBinDocument.Read(result);
        var inserted = FindSingleItem(restored, request.ItemId, request.ParameterId);
        RequireItemLayout(inserted);
        if (GetInteger(inserted.Values[24]) != request.ParameterId
            || GetInteger(inserted.Values[25]) != request.Rarity
            || GetInteger(inserted.Values[26]) != request.SpecialVariant)
            throw new InvalidDataException("The inserted shop character row failed read-back validation.");
        if (GetInteger(restored.Entries[listBeginIndex].Values[0]) != oldMarkerCount + 1)
            throw new InvalidDataException("The shop item-list marker failed read-back validation.");
        if (restored.EntryCount <= oldEntryCount)
            throw new InvalidDataException("The shop character block was not inserted.");
        if (request.IsFree)
        {
            var block = restored.Entries.Skip(inserted.Index)
                .TakeWhile(entry => entry.Index == inserted.Index || entry.Name != "SHOP_INFO_ITEM")
                .ToArray();
            var consumeMarker = block.SingleOrDefault(entry => entry.Name == "SHOP_INFO_ITEM_CONSUME_LIST_BEG")
                ?? throw new InvalidDataException("The free shop character block is missing its consume list.");
            if (consumeMarker.Values.Count != 1 || GetInteger(consumeMarker.Values[0]) != 1)
                throw new InvalidDataException("The free shop character block must retain one price object.");
            var consume = block.SingleOrDefault(entry => entry.Name == "SHOP_INFO_ITEM_CONSUME")
                ?? throw new InvalidDataException("The free shop character block is missing its price object.");
            if (consume.Values.Count < 2 || GetInteger(consume.Values[1]) != 0)
                throw new InvalidDataException("The free shop character price quantity is not zero.");
            var conditionMarker = block.SingleOrDefault(entry => entry.Name == "SHOP_INFO_ITEM_CONDITION_LIST_BEG")
                ?? throw new InvalidDataException("The free shop character block is missing its condition list.");
            if (conditionMarker.Values.Count != 1 || GetInteger(conditionMarker.Values[0]) != 0)
                throw new InvalidDataException("The free shop character condition list is not empty.");
            if (block.Any(entry => entry.Name == "SHOP_INFO_ITEM_CONDITION"))
                throw new InvalidDataException("The free shop character block retained a condition row.");
        }
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int intValue => intValue,
        long longValue => longValue,
        _ => long.MinValue,
    };
}
