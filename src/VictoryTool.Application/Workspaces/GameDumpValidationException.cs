namespace VictoryTool.Application.Workspaces;

public sealed class GameDumpValidationException : Exception
{
    public GameDumpValidationException(GameDumpValidationResult result)
        : base(result.Diagnostics.FirstOrDefault()?.Message ?? "The selected game dump is invalid.")
    {
        Result = result;
    }

    public GameDumpValidationResult Result { get; }
}
