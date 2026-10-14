using System.Numerics;

namespace VictoryTool.G4.Animations;

public sealed record G4AvatarTransform(float[] Translation, float[] Rotation, float[] Scale)
{
    internal G4mtJointRest ToRest() => new(new(Translation[0], Translation[1], Translation[2]),
        Quaternion.Normalize(new(Rotation[0], Rotation[1], Rotation[2], Rotation[3])),
        new(Scale[0], Scale[1], Scale[2]));
}

public sealed record G4AvatarNode(string Name, string? Parent, float[] RestMatrix,
    G4AvatarTransform LocalRest, G4AvatarTransform? TrackRest);
public sealed record G4AvatarTwist(string Bone, string Driver, string End, float Fraction);

public sealed record G4AvatarRecipe(
    int Version, string Algorithm, bool ExportChr, bool ExportEvent, string ChrFamily, string EventSkeleton,
    string SourceSkeletonBase64, string TargetSkeletonBase64,
    IReadOnlyList<G4AvatarNode> SourceNodes, IReadOnlyList<G4AvatarNode> TargetNodes,
    IReadOnlyDictionary<string, string> SourceMapping, IReadOnlyDictionary<string, string> TargetMapping,
    float[] Alignment, IReadOnlyDictionary<string, float[]> ReferenceRotations,
    float BodyScale, float HipScale, IReadOnlyDictionary<string, float> SegmentScales,
    IReadOnlyList<G4AvatarTwist> Twists)
{
    public string SourceScalePolicy { get; init; } = "parent-track-ratio";
    public bool TargetCumulativeScale { get; init; } = true;
    public IReadOnlyList<string> PreserveLocalTracks { get; init; } = ["c_ball_1_0"];

    public void Validate()
    {
        if (Version != 1 || Algorithm != "humanoid-avatar-v1"
            || SourceScalePolicy != "parent-track-ratio" || !TargetCumulativeScale)
            throw new InvalidDataException("Unsupported humanoid animation recipe.");
        if (!ExportChr && !ExportEvent)
            throw new InvalidDataException("The animation recipe selects no animation branch.");
        ValidateId(ChrFamily); ValidateId(EventSkeleton);
        if (!float.IsFinite(BodyScale) || BodyScale <= 0 || !float.IsFinite(HipScale) || HipScale <= 0)
            throw new InvalidDataException("The animation recipe has invalid body proportions.");
        ValidateQuaternion(Alignment);
        var sourceNames = ValidateNodes(SourceNodes, true);
        var targetNames = ValidateNodes(TargetNodes, false);
        ValidateEmbeddedSkeleton(SourceSkeletonBase64, SourceNodes);
        ValidateEmbeddedSkeleton(TargetSkeletonBase64, TargetNodes);
        if (SourceMapping is null || TargetMapping is null || ReferenceRotations is null
            || SegmentScales is null || Twists is null || PreserveLocalTracks is null)
            throw new InvalidDataException("The animation recipe is missing adaptation data.");
        foreach (var (role, name) in SourceMapping)
            if (string.IsNullOrWhiteSpace(role) || !sourceNames.Contains(name))
                throw new InvalidDataException($"Source Avatar role '{role}' refers to a missing bone.");
        foreach (var (role, name) in TargetMapping)
        {
            if (!SourceMapping.ContainsKey(role) || !targetNames.Contains(name)
                || !ReferenceRotations.TryGetValue(name, out var reference))
                throw new InvalidDataException($"Target Avatar role '{role}' has incomplete adaptation data.");
            ValidateQuaternion(reference);
            if (!SegmentScales.TryGetValue(name, out var scale) || !float.IsFinite(scale) || scale <= 0)
                throw new InvalidDataException($"Target Avatar role '{role}' has an invalid segment proportion.");
        }
        if (TargetMapping.Values.Distinct(StringComparer.Ordinal).Count() != TargetMapping.Count)
            throw new InvalidDataException("Multiple Avatar roles target the same bone.");
        if (!SourceMapping.TryGetValue("c_global_0_0", out var sourceRoot) || sourceRoot != "c_global_0_0"
            || !TargetMapping.TryGetValue("c_global_0_0", out var targetRoot) || targetRoot != "c_global_0_0")
            throw new InvalidDataException("The Avatar recipe must map c_global_0_0 to the target root bone.");
        foreach (var role in new[] { "c_c_1_0", "c_head_1_0", "l_l_1_0", "r_l_1_0", "l_foot_1_0", "r_foot_1_0" })
            if (!TargetMapping.ContainsKey(role) || !SourceMapping.ContainsKey(role))
                throw new InvalidDataException($"The Avatar recipe is missing body role '{role}'.");
        var twistNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var twist in Twists)
            if (twist is null || !targetNames.Contains(twist.Bone) || !targetNames.Contains(twist.Driver)
                || !TargetMapping.Values.Contains(twist.End) || twist.Bone == twist.Driver
                || TargetMapping.Values.Contains(twist.Bone) || !float.IsFinite(twist.Fraction)
                || twist.Fraction is < 0 or > 1 || !twistNames.Add(twist.Bone))
                throw new InvalidDataException("The Avatar recipe contains an invalid twist chain.");
        foreach (var name in PreserveLocalTracks)
            if (!sourceNames.Contains(name) || !targetNames.Contains(name))
                throw new InvalidDataException($"Preserved local track '{name}' is absent from a rig.");
    }

    private static HashSet<string> ValidateNodes(IReadOnlyList<G4AvatarNode> nodes, bool source)
    {
        if (nodes is null || nodes.Count == 0 || nodes.Count > ushort.MaxValue)
            throw new InvalidDataException("The Avatar recipe has no valid rig nodes.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Name) || !names.Add(node.Name)
                || (node.Parent is not null && !names.Contains(node.Parent)) || node.Name == node.Parent)
                throw new InvalidDataException("Avatar rig parents must precede their children and bone names must be unique.");
            if (node.RestMatrix is null || node.RestMatrix.Length != 16 || node.RestMatrix.Any(v => !float.IsFinite(v))
                || node.RestMatrix[12] != 0 || node.RestMatrix[13] != 0 || node.RestMatrix[14] != 0
                || node.RestMatrix[15] != 1)
                throw new InvalidDataException($"Avatar bone '{node.Name}' has an invalid rest matrix.");
            ValidateTransform(node.LocalRest);
            if (source && node.TrackRest is null)
                throw new InvalidDataException($"Source Avatar bone '{node.Name}' has no original track rest transform.");
            if (node.TrackRest is not null) ValidateTransform(node.TrackRest);
        }
        return names;
    }

    private static void ValidateTransform(G4AvatarTransform transform)
    {
        if (transform is null || transform.Translation is null || transform.Scale is null
            || transform.Translation.Length != 3 || transform.Scale.Length != 3
            || transform.Translation.Any(v => !float.IsFinite(v))
            || transform.Scale.Any(v => !float.IsFinite(v) || MathF.Abs(v) < 1e-9f))
            throw new InvalidDataException("The Avatar recipe contains an invalid local transform.");
        ValidateQuaternion(transform.Rotation);
    }

    private static void ValidateQuaternion(float[] values)
    {
        if (values is null || values.Length != 4 || values.Any(v => !float.IsFinite(v))
            || values.Sum(v => v * v) < 1e-12f)
            throw new InvalidDataException("The Avatar recipe contains an invalid rotation.");
    }

    private static void ValidateId(string value)
    {
        if (value is null || value.Length is < 5 or > 11 || value[0] != 'c'
            || !value.Skip(1).All(char.IsAsciiDigit))
            throw new InvalidDataException($"Invalid native animation ID '{value}'.");
    }

    private static void ValidateEmbeddedSkeleton(string encoded, IReadOnlyList<G4AvatarNode> nodes)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        { throw new InvalidDataException("The animation recipe has an invalid embedded G4SK.", exception); }
        var skeleton = G4skSkeletonReader.Read(bytes);
        if (!skeleton.Nodes.Select(node => node.Name).SequenceEqual(nodes.Select(node => node.Name)))
            throw new InvalidDataException("Avatar rig nodes do not match the embedded G4SK.");
    }
}
