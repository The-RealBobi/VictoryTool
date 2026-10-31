using CFGBINManager;

namespace VictoryTool.CfgBin;

public sealed record CharacterDeliveryWriteRequest(
    uint DeliveryId,
    uint ReceivedFlag,
    uint CharacterParameterId,
    uint? TitleId = null)
{
    public IReadOnlyList<uint> AdditionalCharacterParameterIds { get; init; } = [];
}

public sealed record CharacterPromotionCloneRequest(
    int SourceDeliveryIndex,
    int CharacterContentOffset,
    uint DeliveryId,
    uint ReceivedFlag,
    uint CharacterParameterId,
    uint? TitleId = null);

public sealed class DeliveryConfigWriter
{
    public byte[] CloneCharacterPromotion(ReadOnlySpan<byte> source, CharacterPromotionCloneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = CfgBinSchema.Read(source);
        var view = new RdbnpDocument(document);
        var contentsView = view.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var infoView = view.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        ValidateSchema(contentsView, infoView);
        if ((uint)request.SourceDeliveryIndex >= infoView.Rows.Count) throw new ArgumentOutOfRangeException(nameof(request), "The source promotion index is outside the delivery list.");
        if (infoView.Rows.Any(row => row.GetUInt32("idCrc") == request.DeliveryId)) throw new InvalidDataException("The destination delivery ID already exists.");
        var range = infoView.Rows[request.SourceDeliveryIndex].GetTuple("deliveryContents");
        if (range.Offset < 0 || range.Count <= 0 || range.Offset > contentsView.Rows.Count - range.Count ||
            (uint)request.CharacterContentOffset >= range.Count)
            throw new InvalidDataException("The source promotion content range is invalid.");
        var contents = document.GetList(contentsView.Name);
        var info = document.GetList(infoView.Name);
        var copies = contents.Rows.Skip(range.Offset).Take(range.Count).Select(row => row.Clone()).ToArray();
        if (copies[request.CharacterContentOffset].GetValue("contents_type").Bits != 2) throw new InvalidDataException("The selected promotion content is not a character reward.");
        SetNumber(copies[request.CharacterContentOffset], "charaParamIdCrc", request.CharacterParameterId);
        int start = contents.Rows.Count;
        foreach (var row in copies) document.InsertListRow(contents.Name, contents.Rows.Count, row);
        var promotion = info.Rows[request.SourceDeliveryIndex].Clone();
        SetNumber(promotion, "idCrc", request.DeliveryId);
        SetNumber(promotion, "receivedFlag", request.ReceivedFlag);
        SetNumber(promotion, "newFlag", 42);
        if (request.TitleId is { } title) SetNumber(promotion, "title", title);
        promotion.SetValue("deliveryContents", CfgValue.Range16(checked((ushort)start), checked((ushort)copies.Length)));
        document.InsertListRow(info.Name, info.Rows.Count, promotion);
        byte[] result = BinCodec.Write(document);
        var restored = RdbnpDocument.Read(result);
        var restoredContents = restored.Lists.Single(list => list.Name == contents.Name);
        var restoredInfo = restored.Lists.Single(list => list.Name == info.Name);
        var written = restoredInfo.Rows.Single(row => row.GetUInt32("idCrc") == request.DeliveryId);
        var writtenRange = written.GetTuple("deliveryContents");
        if (restoredContents.Rows.Count != contentsView.Rows.Count + copies.Length || restoredInfo.Rows.Count != infoView.Rows.Count + 1 ||
            writtenRange != new RdbnpTuple(checked((short)start), range.Count) ||
            restoredContents.Rows[start + request.CharacterContentOffset].GetUInt32("charaParamIdCrc") != request.CharacterParameterId)
            throw new InvalidDataException("The cloned character promotion failed read-back validation.");
        return result;
    }

