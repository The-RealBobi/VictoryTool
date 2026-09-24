using VictoryTool.CfgBin;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Exporting;

public interface ICharacterDeliveryWriter
{
    byte[] Append(ReadOnlySpan<byte> deliveryTable, CharacterDeliveryWriteRequest request);
}

public sealed class CharacterDeliveryWriter : ICharacterDeliveryWriter
{
    private const int NativeCharacterPromotionIndex = 35;

    private readonly DeliveryConfigWriter _writer = new();

    public byte[] Append(ReadOnlySpan<byte> deliveryTable, CharacterDeliveryWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = GlobalLog.BeginOperation("character_delivery_write");
        var result = _writer.CloneCharacterPromotion(
            deliveryTable,
            new CharacterPromotionCloneRequest(
                NativeCharacterPromotionIndex,
                0,
                request.DeliveryId,
                request.ReceivedFlag,
                request.CharacterParameterId,
                request.TitleId));
        GlobalLog.Debug("character_delivery_written", new Dictionary<string, object?>
        {
            ["tableBytes"] = result.Length,
            ["templateIndex"] = NativeCharacterPromotionIndex,
        });
        return result;
    }
}
