using VictoryTool.Application.Packages;
using VictoryTool.Application.Diagnostics;
using VictoryTool.CfgBin;

namespace VictoryTool.Application.Exporting;

public sealed record CharacterTextIds(
    uint FullName,
    uint ShortName,
    uint UpperName,
    uint Description);

public sealed record CharacterLocalizationWriteResult(
    byte[] NameTable,
    byte[] DescriptionTable,
    byte[]? RomanizedNameTable);

public interface ICharacterLocalizationT2bWriter
{
    CharacterLocalizationWriteResult Append(
        ReadOnlySpan<byte> nameTable,
        ReadOnlySpan<byte> descriptionTable,
        ReadOnlySpan<byte> romanizedNameTable,
        CharacterTextIds ids,
        CharacterPackageLocalization text);
}

public sealed class CharacterLocalizationT2bWriter : ICharacterLocalizationT2bWriter
{
    public CharacterLocalizationWriteResult Append(
        ReadOnlySpan<byte> nameTable,
        ReadOnlySpan<byte> descriptionTable,
        ReadOnlySpan<byte> romanizedNameTable,
        CharacterTextIds ids,
        CharacterPackageLocalization text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var operation = GlobalLog.BeginOperation("character_localization_write", new Dictionary<string, object?>
        {
            ["nameTableBytes"] = nameTable.Length,
            ["descriptionTableBytes"] = descriptionTable.Length,
            ["hasRomanizedName"] = !string.IsNullOrWhiteSpace(text.RomanizedName),
        });
        if (string.IsNullOrWhiteSpace(text.LocalizedName))
            throw new ArgumentException("A localized character name is required.", nameof(text));

        var nameDocument = CfgBinDocument.Read(nameTable);
        var nameTemplate = FindTemplate(nameDocument, "NOUN_INFO", minimumValues: 6, formIndex: 1);
        var nameAppends = new[]
        {
            CreateAppend(nameDocument, nameTemplate, ids.FullName, 5, text.LocalizedName),
            CreateAppend(nameDocument, nameTemplate, ids.ShortName, 5, text.ShortName ?? text.LocalizedName),
            CreateAppend(nameDocument, nameTemplate, ids.UpperName, 5, text.UpperName ?? text.LocalizedName),
        };
        var writtenNames = T2bCountedListWriter.InsertClones(
            nameDocument,
            nameTemplate,
            nameAppends.Select(append => append.Values).ToArray(),
            "NOUN_INFO_BEGIN",
            "NOUN_INFO_END");

        var descriptionDocument = CfgBinDocument.Read(descriptionTable);
        var descriptionTemplate = FindTemplate(descriptionDocument, "TEXT_INFO", minimumValues: 3);
        var descriptionAppend = CreateAppend(
            descriptionDocument,
            descriptionTemplate,
            ids.Description,
            2,
            text.Description ?? string.Empty);
        var writtenDescriptions = T2bCountedListWriter.InsertClones(
            descriptionDocument,
            descriptionTemplate,
            [descriptionAppend.Values],
            "TEXT_INFO_BEGIN",
            "TEXT_INFO_END");

        byte[]? writtenRomanized = null;
        if (!romanizedNameTable.IsEmpty && !string.IsNullOrWhiteSpace(text.RomanizedName))
        {
            var romanizedDocument = CfgBinDocument.Read(romanizedNameTable);
            var romanizedTemplate = FindTemplate(
                romanizedDocument, "NOUN_INFO", minimumValues: 6, formIndex: 1);
            var romanizedAppend = CreateAppend(
                romanizedDocument,
                romanizedTemplate,
                ids.FullName,
                5,
                text.RomanizedName);
            writtenRomanized = T2bCountedListWriter.InsertClones(
                romanizedDocument,
                romanizedTemplate,
                [romanizedAppend.Values],
                "NOUN_INFO_BEGIN",
                "NOUN_INFO_END");
        }

        var result = new CharacterLocalizationWriteResult(
            writtenNames,
            writtenDescriptions,
            writtenRomanized);
        GlobalLog.Debug("character_localization_written", new Dictionary<string, object?>
        {
            ["nameTableBytes"] = result.NameTable.Length,
            ["descriptionTableBytes"] = result.DescriptionTable.Length,
            ["hasRomanizedTable"] = result.RomanizedNameTable is not null,
        });
        return result;
    }

    private static CfgBinEntry FindTemplate(
        CfgBinDocument document,
        string entryName,
        int minimumValues,
        int? formIndex = null)
    {
        var candidates = document.Entries.Where(entry =>
            entry.Name == entryName && entry.Values.Count >= minimumValues);
        if (formIndex is not null)
            candidates = candidates.Where(entry => GetInteger(entry.Values[formIndex.Value]) == 0);
        return candidates.FirstOrDefault()
            ?? throw new InvalidDataException(
                $"The localized T2B table does not contain a compatible {entryName} template row.");
    }

    private static CfgBinEntryAppend CreateAppend(
        CfgBinDocument document,
        CfgBinEntry template,
        uint id,
        int stringIndex,
        string text)
    {
        var values = template.Values.Select(value => value.Value).ToArray();
        if (document.ValueWidth == CfgBinValueWidth.Int32)
        {
            values[0] = unchecked((int)id);
            if (values.Length > 1) values[1] = 0;
        }
        else
        {
            values[0] = (long)id;
            if (values.Length > 1) values[1] = 0L;
        }
        values[stringIndex] = text;
        return new CfgBinEntryAppend(template.Index, values);
    }

    private static long GetInteger(CfgBinValue value) => value.Value switch
    {
        int intValue => intValue,
        long longValue => longValue,
        _ => long.MinValue,
    };
}