    public byte[] AppendCharacterDelivery(ReadOnlySpan<byte> source, CharacterDeliveryWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = CfgBinSchema.Read(source);
        var view = new RdbnpDocument(document);
        var contentsView = view.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var infoView = view.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        ValidateSchema(contentsView, infoView);
        if (infoView.Rows.Any(row => row.GetUInt32("idCrc") == request.DeliveryId)) throw new InvalidDataException("The destination delivery ID already exists.");
        var contents = document.GetList(contentsView.Name);
        var info = document.GetList(infoView.Name);
        int start = contents.Rows.Count;
        var parameterIds = new[] { request.CharacterParameterId }.Concat(request.AdditionalCharacterParameterIds).Distinct().ToArray();
        foreach (var parameterId in parameterIds)
        {
            var reward = contents.Rows[21].Clone();
            SetNumber(reward, "contents_type", 2);
            SetNumber(reward, "itemIdCrc", 0);
            SetNumber(reward, "charaParamIdCrc", parameterId);
            SetNumber(reward, "rarity", 0);
            SetNumber(reward, "num", 1);
            SetNumber(reward, "aocIdCrc", 0);
            SetNumber(reward, "replaceItemIdCrc", 0);
            reward.SetValue("isPassphrase", CfgValue.Boolean(false));
            document.InsertListRow(contents.Name, contents.Rows.Count, reward);
        }
        var promotion = info.Rows[25].Clone();
        SetNumber(promotion, "idCrc", request.DeliveryId);
        if (request.TitleId is { } title) SetNumber(promotion, "title", title);
        SetNumber(promotion, "receivedFlag", request.ReceivedFlag);
        SetNumber(promotion, "newFlag", 42);
        SetNumber(promotion, "sendTargetType", 2);
        promotion.SetValue("deliveryContents", CfgValue.Range16(checked((ushort)start), checked((ushort)parameterIds.Length)));
        promotion.SetValue("openCond", CfgValue.String(null));
        document.InsertListRow(info.Name, info.Rows.Count, promotion);
        byte[] result = BinCodec.Write(document);
        ValidateResult(result, request, parameterIds, contentsView.Rows.Count, infoView.Rows.Count);
        return result;
    }

    private static void SetNumber(CfgRow row, string field, uint value)
    {
        CfgValue current = row.GetValue(field);
        if (current.Kind is not (ValueKind.Signed or ValueKind.Unsigned or ValueKind.Hash32)) throw new InvalidDataException($"Delivery field {field} is not an integer.");
        row.SetValue(field, current with { Bits = value });
    }

    private static void ValidateSchema(RdbnpList contents, RdbnpList info)
    {
        if (contents.Type.Name != "DELIVERY_CONTENTS_DATA"
            || info.Type.Name != "DELIVERY_INFO"
            || contents.Rows.Count <= 21
            || info.Rows.Count <= 25
            || contents.Type.Fields.Select(field => field.Name).ToArray() is not
                ["contents_type", "itemIdCrc", "charaParamIdCrc", "rarity", "num", "aocIdCrc", "replaceItemIdCrc", "isPassphrase"]
            || info.Type.Fields.Select(field => field.Name).ToArray() is not
                ["idCrc", "title", "receivedFlag", "newFlag", "sendTargetType", "deliveryContents", "openCond"])
            throw new InvalidDataException("The RDBNP is not the verified delivery configuration schema.");
    }

    private static void ValidateResult(byte[] result, CharacterDeliveryWriteRequest request, IReadOnlyList<uint> parameterIds, int oldContents, int oldInfo)
    {
        var document = RdbnpDocument.Read(result);
        var contents = document.Lists.Single(list => list.Name == "m_DeliveryContentsDataList");
        var info = document.Lists.Single(list => list.Name == "m_DeliveryInfoList");
        if (contents.Rows.Count != oldContents + parameterIds.Count || info.Rows.Count != oldInfo + 1
            || !contents.Rows.Skip(oldContents).Select(row => row.GetUInt32("charaParamIdCrc")).SequenceEqual(parameterIds)
            || info.Rows[^1].GetUInt32("idCrc") != request.DeliveryId
            || info.Rows[^1].GetTuple("deliveryContents") != new RdbnpTuple(checked((short)oldContents), checked((short)parameterIds.Count)))
            throw new InvalidDataException("The appended character delivery failed read-back validation.");
    }

}
