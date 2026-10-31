using CFGBINManager;

namespace VictoryTool.CfgBin;

public static class CfgBinSchema
{
    private static readonly Lazy<StructureCatalog> BundledCatalog = new(LoadCatalog);

    public static StructureCatalog Catalog => BundledCatalog.Value;

    public static CfgDocument Read(ReadOnlySpan<byte> source) => BinCodec.Read(source, Catalog);

    private static StructureCatalog LoadCatalog()
    {
        using var stream = typeof(CfgBinSchema).Assembly.GetManifestResourceStream("VictoryTool.CfgBin.structure.cfg")
            ?? throw new InvalidOperationException("The CFGBINManager structure catalog is missing from the application.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
        return StructureCatalog.Parse(reader.ReadToEnd()).ForProfile("VictoryRoad");
    }
}
