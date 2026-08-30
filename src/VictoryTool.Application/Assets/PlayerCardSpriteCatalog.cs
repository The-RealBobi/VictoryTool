namespace VictoryTool.Application.Assets;

public enum PlayerCardSpriteKind
{
    Position,
    Gender,
    BodyType,
}

public sealed record PlayerCardSpriteDescriptor(GameUiAssetRequest Request, string AccessibleLabel);

public static class PlayerCardSpriteCatalog
{
    public static bool TryResolve(
        PlayerCardSpriteKind kind,
        int value,
        string locale,
        out PlayerCardSpriteDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        var (isSupported, key, name, label) = kind switch
        {
            PlayerCardSpriteKind.Position when value is >= 1 and <= 4 =>
                (true, GameUiAssetKey.PositionLabels, $"icon_position{value:00}", $"Position {value}"),
            PlayerCardSpriteKind.Gender when value is >= 1 and <= 3 =>
                (true, GameUiAssetKey.CommonIcons, $"icon_gender{value:00}", $"Gender {value}"),
            PlayerCardSpriteKind.BodyType when value is >= 1 and <= 7 =>
                (true, GameUiAssetKey.CommonIcons, $"icon_body_type{value:00}", $"Body type {value}"),
            _ => (false, default, string.Empty, string.Empty),
        };

        if (!isSupported)
        {
            descriptor = null!;
            return false;
        }

        descriptor = new PlayerCardSpriteDescriptor(
            new GameUiAssetRequest(key, locale, SubTextureName: name),
            label);
        return true;
    }
}
