using System.Numerics;

namespace VictoryTool.G4.Animations;

public sealed partial record G4AvatarRecipe
{
    public G4AvatarRecipe WithSourceSkeleton(byte[] bytes, string skeletonId)
    {
        Validate();
        var skeleton = G4skSkeletonReader.Read(bytes);
        var rests = skeleton.Nodes.Select(node =>
        {
            var v = node.GlobalMatrix;
            var matrix = new Matrix4x4(v[0], v[4], v[8], 0, v[1], v[5], v[9], 0,
                v[2], v[6], v[10], 0, v[3], v[7], v[11], 1);
            var transform = SourceTransform(matrix);
            return Matrix4x4.CreateFromQuaternion(SourceQuaternion(transform.Rotation))
                * Matrix4x4.CreateTranslation(matrix.Translation);
        }).ToArray();
        var nodes = skeleton.Nodes.Select((node, index) => new G4AvatarNode(node.Name,
            node.ParentIndex is { } parent ? skeleton.Nodes[parent].Name : null,
            SourceMatrix(rests[index]), SourceTransform(node.ParentIndex is { } p
                ? rests[index] * SourceInverse(rests[p]) : rests[index]),
            new G4AvatarTransform(node.LocalSrt[8..11], node.LocalSrt[4..8], node.LocalSrt[0..3]))).ToArray();
        var mapping = nodes.ToDictionary(node => node.Name, node => node.Name, StringComparer.Ordinal);
        foreach (var (role, name) in SourceMapping)
            if (mapping.ContainsKey(name)) mapping[role] = name;
        foreach (var role in TargetMapping.Keys)
        {
            if (mapping.ContainsKey(role)) continue;
            var parts = role.Split('_');
            if (parts.Length == 4 && parts[0] is "l" or "r" && parts[1] is "mid" or "rng" or "pky"
                && mapping.TryGetValue($"{parts[0]}_idx_{parts[2]}_{parts[3]}", out var indexFinger))
                mapping[role] = indexFinger;
            else
                throw new InvalidDataException($"Source skeleton '{skeletonId}' has no Avatar role '{role}'.");
        }
        var source = nodes.ToDictionary(node => node.Name);
        var target = TargetNodes.ToDictionary(node => node.Name);
        var sourceFrame = SourceBodyFrame(source, mapping);
        var targetFrame = SourceBodyFrame(target, TargetMapping);
        var alignment = Quaternion.Normalize(targetFrame.Rotation * Quaternion.Inverse(sourceFrame.Rotation));
        var bodyScale = targetFrame.Height / sourceFrame.Height;
        var pairs = new Dictionary<string, string>
        {
            ["c_c_1_0"] = "c_c_1_1", ["c_c_1_1"] = "c_n_1_0", ["c_n_1_0"] = "c_head_1_0",
        };
        foreach (var side in new[] { "l", "r" })
        {
            pairs[$"{side}_s_1_0"] = $"{side}_a_1_0";
            pairs[$"{side}_a_1_0"] = $"{side}_a_1_1";
            pairs[$"{side}_a_1_1"] = $"{side}_w_1_0";
            pairs[$"{side}_l_1_0"] = $"{side}_l_1_1";
            pairs[$"{side}_l_1_1"] = $"{side}_foot_1_0";
            foreach (var finger in new[] { "thb", "idx", "mid", "rng", "pky" })
                for (var segment = 0; segment < 2; segment++)
                    pairs[$"{side}_{finger}_1_{segment}"] = $"{side}_{finger}_1_{segment + 1}";
            foreach (var finger in new[] { "idx", "mid", "thb" })
                if (TargetMapping.ContainsKey($"{side}_{finger}_1_0"))
                { pairs[$"{side}_w_1_0"] = $"{side}_{finger}_1_0"; break; }
        }
        var inverseTarget = TargetMapping.ToDictionary(pair => pair.Value, pair => pair.Key);
        var offsets = new Dictionary<string, Quaternion>();
        var references = new Dictionary<string, float[]>();
        foreach (var node in TargetNodes)
        {
            var offset = node.Parent is { } parent ? offsets[parent] : Quaternion.Identity;
            var role = inverseTarget.GetValueOrDefault(node.Name);
            // Ankle/toe placement does not define the mesh's neutral sole angle.
            if (role is "c_global_0_0" or "l_foot_1_0" or "r_foot_1_0") offset = Quaternion.Identity;
            if (role is not null && pairs.TryGetValue(role, out var end)
                && mapping.ContainsKey(end) && TargetMapping.TryGetValue(end, out var endpoint))
            {
                if (role == "c_c_1_0")
                {
                    var torso = SourcePoint(target[TargetMapping["c_head_1_0"]]) - SourcePoint(node);
                    var spine = SourcePoint(target[endpoint]) - SourcePoint(node);
                    // Some rigs start the spine at a hip pivot or below the pelvis.
                    if (Vector3.Dot(spine, torso) <= torso.LengthSquared() * .01f)
                    {
                        end = "c_head_1_0";
                        endpoint = TargetMapping[end];
                    }
                }
                var from = SourcePoint(target[endpoint]) - SourcePoint(node);
                var to = Vector3.Transform(SourcePoint(source[mapping[end]]) - SourcePoint(source[mapping[role]]), alignment);
                if (from.LengthSquared() > 1e-12f && to.LengthSquared() > 1e-12f)
                    offset = SourceRotationBetween(from, to);
            }
            offsets[node.Name] = offset;
            Matrix4x4.Decompose(SourceReadMatrix(node.RestMatrix), out _, out var rotation, out _);
            references[node.Name] = SourceQuaternionArray(Quaternion.Normalize(offset * rotation));
        }
        var segments = new Dictionary<string, float>();
        foreach (var (role, name) in TargetMapping)
        {
            var node = source[mapping[role]];
            var parentRole = mapping.FirstOrDefault(pair => pair.Value == node.Parent).Key;
            var scale = bodyScale;
            if (parentRole is not null && TargetMapping.TryGetValue(parentRole, out var parentTarget))
            {
                var length = Vector3.Distance(SourcePoint(node), SourcePoint(source[node.Parent!]));
                if (length > 1e-6f)
                    scale = Vector3.Distance(SourcePoint(target[name]), SourcePoint(target[parentTarget])) / length;
            }
            segments[name] = scale;
        }
        var result = this with
        {
            EventSkeleton = skeletonId, SourceSkeletonBase64 = Convert.ToBase64String(bytes), SourceNodes = nodes,
            SourceMapping = mapping, Alignment = SourceQuaternionArray(alignment), ReferenceRotations = references,
            BodyScale = bodyScale, HipScale = targetFrame.Hip / sourceFrame.Hip, SegmentScales = segments,
        };
        result.Validate();
        return result;
    }

