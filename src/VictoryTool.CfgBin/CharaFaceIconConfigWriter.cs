using CFGBINManager;

namespace VictoryTool.CfgBin;

public sealed class CharaFaceIconConfigWriter
{
    public byte[] CloneSceneSettings(ReadOnlySpan<byte> source, uint sourceCharacterId, uint destinationCharacterId)
    {
        var document = CfgBinSchema.Read(source);
        var view = new RdbnpDocument(document);
        var sceneView = view.Lists.Single(list => list.Name == "m_CharaFaceIconSceneDataList");
        var infoView = view.Lists.Single(list => list.Name == "m_CharaFaceIconInfoList");
        ValidateSchema(sceneView, infoView);
        if (infoView.Rows.Any(row => row.GetUInt32("charaId") == destinationCharacterId)) throw new InvalidDataException("The destination character already has face-icon scene settings.");
        int sourceIndex = Enumerable.Range(0, infoView.Rows.Count).Single(index => infoView.Rows[index].GetUInt32("charaId") == sourceCharacterId);
        var range = infoView.Rows[sourceIndex].GetTuple("sceneDataRef");
        if (range.Offset < 0 || range.Count <= 0 || range.Offset > sceneView.Rows.Count - range.Count) throw new InvalidDataException("The source face-icon scene range is invalid.");
        var scenes = document.GetList(sceneView.Name);
        var info = document.GetList(infoView.Name);
        int start = scenes.Rows.Count;
        var copies = scenes.Rows.Skip(range.Offset).Take(range.Count).Select(row => row.Clone()).ToArray();
        foreach (var row in copies) document.InsertListRow(scenes.Name, scenes.Rows.Count, row);
        var character = info.Rows[sourceIndex].Clone();
        character.SetValue("charaId", character.GetValue("charaId") with { Bits = destinationCharacterId });
        character.SetValue("sceneDataRef", CfgValue.Range16(checked((ushort)start), checked((ushort)copies.Length)));
        document.InsertListRow(info.Name, info.Rows.Count, character);
        byte[] result = BinCodec.Write(document);
        var restored = RdbnpDocument.Read(result);
        var restoredScenes = restored.Lists.Single(list => list.Name == scenes.Name);
        var restoredInfo = restored.Lists.Single(list => list.Name == info.Name);
        if (restoredScenes.Rows.Count != start + copies.Length || restoredInfo.Rows.Count != infoView.Rows.Count + 1 ||
            restoredInfo.Rows.Single(row => row.GetUInt32("charaId") == destinationCharacterId).GetTuple("sceneDataRef") != new RdbnpTuple(checked((short)start), range.Count))
            throw new InvalidDataException("Cloned face-icon scenes failed read-back validation.");
        return result;
    }

    private static void ValidateSchema(RdbnpList scenes, RdbnpList info)
    {
        if (scenes.Type.Name != "CHARA_FACE_ICON_SCENE_DATA"
            || info.Type.Name != "CHARA_FACE_ICON_CONFIG"
            || scenes.Type.Fields.Select(field => field.Name).ToArray() is not ["sceneType", "offsetPos", "scale"]
            || info.Type.Fields.Select(field => field.Name).ToArray() is not ["charaId", "sceneDataRef"])
            throw new InvalidDataException("The RDBNP is not the verified character face-icon configuration schema.");
    }

}
