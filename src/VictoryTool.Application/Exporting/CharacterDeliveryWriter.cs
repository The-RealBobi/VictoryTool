using VictoryTool.CfgBin;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Exporting;

public interface ICharacterDeliveryWriter
{
    byte[] Append(ReadOnlySpan<byte> deliveryTable, CharacterDeliveryWriteRequest request);
}

public sealed class CharacterDeliveryWriter : ICharacterDeliveryWriter
{
    private readonly DeliveryConfigWriter _writer = new();

    public byte[] Append(ReadOnlySpan<byte> deliveryTable, CharacterDeliveryWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = GlobalLog.BeginOperation("character_delivery_write");
        var result = _writer.AppendCharacterDelivery(deliveryTable, request);
        GlobalLog.Debug("character_delivery_written", new Dictionary<string, object?>
        {
            ["tableBytes"] = result.Length,
        });
        return result;
    }
}