    private static (Quaternion Rotation, float Height, float Hip) SourceBodyFrame(
        IReadOnlyDictionary<string, G4AvatarNode> nodes, IReadOnlyDictionary<string, string> mapping)
    {
        Vector3 Point(string role) => SourcePoint(nodes[mapping[role]]);
        var up = Vector3.Normalize(Point("c_head_1_0") - Point("c_c_1_0"));
        var left = Point("l_l_1_0") - Point("r_l_1_0");
        left = Vector3.Normalize(left - up * Vector3.Dot(left, up));
        var forward = Vector3.Normalize(Vector3.Cross(left, up));
        up = Vector3.Normalize(Vector3.Cross(forward, left));
        var rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            forward.X, forward.Y, forward.Z, 0, left.X, left.Y, left.Z, 0,
            up.X, up.Y, up.Z, 0, 0, 0, 0, 1));
        var ankle = (Point("l_foot_1_0") + Point("r_foot_1_0")) * .5f;
        return (rotation, Vector3.Dot(Point("c_head_1_0") - ankle, up), Vector3.Dot(Point("c_c_1_0") - ankle, up));
    }

    private static Quaternion SourceRotationBetween(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot < -.999999f)
        {
            var axis = Vector3.Cross(from, MathF.Abs(from.X) < .9f ? Vector3.UnitX : Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + dot));
    }
    private static Vector3 SourcePoint(G4AvatarNode node) => new(node.RestMatrix[3], node.RestMatrix[7], node.RestMatrix[11]);
    private static Quaternion SourceQuaternion(float[] q) => Quaternion.Normalize(new(q[0], q[1], q[2], q[3]));
    private static float[] SourceQuaternionArray(Quaternion q) => [q.X, q.Y, q.Z, q.W];
    private static Matrix4x4 SourceReadMatrix(float[] v) => Matrix4x4.Transpose(new(
        v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]));
    private static float[] SourceMatrix(Matrix4x4 v) => [v.M11, v.M21, v.M31, v.M41, v.M12, v.M22, v.M32, v.M42,
        v.M13, v.M23, v.M33, v.M43, v.M14, v.M24, v.M34, v.M44];
    private static Matrix4x4 SourceInverse(Matrix4x4 matrix) => Matrix4x4.Invert(matrix, out var result)
        ? result : throw new InvalidDataException("EVENT source skeleton has a singular rest transform.");
    private static G4AvatarTransform SourceTransform(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation))
            throw new InvalidDataException("EVENT source skeleton has a non-decomposable rest transform.");
        return new([translation.X, translation.Y, translation.Z], SourceQuaternionArray(rotation), [scale.X, scale.Y, scale.Z]);
    }
}
