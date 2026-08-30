using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public interface ICharacterDeliveryWriter
{
    byte[] Append(ReadOnlySpan<byte> deliveryTable, CharacterDeliveryWriteRequest request);
}

public sealed class CharacterDeliveryWriter : ICharacterDeliveryWriter
{
    private readonly DeliveryConfigWriter _writer = new();

    public byte[] Append(ReadOnlySpan<byte> deliveryTable, CharacterDeliveryWriteRequest request) =>
        _writer.AppendCharacterDelivery(deliveryTable, request);
}
